using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using GUI.Devices;
using GUI.Motion;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;
using LnScope.Devices.LuigsNeumann.Sm10;

internal static class PythonBackendChecks
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS Python: " + name);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Python safety check timed out.");
            await Task.Delay(20);
        }
    }
    private static LnManipulatorOptions Options(string id, int address) => new()
    {
        Id = id, Driver = "sm10-binary", Transport = "serial", MotionEnabled = true,
        FeedbackIntervalMs = 100, ResponseTimeoutMs = 1, DisplayUserCoordinates = true,
        Axes = [new() { Id = "X", HardwareAxis = address, MinPosition = -100000, MaxPosition = 100000 }]
    };
    private sealed class Bus : ISm10Bus
    {
        public ConcurrentQueue<(ushort Command, byte[] Data)> Commands = new();
        public ConcurrentDictionary<int, double> Positions = new();
        public ConcurrentDictionary<int, byte> Limits = new();
        public ConcurrentDictionary<int, bool> Moving = new();
        public ConcurrentDictionary<(int Axis, int Slot), double> Saved = new();
        public bool Stall;
        public int Leases;
        public Task ConnectAsync(CancellationToken ct = default) { Interlocked.Increment(ref Leases); return Task.CompletedTask; }
        public Task DisconnectAsync() { Interlocked.Decrement(ref Leases); return Task.CompletedTask; }
        public async Task<byte[]> RequestAsync(ushort command, byte[] data, CancellationToken ct = default)
        {
            Commands.Enqueue((command, data));
            if (command == 0xA0FF) return [];
            if (command is 0xA10A or 0xA110)
            {
                foreach (var address in Enumerable.Range(1, 72).Where(a => (data[9 - (a - 1) / 8] & (1 << ((a - 1) % 8))) != 0))
                    if (command == 0xA10A) Saved[(address, data[10])] = Positions.GetValueOrDefault(address);
                    else { Positions[address] = Saved[(address, data[10])]; Moving[address] = false; }
                return [];
            }
            if (Stall) await Task.Delay(Timeout.Infinite, ct);
            int axis = data[0];
            if (command == 0x0120) return [Limits.GetValueOrDefault(axis), 1, 0, 0, 123, 1, (byte)(Moving.GetValueOrDefault(axis) ? 1 : 0), 0];
            if (command is 0x0101 or 0x0131) return BitConverter.GetBytes((float)(Positions.GetValueOrDefault(axis) + (command == 0x0131 ? 100 : 0)));
            if (command is 0x0048 or 0x0049) { Moving[axis] = true; return []; }
            throw new IOException("Unexpected command.");
        }
        public int Moves => Commands.Count(c => c.Command is 0x0048 or 0x0049);
        public bool Stopped(int axis) => Commands.Any(c => c.Command == 0xA0FF && c.Data[0] == 0xA0 &&
            (c.Data[9 - (axis - 1) / 8] & (1 << ((axis - 1) % 8))) != 0);
    }
    public static async Task RunAsync()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            var bus = new Bus(); bus.Positions[1] = -1898; bus.Positions[7] = 12580;
            await using var stage = new PythonPositioner(Options("stage", 1), bus);
            await using var manip = new PythonPositioner(Options("manipulator", 7), bus);
            await stage.ConnectAsync(); await manip.ConnectAsync();
            Check(bus.Leases == 2 && bus.Commands.All(c => c.Command is 0x0120 or 0x0101 or 0x0131), "shared-port connect is read-only without PATH");
            var p = await manip.ReadPositionAsync("X");
            Check(p.Reference == 12580 && p.User == 12680 && !p.IsMoving, "C1/C2 and status byte 6 survive IPC");
            var link = new MotionLink(new() { Source = "stage", Target = "manipulator", Bidirectional = true,
                EnabledByDefault = true, AxisMap = [new() { From = "X", To = "X", Factor = -2 }] });
            await using var motion = new MotionService([stage, manip], [link], Console.WriteLine);
            await motion.Ready;
            await motion.SavePositionAsync("stage", 3);
            Check(bus.Saved[(1, 3)] == -1898 && !bus.Saved.ContainsKey((7, 3)), "native save is scoped to stage despite enabled links");
            bus.Positions[1] = -1800;
            await motion.GotoPositionAsync("stage", 3, 2);
            await Until(() => !motion.IsAxisBusy("stage", "X"));
            Check(bus.Positions[1] == -1898 && bus.Positions[7] == 12580 &&
                bus.Commands.Any(c => c.Command == 0xA110 && c.Data.Length == 12 && c.Data[10] == 3 && c.Data[11] == 2),
                "native Go traverses both Python workers and C# guard with slot and speed");
            Check(await motion.ReferenceTargetAsync("manipulator", "X", 12690) == 12590,
                "displayed C2 target is translated using fresh C1/C2 in Python");
            bus.Limits[7] = 1;
            try { await motion.MoveRelativeAsync("stage", "X", 5, MoveSpeed.Fast); throw new Exception("Limit accepted"); }
            catch (InvalidOperationException) { }
            Check(bus.Moves == 0, "follower limit blocks the entire linked movement before dispatch");
            bus.Limits[7] = 0;
            await motion.MoveRelativeAsync("stage", "X", 5, MoveSpeed.Fast);
            var moves = bus.Commands.Where(c => c.Command == 0x0048).ToArray();
            Check(moves.Length == 2 && moves[0].Data[0] == 1 && moves[1].Data[0] == 7 &&
                BitConverter.ToSingle(moves[0].Data, 1) == -1893 && BitConverter.ToSingle(moves[1].Data, 1) == 12570,
                "Python calculates linked targets, addresses and Fast payloads");
            bus.Limits[1] = 2;
            await Until(() => bus.Stopped(1) && bus.Stopped(7));
            Check(stage.State == DeviceState.Faulted && manip.State == DeviceState.Faulted, "hardware limit stops both groups and latches");
            await stage.DisconnectAsync(); await manip.DisconnectAsync();
            Check(bus.Leases == 0, "fault cleanup releases both shared-port leases");
            bus.Commands.Clear(); bus.Limits.Clear(); bus.Moving.Clear();
            await stage.ConnectAsync(); await manip.ConnectAsync();
            await motion.MoveRelativeAsync("manipulator", "X", 10, MoveSpeed.Slow);
            moves = bus.Commands.Where(c => c.Command == 0x0049).ToArray();
            Check(moves.Length == 2 && BitConverter.ToSingle(moves[1].Data, 1) == -1903, "reverse link uses reciprocal factor and Slow command");
            using (var process = Process.GetProcessById(stage.WorkerProcessId)) process.Kill();
            await Until(() => bus.Stopped(1) && bus.Stopped(7));
            Check(stage.State == DeviceState.Faulted && manip.State == DeviceState.Faulted, "worker crash immediately triggers independent Stop for both groups");
            var count = bus.Moves;
            try { await motion.MoveRelativeAsync("stage", "X", 1, MoveSpeed.Slow); throw new Exception("Crash latch accepted motion"); }
            catch (InvalidOperationException) { }
            Check(bus.Moves == count, "worker crash never automatically retries movement");
            await stage.DisconnectAsync(); await manip.DisconnectAsync();

            bus.Commands.Clear(); bus.Limits.Clear(); bus.Moving.Clear();
            await stage.ConnectAsync(); await manip.ConnectAsync();
            await motion.MoveRelativeAsync("stage", "X", 1, MoveSpeed.Slow);
            await motion.CancelHeldMotionAsync();
            Check(bus.Stopped(1) && bus.Stopped(7) && stage.State == DeviceState.Ready && manip.State == DeviceState.Ready,
                "ordinary key release stops both groups without faulting Python");
            bus.Commands.Clear();
            await stage.DisconnectAsync(); await manip.DisconnectAsync();
            Check(bus.Stopped(1) && bus.Stopped(7) && bus.Leases == 0,
                "disconnect after motion sends an independent Stop before closing the shared port");

            bus.Commands.Clear(); bus.Moving.Clear();
            await stage.ConnectAsync(); await manip.ConnectAsync();
            await motion.MoveRelativeAsync("stage", "X", 1, MoveSpeed.Slow);
            using (var controller = Process.GetProcessById(motion.WorkerProcessId)) controller.Kill();
            await Until(() => bus.Stopped(1) && bus.Stopped(7));
            Check(stage.State == DeviceState.Faulted && manip.State == DeviceState.Faulted,
                "central Python controller crash triggers the independent C# emergency path");
            await stage.DisconnectAsync(); await manip.DisconnectAsync();
            bus.Commands.Clear(); bus.Moving.Clear();
            await stage.ConnectAsync(); await manip.ConnectAsync(); await motion.Ready;
            await motion.MoveRelativeAsync("stage", "X", 1, MoveSpeed.Slow);
            Check(bus.Moves == 2, "central controller restarts only after explicit disconnect/reconnect of all groups");
            await motion.CancelHeldMotionAsync();
            await stage.DisconnectAsync(); await manip.DisconnectAsync();

            var timeoutBus = new Bus();
            await using var timeoutDevice = new PythonPositioner(Options("timeout", 1), timeoutBus);
            await timeoutDevice.ConnectAsync(); timeoutBus.Stall = true;
            var timer = Stopwatch.StartNew();
            try { await timeoutDevice.ReadPositionAsync("X"); throw new Exception("Stalled exchange accepted"); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            await Until(() => timeoutBus.Stopped(1));
            Check(timer.Elapsed < TimeSpan.FromSeconds(8) && timeoutDevice.State == DeviceState.Faulted,
                "RPC deadline cancels a stalled exchange and dispatches Stop");
            await timeoutDevice.DisconnectAsync();
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
    }
}
