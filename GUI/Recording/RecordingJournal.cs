using LnScope.Devices.LuigsNeumann.Sm10;

namespace GUI.Recording;

public sealed record RecordingEvent(DateTimeOffset Utc, string Kind, AxisTelemetry? Axis = null,
    Sm10CommandTrace? Command = null, string Detail = "");

/// <summary>Fan-out only; recording subscribers queue events without blocking device I/O.</summary>
public sealed class RecordingJournal
{
    public event Action<RecordingEvent>? Emitted;
    public void Position(AxisTelemetry axis) => Emitted?.Invoke(new(DateTimeOffset.UtcNow, "position", axis));
    public void Command(Sm10CommandTrace command) => Emitted?.Invoke(new(command.Utc, "command", Command: command));
}
