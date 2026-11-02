using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using LnScope.Devices.LuigsNeumann.Sm10;
using Microsoft.Win32;
using GUI.Devices;

namespace GUI;

public partial class Sm10CommandsWindow : Window
{
    private Sm10SerialBus? _bus;
    private readonly PythonCommandBackend _catalogue = new();
    private PythonCommandBackend? _backend;
    private PythonConsoleCommand[] _commands = [];
    private sealed record AxisGroup(string Name, int[] Axes)
    {
        public override string ToString() => Name;
    }
    private readonly HashSet<int> _targeted = [];
    private bool _busy, _stopping, _closing, _closed, _stopAttempted;
    private Task? _stopTask;
    private Task? _disconnectTask;
    private readonly HashSet<string> _legacyPorts;
    public Sm10CommandsWindow(AppConfig config)
    {
        InitializeComponent();
        _legacyPorts = config.Positioners.Where(p => p.AllowLegacyReplyChecksum && p.Transport == "serial")
            .Select(p => p.PortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Port.ItemsSource = SerialPort.GetPortNames().Order().ToArray();
        Port.Text = config.Positioners.FirstOrDefault(p => p.Transport == "serial")?.PortName ?? "";
        DeviceAxes.ItemsSource = config.Positioners.Select(p => new AxisGroup(p.Id, p.Axes.Select(a => a.HardwareAxis).ToArray())).ToArray();
        Loaded += async (_, _) =>
        {
            try
            {
                _commands = await _catalogue.CatalogueAsync();
                if (_closing) return;
                Category.ItemsSource = new[] { "All commands" }.Concat(_commands.Select(c => c.Category).Distinct().Order()).ToArray();
                Category.SelectedIndex = 0;
                Command.SelectedItem = _commands.First(c => c.Id == 0x0101);
                Append($"Python catalogue loaded: {_commands.Length} documented commands; {_commands.Count(c => c.Supported)} available.");
            }
            catch (Exception ex) { Append("Python catalogue failed: " + ex.Message); }
        };
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            await DisconnectAsync();
            await _catalogue.DisposeAsync();
            _closed = true;
            // Cleanup can complete synchronously; let the current Closing event return first.
            await Dispatcher.InvokeAsync(Close);
        };
        Append("Python command backend. Read-only by default; no port opened, no probes or polling.");
    }
    private void Append(string text) { Log.AppendText($"{DateTime.Now:HH:mm:ss.fff} {text}\n"); Log.ScrollToEnd(); }
    private void OnCommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Help is null || Command.SelectedItem is not PythonConsoleCommand spec) return;
        Values.Text = "";
        Send.IsEnabled = spec.Supported && !_busy;
        Help.Text = spec.Help + $" Reply: {spec.Reply}." + (spec.Continuous ? " Only Stop is allowed until standstill." : "");
    }
    private static string[] Parts(string text) => text.Split([' ', ';', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
    private int[] AxisList() => Parts(Axes.Text).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => FilterCommands();
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => FilterCommands();
    private void FilterCommands()
    {
        if (Command is null || Search is null) return;
        var previous = Command.SelectedItem as PythonConsoleCommand;
        var category = Category.SelectedItem as string;
        var query = Search.Text.Trim();
        var visible = _commands.Where(c => (category is null or "All commands" || c.Category == category) &&
            ($"{c.Id:X4} {c.Name}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        Command.ItemsSource = visible;
        Command.SelectedItem = visible.FirstOrDefault(c => c.Id == previous?.Id) ?? visible.FirstOrDefault();
        if (visible.Length == 0) { Help.Text = "No matching commands."; Send.IsEnabled = false; }
    }
    private void OnUseDeviceAxes(object sender, RoutedEventArgs e)
    {
        if (DeviceAxes.SelectedItem is not AxisGroup group || Command.SelectedItem is not PythonConsoleCommand spec) return;
        Axes.Text = string.Join(" ", spec.MultipleAxes ? group.Axes : group.Axes.Take(1));
    }
    private (PythonConsoleCommand Spec, int[] Axes, double[] Values) Input()
    {
        var spec = Command.SelectedItem as PythonConsoleCommand ?? throw new InvalidOperationException("Select a command.");
        return (spec, AxisList(), Parts(Values.Text).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray());
    }
    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        _busy = true;
        try
        {
            var q = Input();
            var packet = await _catalogue.BuildAsync(q.Spec.Id, q.Axes, q.Values);
            Append($"PREVIEW {q.Spec}: {packet.Frame}");
        }
        catch (Exception ex) { Append("Input error: " + ex.Message); }
        finally { _busy = false; }
    }
    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_bus is not null || _busy || _closing) return;
        if (string.IsNullOrWhiteSpace(Port.Text)) { Append("Select the controller COM port."); return; }
        _busy = true; Connect.IsEnabled = false;
        var bus = new Sm10SerialBus(Port.Text.Trim(),115200,1000,allowLegacyChecksum:_legacyPorts.Contains(Port.Text.Trim()));
        try
        {
            await bus.ConnectAsync();
            if (_closing) { await bus.DisconnectAsync(); return; }
            var backend = new PythonCommandBackend(bus);
            backend.Failed += () =>
            {
                bus.AbortPending();
                Dispatcher.InvokeAsync(async () =>
                {
                    if (!ReferenceEquals(_backend, backend) || _closing) return;
                    Append("Python worker failed; connection latched. Reconnect required.");
                    if (_targeted.Count > 0) await StopAsync(_targeted.ToArray());
                });
            };
            _backend = backend;
            _bus = bus; _targeted.Clear(); _stopAttempted = false; _stopTask = null; _disconnectTask = null;
            Port.IsEnabled = false; Append("Port open, 115200 8N1. No commands sent.");
        }
        catch (Exception ex) { await bus.DisconnectAsync(); Append("Open failed: " + ex.Message); }
        finally { _busy = false; Connect.IsEnabled = _bus is null; }
    }
    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_busy || _stopping || _closing) return;
        (PythonConsoleCommand Spec, int[] Axes, double[] Values) q;
        bool allowWrites = AllowWrites.IsChecked == true;
        try
        {
            q = Input();
            if (!q.Spec.ReadOnly && !q.Spec.IsStop && !allowWrites)
                throw new InvalidOperationException("Enable advanced commands before sending movement or settings changes.");
        }
        catch (Exception ex) { Append("Input error: " + ex.Message); return; }
        var bus = _bus;
        if (bus is null) { Append("Open the controller port first."); return; }
        _busy = true; Send.IsEnabled = false;
        try
        {
            var packet = await _catalogue.BuildAsync(q.Spec.Id, q.Axes, q.Values);
            if (_closing || _stopping || !ReferenceEquals(_bus, bus) || _stopAttempted) return;
            if (Sm10Commands.Get(q.Spec.Id).Moves) _targeted.UnionWith(q.Axes);
            Append($"TX {q.Spec}: {packet.Frame}");
            var result = await _backend!.ExecuteAsync(q.Spec.Id, q.Axes, q.Values, allowWrites);
            Append(result.Description + (result.Data.Length == 0 ? "" : " | RX " + Convert.ToHexString(result.Data)));
        }
        catch (BackendValidationException ex) { Append("Input error: " + ex.Message); }
        catch (Exception ex)
        {
            bus.AbortPending(); Append("ERROR — connection latched, no retries: " + ex.Message);
            if (!_stopping && _targeted.Count > 0) await StopAsync(_targeted.ToArray());
        }
        finally { _busy = false; Send.IsEnabled = Command.SelectedItem is PythonConsoleCommand { Supported: true }; }
    }
    private async Task StopAsync(int[] axes)
    {
        var bus = _bus;
        if (_stopAttempted || bus is null)
        {
            if (_stopTask is not null) try { await _stopTask; } catch { /* First attempt logged the error; do not retry. */ }
            return;
        }
        _stopAttempted = true;
        _stopping = true;
        try { _stopTask = bus.EmergencyStopAsync(axes); await _stopTask; Append("Group STOP sent without ACK; physical stop unconfirmed. Disconnect/reconnect required."); }
        catch (Exception ex) { bus.AbortPending(); Append("STOP failed: " + ex.Message + " Use the controller's physical stop."); }
        finally { _stopping = false; }
    }
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        var axes = new HashSet<int>(_targeted);
        try { foreach(var a in AxisList()) { Sm10Protocol.Axis(a); axes.Add(a); } }
        catch (Exception ex) { Append("Invalid current axis list: " + ex.Message); }
        _bus?.AbortPending();
        if (axes.Count > 0) await StopAsync(axes.ToArray());
        else Append("Pending work aborted. No valid axis addresses for Stop; physical stop unconfirmed.");
    }
    private Task DisconnectAsync() => _disconnectTask ??= DisconnectCoreAsync();
    private async Task DisconnectCoreAsync()
    {
        var bus = _bus;
        if (bus is null) return;
        bus.AbortPending();
        if (_targeted.Count > 0) await StopAsync(_targeted.ToArray());
        var backend = _backend; _backend = null;
        if (backend is not null) await backend.DisposeAsync();
        await bus.DisconnectAsync();
        _bus = null; Port.IsEnabled = true; Connect.IsEnabled = true;
        Append("Disconnected. No automatic reconnect.");
    }
    private async void OnDisconnect(object sender, RoutedEventArgs e) => await DisconnectAsync();
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text log|*.txt", FileName = $"sm10-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) == true)
            try { File.WriteAllText(dialog.FileName,Log.Text); } catch(Exception ex) { Append("Save failed: " + ex.Message); }
    }
}
