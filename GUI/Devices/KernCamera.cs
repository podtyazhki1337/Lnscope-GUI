using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LnScope.Core.Configuration;
using LnScope.Core.Devices;

namespace GUI.Devices;

/// <summary>Native KERN camera API installed with MicroscopeVIS2.0Pro. No driver replacement.</summary>
public sealed class KernCamera(CameraOptions options) : ICamera
{
    private nint _handle, _buffer;
    private int _capacity;
    private KernNative.EventCallback? _callback;
    private TaskCompletionSource? _firstFrame;
    private volatile bool _streaming;
    private readonly object _frameGate = new();
    private double _target = options.TargetFps, _measured;
    private double _nextFrame;
    private long _sequence;
    private readonly Stopwatch _timer = new();
    private double _measureStart;
    private int _measureFrames;
    public string Id => options.Id;
    public DeviceState State { get; private set; } = DeviceState.Disconnected;
    public string? LastError { get; private set; }
    public IReadOnlyList<VideoMode> SupportedModes { get; private set; } = [];
    public VideoMode? CurrentMode { get; private set; }
    public IReadOnlyList<DeviceProperty> Properties { get; } = [];
    public bool IsStreaming => _streaming;
    public double MeasuredFps => Volatile.Read(ref _measured);
    public double TargetFps
    {
        get => Volatile.Read(ref _target);
        set { if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value)); Volatile.Write(ref _target,value); }
    }
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<CameraFrame>? FrameReady;
    private void SetState(DeviceState state) { State=state; StateChanged?.Invoke(this,state); }

    public static IReadOnlyList<CameraDevice> Enumerate()
    {
        if (!KernNative.Available) return [];
        KernNative.EnsureLoaded();
        int size = 256 + IntPtr.Size;
        var buffer=Marshal.AllocHGlobal(size*128);
        try
        {
            uint count=KernNative.Enum(buffer);
            if(count>128) throw new IOException("Invalid camera count returned by KERN SDK.");
            var result=new List<CameraDevice>();
            for(int i=0;i<count;i++)
            {
                var name=Marshal.PtrToStringUni(buffer+i*size,64)!.Split('\0')[0];
                var id=Marshal.PtrToStringUni(buffer+i*size+128,64)!.Split('\0')[0];
                if(!string.IsNullOrWhiteSpace(id)) result.Add(new CameraDevice("KERN " + name,"kern:"+id));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public async Task ConnectAsync(CancellationToken ct=default)
    {
        if(State==DeviceState.Ready) return;
        await DisconnectAsync(); SetState(DeviceState.Connecting);
        try
        {
            KernNative.EnsureLoaded(); ct.ThrowIfCancellationRequested();
            if(options.DeviceId is not { } id || !id.StartsWith("kern:",StringComparison.Ordinal))
                throw new IOException("Choose KERN ODC825 in Hardware setup.");
            if(!Enumerate().Any(d=>d.DeviceId==id)) throw new IOException("Selected KERN camera is not connected.");
            _handle=await Task.Run(()=>KernNative.Open(id[5..]),ct);
            if(_handle==0) throw new IOException("KERN camera could not open. Close MicroscopeVIS2.0Pro or any other camera application.");
            var count=KernNative.ResolutionNumber(_handle); KernNative.Check(count,"read resolution count");
            if(count is <1 or >128) throw new IOException("Invalid KERN resolution count.");
            var modes=new List<VideoMode>();
            for(uint i=0;i<count;i++)
            {
                KernNative.Check(KernNative.Resolution(_handle,i,out var width,out var height),"read resolution");
                if(width<=0 || height<=0 || (long)width*height>100_000_000) throw new IOException("Invalid sensor dimensions.");
                modes.Add(new VideoMode((int)i,width,height,"KERN native / BGR24"));
            }
            SupportedModes=modes;
            // Keep camera session settings local; do not save them back to camera EEPROM on close.
            // Older cameras do not implement EEPROM configuration saving.
            KernNative.Optional(KernNative.Option(_handle,0x3f,0),"disable automatic configuration save");
            await SetModeAsync(Math.Clamp(options.ModeIndex,0,count-1),ct);
            LastError=null; SetState(DeviceState.Ready);
        }
        catch(Exception ex)
        {
            LastError=ex.Message;
            if(_handle!=0) { KernNative.Close(_handle); _handle=0; }
            SetState(DeviceState.Faulted); throw;
        }
    }
    public async Task SetModeAsync(int modeIndex,CancellationToken ct=default)
    {
        if(_handle==0 || modeIndex<0 || modeIndex>=SupportedModes.Count) throw new ArgumentOutOfRangeException(nameof(modeIndex));
        bool resume=IsStreaming;
        await StopAsync(ct);
        KernNative.Check(KernNative.Size(_handle,(uint)modeIndex),"set resolution");
        CurrentMode=SupportedModes[modeIndex];
        if(resume) await StartAsync(ct);
    }
    public async Task StartAsync(CancellationToken ct=default)
    {
        if(_streaming) return;
        if(State!=DeviceState.Ready || CurrentMode is null || _handle==0) throw new InvalidOperationException("Connect KERN first.");
        await StopAsync(ct);
        KernNative.Check(KernNative.Option(_handle,0x04,0),"select processed color");
        KernNative.Check(KernNative.Option(_handle,0x0c,0),"select RGB24");
        KernNative.Check(KernNative.Option(_handle,0x2a,1),"select BGR byte order");
        KernNative.Check(KernNative.Option(_handle,0x0b,0),"select continuous capture");
        var stride=checked((CurrentMode.Width*3+3)&~3);
        _capacity=checked(stride*CurrentMode.Height); _buffer=Marshal.AllocHGlobal(_capacity);
        _firstFrame=new(TaskCreationOptions.RunContinuationsAsynchronously);
        _timer.Restart(); _nextFrame=0; _measureStart=0; _measureFrames=0;
        _callback=OnEvent; _streaming=true;
        try
        {
            KernNative.Check(KernNative.Start(_handle,_callback,0),"start capture");
            await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15),ct);
        }
        catch(Exception ex) { LastError=ex.Message; await StopAsync(); SetState(DeviceState.Faulted); throw; }
    }
    private void OnEvent(uint code,nint context)
    {
        try
        {
            if(!_streaming) return;
            if(code is 0x80 or 0x81 or 0x82 or 0x85) throw new IOException($"KERN camera event 0x{code:X}: disconnected or capture failed.");
            if(code!=4) return;
            lock(_frameGate)
            {
                if(!_streaming) return;
                KernNative.Check(KernNative.Pull(_handle,_buffer,24,out var info),"read frame");
                int width=checked((int)info.Width), height=checked((int)info.Height), stride=checked((width*3+3)&~3);
                int size=checked(stride*height);
                if(width!=CurrentMode!.Width || height!=CurrentMode.Height || size>_capacity) throw new IOException("Unexpected KERN frame dimensions.");
                var now=_timer.Elapsed.TotalSeconds;
                var target=TargetFps;
                if(target>0 && now<_nextFrame) return;
                _nextFrame=target>0 ? Math.Max(_nextFrame+1/target,now) : now;
                var bytes=new byte[size]; Marshal.Copy(_buffer,bytes,0,size);
                FrameReady?.Invoke(this,new CameraFrame(bytes,width,height,stride,CameraPixelFormat.Bgr24,Interlocked.Increment(ref _sequence),DateTimeOffset.UtcNow));
                _firstFrame?.TrySetResult();
                _measureFrames++;
                if(now-_measureStart>=1) { Volatile.Write(ref _measured,_measureFrames/(now-_measureStart)); _measureFrames=0; _measureStart=now; }
            }
        }
        catch(Exception ex)
        {
            LastError=ex.Message; _streaming=false; _firstFrame?.TrySetException(ex);
            // Never stop/close native SDK from its callback thread.
            try { SetState(DeviceState.Faulted); } catch { }
        }
    }
    public async Task StopAsync(CancellationToken ct=default)
    {
        _streaming=false;
        if(_callback is not null && _handle!=0) await Task.Run(()=>KernNative.Stop(_handle));
        _callback=null;
        if(_buffer!=0) { Marshal.FreeHGlobal(_buffer); _buffer=0; }
        _timer.Stop(); Volatile.Write(ref _measured,0);
    }
    public async Task DisconnectAsync(CancellationToken ct=default)
    {
        await StopAsync(ct);
        if(_handle!=0) { KernNative.Close(_handle); _handle=0; }
        SetState(DeviceState.Disconnected);
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

internal static class KernNative
{
    private static string LibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "KERN&SOHN","MicroscopeVIS2.0Pro",IntPtr.Size==8?"x64":"x86","kerncam.dll");
    public static bool Available => File.Exists(LibraryPath);
    private static readonly Lazy<nint> Library=new(()=>
    {
        if(!Available) throw new FileNotFoundException("Install KERN MicroscopeVIS2.0Pro to provide the native camera library.",LibraryPath);
        var handle=NativeLibrary.Load(LibraryPath);
        NativeLibrary.SetDllImportResolver(typeof(KernNative).Assembly,(name,assembly,path)=>name=="kerncam"?handle:0);
        return handle;
    });
    public static void EnsureLoaded() => _=Library.Value;
    public static void Check(int hr,string operation) { if(hr<0) throw new IOException($"KERN {operation}: 0x{hr:X8} ({Marshal.GetExceptionForHR(hr)?.Message})"); }
    public static void Optional(int hr,string operation) { if (hr != unchecked((int)0x80004001)) Check(hr,operation); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void EventCallback(uint code,nint context);
    [StructLayout(LayoutKind.Sequential)] public struct FrameInfo { public uint Width,Height,Flags,Sequence; public ulong Timestamp; }
    [DllImport("kerncam",EntryPoint="Kerncam_EnumV2",CallingConvention=CallingConvention.StdCall)] public static extern uint Enum(nint p);
    [DllImport("kerncam",EntryPoint="Kerncam_Open",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Unicode)] public static extern nint Open(string id);
    [DllImport("kerncam",EntryPoint="Kerncam_Close",CallingConvention=CallingConvention.StdCall)] public static extern void Close(nint h);
    [DllImport("kerncam",EntryPoint="Kerncam_get_ResolutionNumber",CallingConvention=CallingConvention.StdCall)] public static extern int ResolutionNumber(nint h);
    [DllImport("kerncam",EntryPoint="Kerncam_get_Resolution",CallingConvention=CallingConvention.StdCall)] public static extern int Resolution(nint h,uint i,out int w,out int height);
    [DllImport("kerncam",EntryPoint="Kerncam_put_eSize",CallingConvention=CallingConvention.StdCall)] public static extern int Size(nint h,uint i);
    [DllImport("kerncam",EntryPoint="Kerncam_put_Option",CallingConvention=CallingConvention.StdCall)] public static extern int Option(nint h,uint option,int value);
    [DllImport("kerncam",EntryPoint="Kerncam_StartPullModeWithCallback",CallingConvention=CallingConvention.StdCall)] public static extern int Start(nint h,EventCallback callback,nint context);
    [DllImport("kerncam",EntryPoint="Kerncam_PullImageV2",CallingConvention=CallingConvention.StdCall)] public static extern int Pull(nint h,nint buffer,int bits,out FrameInfo info);
    [DllImport("kerncam",EntryPoint="Kerncam_Stop",CallingConvention=CallingConvention.StdCall)] public static extern int Stop(nint h);
}
