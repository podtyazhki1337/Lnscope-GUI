using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using LnScope.Devices.LuigsNeumann.Sm10;

internal static class Sm10CommandChecks
{
    private static void Check(bool valid,string name) { if(!valid) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Fails(Func<Task> action)
    {
        try { await action(); } catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException or NotSupportedException) { return; }
        throw new Exception("Expected rejection");
    }
    public static async Task RunAsync()
    {
        // Independent command index transcribed from the supplied manual pp.33–34.
        var ids = "0012 0013 0014 0015 0022 0024 0034 0035 003A 003C 003D 0048 0049 004A 004B 00F0 00FF 0101 0104 0110 010A 011E 011F 0120 012F 0130 0131 0132 0134 0135 0138 0139 013C 013D 013F 0140 0141 0143 0144 0146 0147 014B 014D 0158 0159 0160 0161 018F 0190 0191 0192 019F 01A2 01E8 042F 0430 0431 044F A012 A013 A014 A015 A022 A024 A034 A035 A048 A049 A04A A04B A0F0 A0FF A101 A104 A110 A10A A120 A131 A132 A13F A140 A141"
            .Split(' ').Select(s=>Convert.ToUInt16(s,16)).Order().ToArray();
        Check(Sm10Commands.All.Select(c=>c.Id).SequenceEqual(ids), $"all {ids.Length} manual command IDs accounted for");
        var mask = Sm10Command.BuildMask([1,8,9,72]);
        Check(mask.SequenceEqual(new byte[] {0xA0,0x80,0,0,0,0,0,0,1,0x81}), "72-axis group mask uses MSB-first wire order");
        Check(Sm10Commands.Get(0x132).Build([7],[]).SequenceEqual(new byte[]{7,2}), "counter reset includes counter selector");
        Check(Sm10Commands.Get(0x3D).Build([1],[258]).SequenceEqual(new byte[]{1,2,1}), "linear velocity is uint16 little-endian");
        Check(Sm10Commands.Get(0x147).Build([1],[-2]).SequenceEqual(new byte[]{1,254}), "signed step uses two's complement");
        var group = Sm10Commands.Get(0xA048).Build([1,2,3,4],[1,-2,3,4]);
        var packet = Sm10Protocol.Encode(0xA048,group);
        Check(packet[3]==0x15 && group.Length==21 && BinaryPrimitives.ReadSingleLittleEndian(group.AsSpan(9)) == -2, "four-axis move uses documented 21-byte payload");
        await Fails(()=>Task.FromResult(Sm10Commands.Get(0xA048).Build([1,2,3,19],[1,2,3,4])));
        await Fails(()=>Task.FromResult(Sm10Commands.Get(0x1E8).Build([1],[1])));
        await Fails(()=>Task.FromResult(Sm10Commands.Get(0x134).Build([1],[16])));
        await Fails(()=>Task.FromResult(Sm10Commands.Get(0x48).Build([1],[double.NaN])));
        var wire = new Wire();
        var traces = new ConcurrentQueue<Sm10CommandTrace>();
        var bus = new Sm10SerialBus("FAKE",115200,150,()=>wire, trace: traces.Enqueue);
        await bus.ConnectAsync(); await bus.ConnectAsync();
        Check(wire.Opens==1 && wire.Writes.Count==0, "shared port opens once and sends no handshake");
        await bus.DisconnectAsync();
        Check(wire.IsOpen, "disconnecting one logical group keeps shared port open");
        wire.Respond = request => Sm10Protocol.Encode(0x101,[0,0,0x48,0x41],6);
        var position = await bus.RequestAsync(0x101,[1]);
        Check(Sm10Protocol.ReadPosition(position)==12.5, "single query validates and decodes real wire frame");
        Check(traces.IsEmpty, "routine feedback reads do not flood the recording journal");
        wire.Respond = _ => null;
        var reads = wire.Reads;
        await bus.RequestAsync(0xA034,Sm10Commands.Get(0xA034).Build([1,4],[]));
        Check(wire.Reads==reads, "no-ACK command never reads or waits for a response");
        Check(traces.Single().Outcome == "sent_unconfirmed" && traces.Single().Command == "0xA034" &&
            traces.Single().Axes == "1;4", "no-ACK command journal records actual send and hardware axes without claiming execution");
        var response = new byte[20]; new byte[]{1,2,3,4}.CopyTo(response,0);
        for(var i=0;i<4;i++) BinaryPrimitives.WriteSingleLittleEndian(response.AsSpan(4+i*4),i+0.5f);
        wire.Respond = _ => Sm10Protocol.Encode(0xA101,response,0x16);
        var positions = await bus.RequestAsync(0xA101,Sm10Commands.Get(0xA101).Build([1,2,3,4],[]));
        Check(Sm10Commands.Get(0xA101).DescribeReply(positions).Contains("3.5"), "group queries use SYN response and decode four positions");
        var before = wire.Writes.Count;
        await Fails(()=>bus.RequestAsync(0x134,[1,255]));
        Check(wire.Writes.Count==before, "invalid parameter rejected before transmission");
        wire.Respond = _ => null;
        await Fails(()=>bus.RequestAsync(0x101,[1]));
        before=wire.Writes.Count;
        await Fails(()=>bus.RequestAsync(0x101,[2]));
        Check(wire.Writes.Count==before, "timeout latches shared connection and never retries");
        Check(traces.Any(t => t.Outcome == "failed_after_send" && t.Command == "0x0101"), "feedback failure is journaled despite normal polling suppression");
        await bus.EmergencyStopAsync([1,4]);
        Check(BinaryPrimitives.ReadUInt16BigEndian(wire.Writes.Last().AsSpan(1))==0xA0FF, "stop can be sent on faulted connection without ACK");
        Check(traces.Last().Command == "0xA0FF" && traces.Last().Outcome == "sent_unconfirmed", "emergency stop is included in the command journal");
        await bus.DisconnectAsync();

        var delayed = new Wire();
        var urgent = new Sm10SerialBus("FAKE",115200,10000,()=>delayed);
        await urgent.ConnectAsync();
        var pending = urgent.RequestAsync(0x101,[1]);
        await delayed.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = urgent.RequestAsync(0x48,Sm10Protocol.Target(1,10));
        var clock=Stopwatch.StartNew();
        await urgent.EmergencyStopAsync([1]);
        await Fails(()=>pending); await Fails(()=>queued);
        Check(clock.ElapsedMilliseconds<1000 && delayed.Writes.Count==2 &&
            delayed.Writes.All(p=>BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1))!=0x48),
            "Stop cancels long reply wait and queued movement before sending group stop");
        await urgent.DisconnectAsync();

        var running = new Wire { Respond = req => Sm10Protocol.Encode(BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(1)),[],6) };
        var continuous = new Sm10SerialBus("FAKE",115200,150,()=>running);
        await continuous.ConnectAsync();
        await continuous.RequestAsync(0x12,[1]);
        await Fails(()=>continuous.RequestAsync(0x101,[1]));
        Check(running.Writes.Count==1,"continuous move prohibits polling and unrelated commands");
        await continuous.EmergencyStopAsync([1]); await continuous.DisconnectAsync();

        var pacedWire = new Wire();
        var pacedBus = new Sm10SerialBus("FAKE",115200,150,()=>pacedWire);
        await pacedBus.ConnectAsync();
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pacedBus.RequestAsync(0xA034, Sm10Commands.Get(0xA034).Build([1], []))));
        var sent = pacedWire.WriteTimes.ToArray();
        Check(sent.Length == 3 && Stopwatch.GetElapsedTime(started, sent[0]).TotalMilliseconds >= 2 &&
            sent.Zip(sent.Skip(1), (a,b) => Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).All(ms => ms >= 2),
            "every no-ACK write waits at least 2 ms inside the shared serial queue");
        started = Stopwatch.GetTimestamp();
        await pacedBus.EmergencyStopAsync([1]);
        Check(Stopwatch.GetElapsedTime(started, pacedWire.WriteTimes.Last()).TotalMilliseconds >= 2,
            "emergency Stop also respects the requested 2 ms pre-send delay");
        await pacedBus.DisconnectAsync();
    }
    private sealed class Wire : ISm10Connection
    {
        public bool IsOpen { get; private set; }
        public int Opens, Reads;
        public ConcurrentQueue<byte[]> Writes { get; } = new();
        public ConcurrentQueue<long> WriteTimes { get; } = new();
        private readonly ConcurrentQueue<byte> _input = new();
        public Func<byte[],byte[]?>? Respond;
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() { IsOpen=true; Opens++; }
        public void Write(byte[] data,int offset,int count)
        {
            WriteTimes.Enqueue(Stopwatch.GetTimestamp());
            var frame=data.AsSpan(offset,count).ToArray(); Writes.Enqueue(frame);
            if(Respond?.Invoke(frame) is { } response) foreach(var b in response) _input.Enqueue(b);
            FirstWrite.TrySetResult();
        }
        public int ReadByte() { Reads++; if(_input.TryDequeue(out var b)) return b; Thread.Sleep(10); throw new TimeoutException(); }
        public void Dispose() => IsOpen=false;
    }
}
