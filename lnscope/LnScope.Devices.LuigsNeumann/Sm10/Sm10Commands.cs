using System.Buffers.Binary;
using System.Globalization;

namespace LnScope.Devices.LuigsNeumann.Sm10;

public enum Sm10Payload { Axis, Byte, SignedByte, UInt16, Float, Counter2, Mask, MaskByte, MaskTwoBytes, MaskStep, FourPositions, FourAxes, Ambiguous }
public enum Sm10Reply { None, Ack, Byte, UInt16, Float, Status, GroupPositions, GroupStatus }
public sealed record Sm10Command(ushort Id, string Name, Sm10Payload Payload, Sm10Reply Reply,
    bool ReadOnly, bool Moves, double Minimum = 0, double Maximum = 15)
{
    public bool IsStop => Id is 0x00FF or 0xA0FF;
    public bool Continuous => Id is >= 0x0012 and <= 0x0015 or >= 0xA012 and <= 0xA015;
    public int ReplyLength => Reply switch { Sm10Reply.Ack => 0, Sm10Reply.Byte => 1, Sm10Reply.UInt16 => 2,
        Sm10Reply.Float => 4, Sm10Reply.Status => 8, Sm10Reply.GroupPositions or Sm10Reply.GroupStatus => 20, _ => 0 };
    public byte ReplyMarker => Reply is Sm10Reply.GroupPositions or Sm10Reply.GroupStatus ? (byte)0x16 : (byte)0x06;
    public override string ToString() => $"0x{Id:X4} · {Name}";
    public string Help => Payload switch
    {
        Sm10Payload.Axis or Sm10Payload.Counter2 => "One axis (1–72); no values.",
        Sm10Payload.Mask => "Axis list (1–72), comma-separated; no values. No reply expected.",
        Sm10Payload.FourAxes => "Exactly four distinct axes in one controller (1–18, 19–36, 37–54 or 55–72); no values.",
        Sm10Payload.FourPositions => "Exactly four axes in one controller; four positions/distances in µm. No reply expected.",
        Sm10Payload.MaskStep => "Axis list; values: speed (0–15), distance (µm). No reply expected.",
        Sm10Payload.MaskTwoBytes => "Axis list; values: saved position (1–5), speed (0–15). No reply expected.",
        Sm10Payload.Ambiguous => "Unavailable: both manuals disagree internally on trackball payload size (1 or 2 step bytes). Firmware specification required.",
        _ => $"{(Payload == Sm10Payload.MaskByte ? "Axis list" : "One axis")}; one value, {Minimum} … {Maximum}. " +
            (Payload == Sm10Payload.Float ? "Units: µm." : "Integer value.")
    };

    public byte[] Build(IReadOnlyList<int> axes, IReadOnlyList<double> values)
    {
        if (Payload == Sm10Payload.Ambiguous) throw new NotSupportedException(Help);
        if (axes.Count == 0 || axes.Distinct().Count() != axes.Count) throw new ArgumentException("Select distinct axes.");
        foreach (var axis in axes) Sm10Protocol.Axis(axis);
        bool group4 = Payload is Sm10Payload.FourAxes or Sm10Payload.FourPositions;
        bool mask = Payload is Sm10Payload.Mask or Sm10Payload.MaskByte or Sm10Payload.MaskTwoBytes or Sm10Payload.MaskStep;
        if (group4 && (axes.Count != 4 || axes.Select(a => (a - 1) / 18).Distinct().Count() != 1))
            throw new ArgumentException("Four-axis commands require four distinct axes in the same controller.");
        if (!group4 && !mask && axes.Count != 1) throw new ArgumentException("This command accepts one axis.");
        int count = Payload switch { Sm10Payload.Byte or Sm10Payload.SignedByte or Sm10Payload.UInt16 or Sm10Payload.Float or Sm10Payload.MaskByte => 1,
            Sm10Payload.MaskTwoBytes or Sm10Payload.MaskStep => 2, Sm10Payload.FourPositions => 4, _ => 0 };
        if (values.Count != count || values.Any(v => !double.IsFinite(v))) throw new ArgumentException($"Expected {count} finite value(s).");
        var data = new List<byte>();
        if (mask) data.AddRange(BuildMask(axes));
        else if (group4) { data.Add(0xA0); data.AddRange(axes.Select(a => (byte)a)); }
        else data.Add((byte)axes[0]);
        byte Integer(double v, double min, double max)
        {
            if (v != Math.Truncate(v) || v < min || v > max) throw new ArgumentOutOfRangeException(nameof(values), $"Integer must be {min} … {max}.");
            return unchecked((byte)(int)v);
        }
        void Float(double v)
        {
            if (Math.Abs(v) > float.MaxValue) throw new ArgumentOutOfRangeException(nameof(values));
            var bytes = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)v); data.AddRange(bytes);
        }
        switch (Payload)
        {
            case Sm10Payload.Counter2: data.Add(2); break;
            case Sm10Payload.Byte: case Sm10Payload.SignedByte: case Sm10Payload.MaskByte:
                data.Add(Integer(values[0], Minimum, Maximum)); break;
            case Sm10Payload.UInt16:
                if (values[0] != Math.Truncate(values[0]) || values[0] < Minimum || values[0] > Maximum) throw new ArgumentOutOfRangeException(nameof(values));
                var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)values[0]); data.AddRange(bytes); break;
            case Sm10Payload.Float:
                if (values[0] < Minimum || values[0] > Maximum) throw new ArgumentOutOfRangeException(nameof(values));
                Float(values[0]); break;
            case Sm10Payload.MaskTwoBytes: data.Add(Integer(values[0], 1, 5)); data.Add(Integer(values[1], 0, 15)); break;
            case Sm10Payload.MaskStep:
                data.Add(Integer(values[0], 0, 15));
                if (values[1] < 0) throw new ArgumentOutOfRangeException(nameof(values));
                Float(values[1]); break;
            case Sm10Payload.FourPositions: foreach (var v in values) Float(v); break;
        }
        return data.ToArray();
    }

    public static byte[] BuildMask(IEnumerable<int> axes)
    {
        var data = new byte[10]; data[0] = 0xA0;
        var found = false;
        foreach (var axis in axes) { Sm10Protocol.Axis(axis); data[9 - (axis - 1) / 8] |= (byte)(1 << ((axis - 1) % 8)); found = true; }
        return found ? data : throw new ArgumentException("Select axes.");
    }
    public void ValidatePayload(byte[] data)
    {
        var mask = Payload is Sm10Payload.Mask or Sm10Payload.MaskByte or Sm10Payload.MaskTwoBytes or Sm10Payload.MaskStep;
        var four = Payload is Sm10Payload.FourAxes or Sm10Payload.FourPositions;
        var prefix = mask ? 10 : four ? 5 : 1;
        var extra = Payload switch { Sm10Payload.Byte or Sm10Payload.SignedByte or Sm10Payload.Counter2 or Sm10Payload.MaskByte => 1,
            Sm10Payload.UInt16 or Sm10Payload.MaskTwoBytes => 2, Sm10Payload.Float => 4, Sm10Payload.MaskStep => 5, Sm10Payload.FourPositions => 16, _ => 0 };
        if (data.Length != prefix + extra || ((mask || four) && data[0] != 0xA0)) throw new ArgumentException("Invalid command payload layout.");
        var axes = mask ? Enumerable.Range(1,72).Where(a => (data[9-(a-1)/8] & (1<<((a-1)%8))) != 0).ToArray()
            : four ? data.Skip(1).Take(4).Select(b=>(int)b).ToArray() : [(int)data[0]];
        double[] values = Payload switch
        {
            Sm10Payload.Byte or Sm10Payload.MaskByte => [data[prefix]],
            Sm10Payload.SignedByte => [unchecked((sbyte)data[prefix])],
            Sm10Payload.UInt16 => [BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(prefix))],
            Sm10Payload.Float => [BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(prefix))],
            Sm10Payload.MaskTwoBytes => [data[prefix],data[prefix+1]],
            Sm10Payload.MaskStep => [data[prefix],BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(prefix+1))],
            Sm10Payload.FourPositions => Enumerable.Range(0,4).Select(i=>(double)BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(prefix+i*4))).ToArray(),
            _ => []
        };
        if (!Build(axes,values).SequenceEqual(data)) throw new ArgumentException("Invalid payload values.");
    }
    public string DescribeReply(byte[] data)
    {
        if (Reply == Sm10Reply.None) return "Sent; protocol defines no acknowledgment. Execution is not confirmed.";
        if (data.Length != ReplyLength) throw new System.IO.IOException($"0x{Id:X4}: expected {ReplyLength} reply bytes, received {data.Length}.");
        string F(ReadOnlySpan<byte> b) { var v = BinaryPrimitives.ReadSingleLittleEndian(b); if (!float.IsFinite(v)) throw new System.IO.IOException("Non-finite position."); return v.ToString("G9", CultureInfo.InvariantCulture); }
        return Reply switch
        {
            Sm10Reply.Ack => "ACK (accepted; movement may still be in progress)",
            Sm10Reply.Byte => $"Value: {data[0]} (0x{data[0]:X2})",
            Sm10Reply.UInt16 => $"Value: {BinaryPrimitives.ReadUInt16LittleEndian(data)}",
            Sm10Reply.Float => $"Position: {F(data)} µm",
            Sm10Reply.Status => $"Limits={data[0]}, power={data[1]}, home={data[2]}, step resolution={data[5]}, moving={data[6]}",
            Sm10Reply.GroupPositions => string.Join("; ", Enumerable.Range(0, 4).Select(i => $"axis {data[i]}: {F(data.AsSpan(4 + i * 4, 4))} µm")),
            Sm10Reply.GroupStatus => string.Join("; ", Enumerable.Range(0, 4).Select(i => $"axis {data[i]}: limits={data[4+i*4]}, power={data[5+i*4]}, moving={data[6+i*4]}, resolution={data[7+i*4]}")),
            _ => Convert.ToHexString(data)
        };
    }
}

/// <summary>Complete command index from SM10 Interface description V1.0, pp.33–34.
/// German original resolves the single slow-speed reply to one byte and main status to eight bytes.</summary>
public static class Sm10Commands
{
    public static IReadOnlyList<Sm10Command> All { get; } = Create();
    public static Sm10Command Get(ushort id) => All.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException($"Undocumented command 0x{id:X4}.");
    private static List<Sm10Command> Create()
    {
        var list = new List<Sm10Command>();
        void Add(ushort id, string name, Sm10Payload p = Sm10Payload.Axis, Sm10Reply r = Sm10Reply.Ack, bool read = false, bool move = false, double min = 0, double max = 15)
            => list.Add(new(id, name, p, r, read, move, min, max));
        foreach (var (id,name) in new (ushort,string)[] { (0x12,"Fast positive"),(0x13,"Fast negative"),(0x14,"Slow positive"),(0x15,"Slow negative"),
            (0x22,"Home return"),(0x24,"Go to zero"),(0x104,"Home"),(0x140,"Step increment"),(0x141,"Step decrement") }) Add(id,name,move:true);
        foreach (var (id,name) in new (ushort,string)[] { (0x34,"Power off"),(0x35,"Power on"),(0xF0,"Zero position counter"),(0xFF,"Stop"),(0x13F,"Home abort"),(0x42F,"Slow ramp off"),(0x430,"Slow ramp on") }) Add(id,name);
        Add(0x132,"Zero counter 2",Sm10Payload.Counter2);
        foreach (var (id,name) in new (ushort,string)[] { (0x3A,"Set ramp length"),(0x134,"Set fast movement speed"),(0x135,"Set slow movement speed"),(0x139,"Set home speed"),(0x144,"Set fast positioning speed"),(0x18F,"Set slow positioning speed"),(0x158,"Set step speed") }) Add(id,name,Sm10Payload.Byte);
        Add(0x13C,"Set home direction",Sm10Payload.Byte,max:1);
        Add(0x191,"Set positioning speed mode",Sm10Payload.Byte,max:1);
        Add(0x146,"Set handwheel resolution",Sm10Payload.Byte,min:1,max:255);
        Add(0x147,"Single steps",Sm10Payload.SignedByte,move:true,min:-127,max:127);
        Add(0x19F,"Set proportional factor",Sm10Payload.SignedByte,min:-127,max:127);
        Add(0x1E8,"Trackball (ambiguous documentation)",Sm10Payload.Ambiguous,move:true);
        Add(0x3C,"Set slow positioning speed (µsteps/s)",Sm10Payload.UInt16,min:1,max:17999);
        Add(0x3D,"Set fast positioning speed (steps/s)",Sm10Payload.UInt16,min:1,max:2999);
        Add(0x110,"Go to saved position",Sm10Payload.Byte,move:true,min:1,max:5);
        Add(0x10A,"Save position",Sm10Payload.Byte,min:1,max:5);
        foreach(var (id,name) in new (ushort,string)[] { (0x48,"Absolute fast"),(0x49,"Absolute slow"),(0x4A,"Relative fast"),(0x4B,"Relative slow") }) Add(id,name,Sm10Payload.Float,move:true,min:-float.MaxValue,max:float.MaxValue);
        Add(0x44F,"Set step distance",Sm10Payload.Float,max:float.MaxValue);
        foreach(var (id,name) in new (ushort,string)[] { (0x101,"Read position"),(0x131,"Read counter 2") }) Add(id,name,r:Sm10Reply.Float,read:true);
        Add(0x120,"Read main status",r:Sm10Reply.Status,read:true);
        foreach(var (id,name) in new (ushort,string)[] { (0x11E,"Read power status"),(0x11F,"Read output-stage presence"),(0x12F,"Read fast movement speed"),(0x130,"Read slow movement speed"),(0x138,"Read home speed"),(0x13D,"Read home direction"),(0x143,"Read fast positioning speed"),(0x14B,"Read motor type"),(0x14D,"Read spindle pitch"),(0x159,"Read step speed"),(0x190,"Read slow positioning speed"),(0x192,"Read speed mode"),(0x1A2,"Read proportional factor"),(0x431,"Read slow ramp state") }) Add(id,name,r:Sm10Reply.Byte,read:true);
        Add(0x160,"Read fast positioning speed (steps/s)",r:Sm10Reply.UInt16,read:true);
        Add(0x161,"Read slow positioning speed (µsteps/s)",r:Sm10Reply.UInt16,read:true);
        foreach(var (id,name) in new (ushort,string)[] { (0xA034,"Group power off"),(0xA035,"Group power on"),(0xA0F0,"Group zero position"),(0xA132,"Group zero counter 2"),(0xA0FF,"Group stop"),(0xA13F,"Group home abort") }) Add(id,name,Sm10Payload.Mask,Sm10Reply.None);
        foreach(var (id,name) in new (ushort,string)[] { (0xA012,"Group fast positive"),(0xA013,"Group fast negative"),(0xA014,"Group slow positive"),(0xA015,"Group slow negative"),(0xA024,"Group go to zero"),(0xA104,"Group home"),(0xA022,"Group home return") }) Add(id,name,Sm10Payload.MaskByte,Sm10Reply.None,move:true);
        Add(0xA10A,"Group save position",Sm10Payload.MaskByte,Sm10Reply.None,min:1,max:5);
        Add(0xA110,"Group go to saved position",Sm10Payload.MaskTwoBytes,Sm10Reply.None,move:true);
        Add(0xA140,"Group step increment",Sm10Payload.MaskStep,Sm10Reply.None,move:true);
        Add(0xA141,"Group step decrement",Sm10Payload.MaskStep,Sm10Reply.None,move:true);
        foreach(var (id,name) in new (ushort,string)[] { (0xA048,"Four-axis absolute fast"),(0xA049,"Four-axis absolute slow"),(0xA04A,"Four-axis relative fast"),(0xA04B,"Four-axis relative slow") }) Add(id,name,Sm10Payload.FourPositions,Sm10Reply.None,move:true);
        Add(0xA101,"Four-axis positions",Sm10Payload.FourAxes,Sm10Reply.GroupPositions,true);
        Add(0xA131,"Four-axis counters 2",Sm10Payload.FourAxes,Sm10Reply.GroupPositions,true);
        Add(0xA120,"Four-axis status",Sm10Payload.FourAxes,Sm10Reply.GroupStatus,true);
        return list.OrderBy(c=>c.Id).ToList();
    }
}
