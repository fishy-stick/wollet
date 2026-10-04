using System.Runtime.InteropServices;

namespace Wollet.Client.Core;

public interface ISystemUptimeProvider
{
    long? ReadMilliseconds();
}

public sealed class WindowsSystemUptimeProvider : ISystemUptimeProvider
{
    // Native GetTickCount64 includes sleep; .NET's TickCount64 has different semantics.
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public long? ReadMilliseconds()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var value = GetTickCount64();
        return value <= 9_007_199_254_740_991UL ? (long)value : null;
    }
}

public sealed record ClientDeviceStatus(long? SystemUptimeMs);
