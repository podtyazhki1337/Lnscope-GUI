using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GUI.Mvvm;
using GUI.Recording;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;

namespace GUI.ViewModels;

public sealed class CameraViewModel : ViewModelBase
{
    private readonly ICamera _camera;
    private readonly CameraOptions _options;
    private readonly VideoRecorder _recorder;
    private readonly RecordingOptions _recording;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _counterTimer;

    private WriteableBitmap? _image;
    private VideoMode? _selectedMode;
    private DeviceState _state;
    private double _measuredFps;
    private long _droppedCounter;
    private long _droppedFrames;
    private double _zoom;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private bool _renderPending;
    private volatile bool _recordingRequested;
    private readonly object _recordingGate = new();

    public CameraViewModel(ICamera camera, CameraOptions options, RecordingOptions recording,
        Dispatcher dispatcher, Action<string> log, Func<IReadOnlyList<AxisTelemetry>>? telemetry = null)
    {
        _camera = camera;
        _options = options;
        _recording = recording;
        _dispatcher = dispatcher;
        _log = log;
        _state = camera.State;
        _zoom = options.Zoom > 0 ? options.Zoom : 0.5;
        _flipHorizontal = options.FlipHorizontal;
        _flipVertical = options.FlipVertical;

        _recorder = new VideoRecorder(recording, log, telemetry);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => State is DeviceState.Disconnected or DeviceState.Faulted)
            { ErrorHandler = HandleError };
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => State is DeviceState.Ready or DeviceState.Faulted)
            { ErrorHandler = HandleError };
        StartCommand = new AsyncRelayCommand(StartAsync, () => State == DeviceState.Ready && !IsStreaming)
            { ErrorHandler = HandleError };
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsStreaming) { ErrorHandler = HandleError };
        RecordCommand = new AsyncRelayCommand(StartRecordingAsync, () => State == DeviceState.Ready && !IsRecording)
            { ErrorHandler = HandleError };
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording)
            { ErrorHandler = HandleError };

        camera.StateChanged += OnStateChanged;
        camera.FrameReady += OnFrameReady;

        // Update counters on a timer rather than per frame to avoid excessive UI refreshes.
        _counterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => RefreshCounters(), dispatcher);
        _counterTimer.Start();
    }

    public string Id => _camera.Id;

    private void OnStateChanged(object? sender, DeviceState state)
    {
        if (_dispatcher.CheckAccess()) State = state;
        else _dispatcher.InvokeAsync(() => State = state);
    }

    public async Task ShutdownAsync()
    {
        _counterTimer.Stop();
        await StopRecordingAsync();
        await _camera.StopAsync();
        _camera.FrameReady -= OnFrameReady;
        _camera.StateChanged -= OnStateChanged;
        try { await _recorder.DisposeAsync(); }
        finally { await _camera.DisposeAsync(); }
    }

    public ObservableCollection<VideoMode> Modes { get; } = [];

    public ObservableCollection<DeviceProperty> Properties { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RecordCommand { get; }

    public AsyncRelayCommand StopRecordingCommand { get; }

    public IReadOnlyList<double> ZoomLevels { get; } = [0.25, 0.5, 1.0];

    public WriteableBitmap? Image
    {
        get => _image;
        private set => SetField(ref _image, value);
    }

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (!SetField(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsConnected));
            RefreshCommands();
        }
    }

    public bool IsConnected => State == DeviceState.Ready;

    public bool IsStreaming => _camera.IsStreaming;

    public bool IsRecording => _recorder.IsActive || _recordingRequested;

    public string RecordingStatus => _recordingRequested && !_recorder.IsRecording
        ? "starting…"
        : _recorder.Status;

    public string OutputDirectory
    {
        get => _recording.OutputDirectory;
        set
        {
            if (_recording.OutputDirectory == value)
            {
                return;
            }

            _recording.OutputDirectory = value;
            OnPropertyChanged();
        }
    }

    public double MeasuredFps
    {
        get => _measuredFps;
        private set => SetField(ref _measuredFps, value);
    }

    public long DroppedFrames
    {
        get => _droppedFrames;
        private set => SetField(ref _droppedFrames, value);
    }

    /// <summary>Apply mirroring to both display and recording so the saved image matches the preview.</summary>
    public bool FlipHorizontal
    {
        get => _flipHorizontal;
        set
        {
            if (!SetField(ref _flipHorizontal, value))
            {
                return;
            }

            _options.FlipHorizontal = value;
            _log($"{Id}: flip horizontal {(value ? "on" : "off")}");
        }
    }

    public bool FlipVertical
    {
        get => _flipVertical;
        set
        {
            if (!SetField(ref _flipVertical, value))
            {
                return;
            }

            _options.FlipVertical = value;
            _log($"{Id}: flip vertical {(value ? "on" : "off")}");
        }
    }

    /// <summary>Display scale: 1.0 maps one image pixel to one screen pixel.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!SetField(ref _zoom, value))
            {
                return;
            }

            _options.Zoom = value;
            RaiseDisplaySize();
        }
    }

    public double DisplayWidth => (CurrentMode?.Width ?? 0) * Zoom;

    public double DisplayHeight => (CurrentMode?.Height ?? 0) * Zoom;

    public VideoMode? CurrentMode => _camera.CurrentMode;

    public double TargetFps
    {
        get => _camera.TargetFps;
        set
        {
            if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value), "FPS must be zero or a positive number.");
            if (Math.Abs(_camera.TargetFps - value) < double.Epsilon)
            {
                return;
            }

            _camera.TargetFps = value;
            _options.TargetFps = value;
            _log($"{Id}: frame rate limit {(value == 0 ? "automatic" : $"{value:0.###} fps")}" +
                (_camera is GUI.Devices.DirectShowCamera && _camera.IsStreaming ? " — applies on next Start live" : ""));
            OnPropertyChanged();
        }
    }

    public VideoMode? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetField(ref _selectedMode, value) || value is null || !IsConnected)
            {
                return;
            }

            _ = ChangeModeAsync(value);
        }
    }

    /// <summary>Choose a scale that fits the entire frame within the available size.</summary>
    public void FitTo(double availableWidth, double availableHeight)
    {
        if (CurrentMode is null)
        {
            return;
        }

        var scale = Math.Min(availableWidth / CurrentMode.Width, availableHeight / CurrentMode.Height);
        Zoom = Math.Clamp(scale, 0.05, 1.0);
    }

    private async Task ConnectAsync()
    {
        await _camera.ConnectAsync();

        Modes.Clear();

        foreach (var mode in _camera.SupportedModes)
        {
            Modes.Add(mode);
        }

        Properties.Clear();

        foreach (var property in _camera.Properties)
        {
            Properties.Add(property);
        }

        SetField(ref _selectedMode, _camera.CurrentMode, nameof(SelectedMode));
        OnPropertyChanged(nameof(TargetFps));
        RaiseDisplaySize();

        _log($"{Id}: connected, {Modes.Count} modes, current {_camera.CurrentMode}");
    }

    private async Task DisconnectAsync()
    {
        await StopRecordingAsync();
        await _camera.DisconnectAsync();
        Image = null;
        RefreshCommands();
        _log($"{Id}: disconnected");
    }

    private async Task StartAsync()
    {
        await _camera.StartAsync();
        RefreshCommands();
        _log($"{Id}: stream started");
    }

    private async Task StopAsync()
    {
        await StopRecordingAsync();
        await _camera.StopAsync();
        RefreshCommands();
        _log($"{Id}: stream stopped");
    }

    private async Task StartRecordingAsync()
    {
        if (State != DeviceState.Ready || IsRecording)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LNscope");
        }

        lock (_recordingGate) _recordingRequested = true;
        RefreshCommands();

        try
        {
            if (!IsStreaming)
            {
                await _camera.StartAsync();
                OnPropertyChanged(nameof(IsStreaming));
            }
        }
        catch
        {
            await StopRecordingAsync();
            throw;
        }

        _log($"{Id}: recording requested");
    }

    private async Task StopRecordingAsync()
    {
        Task finishing;
        lock (_recordingGate)
        {
            _recordingRequested = false;
            finishing = _recorder.StopAsync();
        }
        RefreshCommands();
        await finishing;
        RefreshCommands();
    }
    private async Task ChangeModeAsync(VideoMode mode)
    {
        try
        {
            await StopRecordingAsync();
            await _camera.SetModeAsync(mode.Index);
            _options.ModeIndex = mode.Index;
            RaiseDisplaySize();
            OnPropertyChanged(nameof(TargetFps));
            _log($"{Id}: mode {mode}");
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
        finally
        {
            RefreshCommands();
        }
    }

    /// <summary>Frames arrive on the camera thread. Mirror once and share the result with display and recording. A slow UI drops preview frames only, not recording frames.</summary>
    private void OnFrameReady(object? sender, CameraFrame frame)
    {
        lock (_recordingGate)
        {
            // Native cameras may deliver fewer frames than the requested cap (e.g. long exposure).
            // Wait for the first measured interval instead of encoding a 3-fps stream as 15 fps.
            if (_recordingRequested && (_camera is not GUI.Devices.KernCamera || _camera.MeasuredFps > 0))
            {
                var fps = _camera is GUI.Devices.DirectShowCamera directShow ? directShow.ActiveFps :
                    _camera is GUI.Devices.KernCamera ? _camera.MeasuredFps :
                    TargetFps > 0 ? TargetFps : (_camera.MeasuredFps > 0 ? _camera.MeasuredFps : 25);
                _recordingRequested = false;
                _recorder.Start(Id, frame.Width, frame.Height, fps, frame.Format);
            }
        }
        var processed = Flip(frame);

        _recorder.Push(processed);

        if (_renderPending)
        {
            Interlocked.Increment(ref _droppedCounter);
            return;
        }

        _renderPending = true;

        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                Draw(processed);
            }
            finally
            {
                _renderPending = false;
            }
        }, DispatcherPriority.Render);
    }

    private CameraFrame Flip(CameraFrame frame)
    {
        if (!_flipHorizontal && !_flipVertical)
        {
            return frame;
        }

        var bytesPerPixel = frame.Stride / frame.Width;
        var output = new byte[frame.Data.Length];

        for (var y = 0; y < frame.Height; y++)
        {
            var sourceRow = (_flipVertical ? frame.Height - 1 - y : y) * frame.Stride;
            var targetRow = y * frame.Stride;

            if (!_flipHorizontal)
            {
                // For vertical-only mirroring, reorder entire rows without iterating over individual pixels.
                Array.Copy(frame.Data, sourceRow, output, targetRow, frame.Stride);
                continue;
            }

            for (var x = 0; x < frame.Width; x++)
            {
                var source = sourceRow + (frame.Width - 1 - x) * bytesPerPixel;
                var target = targetRow + x * bytesPerPixel;

                for (var b = 0; b < bytesPerPixel; b++)
                {
                    output[target + b] = frame.Data[source + b];
                }
            }
        }

        return frame with { Data = output };
    }

    private void Draw(CameraFrame frame)
    {
        var format = frame.Format switch
        {
            CameraPixelFormat.Mono8 => PixelFormats.Gray8,
            CameraPixelFormat.Mono16 => PixelFormats.Gray16,
            CameraPixelFormat.Bgr24 => PixelFormats.Bgr24,
            CameraPixelFormat.Bgra32 => PixelFormats.Bgra32,
            _ => PixelFormats.Gray8
        };

        if (Image is null || Image.PixelWidth != frame.Width || Image.PixelHeight != frame.Height ||
            Image.Format != format)
        {
            Image = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format, null);
        }

        Image.WritePixels(
            new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Data,
            frame.Stride,
            0);
    }

    private void RaiseDisplaySize()
    {
        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    private void RefreshCounters()
    {
        MeasuredFps = _camera.MeasuredFps;
        DroppedFrames = Interlocked.Read(ref _droppedCounter);
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingStatus));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingStatus));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        StopRecordingCommand.RaiseCanExecuteChanged();
    }

    private void HandleError(Exception exception) => _log($"{Id}: ERROR — {exception.Message}");
}
