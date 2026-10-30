namespace LnScope.Core.Configuration;

public sealed class VideoModeOptions
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 960;

    public string Description { get; set; } = "";
}

public sealed class CameraOptions
{
    public string Id { get; set; } = "camera";

    /// <summary>simulated or windows-dshow; vendor SDKs can register additional drivers.</summary>
    public string Driver { get; set; } = "simulated";

    /// <summary>Camera serial number or index for installations with multiple cameras.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Hardware mode selected when connecting.</summary>
    public int ModeIndex { get; set; }

    /// <summary>Zero disables the frame rate limit.</summary>
    public double TargetFps { get; set; } = 15;

    /// <summary>Image mirroring applied to both preview and recording.</summary>
    public bool FlipHorizontal { get; set; }

    public bool FlipVertical { get; set; }

    /// <summary>Initial camera window scale.</summary>
    public double Zoom { get; set; } = 0.5;

    /// <summary>Simulated camera modes; physical drivers ignore this list.</summary>
    public List<VideoModeOptions> Modes { get; set; } = [];
}
