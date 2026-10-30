namespace GUI.Motion;

public sealed class AxisMapping
{
    /// <summary>Axis on the leading device.</summary>
    public string From { get; set; } = "X";

    /// <summary>Axis on the following device.</summary>
    public string To { get; set; } = "X";

    /// <summary>Signed multiplier; use -1 when the axes point in opposite directions.</summary>
    public double Factor { get; set; } = 1;
}

/// <summary>Links two devices so that stage movement keeps the pipette aligned with the same point on the specimen.</summary>
public sealed class LinkOptions
{
    public string Id { get; set; } = "link";

    public string Name { get; set; } = "Link";

    public string Source { get; set; } = "stage";

    public string Target { get; set; } = "manipulator";

    /// <summary>Allow either device to lead the linked movement.</summary>
    public bool Bidirectional { get; set; } = true;

    public bool EnabledByDefault { get; set; }

    public List<AxisMapping> AxisMap { get; set; } = [];
}

public sealed class JogBinding
{
    /// <summary>WPF key name, such as W, A, NumPad4, LeftShift or Add.</summary>
    public string Key { get; set; } = "W";

    public string Device { get; set; } = "manipulator";

    public string Axis { get; set; } = "X";

    /// <summary>Direction sign: 1 or -1.</summary>
    public double Direction { get; set; } = 1;
}

public sealed class JogOptions
{
    /// <summary>GUI sensitivity preset; not a remote switch of the physical panel.</summary>
    public string Rate { get; set; } = "L";
    /// <summary>Distance per tick, editable in the interface.</summary>
    public double StepUm { get; set; } = 5;

    /// <summary>Command interval while a key is held. An excessively short interval can overload the controller queue; a long interval produces uneven motion.</summary>
    public int IntervalMs { get; set; } = 80;

    public List<JogBinding> Bindings { get; set; } = [];
}
