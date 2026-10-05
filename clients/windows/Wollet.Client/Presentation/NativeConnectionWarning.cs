using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Wollet.Client;

// A transient shell warning, not a resident tray application. The HWND and icon
// registration live only while the service connection is failing.
internal sealed class NativeConnectionWarning : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private bool _added, _disposed;
    private string _message = "";

    public NativeConnectionWarning()
    {
        // A hidden top-level window receives Explorer's TaskbarCreated broadcast.
        // Message-only windows do not receive that broadcast.
        _source = new HwndSource(new HwndSourceParameters("Wollet connection warning") { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(WindowMessage);
    }

    public bool Show(string message)
    {
        if (_disposed) return false;
        _message = message;
        var data = CreateData();
        if (!_added)
        {
            data.Flags = 1 | 2 | 4 | 0x80; // message, icon, tooltip, standard tooltip with v4
            _added = Shell_NotifyIconW(0, ref data);
            if (!_added) { Trace.TraceWarning("Wollet: could not register the connection warning icon."); return false; }
            data.TimeoutOrVersion = 4;
            Shell_NotifyIconW(4, ref data);
        }
        data.Flags = 0x10; // NIF_INFO
        data.InfoTitle = "Wollet 桌面关机提示不可用";
        data.Info = Truncate(message + "。后台关机计划可能仍会执行，请在管理页面检查或取消。客户端正在重试。", 255);
        data.InfoFlags = 2; // NIIF_WARNING
        var shown = Shell_NotifyIconW(1, ref data);
        if (!shown) Trace.TraceWarning("Wollet: could not show the connection warning notification.");
        return shown;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if ((uint)message == _taskbarCreated) { _added = false; Show(_message); }
        if (message == CallbackMessage)
        {
            var notification = (int)(lParam.ToInt64() & 0xffff);
            if (notification is 0x400 or 0x401 or 0x202) Show(_message); // selection, keyboard selection, legacy click
            handled = true;
        }
        return 0;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = 1,
        Callback = CallbackMessage, Icon = LoadIconW(0, (nint)32515),
        Tip = "Wollet：桌面关机提示不可用", Info = "", InfoTitle = "",
    };
    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added) { var data = CreateData(); Shell_NotifyIconW(2, ref data); _added = false; }
        _source.RemoveHook(WindowMessage);
        _source.Dispose();
        // LoadIcon returns a shared system icon; it must not be destroyed here.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIconW(nint instance, nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string message);
}
