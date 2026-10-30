using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using LnScope.Devices.LuigsNeumann.Transport;

namespace LnScope.Devices.LuigsNeumann.Sm10;

public interface ISm10Bus
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task<byte[]> RequestAsync(ushort command, byte[] data, CancellationToken ct = default);
}

/// <summary>One serialized request/reply stream per physical controller, shared by logical positioners.</summary>
public sealed class Sm10SerialBus(string portName, int baudRate, int timeoutMs, Func<ISm10Connection>? connectionFactory = null, bool allowLegacyChecksum = false) : ISm10Bus
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISm10Connection? _port;
    private int _clients;
    private volatile bool _faulted;
    private CancellationTokenSource _abort = new();
    private readonly HashSet<int> _continuousAxes = [];
    private DateTime _settleUntil;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_faulted && _clients > 0)
                throw new IOException("Disconnect all groups using this controller before reconnecting.");
            if (_clients == 0)
            {
                var port = connectionFactory?.Invoke() ?? new Sm10PortConnection(portName,baudRate,timeoutMs);
                try { await Task.Run(port.Open, ct); }
                catch { port.Dispose(); throw; }
                _port = port;
                _abort.Dispose(); _abort = new CancellationTokenSource();
                _continuousAxes.Clear();
                _settleUntil = default;
                _faulted = false;
            }
            _clients++;
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> RequestAsync(ushort command, byte[] data, CancellationToken ct = default)
    {
        var spec = Sm10Commands.Get(command);
        if (spec.Payload == Sm10Payload.Ambiguous) throw new NotSupportedException(spec.Help);
        spec.ValidatePayload(data);
        await _gate.WaitAsync(ct);
        try
        {
            if (_faulted || _port is null) throw new IOException("SM-10 is disconnected or faulted. Reconnect the controller.");
            if (_continuousAxes.Count > 0 && !spec.IsStop)
                throw new InvalidOperationException("Continuous movement: only Stop is permitted. Wait for standstill before other commands.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
            var token = linked.Token;
            var settling = _settleUntil - DateTime.UtcNow;
            if (!spec.IsStop && settling > TimeSpan.Zero)
                await Task.Delay(settling, token);
            // Keep the shared-port gate during the pause: stage/manipulator cannot interleave writes.
            await CommandPacing.BeforeWriteAsync(token);
            return await Task.Run(() =>
            {
                var port = _port;
                token.ThrowIfCancellationRequested();
                var request = Sm10Protocol.Encode(command, data);
                // No retries: repeating a movement after a lost ACK can move an axis twice.
                port.Write(request, 0, request.Length);
                var axes = command >= 0xA000
                    ? Enumerable.Range(1,72).Where(a => data.Length >= 10 && (data[9-(a-1)/8] & (1<<((a-1)%8))) != 0).ToArray()
                    : new[] { (int)data[0] };
                if (spec.Continuous) _continuousAxes.UnionWith(axes);
                if (spec.IsStop)
                { _continuousAxes.ExceptWith(axes); _settleUntil = DateTime.UtcNow.AddMilliseconds(160); }
                if (spec.Reply == Sm10Reply.None) return Array.Empty<byte>();
                var clock = Stopwatch.StartNew();
                byte ReadByte()
                {
                    while (clock.ElapsedMilliseconds < Math.Clamp(timeoutMs, 100, 10000))
                    {
                        token.ThrowIfCancellationRequested();
                        try { return checked((byte)port.ReadByte()); }
                        catch (TimeoutException) { }
                    }
                    throw new TimeoutException($"SM-10 did not reply to 0x{command:X4} (data {Convert.ToHexString(data)}) on {portName}. No retry was sent.");
                }
                var header = new byte[4];
                for (var i = 0; i < 4; i++) header[i] = ReadByte();
                if (header[0] != spec.ReplyMarker || header[3] != spec.ReplyLength) throw new IOException("Invalid SM-10 reply header or payload length.");
                var frame = new byte[header[3] + 6];
                header.CopyTo(frame, 0);
                for (var i = 4; i < frame.Length; i++) frame[i] = ReadByte();
                byte[] payload;
                try { payload = Sm10Protocol.Decode(command, frame, spec.ReplyMarker, allowLegacyChecksum); }
                catch (IOException ex) { throw new IOException($"{ex.Message} Command 0x{command:X4}, RX={Convert.ToHexString(frame)}. " +
                    (frame[^2] == 0xBB && frame[^1] == 0xBB ? "This controller uses the BBBB reply checksum; enable SM-10 legacy checksum compatibility in Hardware setup." : ""),ex); }
                spec.DescribeReply(payload);
                if (spec.Reply is Sm10Reply.GroupPositions or Sm10Reply.GroupStatus && !payload.AsSpan(0,4).SequenceEqual(data.AsSpan(1,4)))
                    throw new IOException("Group reply axis addresses do not match the request.");
                return payload;
            }, token);
        }
        catch
        {
            // A timeout destroys request/reply alignment. Do not consume a late reply for a new request.
            _faulted = true;
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Latch the connection and cancel queued/in-flight work. Does not itself stop a motor.</summary>
    public void AbortPending()
    {
        _faulted = true;
        _abort.Cancel();
    }

    /// <summary>Preempt reply waiting, send the documented no-ACK group stop, remain latched.
    /// No retries, no assertion that the hardware stopped. A broken link cannot guarantee a stop.</summary>
    public async Task EmergencyStopAsync(IEnumerable<int> axes)
    {
        var data = Sm10Command.BuildMask(axes);
        AbortPending();
        await _gate.WaitAsync();
        try
        {
            if (_port is null || !_port.IsOpen) throw new IOException("Cannot send Stop: port is closed.");
            var frame = Sm10Protocol.Encode(0xA0FF, data);
            await CommandPacing.BeforeWriteAsync();
            await Task.Run(() => _port.Write(frame, 0, frame.Length));
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_clients > 0 && --_clients == 0)
            {
                _port?.Dispose();
                _port = null;
            }
        }
        finally { _gate.Release(); }
    }
}
