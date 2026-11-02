using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GUI.Devices;
using GUI.Motion;
using GUI.ViewModels;
using Microsoft.Win32;

namespace GUI;

public partial class CameraWindow : Window
{
    private readonly CameraViewModel _camera;
    private readonly JogController _jog;
    private readonly PythonWorker _analysis = new(null, timeoutMs: 4500, maxResponseLength: 4 * 1024 * 1024);
    private readonly PythonWorker _video = new(null, maxResponseLength: 4 * 1024 * 1024);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _busy, _closing, _closed, _fileMode, _playing, _reset = true;
    private bool _analyzePending;
    private int _generation;
    private int? _selected;
    private double _videoTime;
    private JsonElement _result;
    private BitmapSource? _fileFrame;

    public CameraWindow(CameraViewModel camera, JogController jog, Window owner)
    {
        InitializeComponent();
        _camera = camera; _jog = jog; DataContext = camera; Owner = owner;
        Title = $"Camera - {camera.Id}";
        camera.PropertyChanged += OnCameraPropertyChanged;
        BindLive();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _generation++; _timer.Stop(); _jog.ReleaseAll();
            camera.PropertyChanged -= OnCameraPropertyChanged;
            try { await _analysis.DisposeAsync(); await _video.DisposeAsync(); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            _closed = true;
            await Dispatcher.InvokeAsync(Close);
        };
    }
    private void BindLive()
    {
        Preview.SetBinding(Image.SourceProperty, new Binding(nameof(CameraViewModel.Image)) { Source = _camera });
        if (_camera.Image is { } image) SizeImage(image);
    }
    private void SizeImage(BitmapSource image)
    {
        ImageSurface.Width = image.PixelWidth; ImageSurface.Height = image.PixelHeight;
    }
    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_fileMode && e.PropertyName is nameof(CameraViewModel.CurrentMode) or nameof(CameraViewModel.Image))
        {
            _generation++; _reset = true; Overlay.Children.Clear();
            if (_camera.Image is { } image) SizeImage(image);
        }
        if (!_fileMode && e.PropertyName == nameof(CameraViewModel.IsStreaming) && !_camera.IsStreaming)
        { _reset = true; Overlay.Children.Clear(); }
    }
    private void OnAnalysisChanged(object sender, RoutedEventArgs e)
    {
        if (Track is null || Segment is null || _camera is null) return;
        if (sender == Track && Track.IsChecked == true) Segment.IsChecked = true;
        if (sender == Segment && Segment.IsChecked != true) Track.IsChecked = false;
        _generation++; _reset = true; _selected = null; Overlay.Children.Clear();
        _result = default;
        _analyzePending = Segment.IsChecked == true;
        if (Segment.IsChecked != true && !_fileMode) BindLive();
    }
    private static byte[] Encode(BitmapSource frame)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static BitmapSource Decode(JsonElement frame)
    {
        using var stream = new MemoryStream(frame.GetProperty("image").GetBytesFromBase64());
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private void ShowVideo(JsonElement frame)
    {
        _fileFrame = Decode(frame); _videoTime = frame.GetProperty("time_ms").GetDouble()/1000;
        Preview.Source = _fileFrame; SizeImage(_fileFrame);
        SourceLabel.Text = $"Test video - {_videoTime:0.00} s";
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(1000/frame.GetProperty("fps").GetDouble(), 30, 2000));
        if (frame.GetProperty("eof").GetBoolean()) _playing = false;
    }
    private async Task AnalyzeAsync()
    {
        var frame = _fileMode ? _fileFrame : _camera.IsStreaming ? _camera.Image?.Clone() : null;
        if (frame is null || Segment.IsChecked != true) return;
        frame.Freeze();
        var generation = _generation;
        var timestamp = _fileMode ? _videoTime : System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        if (_reset) { await _analysis.CallAsync("segment_reset", new { }); _reset = false; }
        var result = await _analysis.CallAsync("segment_frame", new { image = Encode(frame),
            tracking = Track.IsChecked == true, timestamp, confidence = Confidence.Value });
        if (_closing || generation != _generation) return;
        // Display the analyzed frame with its own contours, never stale contours over a newer frame.
        Preview.Source = frame; SizeImage(frame); _result = result; DrawObjects();
        AnalysisStatus.Text = $"{result.GetProperty("objects").GetArrayLength()} objects | {result.GetProperty("elapsed_ms").GetDouble():0} ms analysis | " +
            (Track.IsChecked == true ? "Tracking: image coordinates only" : "Segmentation: experimental cell model");
    }
    private async Task TickAsync()
    {
        if (_busy || _closing || (_fileMode && !_playing && !_analyzePending)) return;
        _busy = true;
        try
        {
            if (_fileMode && _playing) ShowVideo(await _video.CallAsync("vision_next", new { }));
            _analyzePending = false;
            await AnalyzeAsync();
        }
        catch (Exception error)
        {
            _playing = false; Segment.IsChecked = false;
            AnalysisStatus.Text = error.Message + " Reopen the video window to restart analysis.";
        }
        finally { _busy = false; }
    }
    private void DrawObjects()
    {
        Overlay.Children.Clear();
        if (_result.ValueKind != JsonValueKind.Object) return;
        var selectedVisible = false;
        foreach (var obj in _result.GetProperty("objects").EnumerateArray())
        {
            int? id = obj.TryGetProperty("id", out var value) ? value.GetInt32() : null;
            var kind = obj.GetProperty("class").GetString();
            var color = kind == "spheroid" ? Colors.Gold : kind == "cell_group" ? Colors.DeepSkyBlue : Colors.LimeGreen;
            if (id is not null && id == _selected) { color = Colors.Magenta; selectedVisible = true; }
            foreach (var contour in obj.GetProperty("polygons").EnumerateArray())
            {
                var points = new PointCollection(contour.EnumerateArray().Select(p => new Point(p[0].GetDouble(), p[1].GetDouble())));
                Overlay.Children.Add(new Polygon { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)), IsHitTestVisible = false });
            }
            var box = obj.GetProperty("bbox");
            var label = new TextBlock { Text = $"{kind} {(id.HasValue ? "#"+id : "")} {obj.GetProperty("confidence").GetDouble():0.00}",
                Foreground = new SolidColorBrush(color), Background = Brushes.Black, FontSize = 11, IsHitTestVisible = false };
            Canvas.SetLeft(label, box[0].GetDouble()); Canvas.SetTop(label, Math.Max(0, box[1].GetDouble()-15)); Overlay.Children.Add(label);
        }
        SelectionStatus.Text = _selected is null ? "Click an object to select its track. No motion commands are sent." :
            selectedVisible ? $"Selected track #{_selected}" : $"Track #{_selected} LOST - select again; no automatic reacquisition.";
    }
    private void OnSelect(object sender, MouseButtonEventArgs e)
    {
        if (_result.ValueKind != JsonValueKind.Object) return;
        var point = e.GetPosition(Overlay);
        foreach (var obj in _result.GetProperty("objects").EnumerateArray().Reverse())
        {
            var box = obj.GetProperty("bbox");
            if (point.X >= box[0].GetDouble() && point.X <= box[2].GetDouble() && point.Y >= box[1].GetDouble() && point.Y <= box[3].GetDouble()
                && obj.TryGetProperty("id", out var id)) { _selected = id.GetInt32(); DrawObjects(); return; }
        }
    }
    private async void OnOpenVideo(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        if (_camera.IsRecording) { AnalysisStatus.Text = "Stop recording before opening a test video."; return; }
        var dialog = new OpenFileDialog { Filter = "Video|*.mp4;*.wmv;*.avi;*.mov;*.mkv|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await OpenVideoAsync(dialog.FileName);
    }
    internal async Task OpenVideoAsync(string path)
    {
        if (_busy || _closing || _camera.IsRecording) return;
        _busy = true; _jog.ReleaseAll();
        try
        {
            var frame = await _video.CallAsync("vision_open", new { path });
            if (_closing) return;
            _fileMode = true; _playing = false; _generation++; _reset = true; _selected = null;
            RecordButton.IsEnabled = false; Play.IsEnabled = Next.IsEnabled = Seek.IsEnabled = true;
            Overlay.Children.Clear(); ShowVideo(frame); await AnalyzeAsync();
        }
        catch (Exception error) { AnalysisStatus.Text = error.Message; }
        finally { _busy = false; }
    }
    private void OnLive(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        _fileMode = false; _playing = false; _generation++; _reset = true; _selected = null;
        _result = default; _timer.Interval = TimeSpan.FromMilliseconds(250);
        Play.IsEnabled = Next.IsEnabled = Seek.IsEnabled = false; RecordButton.IsEnabled = true;
        Overlay.Children.Clear(); SourceLabel.Text = "Live camera"; BindLive();
    }
    private void OnPlay(object sender, RoutedEventArgs e) { if (_fileMode) _playing = !_playing; }
    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (!_fileMode || _busy || _closing) return;
        _playing = false; _busy = true;
        try { ShowVideo(await _video.CallAsync("vision_next", new { })); await AnalyzeAsync(); }
        catch (Exception error) { AnalysisStatus.Text = error.Message; }
        finally { _busy = false; }
    }
    private async void OnSeek(object sender, RoutedEventArgs e)
    {
        if (!_fileMode || _busy || _closing) return;
        if (!double.TryParse(SeekSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0)
        { AnalysisStatus.Text = "Enter a non-negative time in seconds."; return; }
        _busy = true; _playing = false; _generation++; _reset = true; _selected = null; Overlay.Children.Clear();
        try { ShowVideo(await _video.CallAsync("vision_seek", new { seconds })); await AnalyzeAsync(); }
        catch (Exception error) { AnalysisStatus.Text = error.Message; }
        finally { _busy = false; }
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_fileMode || Keyboard.FocusedElement is TextBox or Slider) return;
        if (_jog.HandleKeyDown(e)) e.Handled = true;
    }
    private void OnKeyUp(object sender, KeyEventArgs e) { if (!_fileMode && _jog.HandleKeyUp(e)) e.Handled = true; }
    private void OnDeactivated(object? sender, EventArgs e) => _jog.ReleaseAll();
}
