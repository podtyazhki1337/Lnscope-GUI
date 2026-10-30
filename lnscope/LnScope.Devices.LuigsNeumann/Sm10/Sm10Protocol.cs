using System.Buffers.Binary;
using System.IO;
namespace LnScope.Devices.LuigsNeumann.Sm10;

/// <summary>SM10 Interface description V1.0, pp. 3, 8, 13, 15, 22, 35 (printed numbering).</summary>
public static class Sm10Protocol
{
    public const ushort Position = 0x0101, Counter2 = 0x0131, Status = 0x0120;
    public const ushort AbsoluteFast = 0x0048, AbsoluteSlow = 0x0049, Stop = 0x00FF;
    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
    public static byte[] Encode(ushort command, ReadOnlySpan<byte> data, byte marker = 0x16)
    {
        // Four-axis moves explicitly use 0x15 (21) bytes, despite the overview's 20-byte limit.
        var maximum = command is >= 0xA048 and <= 0xA04B ? 21 : 20;
        if (data.Length > maximum) throw new ArgumentOutOfRangeException(nameof(data));
        var frame = new byte[data.Length + 6];
        frame[0] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), command);
        frame[3] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4 + data.Length), Crc(data));
        return frame;
    }
    public static byte[] Decode(ushort command, ReadOnlySpan<byte> frame, byte marker = 0x06, bool allowLegacyChecksum = false)
    {
        if (frame.Length < 6 || frame[0] != marker ||
            BinaryPrimitives.ReadUInt16BigEndian(frame[1..]) != command || frame.Length != frame[3] + 6)
            throw new IOException("Unexpected SM-10 reply. Check the selected protocol and reconnect.");
        var data = frame.Slice(4, frame[3]);
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(frame[(4 + data.Length)..]);
        if (Crc(data) != checksum && !(allowLegacyChecksum && checksum == 0xBBBB))
            throw new IOException("SM-10 reply failed CRC validation.");
        return data.ToArray();
    }
    public static byte[] Axis(int axis) => axis is >= 1 and <= 72
        ? [(byte)axis] : throw new ArgumentOutOfRangeException(nameof(axis), "SM-10 axis address must be 1–72.");
    public static byte[] Target(int axis, double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new byte[5];
        bytes[0] = Axis(axis)[0];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1), (float)value);
        return bytes;
    }
    public static double ReadPosition(byte[] data)
    {
        if (data.Length != 4) throw new IOException("Invalid SM-10 position payload.");
        var value = BinaryPrimitives.ReadSingleLittleEndian(data);
        return float.IsFinite(value) ? value : throw new IOException("Non-finite SM-10 position.");
    }
}
