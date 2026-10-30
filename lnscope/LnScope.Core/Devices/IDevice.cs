namespace LnScope.Core.Devices;

/// <summary>Common contract for positioners, stages, cameras, pressure controllers and piezo devices. The GUI depends on this abstraction.</summary>
public interface IDevice : IAsyncDisposable
{
    /// <summary>Device ID supplied by configuration.</summary>
    string Id { get; }

    DeviceState State { get; }

    event EventHandler<DeviceState>? StateChanged;

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);
}
