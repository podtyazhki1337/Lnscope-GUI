using System.Runtime.InteropServices;
using LnScope.Core.Devices;

namespace GUI.Devices;

public sealed partial class KernCamera
{
    internal void RefreshAutoExposure()
    {
        if (!_streaming || State != DeviceState.Ready || _handle == 0) return;
        foreach (var control in Properties.OfType<NumericProperty>())
        {
            if (!control.IsReadOnly) continue;
            if (control.Id == "exposure" && KernNative.GetExpoTime(_handle, out var us) >= 0)
                control.SetFromDevice(us / 1000.0);
            if (control.Id == "gain" && KernNative.GetExpoAGain(_handle, out var gain) >= 0)
                control.SetFromDevice(gain);
        }
    }

    private IReadOnlyList<DeviceProperty> ReadImageProperties()
    {
        var h = _handle;
        var controls = new List<DeviceProperty>();
        NumericProperty? exposure = null, gain = null;
        bool hasAuto = KernNative.HasPair("AutoExpoEnable");
        int automatic = 0;
        hasAuto = hasAuto && KernNative.GetAutoExpoEnable(h, out automatic) >= 0;
        void Write(Func<int> send, string name)
        {
            if (State != DeviceState.Ready || _handle != h)
                throw new InvalidOperationException("Reconnect the camera before changing its settings.");
            KernNative.Check(send(), name);
        }
        if (hasAuto)
            controls.Add(new BooleanProperty("auto_exposure", "Auto exposure", automatic != 0, enabled =>
            {
                Write(() => KernNative.PutAutoExpoEnable(h, enabled ? 1 : 0), "set auto exposure");
                if (exposure != null)
                {
                    exposure.IsReadOnly = enabled;
                    if (KernNative.GetExpoTime(h, out var us) >= 0) exposure.SetFromDevice(us / 1000.0);
                }
                if (gain != null)
                {
                    gain.IsReadOnly = enabled;
                    if (KernNative.GetExpoAGain(h, out var percent) >= 0) gain.SetFromDevice(percent);
                }
            }));
        if (KernNative.HasPair("ExpoTime") && KernNative.Has("get_ExpTimeRange") &&
            KernNative.ExpTimeRange(h, out var minUs, out var maxUs, out _) >= 0 && minUs < maxUs &&
            KernNative.GetExpoTime(h, out var currentUs) >= 0)
        {
            exposure = new("exposure", "Exposure time", currentUs / 1000.0,
                ms => Write(() => KernNative.PutExpoTime(h, (uint)Math.Round(ms * 1000)), "set exposure time"))
                { Minimum = minUs / 1000.0, Maximum = maxUs / 1000.0, Step = 0.001, Unit = "ms", IsReadOnly = automatic != 0 };
            controls.Add(exposure);
        }
        if (KernNative.HasPair("ExpoAGain") && KernNative.Has("get_ExpoAGainRange") &&
            KernNative.ExpoAGainRange(h, out var minGain, out var maxGain, out _) >= 0 && minGain < maxGain &&
            KernNative.GetExpoAGain(h, out var currentGain) >= 0)
        {
            gain = new("gain", "Analog gain", currentGain,
                value => Write(() => KernNative.PutExpoAGain(h, (ushort)Math.Round(value)), "set analog gain"))
                { Minimum = minGain, Maximum = maxGain, Unit = "%", IsReadOnly = automatic != 0 };
            controls.Add(gain);
        }

        void AddColor(string name, int min, int max, KernNative.ReadInt read, Func<nint,int,int> write)
        {
            if (KernNative.HasPair(name) && read(h, out var value) >= 0)
                controls.Add(new NumericProperty(name, name, value,
                    v => Write(() => write(h, (int)Math.Round(v)), "set " + name))
                    { Minimum = min, Maximum = max });
        }
        AddColor("Hue", -180, 180, KernNative.GetHue, KernNative.PutHue);
        AddColor("Saturation", 0, 255, KernNative.GetSaturation, KernNative.PutSaturation);
        AddColor("Brightness", -255, 255, KernNative.GetBrightness, KernNative.PutBrightness);
        AddColor("Contrast", -255, 255, KernNative.GetContrast, KernNative.PutContrast);
        AddColor("Gamma", 20, 180, KernNative.GetGamma, KernNative.PutGamma);
        if (KernNative.HasPair("TempTint") && KernNative.GetTempTint(h, out var temp, out var tint) >= 0)
        {
            void WhiteBalance(int? newTemp, int? newTint) => Write(() =>
            {
                int hr = KernNative.GetTempTint(h, out var t, out var i);
                return hr < 0 ? hr : KernNative.PutTempTint(h, newTemp ?? t, newTint ?? i);
            }, "set white balance");
            controls.Add(new NumericProperty("temperature", "White balance", temp, v => WhiteBalance((int)Math.Round(v), null))
                { Minimum = 2000, Maximum = 15000, Unit = "K" });
            controls.Add(new NumericProperty("tint", "Tint", tint, v => WhiteBalance(null, (int)Math.Round(v)))
                { Minimum = 200, Maximum = 2500 });
        }
        return controls;
    }
}

internal static partial class KernNative
{
    public delegate int ReadInt(nint h, out int value);
    public static bool Has(string suffix) => NativeLibrary.TryGetExport(Library.Value, "Kerncam_" + suffix, out _);
    public static bool HasPair(string name) => Has("get_" + name) && Has("put_" + name);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_AutoExpoEnable")] internal static extern int GetAutoExpoEnable(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_AutoExpoEnable")] internal static extern int PutAutoExpoEnable(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_ExpoTime")] internal static extern int GetExpoTime(nint h, out uint value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_ExpoTime")] internal static extern int PutExpoTime(nint h, uint value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_ExpoAGain")] internal static extern int GetExpoAGain(nint h, out ushort value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_ExpoAGain")] internal static extern int PutExpoAGain(nint h, ushort value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_Hue")] internal static extern int GetHue(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_Hue")] internal static extern int PutHue(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_Saturation")] internal static extern int GetSaturation(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_Saturation")] internal static extern int PutSaturation(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_Brightness")] internal static extern int GetBrightness(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_Brightness")] internal static extern int PutBrightness(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_Contrast")] internal static extern int GetContrast(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_Contrast")] internal static extern int PutContrast(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_Gamma")] internal static extern int GetGamma(nint h, out int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_Gamma")] internal static extern int PutGamma(nint h, int value);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_ExpTimeRange")] internal static extern int ExpTimeRange(nint h, out uint min, out uint max, out uint defaultValue);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_ExpoAGainRange")] internal static extern int ExpoAGainRange(nint h, out ushort min, out ushort max, out ushort defaultValue);
    [DllImport("kerncam", EntryPoint = "Kerncam_get_TempTint")] internal static extern int GetTempTint(nint h, out int temp, out int tint);
    [DllImport("kerncam", EntryPoint = "Kerncam_put_TempTint")] internal static extern int PutTempTint(nint h, int temp, int tint);
}
