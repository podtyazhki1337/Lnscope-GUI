using GUI.Motion;
using GUI.Recording;
using LnScope.Core.Configuration;
using LnScope.Devices.LuigsNeumann;

namespace GUI;

/// <summary>Installation configuration, grouped by device type. Additional drivers can be registered by their type key.</summary>
public sealed class AppConfig
{
    /// <summary>Python operations with a C# safety guardian; csharp retains the original driver.</summary>
    public string MotionBackend { get; set; } = "python";
    public List<LnManipulatorOptions> Positioners { get; set; } = [];

    public List<CameraOptions> Cameras { get; set; } = [];

    /// <summary>Device links that mirror the movement of one device on another.</summary>
    public List<LinkOptions> Links { get; set; } = [];

    /// <summary>Keyboard bindings for axis control.</summary>
    public JogOptions Jog { get; set; } = new();

    public RecordingOptions Recording { get; set; } = new();
}
