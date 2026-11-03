using System.Diagnostics;
using LnScope.Core.Devices;

namespace GUI.Recording;

public sealed record AxisTelemetry(string Device, string Axis, string Unit, string State,
    double? C1, double? C2, bool? Moving, string Limit, DateTimeOffset? SampleUtc, double? AgeMs);

/// <summary>Snapshots existing feedback without adding serial traffic or depending on the UI thread.</summary>
public sealed class PositionTelemetry : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<IPositioner> _devices;
    private readonly Dictionary<(string, string), (AxisPosition Position, long Received)> _positions = new();
    public event Action<AxisTelemetry>? Changed;
    public PositionTelemetry(IReadOnlyList<IPositioner> devices)
    {
        _devices = devices;
        foreach (var device in devices)
        {
            device.PositionChanged += OnPosition;
            device.StateChanged += OnState;
        }
    }
    private void OnPosition(object? sender, AxisPosition position)
    {
        if (sender is not IPositioner device) return;
        lock (_gate) _positions[(device.Id, position.AxisId)] = (position, Stopwatch.GetTimestamp());
        var axis = device.Axes.FirstOrDefault(a => a.Id == position.AxisId);
        if (axis is not null) Changed?.Invoke(new(device.Id, axis.Id, axis.Unit, device.State.ToString(),
            position.Reference, position.User, position.IsMoving, position.Limit.ToString(), position.Timestamp, 0));
    }
    private void OnState(object? sender, DeviceState state)
    {
        if (sender is not IPositioner device || state == DeviceState.Ready) return;
        lock (_gate)
            foreach (var key in _positions.Keys.Where(k => k.Item1 == device.Id).ToArray()) _positions.Remove(key);
        foreach (var axis in device.Axes)
            Changed?.Invoke(new(device.Id, axis.Id, axis.Unit, state.ToString(), null, null, null, "unknown", null, null));
    }
    public IReadOnlyList<AxisTelemetry> Snapshot()
    {
        lock (_gate)
            return _devices.SelectMany(device => device.Axes.Select(axis =>
            {
                var valid = device.State == DeviceState.Ready && _positions.TryGetValue((device.Id, axis.Id), out _);
                var sample = valid ? _positions[(device.Id, axis.Id)] : default;
                return new AxisTelemetry(device.Id, axis.Id, axis.Unit, device.State.ToString(),
                    valid ? sample.Position.Reference : null, valid ? sample.Position.User : null,
                    valid ? sample.Position.IsMoving : null, valid ? sample.Position.Limit.ToString() : "unknown",
                    valid ? sample.Position.Timestamp : null,
                    valid ? Stopwatch.GetElapsedTime(sample.Received).TotalMilliseconds : null);
            })).ToArray();
    }
    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.PositionChanged -= OnPosition;
            device.StateChanged -= OnState;
        }
    }
}
