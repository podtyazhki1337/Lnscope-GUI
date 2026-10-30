using System.Buffers.Binary;
using System.Text.Json;
using GUI.Devices;
using LnScope.Devices.LuigsNeumann.Sm10;

internal static class PythonCommandChecks
{
    private sealed class Bus : ISm10Bus
    {
        public int Requests;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<byte[]> RequestAsync(ushort command, byte[] payload, CancellationToken ct = default)
        {
            Requests++;
            var spec = Sm10Commands.Get(command);
            spec.ValidatePayload(payload);
            var reply = new byte[spec.ReplyLength];
            if (spec.Reply is Sm10Reply.GroupPositions or Sm10Reply.GroupStatus)
                payload.AsSpan(1, 4).CopyTo(reply);
            if (spec.Reply == Sm10Reply.Float) BinaryPrimitives.WriteSingleLittleEndian(reply, 12.5f);
            return Task.FromResult(reply);
        }
    }
    public static async Task RunAsync()
    {
        var bus = new Bus();
        await using var backend = new PythonCommandBackend(bus);
        var catalogue = await backend.CatalogueAsync();
        if (!catalogue.Select(c => c.Id).SequenceEqual(Sm10Commands.All.Select(c => c.Id))) throw new Exception("Python catalogue differs from the documented command index.");
        foreach (var c in catalogue)
        {
            var spec = Sm10Commands.Get(c.Id);
            if (c.ReadOnly != spec.ReadOnly || c.Moves != spec.Moves || c.IsStop != spec.IsStop || c.Continuous != spec.Continuous || c.Reply != spec.Reply.ToString())
                throw new Exception($"Incorrect Python metadata: {c}");
            if (!c.Supported)
            {
                try { await backend.BuildAsync(c.Id, [1], []); throw new Exception("Ambiguous command accepted."); }
                catch (BackendValidationException) { }
                continue;
            }
            int[] axes = spec.Payload is Sm10Payload.FourAxes or Sm10Payload.FourPositions ? [1, 2, 3, 4]
                : spec.Payload.ToString().StartsWith("Mask") ? [1, 7, 72] : [7];
            double[] values = spec.Payload switch
            {
                Sm10Payload.Byte or Sm10Payload.MaskByte or Sm10Payload.UInt16 => [Math.Max(1, spec.Minimum)],
                Sm10Payload.SignedByte => [-2], Sm10Payload.Float => [1.25],
                Sm10Payload.MaskTwoBytes => [3, 2], Sm10Payload.MaskStep => [2, 12.5],
                Sm10Payload.FourPositions => [1, -2, 3, 4], _ => []
            };
            var previous = bus.Requests;
            var packet = await backend.BuildAsync(c.Id, axes, values);
            if (bus.Requests != previous || !packet.Data.SequenceEqual(spec.Build(axes, values)) ||
                packet.Frame != Convert.ToHexString(Sm10Protocol.Encode(c.Id, packet.Data)))
                throw new Exception($"Python payload / CRC differs: {c}");
            var result = await backend.ExecuteAsync(c.Id, axes, values, true);
            if (bus.Requests != previous + 1 || string.IsNullOrEmpty(result.Description)) throw new Exception("Execution must issue exactly one exchange.");
        }
        var before = bus.Requests;
        try { await backend.ExecuteAsync(0x0048, [1], [10], false); throw new Exception("Writes allowed without authorization."); }
        catch (InvalidOperationException) { }
        if (before != bus.Requests) throw new Exception("Denied command reached the wire.");
        await backend.ExecuteAsync(0x0101, [1], [], false);
        Console.WriteLine("PASS Python console: 82 IDs, 81 payloads/CRCs/exchanges/replies, no-IO previews, write gate, ambiguous command blocked.");
    }
}
