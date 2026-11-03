using LnScope.Core.Configuration;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;
using LnScope.Devices.LuigsNeumann.Sm10;
using LnScope.Devices.Simulation;

namespace GUI.Devices;

/// <summary>Driver registration is separate from the UI and from USB/serial transport discovery.</summary>
public sealed class DeviceFactory(string motionBackend = "csharp", Action<Sm10CommandTrace>? commandTrace = null)
{
    private readonly Dictionary<string, Func<CameraOptions, ICamera>> _cameras = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simulated"] = options => new SimulatedCamera(options),
        ["windows-dshow"] = options => new DirectShowCamera(options),
        ["kern-native"] = options => new KernCamera(options)
    };
    private readonly Dictionary<string, ISm10Bus> _controllers = new(StringComparer.OrdinalIgnoreCase);
    public void RegisterCameraDriver(string key, Func<CameraOptions, ICamera> create) => _cameras[key] = create;
    public ICamera CreateCamera(CameraOptions options) => _cameras.TryGetValue(options.Driver, out var create)
        ? create(options) : throw new ArgumentException($"Unknown camera driver '{options.Driver}'.");

    public IPositioner CreatePositioner(LnManipulatorOptions options)
    {
        if (options.Transport.Equals("simulated", StringComparison.OrdinalIgnoreCase))
            return motionBackend == "python" ? new PythonPositioner(options, null) : new LnManipulator(options);
        if (options.Driver.Equals("ln-nextlevel-ascii", StringComparison.OrdinalIgnoreCase))
            return new LnManipulator(options);
        if (!options.Driver.Equals("sm10-binary", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown positioner driver '{options.Driver}'.");
        if (!_controllers.TryGetValue(options.PortName, out var bus))
            _controllers.Add(options.PortName, bus = new Sm10SerialBus(options.PortName, options.BaudRate, options.ResponseTimeoutMs, allowLegacyChecksum: options.AllowLegacyReplyChecksum, trace: commandTrace));
        return motionBackend == "python" ? new PythonPositioner(options, bus) : new Sm10Positioner(options, bus);
    }

    public static void Validate(AppConfig config)
    {
        if (config.MotionBackend is not ("python" or "csharp")) throw new ArgumentException("Unsupported motion backend.");
        if (config.Cameras is null || config.Positioners is null || config.Links is null || config.Jog is null || config.Recording is null)
            throw new ArgumentException("Configuration sections must not be null.");
        var ids = config.Positioners.Select(p => p.Id).Concat(config.Cameras.Select(c => c.Id)).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new ArgumentException("Device IDs must be non-empty and unique.");
        foreach (var camera in config.Cameras)
        {
            if (camera.Driver is not ("simulated" or "windows-dshow" or "kern-native")) throw new ArgumentException("Unsupported camera driver.");
            if (camera.Driver != "simulated" && string.IsNullOrWhiteSpace(camera.DeviceId))
                throw new ArgumentException("Select a discovered Windows camera before applying hardware settings.");
            if (camera.Driver == "kern-native" && !camera.DeviceId!.StartsWith("kern:"))
                throw new ArgumentException("Select a KERN camera for the KERN native driver.");
        }
        foreach (var p in config.Positioners)
        {
            if (p.Transport is not ("serial" or "simulated")) throw new ArgumentException("Unsupported positioner transport.");
            if (p.Driver is not ("sm10-binary" or "ln-nextlevel-ascii")) throw new ArgumentException("Unsupported positioner driver.");
            if (p.Axes is null || p.Axes.Count == 0 || p.Axes.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Axes.Count)
                throw new ArgumentException($"Invalid axes for {p.Id}.");
            foreach (var a in p.Axes)
            {
                Sm10Protocol.Axis(a.HardwareAxis);
                if (string.IsNullOrWhiteSpace(a.Id) || a.Unit != "um")
                    throw new ArgumentException("L&N axes use micrometres (um) and require a logical axis ID.");
                if (a.MinPosition is { } min && !double.IsFinite(min) || a.MaxPosition is { } max && !double.IsFinite(max) ||
                    a.MinPosition >= a.MaxPosition)
                    throw new ArgumentException($"Invalid travel limits for {p.Id}.{a.Id}.");
                if (p.Transport == "serial" && p.MotionEnabled && (a.MinPosition is null || a.MaxPosition is null))
                    throw new ArgumentException("Set both travel limits before enabling real motion.");
            }
            if (p.Transport == "serial" && (string.IsNullOrWhiteSpace(p.PortName) || p.BaudRate != 115200))
                throw new ArgumentException("SM-10 requires a selected COM port and 115200 baud.");
        }
        foreach (var group in config.Positioners.Where(p => p.Transport == "serial").GroupBy(p => p.PortName, StringComparer.OrdinalIgnoreCase))
        {
            var axes = group.SelectMany(p => p.Axes).Select(a => a.HardwareAxis).ToArray();
            if (axes.Distinct().Count() != axes.Length)
                throw new ArgumentException($"Duplicate physical axis addresses on {group.Key}. Each axis can belong to one logical group.");
            if (group.Count() > 1 && group.Any(p => p.Driver != "sm10-binary"))
                throw new ArgumentException("Sharing a COM port is supported by the SM-10 binary driver only.");
            if (group.Select(p => (p.BaudRate, p.ResponseTimeoutMs, p.AllowLegacyReplyChecksum)).Distinct().Count() != 1)
                throw new ArgumentException("Groups sharing a controller must use identical serial settings.");
        }
    }
}
