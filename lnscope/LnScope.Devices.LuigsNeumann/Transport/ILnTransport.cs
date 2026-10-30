namespace LnScope.Devices.LuigsNeumann.Transport;

/// <summary>Byte transport to a controller. The driver is independent of whether the transport is physical or simulated, allowing hardware-free protocol tests.</summary>
public interface ILnTransport : IAsyncDisposable
{
    Task OpenAsync(CancellationToken ct = default);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Continuous stream of received bytes in arbitrarily sized chunks.</summary>
    IAsyncEnumerable<byte[]> ReadAsync(CancellationToken ct = default);
}
