using GUI.Devices;
using LnScope.Core.Devices;

namespace GUI.Motion;

/// <summary>WPF-facing facade. The default motion controller and policies are editable Python.</summary>
public sealed class MotionService : IAsyncDisposable
{
    private readonly Dictionary<string, IPositioner> _positioners;
    private readonly PythonMotionHost? _python;
    private readonly LegacyMotionService? _legacy;
    public event Action<string>? MotionHalted;
    public event Action? ProfilesChanged;
    public IReadOnlyList<MotionLink> Links { get; }
    public MoveSpeed ManualSpeed { get; set; } = MoveSpeed.Slow;
    public bool UsesPython => _python is not null;
    public bool IsStopping => _python?.IsStopping ?? _legacy!.IsStopping;
    public Task Ready => _python?.Ready ?? Task.CompletedTask;
    internal int WorkerProcessId => _python?.ProcessId ?? 0;

    public MotionService(IEnumerable<IPositioner> positioners, IEnumerable<MotionLink> links, Action<string> log, bool usePython = true)
    {
        _positioners = positioners.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Links = links.ToList();
        if (usePython)
        {
            _python = new PythonMotionHost(_positioners, Links, log);
            _python.Halted += reason => MotionHalted?.Invoke(reason);
            _python.ProfilesChanged += () => ProfilesChanged?.Invoke();
        }
        else
        {
            _legacy = new LegacyMotionService(_positioners.Values, Links, log);
            _legacy.MotionHalted += reason => MotionHalted?.Invoke(reason);
        }
    }
    public IPositioner Resolve(string id) => _positioners.TryGetValue(id, out var p) ? p : throw new ArgumentException($"Unknown device '{id}'.");
    public Task<AxisPosition> ReadPositionAsync(string device, string axis, CancellationToken ct = default) => Resolve(device).ReadPositionAsync(axis, ct);
    public bool IsAxisBusy(string device, string axis) => _python?.IsAxisBusy(device, axis) ?? _legacy!.IsAxisBusy(device, axis);
    public async Task<double> ReferenceTargetAsync(string device, string axis, double displayedTarget, CancellationToken ct = default)
    {
        if (Resolve(device) is PythonPositioner python)
            return (await python.CalculateAsync("reference_target", new { axis, value = displayedTarget }, ct)).GetDouble();
        var current = await ReadPositionAsync(device, axis, ct);
        return displayedTarget + current.Reference - current.User;
    }
    public JogProfile JogProfile(string rate) => _python?.Profile(rate) ??
        new JogProfile(rate == "M" ? 5 : rate == "H" ? 20 : 1, rate == "L" ? MoveSpeed.Slow : MoveSpeed.Fast);
    public Task JogAsync(IEnumerable<JogBinding> bindings, double step, string rate) =>
        _python?.JogAsync(bindings, step, rate) ?? throw new InvalidOperationException("Legacy jog uses its original implementation.");
    public Task MoveRelativeAsync(string device, string axis, double delta, MoveSpeed speed, CancellationToken ct = default) =>
        _python is null ? _legacy!.MoveRelativeAsync(device, axis, delta, speed, ct) : MoveAsync(device, axis, delta, false, speed, ct);
    public Task MoveAbsoluteAsync(string device, string axis, double target, MoveSpeed speed, CancellationToken ct = default) =>
        _python is null ? _legacy!.MoveAbsoluteAsync(device, axis, target, speed, ct) : MoveAsync(device, axis, target, true, speed, ct);
    private async Task MoveAsync(string device, string axis, double value, bool absolute, MoveSpeed speed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var movement = _python!.MoveAsync(device, axis, value, absolute, speed);
        using var registration = ct.Register(() => _ = CancelHeldMotionAsync());
        await movement;
    }
    public Task CancelHeldMotionAsync() => _python?.CancelAsync() ?? _legacy!.CancelHeldMotionAsync();
    public bool SupportsPositionMemory(string device) => _python is not null && Resolve(device) is PythonPositioner { SupportsPositionMemory: true };
    public Task SavePositionAsync(string device, int slot) => SupportsPositionMemory(device)
        ? _python!.SavePositionAsync(device, slot)
        : Task.FromException(new NotSupportedException("Position memory requires the Python SM-10 or simulated backend."));
    public Task GotoPositionAsync(string device, int slot, int velocity) => SupportsPositionMemory(device)
        ? _python!.GotoPositionAsync(device, slot, velocity)
        : Task.FromException(new NotSupportedException("Position memory requires the Python SM-10 or simulated backend."));
    public async Task StopAllAsync()
    {
        if (_python is not null) { await _python.StopAllAsync(); return; }
        await _legacy!.CancelHeldMotionAsync();
        var errors = new List<Exception>();
        foreach (var device in _positioners.Values.Where(p => p.State == DeviceState.Ready))
            foreach (var axis in device.Axes)
                try { await device.StopAsync(axis.Id); }
                catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public Task StopAsync(string device, string axis, CancellationToken ct = default) =>
        _python?.StopAsync(device, axis) ?? _legacy!.StopAsync(device, axis, ct);
    public async ValueTask DisposeAsync()
    {
        if (_python is not null) await _python.DisposeAsync();
        else await _legacy!.CancelHeldMotionAsync();
    }
}
