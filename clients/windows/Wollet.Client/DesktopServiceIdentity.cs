using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wollet.Client;

// The SCM registration is administrator-controlled. Querying its PID does not
// require opening the LocalService process or weakening the process DACL.
internal static class DesktopServiceIdentity
{
    internal static uint RunningProcessId()
    {
        using var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) throw Failure("无法查询后台服务");
        using var service = OpenService(manager, "Wollet", 0x0004); // SERVICE_QUERY_STATUS
        if (service.IsInvalid) throw Failure("无法查询 Wollet 服务，请检查客户端安装");
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
            throw Failure("无法读取后台服务状态");
        if (status.State != 4 || status.ProcessId == 0)
            throw new IOException("后台服务未运行或正在重启");
        if (status.Type != 0x10) throw new IOException("后台服务身份无效：服务类型不符");
        return status.ProcessId;
    }

    internal static void Verify(NamedPipeClientStream pipe, uint expectedProcessId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pipeProcessId))
            throw Failure("无法验证后台服务管道");
        // Re-read after connecting so a service restart cannot validate a stale PID.
        Validate(expectedProcessId, RunningProcessId(), pipeProcessId);
    }

    internal static void Validate(uint before, uint after, uint pipeProcessId)
    {
        if (before == 0 || before != after || after != pipeProcessId)
            throw new IOException("后台服务身份无效：管道进程与 Wollet 服务不匹配");
    }

    private static IOException Failure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level,
        out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
