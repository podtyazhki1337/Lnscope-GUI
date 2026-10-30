using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GUI.Devices;

/// <summary>Generic duplex pipe: Python can await hardware while receiving Stop and feedback.</summary>
internal sealed class PythonMotionRpc : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<string> _writes = Channel.CreateBounded<string>(256);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, JsonElement, Task<object?>> _hardware;
    private readonly Action<string, JsonElement> _event;
    private readonly Action<Exception> _failure;
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly Task _reader, _writer;
    private int _next, _failed, _disposed;
    internal int ProcessId => _process.Id;
    internal bool Failed => Volatile.Read(ref _failed) != 0;

    public PythonMotionRpc(Func<string, JsonElement, Task<object?>> hardware, Action<string, JsonElement> notification,
        Action<Exception> failure)
    {
        _hardware = hardware; _event = notification; _failure = failure;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ThirdParty", "python", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[] { "-I", "-u", "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "PythonBackend", "motion_server.py") })
            start.ArgumentList.Add(argument);
        _process = new Process { StartInfo = start };
        _process.Start();
        _process.ErrorDataReceived += (sender, e) => { if (e.Data is { } line) { _errors.Enqueue(line); while (_errors.Count > 20) _errors.TryDequeue(out _); } };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadAsync); _writer = Task.Run(WriteAsync);
    }

    public Task<JsonElement> CallAsync(string method, object args, TimeSpan? timeout = null)
    {
        if (Failed || Volatile.Read(ref _disposed) != 0) return Task.FromException<JsonElement>(new IOException("Python motion process is unavailable."));
        int id = Interlocked.Increment(ref _next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        Send(new { type = "call", id, method, args });
        return AwaitAsync(id, completion.Task, timeout ?? TimeSpan.FromSeconds(40));
    }
    private async Task<JsonElement> AwaitAsync(int id, Task<JsonElement> task, TimeSpan timeout)
    {
        try { return await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException error) { Fail(error); throw; }
        finally { _pending.TryRemove(id, out _); }
    }
    private void Send(object message)
    {
        if (!_writes.Writer.TryWrite(JsonSerializer.Serialize(message))) Fail(new IOException("Python IPC write queue is full."));
    }
    private async Task WriteAsync()
    {
        try
        {
            await foreach (var line in _writes.Reader.ReadAllAsync(_lifetime.Token))
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) Fail(error); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false)
                    ?? throw new IOException("Python exited. " + string.Join("\n", _errors));
                if (line.Length > 262144) throw new IOException("Oversized motion response.");
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                var type = message.GetProperty("type").GetString();
                if (type == "event") _event(message.GetProperty("name").GetString()!, message.GetProperty("data").Clone());
                else if (type == "io") _ = HardwareAsync(message.Clone());
                else if (type is "result" or "error")
                {
                    if (!_pending.TryGetValue(message.GetProperty("id").GetInt32(), out var reply)) continue;
                    if (type == "result") reply.TrySetResult(message.GetProperty("result").Clone());
                    else if (message.GetProperty("cancelled").GetBoolean()) reply.TrySetException(new OperationCanceledException(message.GetProperty("error").GetString()));
                    else reply.TrySetException(new BackendValidationException(message.GetProperty("error").GetString()));
                }
                else throw new IOException("Unknown motion IPC message.");
            }
        }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) Fail(error); }
    }
    private async Task HardwareAsync(JsonElement message)
    {
        var id = message.GetProperty("id").GetInt32();
        try
        {
            var result = await _hardware(message.GetProperty("op").GetString()!, message.GetProperty("args")).ConfigureAwait(false);
            if (!Failed && !_lifetime.IsCancellationRequested) Send(new { type = "io_result", id, result });
        }
        catch (Exception error)
        {
            if (!Failed && !_lifetime.IsCancellationRequested) Send(new { type = "io_result", id, error = error.Message });
        }
    }
    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("Python motion backend failed: " + error.Message, error));
        _failure(error);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _writes.Writer.TryComplete();
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("Python motion process closed."));
        try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { }
        await Task.WhenAll(_reader, _writer).ConfigureAwait(false);
        _process.Dispose(); _lifetime.Dispose();
    }
}
