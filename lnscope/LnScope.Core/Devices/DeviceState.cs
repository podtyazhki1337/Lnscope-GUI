namespace LnScope.Core.Devices;

/// <summary>Lifecycle state of an installation device.</summary>
public enum DeviceState
{
    Disconnected,
    Connecting,
    Ready,
    Faulted
}
