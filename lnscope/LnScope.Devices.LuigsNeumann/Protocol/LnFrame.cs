using System.Globalization;
using System.Text;

namespace LnScope.Devices.LuigsNeumann.Protocol;

/// <summary>One LN NextLevel ASCII frame: STX ";GG;CCCC;field;field;" EOT. GG is the command group (01); CCCC is the hexadecimal command ID.</summary>
public sealed record LnFrame(int Group, int Command, IReadOnlyList<string> Fields)
{
    public const byte Stx = 0x02;
    public const byte Eot = 0x04;
    private const byte Syn = 0x16;

    public const int DefaultGroup = 0x01;

    /// <summary>Build a command using invariant number formatting.</summary>
    public static LnFrame Create(int command, params object[] fields) =>
        new(DefaultGroup, command, fields.Select(FormatField).ToArray());

    private static string FormatField(object value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    public byte[] Encode()
    {
        var text = new StringBuilder();
        text.Append(';')
            .Append(Group.ToString("X2", CultureInfo.InvariantCulture))
            .Append(';')
            .Append(Command.ToString("X4", CultureInfo.InvariantCulture))
            .Append(';');

        foreach (var field in Fields)
        {
            text.Append(field);
            text.Append(';');
        }

        var body = Encoding.ASCII.GetBytes(text.ToString());
        var frame = new byte[body.Length + 2];
        frame[0] = Stx;
        Array.Copy(body, 0, frame, 1, body.Length);
        frame[^1] = Eot;
        return frame;
    }

    /// <summary>Parse the message body between STX and EOT.</summary>
    public static LnFrame Parse(string body)
    {
        var parts = body.Split(';');

        // parts[0] and the last element are empty due to the surrounding semicolons; parts[1] is the group and parts[2] is the command.
        if (parts.Length < 4)
        {
            throw new FormatException($"Frame is too short: '{body}'");
        }

        var group = int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var command = int.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var fields = parts[3..];
        if (fields.Length > 0 && fields[^1].Length == 0)
        {
            fields = fields[..^1];
        }

        return new LnFrame(group, command, fields);
    }

    public int GetInt(int index) =>
        int.Parse(Fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture);

    public double GetDouble(int index) =>
        double.Parse(Fields[index], NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Command acknowledgment: 1 means accepted; 0 means unknown or unavailable.</summary>
    public bool IsAcknowledged => Fields.Count > 0 && Fields[^1] == "1";

    /// <summary>Switch from binary to ASCII with STX F1 (20 x SYN) EOT. The document calls this 24 bytes but lists 23; verify this discrepancy on hardware.</summary>
    public static byte[] SwitchToAsciiSequence()
    {
        var sequence = new byte[23];
        sequence[0] = Stx;
        sequence[1] = 0xF1;
        for (var i = 2; i < 22; i++)
        {
            sequence[i] = Syn;
        }
        sequence[22] = Eot;
        return sequence;
    }

    public static byte[] SwitchToBinarySequence() => [Stx, 0xF0, Eot];

    public override string ToString() =>
        $"{Command:X4}[{string.Join(';', Fields)}]";
}
