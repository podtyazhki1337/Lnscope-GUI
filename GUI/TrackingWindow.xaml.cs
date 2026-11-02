using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GUI.Devices;
using Microsoft.Win32;

namespace GUI;

/// <summary>Video-only UI adapter. No motion service or hardware transport is supplied.</summary>
public partial class TrackingWindow : Window
{
    private readonly PythonWorker _worker = new(null, maxResponseLength: 4 * 1024 * 1024);
    private readonly DispatcherTimer _timer = new();
    private bool _busy, _playing, _loaded, _closing, _closed;
    private JsonElement _frame;
    public TrackingWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await RunAsync("vision_next", new { });
            if (_playing && !_closing) _timer.Start();
        };
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            Pause();
            await _worker.DisposeAsync();
            _closed = true;
            // Cleanup can complete synchronously; let the current Closing event return first.
            await Dispatcher.InvokeAsync(Close);
        };
    }
    private void Pause() { _playing = false; _timer.Stop(); }
    private async Task RunAsync(string method, object args)
    {
        if (_busy || _closing) return;
        _busy = true;
        try
        {
            var result = await _worker.CallAsync(method, args);
            if (!_closing) ShowFrame(result);
        }
        catch (Exception error) { Pause(); if (!_closing) Status.Text = error.Message; }
        finally { _busy = false; }
    }
    private void ShowFrame(JsonElement frame)
    {
        _frame = frame;
        _loaded = true;
        using var stream = new MemoryStream(frame.GetProperty("image").GetBytesFromBase64());
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        VideoImage.Source = bitmap;
        Surface.Width = frame.GetProperty("width").GetInt32(); Surface.Height = frame.GetProperty("height").GetInt32();
        FileLabel.Text = frame.GetProperty("path").GetString();
        Overlay.Children.Clear();
        var tracks = frame.GetProperty("tracks");
        bool lost = false;
        foreach (var name in new[] { "tip", "target" })
        {
            var label = name == "tip" ? TipInfo : TargetInfo;
            if (!tracks.TryGetProperty(name, out var point)) { label.Text = $"{name}: not selected"; continue; }
            var x = point.GetProperty("x").GetDouble(); var y = point.GetProperty("y").GetDouble();
            var missing = point.GetProperty("lost").GetBoolean(); lost |= missing;
            var color = missing ? Brushes.OrangeRed : name == "tip" ? Brushes.Cyan : Brushes.Gold;
            var box = new Rectangle { Width = 31, Height = 31, Stroke = color, StrokeThickness = 1 };
            Canvas.SetLeft(box, x-15); Canvas.SetTop(box, y-15); Overlay.Children.Add(box);
            var dot = new Ellipse { Width = 6, Height = 6, Fill = color };
            Canvas.SetLeft(dot, x-3); Canvas.SetTop(dot, y-3); Overlay.Children.Add(dot);
            label.Text = $"{name}: ({x:0}, {y:0}) px\nMatch: {point.GetProperty("score").GetDouble():0.00}\n{point.GetProperty("reason").GetString()}";
        }
        var offset = frame.GetProperty("offset");
        if (offset.ValueKind == JsonValueKind.Null) OffsetInfo.Text = "Offset unavailable: select / restore both points.";
        else
        {
            OffsetInfo.Text = $"Target − tip\nΔX {offset.GetProperty("dx").GetDouble():0.0} px\nΔY {offset.GetProperty("dy").GetDouble():0.0} px\nDistance {offset.GetProperty("distance").GetDouble():0.0} px";
            var tip = tracks.GetProperty("tip"); var target = tracks.GetProperty("target");
            Overlay.Children.Insert(0, new Line { X1 = tip.GetProperty("x").GetDouble(), Y1 = tip.GetProperty("y").GetDouble(),
                X2 = target.GetProperty("x").GetDouble(), Y2 = target.GetProperty("y").GetDouble(), Stroke = Brushes.White, StrokeThickness = 1 });
        }
        bool eof = frame.GetProperty("eof").GetBoolean();
        if (lost || eof) Pause();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(1000/frame.GetProperty("fps").GetDouble(), 15, 2000));
        Status.Text = $"Frame {frame.GetProperty("frame").GetInt32()+1}/{frame.GetProperty("count").GetInt32()} · video {frame.GetProperty("time_ms").GetDouble()/1000:0.000} s · tracking {frame.GetProperty("elapsed_ms").GetDouble():0.0} ms · image scale {frame.GetProperty("scale").GetDouble():0.###} · CSV: last {frame.GetProperty("log_rows").GetInt32()} rows" +
            (lost ? " · PAUSED: tracking lost; reselect." : eof ? " · End of video." : " · Pixel coordinates; no motor commands.");
    }
    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Pause();
        var dialog = new OpenFileDialog { Filter = "Video|*.mp4;*.wmv;*.avi;*.mov;*.mkv|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await RunAsync("vision_open", new { path = dialog.FileName });
    }
    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _busy || _frame.GetProperty("eof").GetBoolean()) return;
        if (_frame.GetProperty("tracks").EnumerateObject().Any(p => p.Value.GetProperty("lost").GetBoolean()))
        { Status.Text = "Reselect the lost point or clear points before playing."; return; }
        _playing = true; _timer.Start();
    }
    private void OnPause(object sender, RoutedEventArgs e) => Pause();
    private async void OnNext(object sender, RoutedEventArgs e) { Pause(); await RunAsync("vision_next", new { }); }
    private async void OnSeek(object sender, RoutedEventArgs e)
    {
        Pause();
        if (!double.TryParse(SeekSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        { Status.Text = "Enter video time in seconds using a decimal point."; return; }
        await RunAsync("vision_seek", new { seconds });
    }
    private async void OnReset(object sender, RoutedEventArgs e) { Pause(); await RunAsync("vision_reset", new { }); }
    private async void OnMark(object sender, MouseButtonEventArgs e)
    {
        Pause();
        if (!_loaded || _busy) return;
        var point = e.GetPosition(Surface);
        await RunAsync("vision_mark", new { name = TipMode.IsChecked == true ? "tip" : "target", x = point.X, y = point.Y });
    }
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        Pause();
        if (!_loaded || _busy) return;
        var dialog = new SaveFileDialog { Filter = "Tracking CSV|*.csv", FileName = "tracking.csv" };
        if (dialog.ShowDialog(this) != true) return;
        _busy = true;
        try
        {
            var result = await _worker.CallAsync("vision_export", new { });
            await File.WriteAllTextAsync(dialog.FileName, result.GetProperty("csv").GetString());
            Status.Text = $"Exported {result.GetProperty("rows").GetInt32()} rows: {dialog.FileName}";
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { _busy = false; }
    }
}
