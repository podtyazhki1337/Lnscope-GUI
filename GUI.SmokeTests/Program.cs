using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using GUI;
using GUI.Mvvm;
using GUI.ViewModels;
using GUI.Recording;
using LnScope.Core.Devices;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.SequenceEqual(new[] { "--kern-live" })) await LiveHardwareChecks.CameraAsync();
                else if (args.SequenceEqual(new[] { "--sm10-readonly" })) await LiveHardwareChecks.ControllerAsync();
                else if (args.Length == 0) await RunAsync(dispatcher);
                else throw new ArgumentException("Unknown test option. Hardware checks require an explicit --kern-live or --sm10-readonly.");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
    }

    private static async Task RunAsync(Dispatcher dispatcher)
    {
        await WindowLifetimeChecks.RunAsync();
        await HardwareChecks.RunAsync();
        await Sm10CommandChecks.RunAsync();
        await MotionSafetyChecks.RunAsync();
        await PythonBackendChecks.RunAsync();
        await PythonCommandChecks.RunAsync();
        await SparseRecordingChecks.RunAsync();
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "devices.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // Never use hardware when running this test, even if the deployment config changes.
        foreach (var positioner in config.Positioners) positioner.Transport = "simulated";
        foreach (var camera in config.Cameras) camera.Driver = "simulated";
        var path = Path.Combine(AppContext.BaseDirectory, "smoke-config.json");
        var model = new MainViewModel(config, path, dispatcher);
        var output = Path.Combine(AppContext.BaseDirectory, "recording-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        // Reproduce a machine with no externally installed FFmpeg.
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            Check(model.EnvironmentLabel == "SIMULATION", "simulation badge");
            foreach (var value in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
                Reject(() => model.JogStep = value);
            Check(model.JogStep == config.Jog.StepUm, "invalid jog input preserves step");

            var axis = model.Positioners[0].Axes[0];
            Reject(() => axis.Step = double.NaN);
            Reject(() => axis.Target = double.PositiveInfinity);
            Reject(() => axis.Target = double.MaxValue);
            await ExecuteAsync(model.Positioners[0].ConnectCommand);
            Check(model.Positioners[0].IsConnected, "positioner connects");
            await ExecuteAsync(axis.StepUpCommand);
            await axis.RefreshAsync();
            Check(Math.Abs(axis.Position - axis.Step) < 0.001, "relative movement updates position");
            await ExecuteAsync(model.Positioners[0].DisconnectCommand);
            await ExecuteAsync(model.Positioners[0].ConnectCommand);
            Check(model.Positioners[0].IsConnected, "positioner reconnects with a fresh transport");

            model.Links[0].IsEnabled = true;
            await ExecuteAsync(model.SaveConfigCommand);
            var saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path))!;
            Check(saved.Links[0].EnabledByDefault, "link state persists");

            var camera = model.Cameras[0];
            foreach (var fps in new[] { 0d, 7.5d, 15d })
            {
                camera.TargetFps = fps;
                Check(camera.TargetFps == fps && config.Cameras[0].TargetFps == fps, "FPS editable before connection and persisted in configuration");
            }
            await ExecuteAsync(camera.ConnectCommand);
            await ExecuteAsync(camera.StartCommand);
            await UntilAsync(() => camera.Image is not null);
            Check(camera.IsStreaming && camera.Image!.PixelWidth > 0, "camera produces a display frame");
            camera.OutputDirectory = output;
            await ExecuteAsync(camera.StopCommand);
            await ExecuteAsync(camera.RecordCommand);
            await UntilAsync(() => camera.RecordingStatus.StartsWith("recording "));
            await Task.Delay(1000);
            await ExecuteAsync(camera.StopRecordingCommand);
            Check(camera.RecordingStatus.StartsWith("saved "), "Record starts capture and saves MP4 without PATH");
            await VerifyVideoAsync(Directory.GetFiles(output, "*.mp4", SearchOption.AllDirectories).Single());

            await ExecuteAsync(camera.RecordCommand);
            await UntilAsync(() => camera.RecordingStatus.StartsWith("recording "));
            await Task.Delay(600);
            await ExecuteAsync(camera.StopCommand);
            Check(Directory.GetFiles(output, "*.mp4", SearchOption.AllDirectories).Length == 2 && !camera.IsRecording,
                "repeated recording and Stop live finalize video");

            await CheckRecorderAsync(output);

            await ExecuteAsync(camera.RecordCommand);
            await UntilAsync(() => camera.RecordingStatus.StartsWith("recording "));
            await Task.Delay(600);

            var calls = 0;
            var command = new AsyncRelayCommand(() => { calls++; return Task.CompletedTask; }, () => false);
            command.Execute(null);
            Check(calls == 0, "disabled command cannot execute programmatically");
        }
        finally
        {
            try { await model.ShutdownAsync(); }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        }
        Check(!model.Jog.IsEnabled && model.Cameras.All(c => !c.IsStreaming), "shutdown stops capture and keyboard");
        foreach (var video in Directory.GetFiles(output, "camera_*.mp4", SearchOption.AllDirectories))
        {
            await VerifyVideoAsync(video);
            var rows = File.ReadAllLines(Path.ChangeExtension(video, ".csv"));
            Check(rows.Length > 1 && rows[0].Contains("c1,c2"), "camera recording creates a matching coordinate sidecar");
            Check(rows.Skip(1).Any(row => row.Contains("Ready")), "coordinate sidecar includes connected positioner feedback");
            Check(rows.Skip(1).Any(row => row.Contains("\"True\",\"position\"")), "fresh feedback is marked valid");
        }
        await using (var vision = new GUI.Devices.PythonWorker(null, maxResponseLength: 4 * 1024 * 1024))
        {
            var frame = await vision.CallAsync("vision_open", new { path = Directory.GetFiles(output, "camera_*.mp4", SearchOption.AllDirectories)[0] });
            Check(frame.GetProperty("image").GetBytesFromBase64().Length > 100 && frame.GetProperty("tracks").EnumerateObject().Count() == 0,
                "offline video IPC opens bundled OpenCV and returns a frame without hardware");
            frame = await vision.CallAsync("vision_next", new { });
            Check(frame.GetProperty("frame").GetInt32() >= 1 && frame.GetProperty("offset").ValueKind == JsonValueKind.Null,
                "offline video advances and does not invent coordinates before manual selection");
        }
        Console.WriteLine($"Recorded videos: {output}");
        await VideoWindowChecks.RunAsync(model, Directory.GetFiles(output, "camera_*.mp4", SearchOption.AllDirectories)[0]);
        Console.WriteLine("All smoke checks passed.");
    }

    private static async Task CheckRecorderAsync(string output)
    {
        var options = new RecordingOptions { OutputDirectory = output, QueueCapacity = 4 };
        await using var recorder = new VideoRecorder(options, Console.WriteLine);
        Check(recorder.Start("padded", 64, 48, 10, CameraPixelFormat.Mono8), "bundled encoder starts");
        for (var i = 0; i < 10; i++)
        {
            recorder.Push(new CameraFrame(new byte[68 * 48], 64, 48, 68, CameraPixelFormat.Mono8, i, DateTimeOffset.UtcNow));
            await Task.Delay(25);
        }
        await Task.WhenAll(recorder.StopAsync(), recorder.StopAsync());
        Check(recorder.WrittenFrames > 0 && recorder.Status.StartsWith("saved "), "padded rows and simultaneous stops");
        await VerifyVideoAsync(recorder.CurrentFile!);
        var rows = File.ReadAllLines(Path.ChangeExtension(recorder.CurrentFile!, ".csv"));
        Check(rows.Count(row => row.Contains("\"position\"")) == 1, "unchanged missing-coordinate state is logged once");
        Check(rows[0].Contains("position_age_ms") && rows.Any(row => row.Contains("no_positioners")), "missing coordinates are explicit, not fabricated zeros");

        options.CodecArguments = "-c:v deliberately_missing_encoder";
        Check(recorder.Start("failure", 64, 48, 10, CameraPixelFormat.Mono8), "encoder failure scenario starts");
        recorder.Push(new CameraFrame(new byte[64 * 48], 64, 48, 64, CameraPixelFormat.Mono8, 0, DateTimeOffset.UtcNow));
        await recorder.StopAsync();
        Check(recorder.Status.StartsWith("Recording failed"), "encoder error is reported instead of saved");
    }

    private static async Task VerifyVideoAsync(string path)
    {
        Check(new FileInfo(path).Length > 1000, "MP4 is non-empty");
        var start = new ProcessStartInfo(VideoRecorder.EncoderPath)
        {
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-xerror", "-i", path, "-f", "null", "-" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (process.ExitCode != 0) throw new Exception(await errors);
        Check(true, "MP4 fully decodes without errors");
    }

    private static async Task ExecuteAsync(AsyncRelayCommand command)
    {
        Exception? error = null;
        var previous = command.ErrorHandler;
        command.ErrorHandler = exception => error = exception;
        try
        {
            if (!command.CanExecute(null)) throw new Exception("Command unexpectedly disabled.");
            await command.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (error is not null) throw error;
        }
        finally { command.ErrorHandler = previous; }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid number was accepted.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"FAIL: {name}");
        Console.WriteLine($"PASS: {name}");
    }
}
