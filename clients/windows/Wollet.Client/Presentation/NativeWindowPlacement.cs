using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wollet.Client;

internal static class NativeWindowPlacement
{
    public static void ConstrainToWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(handle, 1), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        // Keep actions reachable even on a small work area at a large DPI scale.
        window.MaxHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        window.MaxWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
    }

    public static void CenterInWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(handle, out var bounds)) return;
        SetWindowPos(handle, 0,
            Math.Max(info.Work.Left, info.Work.Left + (info.Work.Right - info.Work.Left - bounds.Right + bounds.Left) / 2),
            Math.Max(info.Work.Top, info.Work.Top + (info.Work.Bottom - info.Work.Top - bounds.Bottom + bounds.Top) / 2),
            0, 0, 0x01 | 0x04 | 0x10); // no size, no z-order, no activation
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
