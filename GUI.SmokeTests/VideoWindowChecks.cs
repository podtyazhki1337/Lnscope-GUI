using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GUI;
using GUI.ViewModels;

internal static class VideoWindowChecks
{
    public static async Task RunAsync(MainViewModel model, string video)
    {
        var owner = new Window { Width = 100, Height = 100, Left = -10000, Top = -10000, ShowInTaskbar = false };
        owner.Show();
        var window = new CameraWindow(model.Cameras[0], model.Jog, owner)
            { Left = -10000, Top = -10000, ShowInTaskbar = false };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            await window.OpenVideoAsync(video);
            ((ToggleButton)window.FindName("Track")).IsChecked = true;
            var status = (TextBlock)window.FindName("AnalysisStatus");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!status.Text.Contains("objects |")) await Task.Delay(50, timeout.Token);
            if (((Image)window.FindName("Preview")).Source is null)
                throw new InvalidOperationException("Video preview missing after model inference.");
            window.UpdateLayout();
            var surface = (FrameworkElement)window.FindName("ImageSurface");
            var viewport = (ScrollViewer)window.FindName("VideoViewport");
            var preview = (Image)window.FindName("Preview");
            var source = (BitmapSource)preview.Source;
            var dpi = VisualTreeHelper.GetDpi(window);
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("NativeSizeButton"); window.UpdateLayout();
            if (Math.Abs(((ScaleTransform)surface.LayoutTransform).ScaleX * dpi.DpiScaleX - 1) > .001)
                throw new InvalidOperationException("Native zoom must map one image pixel to one physical pixel.");
            Click("ZoomInButton"); window.UpdateLayout();
            if (((ScaleTransform)surface.LayoutTransform).ScaleX * dpi.DpiScaleX <= 1)
                throw new InvalidOperationException("Zoom in did not enlarge the native image.");
            Click("FitSizeButton"); window.UpdateLayout();
            if (surface.ActualWidth * ((ScaleTransform)surface.LayoutTransform).ScaleX > viewport.ActualWidth + 1)
                throw new InvalidOperationException("Fit view exceeds the available viewport.");
            if (!ReferenceEquals(source, preview.Source)) throw new InvalidOperationException("Display zoom replaced the source frame.");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "video-window-check.png"))) encoder.Save(file);
            window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
            bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "video-window-small-check.png"))) encoder.Save(file);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!owner.IsVisible) throw new InvalidOperationException("Video window closed its owner.");
            Console.WriteLine("PASS: unified video window loads video, segments/tracks via bundled ONNX and closes independently.");
        }
        finally
        {
            if (!closed.Task.IsCompleted) window.Close();
            await window.Dispatcher.InvokeAsync(owner.Close, DispatcherPriority.Background);
        }
    }
}
