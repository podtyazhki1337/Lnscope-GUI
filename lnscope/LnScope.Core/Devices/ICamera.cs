namespace LnScope.Core.Devices;

public enum CameraPixelFormat
{
    Mono8,
    Mono16,
    Bgr24,
    Bgra32
}

/// <summary>Capture mode reported by the camera. The driver discovers sensor modes; configuration selects the startup mode.</summary>
public sealed record VideoMode(int Index, int Width, int Height, string Description)
{
    public override string ToString() => $"{Width}×{Height} — {Description}";
}

/// <summary>One frame of raw pixels, without intermediate format copies, shared by display and image analysis.</summary>
public sealed record CameraFrame(
    byte[] Data,
    int Width,
    int Height,
    int Stride,
    CameraPixelFormat Format,
    long Number,
    DateTimeOffset Timestamp);

public interface ICamera : IDevice
{
    /// <summary>Hardware modes discovered when the camera connects.</summary>
    IReadOnlyList<VideoMode> SupportedModes { get; }

    VideoMode? CurrentMode { get; }

    Task SetModeAsync(int modeIndex, CancellationToken ct = default);

    /// <summary>Requested frame rate limit; zero selects the maximum available rate.</summary>
    double TargetFps { get; set; }

    /// <summary>Actual frame rate measured from received frames.</summary>
    double MeasuredFps { get; }

    /// <summary>Properties exposed by the camera driver.</summary>
    IReadOnlyList<DeviceProperty> Properties { get; }

    bool IsStreaming { get; }

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    event EventHandler<CameraFrame>? FrameReady;
}
