using System.IO.Ports;
using System.Runtime.CompilerServices;

namespace LnScope.Devices.LuigsNeumann.Transport;

/// <summary>Physical COM port. SM-10 settings: 115200 baud, eight data bits, one stop bit, no parity.</summary>
public sealed class SerialLnTransport : ILnTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public SerialLnTransport(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task OpenAsync(CancellationToken ct = default)
    {
        _port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = true
        };

        _port.Open();
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            if (_port is null)
                throw new InvalidOperationException("Serial port is not open.");
            await CommandPacing.BeforeWriteAsync(ct);
            await _port.BaseStream.WriteAsync(data, ct);
        }
        finally { _writes.Release(); }
    }

    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_port is null)
        {
            throw new InvalidOperationException("Serial port is not open.");
        }

        var buffer = new byte[1024];

        while (!ct.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await _port.BaseStream.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (read > 0)
            {
                yield return buffer[..read];
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
