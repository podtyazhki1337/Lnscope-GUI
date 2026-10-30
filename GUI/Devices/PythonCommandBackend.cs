using System.IO;
using System.Text.Json;
using LnScope.Devices.LuigsNeumann.Sm10;

namespace GUI.Devices;

public sealed record PythonConsoleCommand(ushort Id, string Name, string Category, string Help, string Reply,
    bool ReadOnly, bool Moves, bool IsStop, bool Continuous, bool Supported, bool MultipleAxes)
{
    public override string ToString() => $"0x{Id:X4} · {Name}";
}
public sealed record PythonConsolePacket(byte[] Data, string Frame);
public sealed record PythonConsoleResult(string Description, byte[] Data);

/// <summary>Python command logic with an independent C# authorization and wire guard.</summary>
public sealed class PythonCommandBackend : IAsyncDisposable
{
    private readonly PythonWorker _worker;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private (ushort Id, byte[] Data, bool AllowWrites)? _authorized;
    public event Action? Failed;
    public PythonCommandBackend(ISm10Bus? bus = null)
    {
        _worker = new PythonWorker(bus, validateRequest: Guard);
        _worker.Failed += () => Failed?.Invoke();
    }
    private void Guard(ushort command, byte[] payload)
    {
        var grant = _authorized ?? throw new IOException("No command authorized for this exchange.");
        if (command != grant.Id || !payload.SequenceEqual(grant.Data))
            throw new IOException("Python command differs from the selected command and parameters.");
        var spec = Sm10Commands.Get(command);
        spec.ValidatePayload(payload);
        if (!spec.ReadOnly && !spec.IsStop && !grant.AllowWrites)
            throw new IOException("Advanced writes are disabled.");
        // Consume the grant: one click may cause exactly one controller exchange.
        _authorized = null;
    }
    public async Task<PythonConsoleCommand[]> CatalogueAsync() =>
        (await _worker.CallAsync("console_catalogue", new { })).Deserialize<PythonConsoleCommand[]>()
        ?? throw new IOException("Missing Python command catalogue.");
    public async Task<PythonConsolePacket> BuildAsync(ushort command, int[] axes, double[] values)
    {
        var result = await _worker.CallAsync("console_build", new { command, axes, values });
        return new(result.GetProperty("data").GetBytesFromBase64(), result.GetProperty("frame").GetString()!);
    }
    public async Task<PythonConsoleResult> ExecuteAsync(ushort command, int[] axes, double[] values, bool allowWrites)
    {
        await _sendGate.WaitAsync();
        try
        {
            var spec = Sm10Commands.Get(command);
            if (!spec.ReadOnly && !spec.IsStop && !allowWrites)
                throw new InvalidOperationException("Enable advanced commands before sending writes or movement.");
            _authorized = (command, spec.Build(axes, values), allowWrites);
            var result = await _worker.CallAsync("console_execute", new { command, axes, values, allow_writes = allowWrites });
            return new(result.GetProperty("description").GetString()!, result.GetProperty("data").GetBytesFromBase64());
        }
        finally { _authorized = null; _sendGate.Release(); }
    }
    public ValueTask DisposeAsync() => _worker.DisposeAsync();
}
