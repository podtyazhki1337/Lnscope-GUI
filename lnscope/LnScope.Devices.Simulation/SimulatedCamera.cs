using System.Diagnostics;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;

namespace LnScope.Devices.Simulation;

/// <summary>Simulated camera with a synthetic spheroid scene. Supports testing image display, frame rate limiting and property controls without a microscope.</summary>
public sealed class SimulatedCamera : ICamera
{
    private sealed record Blob(double X, double Y, double Radius, double SpeedX, double SpeedY);

    private readonly CameraOptions _options;
    private readonly List<VideoMode> _modes = [];
    private readonly List<Blob> _blobs = [];
    private readonly Random _random = new(42);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly NumericProperty _exposure;
    private readonly NumericProperty _gain;
    private readonly BooleanProperty _invert;
    private readonly ChoiceProperty _pattern;

    private byte[]? _background;
    private CancellationTokenSource? _streaming;
    private Task? _streamTask;
    private DeviceState _state = DeviceState.Disconnected;
    private long _frameNumber;
    private double _measuredFps;
    private double _targetFps;

    public SimulatedCamera(CameraOptions options)
    {
        _options = options;
        _targetFps = options.TargetFps;

        _exposure = new NumericProperty("exposure", "Exposure", 20)
        {
            Unit = "ms",
            Minimum = 0.5,
            Maximum = 500,
            Step = 0.5
        };

        _gain = new NumericProperty("gain", "Gain", 2)
        {
            Minimum = 1,
            Maximum = 16,
            Step = 0.1
        };

        _invert = new BooleanProperty("invert", "Invert", false);

        _pattern = new ChoiceProperty("pattern", "Scene",
            ["spheroid", "gradient", "noise"], "spheroid");

        Properties = [_exposure, _gain, _invert, _pattern];
    }

    public string Id => _options.Id;

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public IReadOnlyList<VideoMode> SupportedModes => _modes;

    public VideoMode? CurrentMode { get; private set; }

    public IReadOnlyList<DeviceProperty> Properties { get; }

    public bool IsStreaming => _streaming is not null;

    public double MeasuredFps => _measuredFps;

    public double TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Max(0, value);
    }

    public event EventHandler<DeviceState>? StateChanged;

    public event EventHandler<CameraFrame>? FrameReady;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        State = DeviceState.Connecting;

        _modes.Clear();

        // A physical driver reads supported camera modes here.
        var declared = _options.Modes.Count > 0
            ? _options.Modes
            : new List<VideoModeOptions>
            {
                new() { Width = 1280, Height = 960, Description = "2x2 binning" }
            };

        for (var i = 0; i < declared.Count; i++)
        {
            _modes.Add(new VideoMode(i, declared[i].Width, declared[i].Height,
                string.IsNullOrWhiteSpace(declared[i].Description) ? "full frame" : declared[i].Description));
        }

        ApplyMode(Math.Clamp(_options.ModeIndex, 0, _modes.Count - 1));

        State = DeviceState.Ready;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        State = DeviceState.Disconnected;
    }

    public async Task SetModeAsync(int modeIndex, CancellationToken ct = default)
    {
        var wasStreaming = IsStreaming;

        // Changing a physical camera mode requires stopping the stream because frame buffer dimensions change.
        await StopAsync(ct);
        ApplyMode(modeIndex);

        if (wasStreaming)
        {
            await StartAsync(ct);
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsStreaming || CurrentMode is null)
        {
            return Task.CompletedTask;
        }

        _streaming = new CancellationTokenSource();
        _streamTask = Task.Run(() => StreamAsync(_streaming.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_streaming is null)
        {
            return;
        }

        await _streaming.CancelAsync();

        if (_streamTask is not null)
        {
            try
            {
                await _streamTask;
            }
            catch (OperationCanceledException)
            {
                // Normal stream shutdown.
            }
        }

        _streaming.Dispose();
        _streaming = null;
        _streamTask = null;
        _measuredFps = 0;
    }

    private void ApplyMode(int modeIndex)
    {
        if (modeIndex < 0 || modeIndex >= _modes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(modeIndex), "The requested camera mode is not available.");
        }

        CurrentMode = _modes[modeIndex];
        _background = null;
        _blobs.Clear();

        var mode = CurrentMode;

        for (var i = 0; i < 14; i++)
        {
            _blobs.Add(new Blob(
                _random.NextDouble() * mode.Width,
                _random.NextDouble() * mode.Height,
                mode.Height * (0.02 + _random.NextDouble() * 0.03),
                (_random.NextDouble() - 0.5) * 8,
                (_random.NextDouble() - 0.5) * 8));
        }
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        var fpsClock = Stopwatch.StartNew();
        var framesInWindow = 0;

        while (!ct.IsCancellationRequested)
        {
            var started = _clock.Elapsed;

            var frame = Render();
            FrameReady?.Invoke(this, frame);

            framesInWindow++;

            if (fpsClock.ElapsedMilliseconds >= 500)
            {
                _measuredFps = framesInWindow * 1000.0 / fpsClock.ElapsedMilliseconds;
                framesInWindow = 0;
                fpsClock.Restart();
            }

            if (_targetFps > 0)
            {
                var budget = TimeSpan.FromSeconds(1.0 / _targetFps);
                var spent = _clock.Elapsed - started;
                var rest = budget - spent;

                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, ct);
                }
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private CameraFrame Render()
    {
        var mode = CurrentMode!;
        var width = mode.Width;
        var height = mode.Height;

        _background ??= RenderBackground(width, height);

        var pixels = new byte[_background.Length];
        Array.Copy(_background, pixels, pixels.Length);

        // Make exposure and gain affect brightness so slider changes visibly exercise the property pipeline.
        var scale = _exposure.Value / 20.0 * (_gain.Value / 2.0);
        var time = _clock.Elapsed.TotalSeconds;

        if (_pattern.Value != "gradient")
        {
            foreach (var blob in _blobs)
            {
                var x = Wrap(blob.X + blob.SpeedX * time, width);
                var y = Wrap(blob.Y + blob.SpeedY * time, height);
                DrawBlob(pixels, width, height, x, y, blob.Radius);
            }
        }

        var noise = _pattern.Value == "noise" ? 60 : 8;

        for (var i = 0; i < pixels.Length; i += 7)
        {
            pixels[i] = Clamp(pixels[i] + _random.Next(-noise, noise));
        }

        if (Math.Abs(scale - 1) > 0.01 || _invert.Value)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var value = pixels[i] * scale;
                pixels[i] = Clamp(_invert.Value ? 255 - value : value);
            }
        }

        return new CameraFrame(pixels, width, height, width, CameraPixelFormat.Mono8,
            Interlocked.Increment(ref _frameNumber), DateTimeOffset.UtcNow);
    }

    private static byte[] RenderBackground(int width, int height)
    {
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * width;

            for (var x = 0; x < width; x++)
            {
                // Smooth gradient resembling a hydrogel background in transmitted light.
                var value = 110 + 30.0 * x / width + 20.0 * y / height;
                pixels[row + x] = Clamp(value);
            }
        }

        return pixels;
    }

    private static void DrawBlob(byte[] pixels, int width, int height, double cx, double cy, double radius)
    {
        var left = Math.Max(0, (int)(cx - radius));
        var right = Math.Min(width - 1, (int)(cx + radius));
        var top = Math.Max(0, (int)(cy - radius));
        var bottom = Math.Min(height - 1, (int)(cy + radius));

        for (var y = top; y <= bottom; y++)
        {
            var row = y * width;

            for (var x = left; x <= right; x++)
            {
                var dx = (x - cx) / radius;
                var dy = (y - cy) / radius;
                var distance = dx * dx + dy * dy;

                if (distance > 1)
                {
                    continue;
                }

                // Dark center with a bright rim approximating gradient contrast at a cell boundary.
                var edge = Math.Sqrt(distance);
                var delta = edge > 0.8 ? 55 * (edge - 0.8) / 0.2 : -70 * (1 - edge);
                pixels[row + x] = Clamp(pixels[row + x] + delta);
            }
        }
    }

    private static double Wrap(double value, int size)
    {
        var wrapped = value % size;
        return wrapped < 0 ? wrapped + size : wrapped;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
