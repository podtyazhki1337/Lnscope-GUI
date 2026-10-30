using GUI.Motion;
using GUI.ViewModels;
using LnScope.Core.Devices;

internal static class MotionSafetyChecks
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Fail(Func<Task> work)
    {
        try { await work(); } catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or System.IO.IOException) { return; }
        throw new Exception("Expected rejected motion");
    }
    private static async Task Until(Func<bool> check)
    {
        using var ct = new CancellationTokenSource(5000);
        while (!check()) await Task.Delay(5, ct.Token);
    }
    private static (Fake a, Fake b, MotionService motion, MotionLink link) Setup(bool linked = true)
    {
        var a = new Fake("stage"); var b = new Fake("manipulator");
        var link = new MotionLink(new LinkOptions { Source = a.Id, Target = b.Id, EnabledByDefault = linked,
            AxisMap = [new AxisMapping { From = "X", To = "X", Factor = 1 }] });
        return (a, b, new MotionService([a,b], [link], _ => {}), link);
    }
    public static async Task RunAsync()
    {
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready; b.Limit = AxisLimit.Positive;
            await Fail(() => m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast));
            Check(a.Moves == 0 && b.Moves == 0,"follower hardware limit blocks leader before any move");
            b.Limit = AxisLimit.None; b.Position = 95;
            await Fail(() => m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast));
            Check(a.Moves == 0 && b.Moves == 0,"follower software boundary preflight is atomic");
        }
        {
            var (a,b,m,link) = Setup(); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            link.IsEnabled = false; // Existing motion keeps its original membership.
            b.Limit = AxisLimit.Positive; b.Emit();
            await Until(() => a.EmergencyStops > 0 && b.EmergencyStops > 0);
            Check(a.EmergencyStops == 1 && b.EmergencyStops == 1,"follower limit stops both axes even after link toggle");
            await Fail(() => m.MoveRelativeAsync(a.Id,"X",1,MoveSpeed.Fast));
            Check(a.Moves == 1,"limit latch prevents automatic restart");
        }
        {
            var (a,b,m,_) = Setup(false); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            await m.MoveRelativeAsync(b.Id,"X",10,MoveSpeed.Fast);
            a.Limit = AxisLimit.Positive; a.Emit();
            await Until(() => b.EmergencyStops == 1);
            Check(a.EmergencyStops == 1,"limit also stops simultaneously held independent axes");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            a.Position = 10; a.Emit();
            a.Limit = AxisLimit.Positive; a.Emit();
            await Until(() => a.EmergencyStops == 1 && b.EmergencyStops == 1);
            Check(true,"late limit on an already-arrived member still stops its moving follower");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready; b.ThrowMove = true;
            await Fail(() => m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast));
            await Until(() => a.EmergencyStops == 1 && b.EmergencyStops == 1);
            Check(a.Moves == 1 && b.Moves == 1,"partial dispatch failure stops leader without retry");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready; a.ReadWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var moving = m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            await a.ReadEntered.Task;
            var stopping = m.CancelHeldMotionAsync(); a.ReadWait.SetResult();
            await Fail(() => moving); await stopping;
            Check(a.Moves == 0 && b.Moves == 0,"release during preflight cancels queued movement");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready; a.MoveWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var moving = m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            await a.MoveEntered.Task;
            var stopping = m.CancelHeldMotionAsync(); a.MoveWait.SetResult();
            await Fail(() => moving); await stopping;
            Check(a.Stops == 1 && b.Moves == 0,"release during ACK stops leader and never starts follower");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready; a.Limit = AxisLimit.Positive;
            await m.MoveRelativeAsync(a.Id,"X",-10,MoveSpeed.Slow);
            Check(a.LastTarget == -10 && b.LastTarget == -10,"retreat away from an active directional limit is allowed");
            await m.CancelHeldMotionAsync();
            Check(a.Stops == 1 && b.Stops == 1,"release stops both members of a linked move");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            a.Fault();
            await Until(() => a.EmergencyStops == 1 && b.EmergencyStops == 1);
            Check(true,"lost controller feedback stops linked counterpart");
        }
        {
            var (a,b,m,_) = Setup(false); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            b.Fault();
            await Until(() => a.EmergencyStops == 1);
            Check(true,"fault in an idle controller group stops ongoing motion too");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready;
            await m.MoveRelativeAsync(a.Id,"X",10,MoveSpeed.Fast);
            await Until(() => a.EmergencyStops == 1 && b.EmergencyStops == 1);
            Check(true,"stale feedback watchdog stops both devices");
        }
        {
            var (a,b,m,_) = Setup(); await using var scope = m; await m.Ready;
            var vm = new AxisViewModel(a.Id,a.Axes[0],m,_=>{});
            vm.Apply(new("X",0,0,false,true,DateTimeOffset.UtcNow,AxisLimit.Negative));
            Check(vm.AtLimit && vm.LimitText.Contains("−"),"negative hardware switch produces red-limit state");
            vm.Apply(new("X",101,0,false,false,DateTimeOffset.UtcNow));
            Check(vm.AtLimit && vm.LimitText.Contains("SOFTWARE"),"software bounds remain separately identified");
            vm.Apply(new("X",0,0,false,false,DateTimeOffset.UtcNow));
            Check(!vm.AtLimit,"red indication clears on fresh in-range feedback");
            var jog = new JogController(m,new JogOptions(),System.Windows.Threading.Dispatcher.CurrentDispatcher,_=>{});
            Check(jog.StepMultiplier == 1 && jog.Speed == MoveSpeed.Slow,"L uses fine slow bounded steps");
            jog.Rate = "M"; Check(jog.StepMultiplier == 5 && jog.Speed == MoveSpeed.Fast,"M uses medium fast bounded steps");
            jog.Rate = "H"; Check(jog.StepMultiplier == 20 && jog.Speed == MoveSpeed.Fast,"H uses coarse fast bounded steps");
            jog.IsEnabled = false;
        }
    }
    private sealed class Fake(string id) : IPositioner, IPositionerSafety
    {
        public string Id => id;
        public DeviceState State { get; private set; } = DeviceState.Ready;
        public IReadOnlyList<AxisInfo> Axes { get; } = [new("X","um",-100,100)];
        public event EventHandler<DeviceState>? StateChanged;
        public event EventHandler<AxisPosition>? PositionChanged;
        public int Moves, Stops, EmergencyStops;
        public double Position, LastTarget;
        public AxisLimit Limit;
        public bool ThrowMove;
        public TaskCompletionSource? ReadWait, MoveWait;
        public TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MoveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit() => PositionChanged?.Invoke(this,new("X",Position,Position,false,Limit!=AxisLimit.None,DateTimeOffset.UtcNow,Limit));
        public void Fault() { State=DeviceState.Faulted; StateChanged?.Invoke(this,State); }
        public Task ConnectAsync(CancellationToken ct=default) { State=DeviceState.Ready; StateChanged?.Invoke(this,State); return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct=default) { State=DeviceState.Disconnected; StateChanged?.Invoke(this,State); return Task.CompletedTask; }
        public async Task<AxisPosition> ReadPositionAsync(string axis,CancellationToken ct=default)
        { ReadEntered.TrySetResult(); if(ReadWait is not null) await ReadWait.Task; return new(axis,Position,Position,false,Limit!=AxisLimit.None,DateTimeOffset.UtcNow,Limit); }
        public async Task MoveAbsoluteAsync(string axis,double target,MoveSpeed speed,CancellationToken ct=default)
        { Moves++; LastTarget=target; MoveEntered.TrySetResult(); if(MoveWait is not null) await MoveWait.Task; if(ThrowMove) throw new System.IO.IOException("lost ACK"); }
        public Task MoveRelativeAsync(string axis,double delta,MoveSpeed speed,CancellationToken ct=default) => MoveAbsoluteAsync(axis,Position+delta,speed,ct);
        public Task StopAsync(string axis,CancellationToken ct=default) { Stops++; return Task.CompletedTask; }
        public void ValidateTarget(string axis,double target) { if(target < -100 || target >100) throw new InvalidOperationException(); }
        public Task EmergencyStopAsync() { EmergencyStops++; Fault(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
