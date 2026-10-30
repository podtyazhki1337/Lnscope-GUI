namespace LnScope.Devices.LuigsNeumann;

public sealed class LnAxisOptions
{
    /// <summary>Logical axis name in the interface: X, Y, Z or D.</summary>
    public string Id { get; set; } = "X";

    /// <summary>Physical axis address in the controller (SM-10).</summary>
    public int HardwareAxis { get; set; } = 1;

    public string Unit { get; set; } = "um";

    /// <summary>Software travel bound; null disables this bound.</summary>
    public double? MinPosition { get; set; }

    public double? MaxPosition { get; set; }
}

public sealed class LnManipulatorOptions
{
    public string Id { get; set; } = "manipulator";

    /// <summary>ln-nextlevel-ascii (legacy) or sm10-binary (SM-10 documentation).</summary>
    public string Driver { get; set; } = "ln-nextlevel-ascii";

    /// <summary>Real SM-10 motion requires an explicitly verified axis map and limits.</summary>
    public bool MotionEnabled { get; set; }
    /// <summary>Compatibility for controllers observed to return BBBB in place of response CRC.</summary>
    public bool AllowLegacyReplyChecksum { get; set; }
    /// <summary>Show counter 2 in the GUI; motion API and limits remain in counter 1 coordinates.</summary>
    public bool DisplayUserCoordinates { get; set; }

    /// <summary>serial selects a physical controller; simulated runs without hardware.</summary>
    public string Transport { get; set; } = "simulated";

    public string PortName { get; set; } = "COM3";

    public int BaudRate { get; set; } = 115200;

    /// <summary>Automatic InterfaceManager feedback interval, 100-1000 ms.</summary>
    public int FeedbackIntervalMs { get; set; } = 200;

    /// <summary>Send the binary-to-ASCII switching sequence when connecting. The controller remembers its last protocol, which may have been changed by other software.</summary>
    public bool SwitchToAsciiOnConnect { get; set; } = true;

    public int ResponseTimeoutMs { get; set; } = 2000;

    public List<LnAxisOptions> Axes { get; set; } = [];
}
