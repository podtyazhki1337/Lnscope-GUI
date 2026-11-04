using System.Globalization;
using System.IO;
using System.Threading.Channels;

namespace GUI.Recording;

/// <summary>Asynchronous sparse event log. Device callbacks never wait for disk I/O.</summary>
internal sealed class RecordingCsvLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly RecordingJournal? _journal;
    private readonly Channel<object?[]> _rows = Channel.CreateBounded<object?[]>(new BoundedChannelOptions(8192)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<(string, string), (AxisTelemetry Axis, bool Valid)> _last = new();
    private readonly double _fps;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private bool _accepting = true;
    private bool _completed;
    private long? _videoFrame, _cameraFrame;
    private DateTimeOffset? _capture;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);
    public Task Writer { get; }

    public RecordingCsvLog(StreamWriter writer, double fps, RecordingJournal? journal)
    {
        _writer = writer; _fps = fps; _journal = journal;
        _writer.WriteLine("video_frame,camera_frame,capture_utc,capture_elapsed_ms,video_time_ms,device,axis,unit,state,c1,c2,moving,limit,position_sample_utc,position_age_ms,valid,event,event_utc,event_elapsed_ms,detail");
        Writer = Task.Run(WriteAsync);
        lock (_gate) Enqueue(new(_started, "recording_start"));
        if (_journal is not null) _journal.Emitted += OnEvent;
    }

    public void FrameAccepted(long videoFrame, long cameraFrame, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            _videoFrame = videoFrame;
            _cameraFrame = cameraFrame;
            _capture = timestamp;
            Enqueue(new(DateTimeOffset.UtcNow, "frame"));
        }
    }

    public void Positions(IReadOnlyList<AxisTelemetry> axes)
    {
        if (axes.Count == 0)
            OnEvent(new(DateTimeOffset.UtcNow, "position", new("", "", "", "no_positioners", null, null, null, "unknown", null, null)));
        else foreach (var axis in axes) OnEvent(new(DateTimeOffset.UtcNow, "position", axis));
    }

    private void OnEvent(RecordingEvent entry)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (entry.Axis is { } axis)
            {
                var valid = IsValid(axis);
                if (_last.TryGetValue((axis.Device, axis.Axis), out var old) && old.Valid == valid &&
                    old.Axis.Unit == axis.Unit && old.Axis.State == axis.State && old.Axis.C1 == axis.C1 &&
                    old.Axis.C2 == axis.C2 && old.Axis.Moving == axis.Moving && old.Axis.Limit == axis.Limit) return;
                _last[(axis.Device, axis.Axis)] = (axis, valid);
            }
            Enqueue(entry);
        }
    }

    private static bool IsValid(AxisTelemetry axis) => axis.State == "Ready" &&
        axis.C1 is { } c1 && double.IsFinite(c1) && axis.C2 is { } c2 && double.IsFinite(c2) && axis.AgeMs is >= 0 and <= 3000;

    private void Enqueue(RecordingEvent entry)
    {
        var axis = entry.Axis;
        object?[] row = [_videoFrame, _cameraFrame, _capture?.ToUniversalTime().ToString("O"),
            _capture.HasValue ? (_capture.Value-_started).TotalMilliseconds : null,
            _videoFrame.HasValue ? _videoFrame.Value*1000/_fps : null,
            axis?.Device, axis?.Axis, axis?.Unit, axis?.State,
            axis?.C1, axis?.C2, axis?.Moving, axis?.Limit, axis?.SampleUtc?.ToUniversalTime().ToString("O"),
            axis?.AgeMs, axis is null ? null : IsValid(axis), entry.Kind,
            entry.Utc.ToUniversalTime().ToString("O"), (entry.Utc-_started).TotalMilliseconds,
            entry.Detail];
        if (!_rows.Writer.TryWrite(row))
            Interlocked.CompareExchange(ref _error, "Recording event queue overflowed or closed; the CSV is incomplete.", null);
    }

    public void StopAccepting()
    {
        lock (_gate) _accepting = false;
        if (_journal is not null) _journal.Emitted -= OnEvent;
    }

    public async Task CompleteAsync(long written, long dropped)
    {
        StopAccepting();
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                Enqueue(new(DateTimeOffset.UtcNow, "recording_stop", Detail: $"written_frames={written}; dropped_frames={dropped}"));
                _rows.Writer.TryComplete();
            }
        }
        await Writer;
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var row in _rows.Reader.ReadAllAsync())
                await _writer.WriteLineAsync(string.Join(",", row.Select(Csv)));
            await _writer.FlushAsync();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref _error, error.Message, null);
            _rows.Writer.TryComplete();
        }
    }

    private static string Csv(object? value)
    {
        var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose() { StopAccepting(); _writer.Dispose(); }
}
