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

    private readonly AxisTelemetry[] _axes;
    private readonly string[] _devices;

    public RecordingCsvLog(StreamWriter writer, double fps, RecordingJournal? journal,
        Func<IReadOnlyList<AxisTelemetry>> snapshot)
    {
        _writer = writer;
        _fps = fps;
        _journal = journal;
        lock (_gate)
        {
            if (_journal is not null) _journal.Emitted += OnEvent;
            try
            {
                _axes = snapshot().ToArray();
                _devices = _axes.Select(a => a.Device).Distinct().ToArray();
                var header = new List<string> { "event", "elapsed_ms", "event_utc", "video_frame", "camera_frame",
                    "capture_utc", "video_time_ms" };
                header.AddRange(_devices.Select(d => $"{d}_port"));
                header.AddRange(_axes.Select(a => $"{a.Device}_{a.Axis}_{a.Unit}"));
                foreach (var a in _axes)
                {
                    _last[(a.Device, a.Axis)] = (a, IsValid(a));
                    var prefix = $"{a.Device}_{a.Axis}_";
                    header.AddRange(new[] { "reference_" + a.Unit, "state", "moving", "limit", "sample_utc", "valid" }
                        .Select(field => prefix + field));
                }
                header.Add("detail");
                _writer.WriteLine(string.Join(",", header.Select(Csv)));
                Enqueue(new(_started, "initial", Detail: _axes.Length == 0 ? "no_positioners" : "start"));
            }
            catch
            {
                if (_journal is not null) _journal.Emitted -= OnEvent;
                throw;
            }
        }
        Writer = Task.Run(WriteAsync);
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
        foreach (var axis in axes) OnEvent(new(DateTimeOffset.UtcNow, "position", axis));
    }

    private void OnEvent(RecordingEvent entry)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (entry.Axis is { } axis)
            {
                var valid = IsValid(axis);
                var key = (axis.Device, axis.Axis);
                if (!_last.TryGetValue(key, out var old)) return;
                _last[key] = (axis, valid);
                if (old.Valid == valid &&
                    old.Axis.Unit == axis.Unit && old.Axis.State == axis.State && old.Axis.C1 == axis.C1 &&
                    old.Axis.C2 == axis.C2 && old.Axis.Moving == axis.Moving && old.Axis.Limit == axis.Limit) return;
            }
            Enqueue(entry);
        }
    }

    private static bool IsValid(AxisTelemetry axis) => axis.State == "Ready" &&
        axis.C1 is { } c1 && double.IsFinite(c1) && axis.C2 is { } c2 && double.IsFinite(c2) && axis.AgeMs is >= 0 and <= 3000;

    private void Enqueue(RecordingEvent entry)
    {
        var row = new List<object?> { entry.Kind, (entry.Utc - _started).TotalMilliseconds,
            entry.Utc.ToUniversalTime().ToString("O"), _videoFrame, _cameraFrame,
            _capture?.ToUniversalTime().ToString("O"), _videoFrame * 1000.0 / _fps };
        foreach (var device in _devices)
            row.Add(_journal?.Ports.GetValueOrDefault(device) ?? "unknown");
        foreach (var axis in _axes)
            row.Add(_last[(axis.Device, axis.Axis)].Axis.C2);
        foreach (var axis in _axes)
        {
            var (value, valid) = _last[(axis.Device, axis.Axis)];
            row.AddRange([value.C1, value.State, value.Moving, value.Limit,
                value.SampleUtc?.ToUniversalTime().ToString("O"), valid]);
        }
        row.Add(entry.Axis is { } changed ? $"{changed.Device}.{changed.Axis}" :
            string.IsNullOrEmpty(entry.Detail) ? "-" : entry.Detail);
        if (!_rows.Writer.TryWrite(row.ToArray()))
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
        var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "NA";
        if (text.Length == 0) return "NA";
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose() { StopAccepting(); _writer.Dispose(); }
}
