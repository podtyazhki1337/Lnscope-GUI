using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GUI.Devices;
using GUI.Mvvm;

namespace GUI;

public partial class HardwareSetupWindow : Window
{
    private readonly SetupModel _model;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    public AppConfig Configuration => _model.Config;

    public HardwareSetupWindow(AppConfig config)
    {
        InitializeComponent();
        _model = new SetupModel(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!);
        DataContext = _model;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _model.DiscoveryStatus = "Looking for Windows video devices and serial ports…";
        // Preserve selected stable IDs even when a camera has temporarily disappeared.
        var selected = _model.Config.Cameras.Select(c => c.DeviceId).ToArray();
        try
        {
            _model.Ports.Clear();
            foreach (var port in SerialPort.GetPortNames().Order()) _model.Ports.Add(port);
            var cameras = (await DirectShowDiscovery.EnumerateAsync(_lifetime.Token)).ToList();
            string? kernError = null;
            try { cameras.AddRange(await Task.Run(KernCamera.Enumerate, _lifetime.Token)); }
            catch (Exception ex) { kernError = ex.Message; }
            _model.Cameras.Clear();
            foreach (var camera in cameras) _model.Cameras.Add(camera);
            foreach (var id in selected.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
                if (!_model.Cameras.Any(c => c.DeviceId == id))
                    _model.Cameras.Add(new CameraDevice("Unavailable — reconnect this camera", id!));
            for (var i = 0; i < selected.Length; i++) _model.Config.Cameras[i].DeviceId = selected[i];
            _model.RefreshConfig();
            _model.DiscoveryStatus = $"Found {cameras.Count} video device(s), {_model.Ports.Count} COM port(s). " +
                "Refresh after plugging in equipment. Discovery sends no controller commands." +
                (kernError is null ? "" : " KERN: " + kernError);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _model.DiscoveryStatus = exception.Message; }
        finally { _refreshing = false; }
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        var kern = _model.Cameras.Where(c => c.Name.Contains("KERN", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("ODC", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var camera in _model.Config.Cameras)
        {
            camera.Driver = "kern-native";
            camera.DeviceId = kern.Length == 1 ? kern[0].DeviceId : null;
            camera.ModeIndex = 0;
        }
        var nextAxis = 1;
        _model.SharedController = true;
        _model.ControllerPort = "";
        foreach (var p in _model.Config.Positioners)
        {
            p.Driver = "sm10-binary"; p.Transport = "serial"; p.BaudRate = 115200;
            p.PortName = ""; // Never guess which COM device is the motion controller.
            p.MotionEnabled = false;
            foreach (var a in p.Axes)
            {
                a.HardwareAxis = nextAxis++;
                a.MinPosition = null; a.MaxPosition = null;
            }
        }
        _model.RefreshConfig();
        _model.DiscoveryStatus = "SM-10 preset: select ONE controller COM port for the stage and manipulator. Axis numbers are suggestions, not identified wiring. Set actual limits before enabling movement.";
    }
    private void OnSimulation(object sender, RoutedEventArgs e)
    {
        foreach (var c in _model.Config.Cameras) { c.Driver = "simulated"; c.DeviceId = null; }
        foreach (var p in _model.Config.Positioners) { p.Transport = "simulated"; p.MotionEnabled = false; }
        _model.RefreshConfig();
    }
    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var grid in Descendants(this).OfType<DataGrid>())
            {
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))
                    throw new ArgumentException("Correct invalid numeric values before applying.");
            }
            if (Descendants(this).Any(Validation.GetHasError)) throw new ArgumentException("Correct the highlighted values.");
            if (_model.SharedController)
                foreach (var p in _model.Config.Positioners) p.PortName = _model.ControllerPort.Trim();
            foreach (var camera in _model.Config.Cameras.Where(c => c.Driver != "simulated" && c.DeviceId is not null))
                camera.Driver = camera.DeviceId!.StartsWith("kern:") ? "kern-native" : "windows-dshow";
            DeviceFactory.Validate(_model.Config);
            DialogResult = true;
        }
        catch (Exception exception) { _model.Error = exception.Message; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class SetupModel(AppConfig config) : ViewModelBase
    {
        public AppConfig Config { get; private set; } = config;
        public ObservableCollection<CameraDevice> Cameras { get; } = [];
        public ObservableCollection<string> Ports { get; } = [];
        private bool _sharedController = config.Positioners.All(p => p.Driver == "sm10-binary") &&
            config.Positioners.Select(p => p.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
        private string _controllerPort = config.Positioners.FirstOrDefault()?.PortName ?? "";
        public bool SharedController
        {
            get => _sharedController;
            set { if (SetField(ref _sharedController, value)) OnPropertyChanged(nameof(SeparatePorts)); }
        }
        public bool SeparatePorts => !SharedController;
        public string ControllerPort { get => _controllerPort; set => SetField(ref _controllerPort, value); }
        private string _status = "", _error = "";
        public string DiscoveryStatus { get => _status; set => SetField(ref _status, value); }
        public string Error { get => _error; set => SetField(ref _error, value); }
        public void RefreshConfig()
        {
            Config = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config))!;
            OnPropertyChanged(nameof(Config));
        }
    }
}
