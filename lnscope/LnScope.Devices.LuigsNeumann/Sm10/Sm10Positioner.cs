using System.IO;
using LnScope.Core.Devices;

namespace LnScope.Devices.LuigsNeumann.Sm10;

public sealed class Sm10Positioner(LnManipulatorOptions options, ISm10Bus bus) : IPositioner, IPositionerSafety
{
    private CancellationTokenSource? _pollCts;
    private Task? _poll;
    private bool _leased;
    private readonly SemaphoreSlim _motion = new(1, 1);
    public string Id => options.Id;
    public DeviceState State { get; private set; } = DeviceState.Disconnected;
    public IReadOnlyList<AxisInfo> Axes { get; } = options.Axes
        .Select(a => new AxisInfo(a.Id, a.Unit, a.MinPosition, a.MaxPosition, options.DisplayUserCoordinates)).ToArray();
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<AxisPosition>? PositionChanged;
    private void SetState(DeviceState state) { State = state; StateChanged?.Invoke(this, state); }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State == DeviceState.Ready) return;
        if (_leased) await DisconnectAsync(ct);
        SetState(DeviceState.Connecting);
        try
        {
            await bus.ConnectAsync(ct);
            _leased = true;
            // Read-only handshake: no homing, enabling axes, protocol switching or motion.
            foreach (var axis in options.Axes) await ReadPositionAsync(axis.Id, ct);
            SetState(DeviceState.Ready);
            _pollCts = new CancellationTokenSource();
            _poll = PollAsync(_pollCts.Token);
        }
        catch
        {
            if (_leased) { await bus.DisconnectAsync(); _leased = false; }
            SetState(DeviceState.Faulted);
            throw;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Math.Clamp(options.FeedbackIntervalMs, 100, 2000), ct);
                foreach (var axis in options.Axes)
                {
                    ct.ThrowIfCancellationRequested();
                    // Finish any in-flight exchange before releasing a shared port lease.
                    PositionChanged?.Invoke(this, await ReadPositionAsync(axis.Id));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { SetState(DeviceState.Faulted); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();
            if (_poll is not null) await _poll;
            _pollCts.Dispose(); _pollCts = null; _poll = null;
        }
        if (_leased) { await bus.DisconnectAsync(); _leased = false; }
        SetState(DeviceState.Disconnected);
    }

    private LnAxisOptions Axis(string id) => options.Axes.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown axis '{id}'.");

    public async Task<AxisPosition> ReadPositionAsync(string axisId, CancellationToken ct = default)
    {
        if (!_leased) throw new InvalidOperationException("Connect the SM-10 first.");
        var address = Sm10Protocol.Axis(Axis(axisId).HardwareAxis);
        var status = await bus.RequestAsync(Sm10Protocol.Status, address, ct);
        var position = Sm10Protocol.ReadPosition(await bus.RequestAsync(Sm10Protocol.Position, address, ct));
        var counter = Sm10Protocol.ReadPosition(await bus.RequestAsync(Sm10Protocol.Counter2, address, ct));
        // German original p.18: eight bytes, TWO reserved bytes before step resolution.
        // The English translation omits one reserved byte. Motor status is byte index 6.
        if (status.Length != 8) throw new IOException("Invalid SM-10 status payload.");
        return new AxisPosition(axisId, position, counter, status[6] != 0, status[0] != 0, DateTimeOffset.UtcNow,
            status[0] <= 2 ? (AxisLimit)status[0] : AxisLimit.Unknown);
    }

    public void ValidateTarget(string axisId, double referenceTarget) => ValidateTarget(Axis(axisId), referenceTarget);

    public async Task EmergencyStopAsync()
    {
        try
        {
            if (!_leased) return;
            if (bus is Sm10SerialBus serial) await serial.EmergencyStopAsync(options.Axes.Select(a => a.HardwareAxis));
            else await bus.RequestAsync(0xA0FF, Sm10Command.BuildMask(options.Axes.Select(a => a.HardwareAxis).ToArray()));
        }
        finally { SetState(DeviceState.Faulted); }
    }

    private void ValidateTarget(LnAxisOptions axis, double target)
    {
        if (State != DeviceState.Ready) throw new InvalidOperationException("SM-10 is not ready.");
        if (!options.MotionEnabled) throw new InvalidOperationException("Verify axis mapping and travel limits in Hardware setup, then enable motion.");
        if (axis.MinPosition is not { } min || axis.MaxPosition is not { } max ||
            !double.IsFinite(min) || !double.IsFinite(max) || min >= max)
            throw new InvalidOperationException("Real axes require verified finite travel limits.");
        if (!double.IsFinite(target) || target < min || target > max)
            throw new ArgumentOutOfRangeException(nameof(target), "Target is outside the configured travel limits.");
    }

    public async Task MoveAbsoluteAsync(string axisId, double position, MoveSpeed speed, CancellationToken ct = default)
    {
        await _motion.WaitAsync(ct);
        try { await MoveCoreAsync(axisId, position, speed, ct); }
        finally { _motion.Release(); }
    }
    private async Task MoveCoreAsync(string axisId, double position, MoveSpeed speed, CancellationToken ct)
    {
        var axis = Axis(axisId);
        ValidateTarget(axis, position);
        var result = await bus.RequestAsync(speed == MoveSpeed.Fast ? Sm10Protocol.AbsoluteFast : Sm10Protocol.AbsoluteSlow,
            Sm10Protocol.Target(axis.HardwareAxis, position), ct);
        if (result.Length != 0) throw new IOException("Unexpected movement acknowledgment.");
    }
    public async Task MoveRelativeAsync(string axisId, double delta, MoveSpeed speed, CancellationToken ct = default)
    {
        await _motion.WaitAsync(ct);
        try
        {
            var current = await ReadPositionAsync(axisId, ct);
            await MoveCoreAsync(axisId, current.Reference + delta, speed, ct);
        }
        finally { _motion.Release(); }
    }
    public async Task StopAsync(string axisId, CancellationToken ct = default)
    {
        if (!_leased) throw new InvalidOperationException("SM-10 is disconnected.");
        // Documented group Stop has no ACK and is not queued behind the positioner's motion semaphore.
        var result = await bus.RequestAsync(0xA0FF, Sm10Command.BuildMask([Axis(axisId).HardwareAxis]), ct);
        if (result.Length != 0) throw new IOException("Unexpected stop acknowledgment.");
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
