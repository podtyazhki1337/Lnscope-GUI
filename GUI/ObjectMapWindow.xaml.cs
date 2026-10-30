using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GUI.Devices;
using GUI.ViewModels;
using Microsoft.Win32;

namespace GUI;

/// <summary>Observation bookmarks. This adapter does not issue motion commands.</summary>
public partial class ObjectMapWindow : Window
{
    private readonly MainViewModel _model;
    private readonly PythonWorker _worker = new(null, maxResponseLength: 4 * 1024 * 1024);
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LNscope", "object-map.json");
    private bool _busy, _closed;
    public record MapRow(string Id, string Name, string Kind, string Status, string Coordinates, string Note);

    public ObjectMapWindow(MainViewModel model)
    {
        InitializeComponent();
        _model = model;
        Loaded += async (_, _) => await Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Show(await _worker.CallAsync(File.Exists(_path) ? "map_load" : "map_list", new { path = _path }));
        });
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_busy) return;
            _closed = true;
            await _worker.DisposeAsync();
            Close();
        };
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        try { await action(); }
        catch (Exception error) { Status.Text = error.Message; }
        finally { _busy = false; }
    }

    private void Show(JsonElement map)
    {
        Objects.ItemsSource = map.GetProperty("objects").EnumerateArray().Select(o => new MapRow(
            o.GetProperty("id").GetString()!, o.GetProperty("name").GetString()!, o.GetProperty("kind").GetString()!,
            o.GetProperty("status").GetString()!,
            string.Join("; ", o.GetProperty("snapshot").EnumerateArray().Select(d =>
                d.GetProperty("device_id").GetString() + ": " + string.Join(", ", d.GetProperty("axes").EnumerateArray().Select(a =>
                    $"{a.GetProperty("id").GetString()} {a.GetProperty("reference").GetDouble():0.###} {a.GetProperty("unit").GetString()}")))),
            o.TryGetProperty("note", out var note) ? note.GetString() ?? "" : "")).ToArray();
    }

    private async Task Save()
    {
        Show(await _worker.CallAsync("map_save", new { path = _path }));
        Status.Text = $"Saved to {_path}. Last observed view only; object XYZ and automatic return are not calibrated.";
    }

    private async void OnCapture(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var devices = _model.Positioners.Where(p => p.IsConnected).ToArray();
        if (devices.Length == 0) throw new InvalidOperationException("Connect a positioner first.");
        if (devices.SelectMany(p => p.Axes).Any(a => a.IsMoving)) throw new InvalidOperationException("Stop movement before saving.");
        var snapshot = new List<object>();
        foreach (var device in devices)
        {
            var axes = new List<object>();
            foreach (var axis in device.Axes)
            {
                await axis.RefreshAsync();
                if (axis.IsMoving) throw new InvalidOperationException("Movement detected. Stop and capture again.");
                axes.Add(new { id = axis.Id, unit = axis.Unit, reference = axis.ReferencePosition,
                    display = axis.Position, read_time_ms = axis.PositionReadTimeMs, moving = axis.IsMoving });
            }
            if (!device.IsConnected) throw new InvalidOperationException("Device disconnected during capture.");
            snapshot.Add(new { device_id = device.Id, axes });
        }
        Show(await _worker.CallAsync("map_add", new { name = ObjectName.Text,
            kind = ((ComboBoxItem)ObjectKind.SelectedItem).Content.ToString(), note = Note.Text, snapshot }));
        await Save();
    });

    private async void OnInvalidate(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await _worker.CallAsync("map_invalidate", new { });
        await Save();
    });
    private async void OnRemove(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Objects.SelectedItem is not MapRow row) return;
        await _worker.CallAsync("map_remove", new { id = row.Id });
        await Save();
    });
    private async void OnExport(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new SaveFileDialog { Filter = "Object map|*.json", FileName = "object-map.json" };
        if (dialog.ShowDialog(this) == true) { await _worker.CallAsync("map_save", new { path = dialog.FileName }); Status.Text = "Map exported."; }
    });
    private async void OnImport(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "Object map|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            if (MessageBox.Show(this, "Replace the current map with this file?", "Import map", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Show(await _worker.CallAsync("map_load", new { path = dialog.FileName }));
            await Save();
        }
    });
}
