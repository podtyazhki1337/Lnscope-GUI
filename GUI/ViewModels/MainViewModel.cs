using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;
using GUI.Motion;
using GUI.Mvvm;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;
using LnScope.Devices.Simulation;
using GUI.Devices;

namespace GUI.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxLogEntries = 500;

    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly Dispatcher _dispatcher;
    private readonly GUI.Recording.PositionTelemetry _telemetry;
    private readonly GUI.Recording.RecordingJournal _recordingJournal;

    private double _jogStep;

    public MainViewModel(AppConfig config, string configPath, Dispatcher dispatcher)
    {
        _config = config;
        _configPath = configPath;
        _dispatcher = dispatcher;

        var factory = new DeviceFactory(config.MotionBackend);
        var positioners = config.Positioners
            .Select(factory.CreatePositioner)
            .ToList();

        Links = new ObservableCollection<MotionLink>(config.Links.Select(link => new MotionLink(link)));

        Motion = new MotionService(positioners, Links, Log, config.MotionBackend == "python");

        Positioners = new ObservableCollection<PositionerViewModel>(
            positioners.Select(positioner => new PositionerViewModel(positioner, Motion, dispatcher, Log)));

        _telemetry = new GUI.Recording.PositionTelemetry(positioners);
        _recordingJournal = new(config.Positioners.ToDictionary(p => p.Id,
            p => p.Transport == "simulated" ? "simulated" : p.PortName));
        _telemetry.Changed += _recordingJournal.Position;
        Cameras = new ObservableCollection<CameraViewModel>(
            config.Cameras.Select(options => new CameraViewModel(
                factory.CreateCamera(options), options, config.Recording, dispatcher, Log, _telemetry.Snapshot, _recordingJournal)));

        DeviceIds = positioners.Select(p => p.Id).ToArray();

        Bindings = new ObservableCollection<JogBindingViewModel>(
            config.Jog.Bindings.Select(binding => new JogBindingViewModel(binding)));

        _jogStep = config.Jog.StepUm;
        Jog = new JogController(Motion, config.Jog, dispatcher, Log) { Step = _jogStep, Rate = config.Jog.Rate };
        Motion.ManualSpeed = Jog.Speed;
        Motion.ProfilesChanged += () => dispatcher.InvokeAsync(() =>
        {
            Motion.ManualSpeed = Jog.Speed;
            OnPropertyChanged(nameof(JogRateDescription));
        });

        StopAllCommand = new AsyncRelayCommand(async () =>
        {
            Jog.ReleaseAll();
            await Motion.StopAllAsync();
            Log("Stop requested for all connected motion axes.");
        }) { ErrorHandler = error => Log($"Stop failed: {error.Message}") };
        AddBindingCommand = new AsyncRelayCommand(AddBindingAsync);
        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);

        Log($"configuration loaded: {Positioners.Count} positioners, {Cameras.Count} cameras, {Links.Count} links; motion backend: {config.MotionBackend}");
    }

    /// <summary>Current configuration used to create registered device drivers.</summary>
    public AppConfig Configuration => _config;

    public MotionService Motion { get; }

    public string EnvironmentLabel => _config.Positioners.All(p => p.Transport.Equals("simulated", StringComparison.OrdinalIgnoreCase))
        && _config.Cameras.All(c => c.Driver.Equals("simulated", StringComparison.OrdinalIgnoreCase))
        ? "SIMULATION" : "HARDWARE CONFIGURED";

    public string LatestMessage => Journal.FirstOrDefault() ?? "Ready";

    public async Task ShutdownAsync()
    {
        Jog.IsEnabled = false;
        Jog.ReleaseAll();
        await Motion.CancelHeldMotionAsync();
        foreach (var camera in Cameras)
        {
            try { await camera.ShutdownAsync(); }
            catch (Exception exception) { Log($"{camera.Id}: shutdown error — {exception.Message}"); }
        }
        foreach (var positioner in Positioners)
        {
            try { await positioner.ShutdownAsync(); }
            catch (Exception exception) { Log($"{positioner.Id}: shutdown error — {exception.Message}"); }
        }
        await Motion.DisposeAsync();
        _telemetry.Dispose();
    }

    public JogController Jog { get; }
    public IReadOnlyList<string> JogRates { get; } = ["L", "M", "H"];
    public string JogRate
    {
        get => Jog.Rate;
        set
        {
            if (!JogRates.Contains(value) || value == Jog.Rate) return;
            Jog.ReleaseAll();
            Jog.Rate = value; _config.Jog.Rate = value; Motion.ManualSpeed = Jog.Speed;
            OnPropertyChanged(); OnPropertyChanged(nameof(JogRateDescription));
        }
    }
    public string JogRateDescription => $"Hold: {Jog.StepMultiplier:0}× keyboard step · {Jog.Speed}. Buttons use one step. Panel L/M/H remain unchanged.";

    public ObservableCollection<PositionerViewModel> Positioners { get; }

    public ObservableCollection<CameraViewModel> Cameras { get; }

    public ObservableCollection<MotionLink> Links { get; }

    public ObservableCollection<JogBindingViewModel> Bindings { get; }

    public IReadOnlyList<string> DeviceIds { get; }

    public IReadOnlyList<double> Directions { get; } = [1, -1];

    public AsyncRelayCommand StopAllCommand { get; }

    public AsyncRelayCommand AddBindingCommand { get; }

    public AsyncRelayCommand SaveConfigCommand { get; }

    public double JogStep
    {
        get => _jogStep;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a positive, finite step.");
            if (!SetField(ref _jogStep, value))
            {
                return;
            }

            Jog.Step = value;
            _config.Jog.StepUm = value;
            Log($"keyboard step: {value:0.###} um");
        }
    }

    /// <summary>In-memory action journal, intended as the basis for an experiment log.</summary>
    public ObservableCollection<string> Journal { get; } = [];

    public void RemoveBinding(JogBindingViewModel binding)
    {
        Bindings.Remove(binding);
        ApplyBindings();
    }

    private Task AddBindingAsync()
    {
        var binding = new JogBinding
        {
            Key = "None",
            Device = DeviceIds.FirstOrDefault() ?? "manipulator",
            Axis = "X",
            Direction = 1
        };

        Bindings.Add(new JogBindingViewModel(binding));
        return Task.CompletedTask;
    }

    /// <summary>Rebuild keyboard bindings from the current editor contents.</summary>
    public void ApplyBindings()
    {
        _config.Jog.Bindings = Bindings.Select(b => b.Binding).ToList();
        Jog.Rebuild(_config.Jog.Bindings);
    }

    private Task SaveConfigAsync()
    {
        ApplyBindings();

        try
        {
            foreach (var link in Links) link.Options.EnabledByDefault = link.IsEnabled;
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            File.WriteAllText(_configPath, json);
            Log($"configuration saved: {_configPath}");
        }
        catch (Exception exception)
        {
            Log($"configuration not saved — {exception.Message}");
        }

        return Task.CompletedTask;
    }

    private void Log(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => Log(message));
            return;
        }
        Journal.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        OnPropertyChanged(nameof(LatestMessage));

        while (Journal.Count > MaxLogEntries)
        {
            Journal.RemoveAt(Journal.Count - 1);
        }
    }
}
