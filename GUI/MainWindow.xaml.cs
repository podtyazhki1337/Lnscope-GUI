using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GUI.ViewModels;
using Microsoft.Win32;

namespace GUI;

public partial class MainWindow : Window
{
    private const string ConfigFileName = "devices.json";

    private MainViewModel _model;
    private readonly Dictionary<string, CameraWindow> _cameraWindows = new();
    private bool _closing;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();

        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        _model = new MainViewModel(LoadConfig(configPath), configPath, Dispatcher);
        DataContext = _model;
        Closing += OnClosing;
    }

    private async void OnHardwareSetup(object sender, RoutedEventArgs e)
    {
        if (_model.Positioners.Any(p => p.State != LnScope.Core.Devices.DeviceState.Disconnected))
        {
            MessageBox.Show(this, "Disconnect all positioners before changing controller settings.", "Hardware setup");
            return;
        }
        _model.Jog.ReleaseAll();
        var dialog = new HardwareSetupWindow(_model.Configuration) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        IsEnabled = false;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            var json = JsonSerializer.Serialize(dialog.Configuration, new JsonSerializerOptions { WriteIndented = true });
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            foreach (var camera in _cameraWindows.Values.ToArray()) camera.Close();
            await _model.ShutdownAsync();
            _model = new MainViewModel(dialog.Configuration, path, Dispatcher);
            DataContext = _model;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not apply hardware setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void OnOfflineTracking(object sender, RoutedEventArgs e)
    {
        var enabled = _model.Jog.IsEnabled;
        _model.Jog.IsEnabled = false;
        try { new TrackingWindow { Owner = this }.ShowDialog(); }
        finally { _model.Jog.IsEnabled = enabled; }
    }

    private void OnObjectMap(object sender, RoutedEventArgs e)
    {
        var enabled = _model.Jog.IsEnabled;
        _model.Jog.ReleaseAll();
        _model.Jog.IsEnabled = false;
        try { new ObjectMapWindow(_model) { Owner = this }.ShowDialog(); }
        finally { _model.Jog.IsEnabled = enabled; }
    }

    private void OnSm10Commands(object sender, RoutedEventArgs e)
    {
        if (_model.Positioners.Any(p => p.State != LnScope.Core.Devices.DeviceState.Disconnected))
        {
            MessageBox.Show(this,"Disconnect all positioners before opening the SM-10 console.","SM-10 commands");
            return;
        }
        _model.Jog.ReleaseAll();
        new Sm10CommandsWindow(_model.Configuration) { Owner = this }.ShowDialog();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        foreach (var window in _cameraWindows.Values.ToArray()) window.Close();
        await _model.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }

    private static AppConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"Configuration file was not found:\n{path}", "LNscope",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
        }
        catch (JsonException exception)
        {
            MessageBox.Show($"Invalid {ConfigFileName}:\n{exception.Message}", "LNscope",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return new AppConfig();
        }
    }

    private void OnOpenCameraWindow(object sender, RoutedEventArgs e)
    {
        foreach (var camera in _model.Cameras)
        {
            if (_cameraWindows.TryGetValue(camera.Id, out var existing))
            {
                existing.Activate();
                continue;
            }

            var window = new CameraWindow(camera, _model.Jog, this);
            window.Closed += (_, _) => _cameraWindows.Remove(camera.Id);
            _cameraWindows[camera.Id] = window;
            window.Show();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        if (_model.Jog.HandleKeyDown(e))
        {
            e.Handled = true;
        }
    }

    private void OnFrameRateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_model.Jog.HandleKeyUp(e))
        {
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => _model.Jog.ReleaseAll();

    private void OnApplyBindings(object sender, RoutedEventArgs e) => _model.ApplyBindings();

    private void OnRemoveBinding(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JogBindingViewModel binding })
        {
            _model.RemoveBinding(binding);
        }
    }

    private void OnBrowseOutputFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CameraViewModel camera })
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select recording output folder",
            InitialDirectory = Directory.Exists(camera.OutputDirectory)
                ? camera.OutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog(this) == true)
        {
            camera.OutputDirectory = dialog.FolderName;
        }
    }
}
