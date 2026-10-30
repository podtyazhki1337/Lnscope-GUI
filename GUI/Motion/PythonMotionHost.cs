using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using GUI.Devices;
using LnScope.Core.Devices;

namespace GUI.Motion;

/// <summary>UI/hardware adapter. Movement decisions and active batches live in Python.</summary>
internal sealed class PythonMotionHost : IAsyncDisposable
{
    private readonly Dictionary<string, IPositioner> _devices;
    private readonly IReadOnlyList<MotionLink> _links;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private PythonMotionRpc _rpc = null!;
    private HashSet<(string, string)> _busy = [];
    private readonly HashSet<string> _disconnected = [];
    private readonly ConcurrentDictionary<string, int> _generations = new();
    private bool _stopping;
    private volatile bool _disposed, _failed;
    private Task _guardStop = Task.CompletedTask;
    private Task _ready = Task.CompletedTask;
    private Dictionary<string, JogProfile> _profiles = [];
    public event Action<string>? Halted;
    public event Action? ProfilesChanged;
    public bool IsStopping { get { lock (_gate) return _stopping; } }
    public bool IsAxisBusy(string device, string axis) { lock (_gate) return _busy.Contains((device, axis)); }
    public Task Ready => _ready;
    internal int ProcessId => _rpc.ProcessId;
    public JogProfile Profile(string rate) { lock (_gate) return _profiles.GetValueOrDefault(rate, new(1, MoveSpeed.Slow)); }

    public PythonMotionHost(Dictionary<string, IPositioner> devices, IReadOnlyList<MotionLink> links, Action<string> log)
    {
        _devices = devices; _links = links; _log = log;
        foreach (var id in devices.Keys) _generations[id] = 0;
        Start();
        foreach (var device in devices.Values)
        {
            device.PositionChanged += OnPosition;
            device.StateChanged += OnState;
        }
        _ = HeartbeatAsync();
    }
    private void Start()
    {
        lock (_gate) _failed = false;
        PythonMotionRpc? session = null;
        session = new PythonMotionRpc(HardwareAsync, Notification,
            error => { if (ReferenceEquals(_rpc, session)) Failed(error); });
        _rpc = session;
        _ready = InitializeAsync(session);
    }
    private async Task InitializeAsync(PythonMotionRpc session)
    {
        try
        {
            var hello = await session.CallAsync("init", new { devices = _devices.Values.Select(d => new { d.Id, d.Axes, d.State,
                PythonFeedback = d is PythonPositioner, Generation = _generations[d.Id],
                FeedbackIntervalMs = d is PythonPositioner p ? p.FeedbackIntervalMs : 200 }).ToArray() }).ConfigureAwait(false);
            if (hello.GetProperty("protocol").GetInt32() != 2) throw new IOException("Incompatible Python motion backend.");
            var profiles = hello.GetProperty("profiles").EnumerateObject().ToDictionary(p => p.Name,
                p => new JogProfile(p.Value.GetProperty("multiplier").GetDouble(), (MoveSpeed)p.Value.GetProperty("speed").GetInt32()));
            lock (_gate) { _profiles = profiles; _stopping = false; _busy.Clear(); }
            ProfilesChanged?.Invoke();
        }
        catch (Exception error) { if (ReferenceEquals(_rpc, session)) Failed(error); throw; }
    }
    private void Notification(string name, JsonElement data)
    {
        if (name == "motion")
        {
            lock (_gate)
            {
                _busy = data.GetProperty("busy").EnumerateArray().Select(m => (m.GetProperty("device").GetString()!, m.GetProperty("axis").GetString()!)).ToHashSet();
                _stopping = data.GetProperty("stopping").GetBoolean();
            }
        }
        else if (name == "halted") { var reason = data.GetProperty("reason").GetString()!; _log(reason); Halted?.Invoke(reason); }
        else if (name == "log") _log(data.GetProperty("message").GetString()!);
        else if (name == "position" && _devices[data.GetProperty("device").GetString()!] is PythonPositioner positioner)
        {
            if (data.GetProperty("generation").GetInt32() == _generations[positioner.Id])
                positioner.PublishPosition(data.GetProperty("position").Deserialize<AxisPosition>()!);
        }
    }
    private async Task<object?> HardwareAsync(string op, JsonElement args)
    {
        if (_failed && op is not ("stop" or "emergency")) throw new IOException("Python motion safety latch is set.");
        var device = _devices[args.GetProperty("device").GetString()!];
        if (args.GetProperty("generation").GetInt32() != _generations[device.Id])
            throw new IOException("Stale operation from a previous device connection.");
        var axis = args.TryGetProperty("axis", out var a) ? a.GetString()! : "";
        switch (op)
        {
            case "save_position":
            case "goto_position":
                if (device is not PythonPositioner memory || !memory.SupportsPositionMemory || device.State != DeviceState.Ready)
                    throw new InvalidOperationException("Position memory requires a connected SM-10 or simulated Python device.");
                await memory.CalculateAsync(op, new { slot = args.GetProperty("slot").GetInt32(),
                    velocity = args.TryGetProperty("velocity", out var velocity) ? velocity.GetInt32() : 1 }).ConfigureAwait(false);
                return null;
            case "read": return await device.ReadPositionAsync(axis).ConfigureAwait(false);
            case "validate":
                if (device is IPositionerSafety safety) safety.ValidateTarget(axis, args.GetProperty("value").GetDouble());
                return null;
            case "move":
                await device.MoveAbsoluteAsync(axis, args.GetProperty("value").GetDouble(), (MoveSpeed)args.GetProperty("speed").GetInt32()).ConfigureAwait(false); return null;
            case "stop": await device.StopAsync(axis).ConfigureAwait(false); return null;
            case "emergency":
                if (device is IPositionerSafety stop) await stop.EmergencyStopAsync().ConfigureAwait(false);
                else foreach (var info in device.Axes) await device.StopAsync(info.Id).ConfigureAwait(false);
                return null;
            default: throw new IOException("Unknown Python hardware operation.");
        }
    }
    private object Links() => _links.Select(l => new { Enabled = l.IsEnabled, l.Options }).ToArray();
    public Task MoveAsync(string device, string axis, double value, bool absolute, MoveSpeed speed) =>
        _failed ? Task.FromException(new IOException("Python motion stopped. Reconnect all groups.")) :
        _rpc.CallAsync("move", new { device, axis, value, absolute, speed = (int)speed, links = Links() });
    public Task JogAsync(IEnumerable<JogBinding> bindings, double step, string rate) =>
        _failed ? Task.FromException(new IOException("Python motion stopped. Reconnect all groups.")) :
        _rpc.CallAsync("jog", new { bindings = bindings.ToArray(), step, rate, links = Links() });
    public Task CancelAsync() => _failed ? _guardStop : _rpc.CallAsync("cancel", new { });
    public Task StopAllAsync() => _failed ? _guardStop : _rpc.CallAsync("stop_all", new { });
    public Task SavePositionAsync(string device, int slot) => _rpc.CallAsync("save_position", new { device, slot });
    public Task GotoPositionAsync(string device, int slot, int velocity) => _rpc.CallAsync("goto_position", new { device, slot, velocity });
    public Task StopAsync(string device, string axis) => _rpc.CallAsync("stop", new { device, axis });
    private void OnPosition(object? sender, AxisPosition position)
    {
        if (!_disposed && !_failed && sender is IPositioner device && sender is not PythonPositioner)
            Observe(_rpc.CallAsync("feedback", new { device = device.Id, position }));
    }
    private void OnState(object? sender, DeviceState state)
    {
        if (_disposed || sender is not IPositioner device) return;
        // A fault still belongs to the same connection: Stop must remain possible.
        // Only opening/closing a connection invalidates callbacks from the previous one.
        var generation = state is DeviceState.Connecting or DeviceState.Disconnected
            ? _generations.AddOrUpdate(device.Id, 1, (_, value) => value + 1) : _generations[device.Id];
        if (_failed)
        {
            lock (_gate)
            {
                if (state == DeviceState.Disconnected) _disconnected.Add(device.Id);
                if (_disconnected.Count == _devices.Count && _devices.Values.All(d => d.State == DeviceState.Ready))
                {
                    var old = _rpc; _disconnected.Clear(); _ready = RestartAsync(old);
                }
            }
            return;
        }
        Observe(_rpc.CallAsync("state", new { device = device.Id, state = (int)state, generation }));
    }
    private async void Observe(Task task)
    {
        var session = _rpc;
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { if (!_disposed && !_failed && ReferenceEquals(session, _rpc)) Failed(error); }
    }
    private async Task RestartAsync(PythonMotionRpc previous)
    {
        await _guardStop.ConfigureAwait(false);
        await previous.DisposeAsync().ConfigureAwait(false);
        Task initialized;
        lock (_gate)
        {
            if (_disposed) return;
            Start(); initialized = _ready;
        }
        await initialized.ConfigureAwait(false);
    }
    private void Failed(Exception error)
    {
        lock (_gate)
        {
            if (_disposed || _failed) return;
            _failed = true; _stopping = true; _disconnected.Clear();
            _guardStop = GuardStopAsync();
        }
        _log("Python motion failure: " + error.Message);
        Halted?.Invoke("Python motion process failed; emergency Stop dispatched.");
    }
    private async Task GuardStopAsync()
    {
        await Task.Yield();
        foreach (var device in _devices.Values)
        {
            if (device.State == DeviceState.Disconnected) continue;
            try
            {
                if (device is IPositionerSafety safety) await safety.EmergencyStopAsync().ConfigureAwait(false);
                else foreach (var axis in device.Axes) await device.StopAsync(axis.Id).ConfigureAwait(false);
            }
            catch (Exception error) { _log($"Emergency Stop {device.Id}: {error.Message}"); }
        }
    }
    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
                if (!_failed)
                {
                    var session = _rpc;
                    try { await session.CallAsync("ping", new { }, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception error) { if (!_disposed && ReferenceEquals(session, _rpc)) Failed(error); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) Failed(error); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await CancelAsync().ConfigureAwait(false); }
        finally
        {
            _disposed = true; _lifetime.Cancel();
            foreach (var device in _devices.Values) { device.PositionChanged -= OnPosition; device.StateChanged -= OnState; }
            await _rpc.DisposeAsync().ConfigureAwait(false); await _guardStop.ConfigureAwait(false);
        }
    }
}

public sealed record JogProfile(double Multiplier, MoveSpeed Speed);
