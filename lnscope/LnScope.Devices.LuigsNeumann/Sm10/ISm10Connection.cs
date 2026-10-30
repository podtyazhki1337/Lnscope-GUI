using System.IO.Ports;

namespace LnScope.Devices.LuigsNeumann.Sm10;

/// <summary>Byte connection seam for hardware-free protocol and cancellation tests.</summary>
public interface ISm10Connection : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Write(byte[] buffer, int offset, int count);
    int ReadByte();
}

internal sealed class Sm10PortConnection(string name, int baudRate, int timeoutMs) : ISm10Connection
{
    private readonly SerialPort _port = new(name, baudRate, Parity.None, 8, StopBits.One)
    { ReadTimeout = 50, WriteTimeout = Math.Clamp(timeoutMs,100,10000), Handshake = Handshake.None };
    public bool IsOpen => _port.IsOpen;
    public void Open() => _port.Open();
    public void Write(byte[] buffer,int offset,int count) => _port.Write(buffer,offset,count);
    public int ReadByte() => _port.ReadByte();
    public void Dispose() => _port.Dispose();
}
