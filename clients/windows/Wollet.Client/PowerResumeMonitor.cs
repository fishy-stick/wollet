using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wollet.Client;

// Service-compatible power notifications; no dependency on an interactive message loop.
internal sealed class PowerResumeMonitor : IDisposable
{
    private readonly Callback _callback;
    private readonly IntPtr _registration;
    private int _changed;
    public PowerResumeMonitor()
    {
        _callback = (_, type, _) => { if (type is 4 or 7 or 18) Interlocked.Exchange(ref _changed, 1); return 0; };
        var parameters = new Parameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback) };
        var result = PowerRegisterSuspendResumeNotification(2, ref parameters, out _registration);
        if (result != 0) throw new Win32Exception((int)result, "无法订阅系统电源状态");
    }
    public bool ConsumeChange() => Interlocked.Exchange(ref _changed, 0) != 0;
    public void Dispose() { PowerUnregisterSuspendResumeNotification(_registration); GC.KeepAlive(_callback); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public IntPtr Callback; public IntPtr Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Parameters recipient, out IntPtr registration);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
