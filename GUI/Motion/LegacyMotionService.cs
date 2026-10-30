using LnScope.Core.Devices;

namespace GUI.Motion;

/// <summary>Validate every linked axis before dispatch and monitor commanded motion until completion.</summary>
public sealed class LegacyMotionService
{
    private sealed class Member(string device, string axis, double factor)
    {
        public string Device = device, Axis = axis;
        public double Factor = factor, Target, Direction;
        public bool Sent, Done;
        public DateTime Feedback = DateTime.UtcNow;
        public DateTime SentAt;
    }
    private sealed class Batch(List<Member> members)
    {
        public List<Member> Members = members;
        public CancellationTokenSource Cancel = new();
    }
    private readonly Dictionary<string, IPositioner> _positioners;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly object _gate = new();
    private readonly List<Batch> _active = [];
    private bool _tripped;
    private Task _stopping = Task.CompletedTask;
    public event Action<string>? MotionHalted;
    public IReadOnlyList<MotionLink> Links { get; }
    public MoveSpeed ManualSpeed { get; set; } = MoveSpeed.Slow;
    public bool IsStopping { get { lock (_gate) return !_stopping.IsCompleted; } }

    public LegacyMotionService(IEnumerable<IPositioner> positioners, IEnumerable<MotionLink> links, Action<string> log)
    {
        _positioners = positioners.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Links = links.ToList(); _log = log;
        foreach (var device in _positioners.Values)
        {
            device.PositionChanged += (_, position) => Feedback(device.Id, position);
            device.StateChanged += (_, state) =>
            {
                if (state is DeviceState.Faulted or DeviceState.Disconnected)
                {
                    bool active;
                    // Even an idle logical group may share the serial bus with a moving one.
                    lock (_gate) active = _active.Count > 0;
                    if (active) Trip($"{device.Id}: feedback lost or disconnected");
                }
                lock (_gate)
                    if (_stopping.IsCompleted && _positioners.Values.All(p => p.State == DeviceState.Ready)) _tripped = false;
            };
        }
    }
    public IPositioner Resolve(string id) => _positioners.TryGetValue(id, out var p) ? p : throw new ArgumentException($"Unknown device '{id}'.");
    public Task<AxisPosition> ReadPositionAsync(string device, string axis, CancellationToken ct = default) => Resolve(device).ReadPositionAsync(axis, ct);
    public bool IsAxisBusy(string device, string axis)
    {
        lock (_gate) return _active.Any(b => b.Members.Any(m => m.Device == device && m.Axis == axis));
    }
    public Task MoveRelativeAsync(string device, string axis, double delta, MoveSpeed speed, CancellationToken ct = default) =>
        ExecuteAsync(device, axis, delta, false, speed, ct);
    public Task MoveAbsoluteAsync(string device, string axis, double target, MoveSpeed speed, CancellationToken ct = default) =>
        ExecuteAsync(device, axis, target, true, speed, ct);

    private List<Member> Members(string device, string axis)
    {
        var result = new List<Member> { new(device, axis, 1) };
        foreach (var link in Links.Where(l => l.IsEnabled))
        {
            var o = link.Options;
            if (o.Source == device)
                result.AddRange(o.AxisMap.Where(m => m.From == axis).Select(m => new Member(o.Target, m.To, m.Factor)));
            else if (o.Bidirectional && o.Target == device)
                result.AddRange(o.AxisMap.Where(m => m.To == axis).Select(m => new Member(o.Source, m.From,
                    m.Factor != 0 ? 1 / m.Factor : throw new ArgumentException("A bidirectional link factor cannot be zero."))));
        }
        if (result.Select(m => (m.Device, m.Axis)).Distinct().Count() != result.Count)
            throw new InvalidOperationException("Conflicting linked axes.");
        return result;
    }

    private async Task ExecuteAsync(string device, string axis, double value, bool absolute, MoveSpeed speed, CancellationToken ct)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var batch = new Batch(Members(device, axis));
        using var registration = ct.Register(() => batch.Cancel.Cancel());
        lock (_gate)
        {
            if (_tripped || !_stopping.IsCompleted) throw new InvalidOperationException("Motion stopped. Disconnect and reconnect all controller groups before resuming.");
            if (_active.Any(b => b.Members.Any(a => batch.Members.Any(m => m.Device == a.Device && m.Axis == a.Axis))))
                throw new InvalidOperationException("Wait for the current movement to finish.");
            _active.Add(batch);
        }
        var entered = false;
        try
        {
            await _dispatch.WaitAsync(ct); entered = true;
            double delta = value;
            for (int i = 0; i < batch.Members.Count; i++)
            {
                batch.Cancel.Token.ThrowIfCancellationRequested();
                var m = batch.Members[i]; var p = Resolve(m.Device);
                if (p.State != DeviceState.Ready) throw new InvalidOperationException($"{m.Device} is not ready.");
                // On ordinary key release finish an already-sent read without corrupting serial framing.
                var current = await p.ReadPositionAsync(m.Axis);
                if (current.IsMoving) throw new InvalidOperationException($"{m.Device}.{m.Axis} is already moving; wait for standstill.");
                if (i == 0 && absolute) delta = value - current.Reference;
                m.Target = current.Reference + delta * m.Factor;
                m.Direction = Math.Sign(m.Target - current.Reference);
                var info = p.Axes.Single(a => a.Id == m.Axis);
                if (!double.IsFinite(m.Target) || m.Target < info.MinPosition || m.Target > info.MaxPosition)
                    throw new InvalidOperationException($"{m.Device}.{m.Axis}: target is outside software C1 limits. No linked movement started.");
                if (Blocks(current, m.Direction)) throw new InvalidOperationException($"{m.Device}.{m.Axis}: hardware {current.Limit} limit. Move away from the limit.");
                if (p is IPositionerSafety safety) safety.ValidateTarget(m.Axis, m.Target);
            }
            foreach (var m in batch.Members)
            {
                batch.Cancel.Token.ThrowIfCancellationRequested();
                lock (_gate) { m.Sent = true; m.SentAt = m.Feedback = DateTime.UtcNow; }
                await Resolve(m.Device).MoveAbsoluteAsync(m.Axis, m.Target, speed);
                batch.Cancel.Token.ThrowIfCancellationRequested();
            }
            _ = WatchAsync(batch);
        }
        catch (Exception ex)
        {
            bool sent; lock (_gate) sent = batch.Members.Any(m => m.Sent);
            if (sent && !batch.Cancel.IsCancellationRequested) Trip($"Motion error: {ex.Message}");
            else if (!sent) { lock (_gate) _active.Remove(batch); }
            throw;
        }
        finally { if (entered) _dispatch.Release(); }
    }

    private static bool Blocks(AxisPosition p, double direction) => p.StopSwitchActive &&
        (p.Limit is AxisLimit.None or AxisLimit.Unknown || p.Limit == AxisLimit.Negative && direction < 0 || p.Limit == AxisLimit.Positive && direction > 0);

    private void Feedback(string device, AxisPosition position)
    {
        string? reason = null;
        lock (_gate)
        {
            foreach (var b in _active.ToArray())
            {
                foreach (var m in b.Members.Where(m => m.Device == device && m.Axis == position.AxisId && m.Sent))
                {
                    m.Feedback = DateTime.UtcNow;
                    var info = Resolve(device).Axes.Single(a => a.Id == m.Axis);
                    if (Blocks(position, m.Direction) ||
                        m.Direction < 0 && position.Reference <= info.MinPosition ||
                        m.Direction > 0 && position.Reference >= info.MaxPosition)
                        reason = $"{device}.{position.AxisId}: limit reached; stopping all active motion";
                    if (!position.IsMoving && Math.Abs(position.Reference - m.Target) <= 0.05) m.Done = true;
                    else if (!m.Done && !position.IsMoving && DateTime.UtcNow - m.SentAt > TimeSpan.FromSeconds(1))
                        reason = $"{device}.{position.AxisId}: stopped before target; stopping linked motion";
                }
                if (b.Members.All(m => m.Done) && reason is null) _active.Remove(b);
            }
        }
        if (reason is not null) Trip(reason);
    }

    private async Task WatchAsync(Batch batch)
    {
        while (true)
        {
            await Task.Delay(250);
            bool stale;
            lock (_gate)
            {
                if (!_active.Contains(batch) || batch.Cancel.IsCancellationRequested) return;
                stale = batch.Members.Any(m => m.Sent && !m.Done && DateTime.UtcNow - m.Feedback > TimeSpan.FromSeconds(3));
            }
            if (stale) { Trip("No fresh position feedback for 3 seconds; stopping active motion"); return; }
        }
    }

    private void Trip(string reason)
    {
        Batch[] batches;
        lock (_gate)
        {
            if (_tripped) return;
            _tripped = true; batches = _active.ToArray();
            foreach (var b in batches) b.Cancel.Cancel();
            _stopping = StopBatchesAsync(batches, emergency: true);
        }
        _log(reason + ". Reconnect controller groups to reset the stop latch.");
        MotionHalted?.Invoke(reason);
    }

    public Task CancelHeldMotionAsync()
    {
        lock (_gate)
        {
            if (_tripped || !_stopping.IsCompleted) return _stopping;
            var batches = _active.ToArray();
            if (batches.Length == 0) return Task.CompletedTask;
            foreach (var b in batches) b.Cancel.Cancel();
            return _stopping = StopBatchesAsync(batches, emergency: false);
        }
    }
    public async Task StopAsync(string device, string axis, CancellationToken ct = default)
    {
        await CancelHeldMotionAsync();
        if (Resolve(device).State == DeviceState.Ready) await Resolve(device).StopAsync(axis, ct);
    }
    private async Task StopBatchesAsync(Batch[] batches, bool emergency)
    {
        await Task.Yield();
        var entered = false;
        try
        {
            if (!emergency) { await _dispatch.WaitAsync(); entered = true; }
            var members = batches.SelectMany(b => b.Members).ToArray();
            foreach (var group in members.GroupBy(m => m.Device))
            {
                var p = Resolve(group.Key);
                try
                {
                    if (emergency && p is IPositionerSafety safety) await safety.EmergencyStopAsync();
                    else foreach (var m in group.DistinctBy(m => m.Axis))
                        if (m.Sent) await p.StopAsync(m.Axis);
                }
                catch (Exception ex)
                {
                    _log($"STOP {group.Key}: {ex.Message}");
                    if (p is IPositionerSafety safety)
                        try { await safety.EmergencyStopAsync(); } catch (Exception stop) { _log($"Emergency stop failed: {stop.Message}"); }
                }
            }
        }
        finally
        {
            lock (_gate) foreach (var b in batches) _active.Remove(b);
            if (entered) _dispatch.Release();
        }
    }
}
