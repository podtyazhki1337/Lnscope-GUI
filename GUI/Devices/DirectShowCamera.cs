using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;
using GUI.Recording;

namespace GUI.Devices;

public sealed record CameraDevice(string Name, string DeviceId)
{
    public override string ToString() => Name;
}
public sealed record CaptureMode(int Width, int Height, double MinFps, double MaxFps, string Format, bool Compressed)
{
    public override string ToString() => $"{Width}×{Height} · {Format} · {MinFps:0.##}–{MaxFps:0.##} fps";
}

/// <summary>Windows video devices exposed by UVC or a vendor's DirectShow driver.</summary>
public static class DirectShowDiscovery
{
    public static ProcessStartInfo StartInfo(IEnumerable<string> arguments, bool video = false)
    {
        var info = new ProcessStartInfo(VideoRecorder.EncoderPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<string> ProbeAsync(IEnumerable<string> arguments, CancellationToken ct = default)
    {
        using var process = Process.Start(StartInfo(arguments)) ?? throw new IOException("Cannot start video device discovery.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            await output;
            return await errors; // FFmpeg intentionally returns nonzero for list-only invocations.
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            throw;
        }
    }

    public static async Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken ct = default) =>
        ParseDevices(await ProbeAsync(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], ct));

    public static IReadOnlyList<CameraDevice> ParseDevices(string text)
    {
        var devices = new List<CameraDevice>();
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var video = Regex.Match(line, @"""(.*)"" \(video\)");
            if (video.Success) { name = video.Groups[1].Value; continue; }
            var alternative = Regex.Match(line, @"Alternative name ""(.*)""");
            if (name is not null && alternative.Success)
            {
                devices.Add(new CameraDevice(name, alternative.Groups[1].Value));
                name = null;
            }
            else if (line.Contains("(audio)")) name = null;
        }
        return devices;
    }

    public static IReadOnlyList<CaptureMode> ParseModes(string text)
    {
        var result = new List<CaptureMode>();
        foreach (var line in text.Split('\n'))
        {
            var format = Regex.Match(line, @"(pixel_format|vcodec)=(\S+)");
            var sizes = Regex.Matches(line, @"s=(\d+)x(\d+) fps=([\d.]+)");
            if (!format.Success || sizes.Count == 0) continue;
            var minFps = double.Parse(sizes[0].Groups[3].Value, CultureInfo.InvariantCulture);
            var maxFps = double.Parse(sizes[^1].Groups[3].Value, CultureInfo.InvariantCulture);
            foreach (Match size in sizes)
                result.Add(new CaptureMode(int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value),
                    minFps, maxFps, format.Groups[2].Value, format.Groups[1].Value == "vcodec"));
        }
        return result.Where(m => m.Width > 0 && m.Height > 0 && m.MinFps > 0 && m.MaxFps >= m.MinFps)
            .Distinct().OrderByDescending(m => (long)m.Width * m.Height).ThenBy(m => m.Format).ThenBy(m => m.MaxFps).ToArray();
    }

    public static async Task<IReadOnlyList<CaptureMode>> ModesAsync(string device, CancellationToken ct = default) =>
        ParseModes(await ProbeAsync(["-hide_banner", "-f", "dshow", "-list_options", "true", "-i", "video=" + device], ct));
}

public sealed class DirectShowCamera(CameraOptions options) : ICamera
{
    private Process? _capture;
    private CancellationTokenSource? _streamCts;
    private Task? _reader;
    private Task? _errors;
    private string _lastError = "";
    private IReadOnlyList<CaptureMode> _captureModes = [];
    private IReadOnlyList<VideoMode> _modes = [];
    private double _target = options.TargetFps;
    private double _measured;
    private long _number;
    public double ActiveFps { get; private set; }
    public string Id => options.Id;
    public DeviceState State { get; private set; } = DeviceState.Disconnected;
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<CameraFrame>? FrameReady;
    public IReadOnlyList<VideoMode> SupportedModes => _modes;
    public VideoMode? CurrentMode { get; private set; }
    public IReadOnlyList<DeviceProperty> Properties { get; } = [];
    public bool IsStreaming => _capture is { HasExited: false } && _reader is { IsCompleted: false };
    public double MeasuredFps => Volatile.Read(ref _measured);
    public double TargetFps
    {
        get => _target;
        set
        {
            if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _target = value;
        }
    }
    private void SetState(DeviceState value) { State = value; StateChanged?.Invoke(this, value); }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State == DeviceState.Ready) return;
        await StopAsync(ct);
        SetState(DeviceState.Connecting);
        try
        {
            if (string.IsNullOrWhiteSpace(options.DeviceId))
                throw new InvalidOperationException("Choose a Windows camera in Hardware setup.");
            var devices = await DirectShowDiscovery.EnumerateAsync(ct);
            if (!devices.Any(d => d.DeviceId == options.DeviceId))
                throw new IOException("Selected camera is unavailable. Connect it, install its Windows/DirectShow driver and refresh Hardware setup.");
            _captureModes = await DirectShowDiscovery.ModesAsync(options.DeviceId, ct);
            if (_captureModes.Count == 0)
                throw new IOException("The camera driver did not report supported capture modes. Check its vendor driver.");
            _modes = _captureModes.Select((m, i) => new VideoMode(i, m.Width, m.Height, m.ToString())).ToArray();
            CurrentMode = _modes[Math.Clamp(options.ModeIndex, 0, _modes.Count - 1)];
            SetState(DeviceState.Ready);
        }
        catch { SetState(DeviceState.Faulted); throw; }
    }

    public async Task SetModeAsync(int modeIndex, CancellationToken ct = default)
    {
        if (modeIndex < 0 || modeIndex >= _modes.Count) throw new ArgumentOutOfRangeException(nameof(modeIndex));
        var resume = IsStreaming;
        await StopAsync(ct);
        CurrentMode = _modes[modeIndex];
        if (resume) await StartAsync(ct);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsStreaming) return;
        if (State != DeviceState.Ready || CurrentMode is null) throw new InvalidOperationException("Connect the camera first.");
        await StopAsync(ct);
        var mode = _captureModes[CurrentMode.Index];
        ActiveFps = ResolveCaptureFps(_target, mode);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "dshow",
            "-rtbufsize", "128M", "-video_size", $"{mode.Width}x{mode.Height}",
            "-framerate", ActiveFps.ToString("0.###", CultureInfo.InvariantCulture),
            mode.Compressed ? "-vcodec" : "-pixel_format", mode.Format,
            "-i", "video=" + options.DeviceId, "-an", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1" };
        var process = Process.Start(DirectShowDiscovery.StartInfo(args)) ?? throw new IOException("Cannot open camera.");
        _capture = process;
        _streamCts = new CancellationTokenSource();
        _lastError = "";
        _errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) _lastError = line;
        });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _streamCts.Token;
        _reader = Task.Run(async () =>
        {
            try
            {
                var timer = Stopwatch.StartNew();
                long frames = 0;
                while (true)
                {
                    var data = new byte[checked(mode.Width * mode.Height * 3)];
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(data, token);
                    FrameReady?.Invoke(this, new CameraFrame(data, mode.Width, mode.Height, mode.Width * 3,
                        CameraPixelFormat.Bgr24, Interlocked.Increment(ref _number), DateTimeOffset.UtcNow));
                    ready.TrySetResult();
                    frames++;
                    if (timer.Elapsed.TotalSeconds >= 1)
                    {
                        Volatile.Write(ref _measured, frames / timer.Elapsed.TotalSeconds);
                        frames = 0; timer.Restart();
                    }
                }
            }
            catch (Exception) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
            catch (Exception exception)
            {
                ready.TrySetException(new IOException("Camera capture failed: " + _lastError, exception));
                SetState(DeviceState.Faulted);
            }
        }, CancellationToken.None);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch
        {
            await StopAsync();
            SetState(DeviceState.Faulted);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_capture is not { } process) return;
        _streamCts?.Cancel();
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync();
        if (_reader is not null) await _reader;
        if (_errors is not null) await _errors;
        process.Dispose(); _capture = null;
        _streamCts?.Dispose(); _streamCts = null;
        _reader = null; _errors = null;
        Volatile.Write(ref _measured, 0);
    }
    public static double ResolveCaptureFps(double requested, CaptureMode mode)
    {
        if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        return Math.Clamp(requested == 0 ? mode.MaxFps : requested, mode.MinFps, mode.MaxFps);
    }
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        SetState(DeviceState.Disconnected);
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
