using System.IO;
using System.Text.Json;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;
using LnScope.Devices.LuigsNeumann.Sm10;

namespace GUI.Devices;

/// <summary>C# lifecycle/stop guardian; Python implements positioner operations and motion calculations.</summary>
public sealed class PythonPositioner(LnManipulatorOptions options, ISm10Bus? bus) : IPositioner, IPositionerSafety
{
    private PythonWorker? _worker;
    private Task _emergency = Task.CompletedTask;
    private readonly object _safetyGate = new();
    private bool _leased, _faulted;
    private volatile bool _motionIssued;
    public string Id => options.Id;
    internal int FeedbackIntervalMs => options.FeedbackIntervalMs;
    internal bool SupportsPositionMemory => options.Transport.Equals("simulated", StringComparison.OrdinalIgnoreCase) || bus is not null;
    internal void PublishPosition(AxisPosition position) => PositionChanged?.Invoke(this, position);
    internal int WorkerProcessId => _worker?.ProcessId ?? throw new InvalidOperationException("Worker has not started.");
    public DeviceState State { get; private set; } = DeviceState.Disconnected;
    public IReadOnlyList<AxisInfo> Axes { get; } = options.Axes.Select(a =>
        new AxisInfo(a.Id, a.Unit, a.MinPosition, a.MaxPosition, options.DisplayUserCoordinates)).ToArray();
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<AxisPosition>? PositionChanged;
    private void SetState(DeviceState state) { State = state; StateChanged?.Invoke(this, state); }
    private LnAxisOptions Axis(string id) => options.Axes.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown axis '{id}'.");

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State == DeviceState.Ready) return;
        if (_worker is not null || _leased) await DisconnectAsync(ct);
        SetState(DeviceState.Connecting);
        lock (_safetyGate) _faulted = false;
        _motionIssued = false;
        var worker = new PythonWorker(bus, options.ResponseTimeoutMs, GuardRequest);
        _worker = worker;
        worker.Failed += () => { if (ReferenceEquals(_worker, worker)) _ = EmergencyStopAsync(); };
        try
        {
            var hello = await worker.CallAsync("init", options, ct);
            if (hello.GetProperty("protocol").GetInt32() != 1) throw new IOException("Incompatible Python backend.");
            if (bus is not null) { await bus.ConnectAsync(ct); _leased = true; }
            await worker.CallAsync("connect", new { }, ct);
            if (_faulted) throw new IOException("Backend failed during connection.");
            SetState(DeviceState.Ready);
        }
        catch
        {
            await DisconnectAsync(); SetState(DeviceState.Faulted); throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_motionIssued) await EmergencyStopAsync();
            await _emergency;
        }
        finally
        {
            var worker = _worker; _worker = null;
            try { if (worker is not null) await worker.DisposeAsync(); }
            finally
            {
                try { if (_leased) await bus!.DisconnectAsync(); }
                finally { _leased = false; SetState(DeviceState.Disconnected); }
            }
        }
    }

    public Task<JsonElement> CalculateAsync(string method, object args, CancellationToken ct = default) =>
        Worker.CallAsync(method, args, ct);
    private void GuardRequest(ushort command, byte[] payload)
    {
        if (_faulted || command is not (0x0101 or 0x0131 or 0x0120 or 0x0048 or 0x0049 or 0xA0FF or 0xA10A or 0xA110))
            throw new IOException("Python requested an operation outside the positioner contract.");
        if (command is 0xA0FF or 0xA10A or 0xA110)
        {
            Sm10Commands.Get(command).ValidatePayload(payload);
            if (Enumerable.Range(1, 72).Any(a => (payload[9 - (a - 1) / 8] & (1 << ((a - 1) % 8))) != 0 && !options.Axes.Any(axis => axis.HardwareAxis == a)))
                throw new IOException("Group command addresses an axis outside this logical group.");
            if (command == 0xA10A && State != DeviceState.Ready)
                throw new IOException("Connect the device before saving a position.");
            if (command == 0xA110)
            {
                if (State != DeviceState.Ready || !options.MotionEnabled || payload[11] is < 1 or > 15 ||
                    options.Axes.Any(a => a.MinPosition is not { } min || a.MaxPosition is not { } max ||
                        !double.IsFinite(min) || !double.IsFinite(max) || min >= max))
                    throw new IOException("Verify axis mapping and finite travel limits, then enable motion.");
                _motionIssued = true;
            }
            return;
        }
        var moving = command is 0x0048 or 0x0049;
        if (payload.Length != (moving ? 5 : 1)) throw new IOException("Invalid Python command payload.");
        var axis = options.Axes.SingleOrDefault(a => a.HardwareAxis == payload[0])
            ?? throw new IOException("Python requested an axis outside this logical group.");
        if (moving)
        {
            ValidateTarget(axis.Id, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(1)));
            _motionIssued = true;
        }
    }
    private PythonWorker Worker => !_faulted && _worker is { } worker ? worker
        : throw new IOException("Python backend is disconnected or faulted. Reconnect controller groups.");

    public async Task<AxisPosition> ReadPositionAsync(string axisId, CancellationToken ct = default)
    {
        Axis(axisId);
        var response = await Worker.CallAsync("read", new { axis = axisId }, ct);
        return response.Deserialize<AxisPosition>() ?? throw new IOException("Invalid Python position response.");
    }

    // Independent guard: a Python regression must not bypass enablement or software bounds.
    public void ValidateTarget(string axisId, double referenceTarget)
    {
        var axis = Axis(axisId);
        if (State != DeviceState.Ready || _faulted) throw new InvalidOperationException("Positioner is not ready.");
        if (bus is not null && (!options.MotionEnabled || axis.MinPosition is not { } min || axis.MaxPosition is not { } max ||
            !double.IsFinite(min) || !double.IsFinite(max) || min >= max))
            throw new InvalidOperationException("Verify axis mapping and finite travel limits, then enable motion.");
        if (!double.IsFinite(referenceTarget) || referenceTarget < axis.MinPosition || referenceTarget > axis.MaxPosition)
            throw new ArgumentOutOfRangeException(nameof(referenceTarget), "Target is outside configured travel limits.");
    }

    public async Task MoveAbsoluteAsync(string axisId, double position, MoveSpeed speed, CancellationToken ct = default)
    {
        ValidateTarget(axisId, position);
        await Worker.CallAsync("move", new { axis = axisId, value = position, speed = (int)speed, relative = false }, ct);
    }
    public async Task MoveRelativeAsync(string axisId, double delta, MoveSpeed speed, CancellationToken ct = default)
    {
        if (State != DeviceState.Ready || _faulted) throw new InvalidOperationException("Positioner is not ready.");
        await Worker.CallAsync("move", new { axis = axisId, value = delta, speed = (int)speed, relative = true }, ct);
    }

    public async Task StopAsync(string axisId, CancellationToken ct = default)
    {
        await Worker.CallAsync("stop", new { axis = axisId }, ct);
    }

    public Task EmergencyStopAsync()
    {
        lock (_safetyGate)
        {
            if (_faulted) return _emergency;
            _faulted = true;
            _emergency = StopCoreAsync();
            SetState(DeviceState.Faulted);
            return _emergency;
        }
    }
    private async Task StopCoreAsync()
    {
        await Task.Yield();
        try
        {
            if (!_leased) return;
            if (bus is Sm10SerialBus serial) await serial.EmergencyStopAsync(options.Axes.Select(a => a.HardwareAxis));
            else await bus!.RequestAsync(0xA0FF, Sm10Command.BuildMask(options.Axes.Select(a => a.HardwareAxis).ToArray()));
        }
        finally
        {
            // Terminate a hung worker after dispatching Stop through the independent serial path.
            if (_worker is { } worker) await worker.DisposeAsync();
        }
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
