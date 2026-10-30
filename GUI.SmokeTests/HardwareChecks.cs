using System.Buffers.Binary;
using GUI.Devices;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;
using LnScope.Devices.LuigsNeumann.Sm10;

internal static class HardwareChecks
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Unsafe motion was accepted");
    }
    public static async Task RunAsync()
    {
        Check(Sm10Protocol.Crc("123456789"u8) == 0x31C3, "SM10 CRC reference vector");
        Check(Sm10Protocol.Encode(0x0101, [1]).SequenceEqual(new byte[] { 0x16, 1, 1, 1, 1, 0x10, 0x21 }), "SM10 wire frame");
        var reply = Sm10Protocol.Encode(0x0101, [1, 2, 3, 4], 6);
        Check(Sm10Protocol.Decode(0x0101, reply).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "SM10 response decoding");
        reply[^1] ^= 1;
        var rejected = false;
        try { Sm10Protocol.Decode(0x0101, reply); } catch (System.IO.IOException) { rejected = true; }
        Check(rejected, "SM10 corrupt response rejected");
        var legacy = Convert.FromHexString("06010104265F87C6BBBB");
        rejected = false;
        try { Sm10Protocol.Decode(0x0101, legacy); } catch (System.IO.IOException) { rejected = true; }
        Check(rejected, "legacy checksum requires explicit opt-in");
        Check(Sm10Protocol.Decode(0x0101, legacy, allowLegacyChecksum: true).Length == 4, "captured COM3 BBBB response accepted in compatibility mode");
        rejected = false;
        try { Sm10Protocol.Decode(0x0101, reply, allowLegacyChecksum: true); } catch (System.IO.IOException) { rejected = true; }
        Check(rejected, "compatibility mode still rejects other bad checksums");
        legacy[2] = 2;
        rejected = false;
        try { Sm10Protocol.Decode(0x0101, legacy, allowLegacyChecksum: true); } catch (System.IO.IOException) { rejected = true; }
        Check(rejected, "compatibility mode still rejects mismatched command");
        var devices = DirectShowDiscovery.ParseDevices("[dshow] \"Camera\" (video)\n[dshow] Alternative name \"@device_pnp_camera\"\n[dshow] \"Mic\" (audio)\n[dshow] Alternative name \"@device_mic\"");
        Check(devices.Count == 1 && devices[0].DeviceId == "@device_pnp_camera", "camera discovery preserves stable ID and excludes audio");
        var modes = DirectShowDiscovery.ParseModes("pixel_format=yuyv422 min s=2592x1944 fps=7 max s=2592x1944 fps=7\nvcodec=mjpeg min s=1296x972 fps=5 max s=1296x972 fps=28");
        Check(modes.Count == 2 && modes[0].MaxFps == 7 && modes[1].Compressed, "camera mode discovery");
        await using (var camera = new DirectShowCamera(new LnScope.Core.Configuration.CameraOptions()))
        {
            foreach (var fps in new[] { 0d, 1d, 7.5d, 30d, 120d })
            {
                camera.TargetFps = fps;
                Check(camera.TargetFps == fps, $"requested FPS {fps} is preserved before connection");
            }
            await Reject(() => { camera.TargetFps = -1; return Task.CompletedTask; });
            await Reject(() => { camera.TargetFps = double.NaN; return Task.CompletedTask; });
        }
        Check(DirectShowCamera.ResolveCaptureFps(0, modes[1]) == 28 &&
            DirectShowCamera.ResolveCaptureFps(120, modes[1]) == 28 &&
            DirectShowCamera.ResolveCaptureFps(7.5, modes[1]) == 7.5,
            "actual capture FPS respects mode while zero selects maximum");
        var shared = new GUI.AppConfig { Positioners = [
            new LnManipulatorOptions { Id = "manipulator", Driver = "sm10-binary", Transport = "serial", PortName = "COM7", Axes = [new LnAxisOptions { HardwareAxis = 1 }] },
            new LnManipulatorOptions { Id = "stage", Driver = "sm10-binary", Transport = "serial", PortName = "COM7", Axes = [new LnAxisOptions { HardwareAxis = 4 }] }
        ] };
        DeviceFactory.Validate(shared);
        Check(true, "stage and manipulator accept a shared controller COM port");
        shared.Positioners[1].Axes[0].HardwareAxis = 1;
        rejected = false;
        try { DeviceFactory.Validate(shared); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "shared controller rejects conflicting axis addresses");
        var options = new LnManipulatorOptions { Driver = "sm10-binary", FeedbackIntervalMs = 2000, DisplayUserCoordinates = true,
            Axes = [new LnAxisOptions { Id = "X", HardwareAxis = 7, MinPosition = -100, MaxPosition = 100 }] };
        var bus = new FakeBus();
        await using var positioner = new Sm10Positioner(options, bus);
        await positioner.ConnectAsync();
        Check(bus.Commands.All(c => c is 0x0101 or 0x0131 or 0x0120), "SM10 connect sends only read commands");
        var position = await positioner.ReadPositionAsync("X");
        Check(position.Reference == 12.5 && !position.IsMoving, "SM10 little-endian position and original German status layout");
        await Reject(() => positioner.MoveAbsoluteAsync("X", 20, MoveSpeed.Fast));
        options.MotionEnabled = true;
        await Reject(() => positioner.MoveAbsoluteAsync("X", 101, MoveSpeed.Fast));
        await Reject(() => positioner.MoveAbsoluteAsync("X", double.NaN, MoveSpeed.Fast));
        Check(!bus.Commands.Any(c => c is 0x0048 or 0x0049), "unverified or out-of-range motion sends no movement");
        await positioner.MoveRelativeAsync("X", 2.5, MoveSpeed.Slow);
        Check(bus.LastTarget == 15 && bus.LastAxis == 7 && bus.Commands.Contains(0x0049), "bounded relative motion addresses configured physical axis");
        await positioner.StopAsync("X");
        Check(bus.Commands[^1] == 0xA0FF, "SM10 stop uses documented no-ACK group command");
        await positioner.DisconnectAsync();
        await positioner.ConnectAsync();
        Check(positioner.State == DeviceState.Ready, "SM10 reconnect lifecycle");
        await using var motion = new GUI.Motion.MotionService([positioner], [], _ => { }) { ManualSpeed = MoveSpeed.Fast };
        var axisModel = new GUI.ViewModels.AxisViewModel(positioner.Id, positioner.Axes[0], motion, _ => { });
        bus.UserPosition = -8;
        await axisModel.RefreshAsync();
        Check(axisModel.Position == -8 && axisModel.ReferenceText.Contains("12"), "GUI displays counter 2 while preserving reference readout");
        axisModel.Target = 20;
        await axisModel.GoCommand.ExecuteAsync();
        Check(bus.LastTarget == 40.5f && bus.LastAxis == 7, "Go translates displayed target into counter 1 coordinates");
        await motion.CancelHeldMotionAsync();
        bus.UserPosition = 5; // Panel reset occurred after the last GUI refresh.
        await axisModel.GoCommand.ExecuteAsync();
        Check(bus.LastTarget == 27.5f, "Go reads fresh counter offset after panel reset");
        await motion.CancelHeldMotionAsync();
        bus.UserPosition = -1000;
        int before = bus.Commands.Count(c => c == Sm10Protocol.AbsoluteFast);
        await axisModel.GoCommand.ExecuteAsync();
        Check(bus.Commands.Count(c => c == Sm10Protocol.AbsoluteFast) == before, "converted target outside reference limits sends no move");
    }
    private sealed class FakeBus : ISm10Bus
    {
        public List<ushort> Commands { get; } = [];
        public float LastTarget;
        public byte LastAxis;
        public float UserPosition = 12.5f;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<byte[]> RequestAsync(ushort command, byte[] data, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (command == Sm10Protocol.Status) return Task.FromResult(new byte[] { 0, 1, 0, 0, 0, 16, 0, 0 });
            if (command is Sm10Protocol.Position or Sm10Protocol.Counter2)
            {
                var result = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(result, command == Sm10Protocol.Counter2 ? UserPosition : 12.5f); return Task.FromResult(result);
            }
            if (command is Sm10Protocol.AbsoluteFast or Sm10Protocol.AbsoluteSlow)
            { LastAxis = data[0]; LastTarget = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(1)); }
            return Task.FromResult(Array.Empty<byte>());
        }
    }
}
