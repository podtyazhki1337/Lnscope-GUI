using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using LnScope.Core.Devices;

namespace GUI.Recording;

/// <summary>Records with the private encoder shipped with LNscope. No PATH or system installation is used.</summary>
public sealed class VideoRecorder : IAsyncDisposable
{
    private sealed class Session(Process process, Channel<byte[]> queue, int width, int height, CameraPixelFormat format)
    {
        public Process Process { get; } = process;
        public Channel<byte[]> Queue { get; } = queue;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public CameraPixelFormat Format { get; } = format;
        public Stream Input { get; } = process.StandardInput.BaseStream;
        public Task<string> Errors { get; } = process.StandardError.ReadToEndAsync();
        public Task Writer { get; set; } = Task.CompletedTask;
        public long Written;
        public long Dropped;
    }

    private readonly object _gate = new();
    private readonly RecordingOptions _options;
    private readonly Action<string> _log;
    private Session? _session;
    private Task _stopTask = Task.CompletedTask;
    private string _status = "idle";
    private long _written;
    private long _dropped;

    public VideoRecorder(RecordingOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
    }

    public static string EncoderPath => Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ffmpeg", "ffmpeg.exe");
    public bool IsRecording { get { lock (_gate) return _session is { } s && _stopTask.IsCompleted && !s.Process.HasExited; } }
    public bool IsActive { get { lock (_gate) return _session is not null; } }
    public string? CurrentFile { get; private set; }
    public long WrittenFrames { get { lock (_gate) return _session is { } s ? Interlocked.Read(ref s.Written) : _written; } }
    public long DroppedFrames { get { lock (_gate) return _session is { } s ? Interlocked.Read(ref s.Dropped) : _dropped; } }
    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_session is { } s && _stopTask.IsCompleted && s.Process.HasExited)
                    return "Recording failed. Stop recording to finish cleanup. " +
                        (s.Errors.IsCompletedSuccessfully ? s.Errors.Result.Trim() : "Encoder exited unexpectedly.");
                return _status;
            }
        }
    }

    public bool Start(string cameraId, int width, int height, double fps, CameraPixelFormat format)
    {
        lock (_gate)
        {
            if (_session is not null) return IsRecording;
            try
            {
                if (!File.Exists(EncoderPath))
                    throw new FileNotFoundException("The application recording component is missing. Restore the complete LNscope application folder.", EncoderPath);
                if (width <= 0 || height <= 0 || !double.IsFinite(fps))
                    throw new ArgumentException("Invalid recording dimensions or frame rate.");
                var directory = string.IsNullOrWhiteSpace(_options.OutputDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LNscope")
                    : _options.OutputDirectory;
                Directory.CreateDirectory(directory);
                var effectiveFps = fps > 0 ? fps : 25;
                var safeId = string.Concat(cameraId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var name = _options.FileNameTemplate.Replace("{camera}", safeId)
                    .Replace("{timestamp}", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"))
                    .Replace("{width}", width.ToString()).Replace("{height}", height.ToString())
                    .Replace("{fps}", effectiveFps.ToString("0", CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Invalid recording file name.");
                var path = Path.Combine(directory, name);
                // Never overwrite a previous recording, including custom constant file names.
                if (File.Exists(path))
                    path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "_" + Guid.NewGuid().ToString("N") + ".mp4");
                var pixelFormat = format switch
                {
                    CameraPixelFormat.Mono8 => "gray",
                    CameraPixelFormat.Mono16 => "gray16le",
                    CameraPixelFormat.Bgr24 => "bgr24",
                    CameraPixelFormat.Bgra32 => "bgra",
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = EncoderPath,
                    Arguments = $"-hide_banner -loglevel error -nostdin -n -f rawvideo -pixel_format {pixelFormat} " +
                        $"-video_size {width}x{height} -framerate {effectiveFps.ToString("0.###", CultureInfo.InvariantCulture)} " +
                        $"-i pipe:0 {_options.CodecArguments} -movflags +faststart \"{path}\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }) ?? throw new IOException("Could not start the recording component.");
                var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
                var session = new Session(process, queue, width, height, format);
                _session = session;
                CurrentFile = path;
                _written = _dropped = 0;
                _stopTask = Task.CompletedTask;
                session.Writer = Task.Run(() => WriteAsync(session));
                _status = $"recording {Path.GetFileName(path)}";
                _log($"recording started: {path}");
                return true;
            }
            catch (Exception exception)
            {
                _status = $"Cannot record — {exception.Message}";
                _log(_status);
                return false;
            }
        }
    }

    public void Push(CameraFrame frame)
    {
        lock (_gate)
        {
            if (_session is not { } s || !_stopTask.IsCompleted || s.Process.HasExited) return;
            var rowBytes = checked(s.Width * BytesPerPixel(s.Format));
            if (frame.Width != s.Width || frame.Height != s.Height || frame.Format != s.Format ||
                frame.Stride < rowBytes || frame.Data.Length < (long)frame.Stride * frame.Height)
            {
                Interlocked.Increment(ref s.Dropped);
                return;
            }
            // Camera rows may contain alignment padding; rawvideo requires tightly packed rows.
            var packed = new byte[checked(rowBytes * frame.Height)];
            for (var y = 0; y < frame.Height; y++)
                Buffer.BlockCopy(frame.Data, y * frame.Stride, packed, y * rowBytes, rowBytes);
            if (!s.Queue.Writer.TryWrite(packed)) Interlocked.Increment(ref s.Dropped);
        }
    }

    private async Task WriteAsync(Session session)
    {
        try
        {
            await foreach (var frame in session.Queue.Reader.ReadAllAsync())
            {
                await session.Input.WriteAsync(frame);
                Interlocked.Increment(ref session.Written);
            }
            await session.Input.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            lock (_gate) _status = $"Recording write failed — {exception.Message}";
            _log(_status);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_session is null) return _stopTask;
            if (!_stopTask.IsCompleted) return _stopTask;
            _status = "finishing recording…";
            _session.Queue.Writer.TryComplete();
            return _stopTask = FinishAsync(_session);
        }
    }

    private async Task FinishAsync(Session session)
    {
        // Ensure StopAsync has published its shared completion task before any cleanup runs.
        await Task.Yield();
        var timedOut = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await session.Writer.WaitAsync(timeout.Token);
                session.Input.Close();
                await session.Process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                if (!session.Process.HasExited) session.Process.Kill();
                await session.Process.WaitForExitAsync();
                await session.Writer;
            }
            var error = (await session.Errors).Trim();
            lock (_gate)
            {
                _written = Interlocked.Read(ref session.Written);
                _dropped = Interlocked.Read(ref session.Dropped);
                _status = timedOut ? "Recording timed out; the video may be incomplete."
                    : session.Process.ExitCode != 0 ? $"Recording failed — {error}"
                    : _written == 0 ? "No frames received; no video was recorded."
                    : $"saved {Path.GetFileName(CurrentFile)} — {_written} frames, {_dropped} dropped";
            }
            _log($"recording: {_status}");
        }
        finally
        {
            session.Input.Dispose();
            lock (_gate)
            {
                session.Process.Dispose();
                _session = null;
            }
        }
    }

    private static int BytesPerPixel(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Mono8 => 1,
        CameraPixelFormat.Mono16 => 2,
        CameraPixelFormat.Bgr24 => 3,
        CameraPixelFormat.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public async ValueTask DisposeAsync() => await StopAsync();
}
