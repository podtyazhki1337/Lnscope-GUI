namespace LnScope.Core.Devices;

public enum MoveSpeed
{
    Slow,
    Fast
}

public enum AxisLimit { None = 0, Negative = 1, Positive = 2, Unknown = 255 }

/// <summary>Axis description supplied by configuration.</summary>
/// <param name="Id">Logical GUI axis name: X, Y, Z or D.</param>
/// <param name="Unit">Axis units, usually um.</param>
/// <param name="MinPosition">Lower software bound; null disables it.</param>
/// <param name="MaxPosition">Upper software bound; null disables it.</param>
public sealed record AxisInfo(
    string Id,
    string Unit,
    double? MinPosition,
    double? MaxPosition,
    bool DisplayUserCoordinates = false);

/// <param name="Reference">Absolute machine coordinate from the reference counter.</param>
/// <param name="User">User coordinate from the operator-resettable counter.</param>
public sealed record AxisPosition(
    string AxisId,
    double Reference,
    double User,
    bool IsMoving,
    bool StopSwitchActive,
    DateTimeOffset Timestamp,
    AxisLimit Limit = AxisLimit.None);

/// <summary>Preflight validation and a stop path that does not wait behind ordinary movement work.</summary>
public interface IPositionerSafety
{
    void ValidateTarget(string axisId, double referenceTarget);
    Task EmergencyStopAsync();
}

/// <summary>Common contract for devices with motorized axes, including manipulators, stages, focus drives and piezo devices.</summary>
public interface IPositioner : IDevice
{
    IReadOnlyList<AxisInfo> Axes { get; }

    Task MoveAbsoluteAsync(string axisId, double position, MoveSpeed speed, CancellationToken ct = default);

    Task MoveRelativeAsync(string axisId, double delta, MoveSpeed speed, CancellationToken ct = default);

    Task StopAsync(string axisId, CancellationToken ct = default);

    Task<AxisPosition> ReadPositionAsync(string axisId, CancellationToken ct = default);

    /// <summary>Position updates produced by automatic controller feedback.</summary>
    event EventHandler<AxisPosition>? PositionChanged;
}
