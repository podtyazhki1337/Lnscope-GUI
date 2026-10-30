using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GUI.Devices;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann.Sm10;

// Never called by the ordinary smoke suite; these explicitly opted-in checks use real hardware.
internal static class LiveHardwareChecks
{
    public static async Task CameraAsync()
    {
        var devices = KernCamera.Enumerate();
        foreach (var device in devices) Console.WriteLine($"KERN {device.Name} | {device.DeviceId}");
        if (devices.Count != 1) throw new IOException("Expected exactly one KERN camera.");
        await using var camera = new KernCamera(new CameraOptions { DeviceId = devices[0].DeviceId, ModeIndex = 1, TargetFps = 0 });
        CameraFrame? last = null;
        int frames = 0;
        camera.FrameReady += (_, frame) => { last = frame; Interlocked.Increment(ref frames); };
        await camera.ConnectAsync();
        Console.WriteLine("Modes: " + string.Join(", ", camera.SupportedModes));
        await camera.StartAsync();
        await Task.Delay(2500);
        Console.WriteLine($"Capture: {frames} frames, measured {camera.MeasuredFps:F2} fps");
        await camera.StopAsync();
        var image = last ?? throw new IOException("No KERN image received.");
        var path = Path.Combine(AppContext.BaseDirectory, "kern-live.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr24, null, image.Data, image.Stride)));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine("Saved " + path);
        await camera.StartAsync();
        await Task.Delay(500);
        await camera.StopAsync();
        Console.WriteLine("PASS KERN capture and restart");
        await camera.DisconnectAsync();
        var options = new CameraOptions { DeviceId = devices[0].DeviceId, ModeIndex = 1, TargetFps = 15 };
        var recording = new GUI.Recording.RecordingOptions { OutputDirectory = Path.Combine(AppContext.BaseDirectory, "kern-recording-check", Guid.NewGuid().ToString("N")) };
        var model = new GUI.ViewModels.CameraViewModel(new KernCamera(options), options, recording,
            System.Windows.Threading.Dispatcher.CurrentDispatcher, Console.WriteLine);
        try
        {
            await ExecuteAsync(model.ConnectCommand);
            await ExecuteAsync(model.RecordCommand);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!model.RecordingStatus.StartsWith("recording ")) await Task.Delay(50, timeout.Token);
            await Task.Delay(2000);
            await ExecuteAsync(model.StopRecordingCommand);
            if (!model.RecordingStatus.StartsWith("saved ")) throw new IOException(model.RecordingStatus);
            var file = Directory.GetFiles(recording.OutputDirectory, "*.mp4").Single();
            var info = new System.Diagnostics.ProcessStartInfo(GUI.Recording.VideoRecorder.EncoderPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-i", file, "-f", "null", "-" }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(errors);
            Console.WriteLine("PASS KERN Record button path + MP4 decode: " + file);
        }
        finally { await model.ShutdownAsync(); }
    }

    private static async Task ExecuteAsync(GUI.Mvvm.AsyncRelayCommand command)
    {
        if (!command.CanExecute(null)) throw new InvalidOperationException("Command disabled");
        await command.ExecuteAsync();
    }

    public static async Task ControllerAsync()
    {
        var bus = new Sm10SerialBus("COM3", 115200, 2000, allowLegacyChecksum: true);
        await bus.ConnectAsync();
        try
        {
            for (int axis = 1; axis <= 9; axis++)
            {
                var data = Sm10Protocol.Axis(axis);
                var position = Sm10Protocol.ReadPosition(await bus.RequestAsync(Sm10Protocol.Position, data));
                var counter = Sm10Protocol.ReadPosition(await bus.RequestAsync(Sm10Protocol.Counter2, data));
                var status = await bus.RequestAsync(Sm10Protocol.Status, data);
                Console.WriteLine($"Axis {axis}: position={position}, counter2={counter}, status={Convert.ToHexString(status)}");
            }
            Console.WriteLine("PASS COM3: only position/counter/status queries, no motion commands");
        }
        finally { await bus.DisconnectAsync(); }
    }
}
