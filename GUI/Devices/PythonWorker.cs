using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LnScope.Devices.LuigsNeumann.Sm10;

namespace GUI.Devices;

/// <summary>Private, serialized JSON pipe. No system Python, network listener or shell.</summary>
public sealed class PythonWorker : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISm10Bus? _bus;
    private readonly TimeSpan _timeout;
    private readonly int _maxResponseLength;
    private readonly Action<ushort, byte[]>? _validateRequest;
    private Process? _process;
    private int _sequence;
    private volatile bool _closed, _faulted;
    public event Action? Failed;
    internal int ProcessId => _process?.Id ?? throw new InvalidOperationException("Worker has not started.");
    public PythonWorker(ISm10Bus? bus, int timeoutMs = 2000, Action<ushort, byte[]>? validateRequest = null, int maxResponseLength = 65536)
    {
        _bus = bus;
        _validateRequest = validateRequest;
        _maxResponseLength = maxResponseLength;
        // One position read has three exchanges; also allow a shared controller's polling.
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp((long)timeoutMs * 6 + 2000, 5000, 30000));
    }

    private void Start()
    {
        if (_closed || _faulted) throw new IOException("Python backend stopped. Reconnect the device.");
        if (_process is not null) return;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ThirdParty", "python", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-u"); start.ArgumentList.Add("-X"); start.ArgumentList.Add("utf8");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PythonBackend", "backend.py"));
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.Exited += (_, _) => { if (!_closed) Fault(); };
        _process.Start();
        // Drain stderr continuously so an unexpected traceback cannot block the worker.
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }

    private void Fault()
    {
        if (_closed || _faulted) return;
        _faulted = true;
        Failed?.Invoke();
    }

    public async Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Start();
            var process = _process!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);
            var token = deadline.Token;
            int id = ++_sequence;
            await WriteAsync(new { id, method, args }, token).ConfigureAwait(false);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)
                    ?? throw new IOException("Python backend exited unexpectedly.");
                if (line.Length > _maxResponseLength) throw new IOException("Oversized Python response.");
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement;
                if (message.GetProperty("id").GetInt32() != id) throw new IOException("Python response ID mismatch.");
                switch (message.GetProperty("type").GetString())
                {
                    case "result": return message.GetProperty("result").Clone();
                    case "error":
                        if (message.GetProperty("validation").GetBoolean())
                            throw new BackendValidationException(message.GetProperty("error").GetString());
                        throw new IOException(message.GetProperty("error").GetString());
                    case "io":
                        if (_bus is null) throw new IOException("No hardware transport for this worker.");
                        // Bus validates command, payload, ACK policy and response CRC independently.
                        var command = message.GetProperty("command").GetUInt16();
                        var payload = message.GetProperty("data").GetBytesFromBase64();
                        _validateRequest?.Invoke(command, payload);
                        var data = await _bus.RequestAsync(command, payload, token).ConfigureAwait(false);
                        await WriteAsync(new { id, type = "io_result", data }, token).ConfigureAwait(false);
                        break;
                    default: throw new IOException("Unknown Python message type.");
                }
            }
        }
        catch (BackendValidationException) { throw; }
        catch { Fault(); throw; }
        finally { _gate.Release(); }
    }

    private Task WriteAsync(object message, CancellationToken ct) =>
        _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        // Kill first: this also releases a blocked stdout reader before taking the gate.
        if (_process is { } process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _process?.Dispose(); _process = null; }
        finally { _gate.Release(); }
    }
}

public sealed class BackendValidationException(string? message) : InvalidOperationException(message);
