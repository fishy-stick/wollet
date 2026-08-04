using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wollet.Client;

internal enum WindowsServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Stopping,
    Running,
    Paused,
    Transitioning,
    Unknown,
}

internal sealed class WindowsServiceInstaller
{
    public const string ServiceName = "Wollet";
    public const string EventSourceName = "wollet-client";
    private const string DisplayName = "Wollet Client";
    private const string ServiceAccount = "NT AUTHORITY\\LocalService";
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceDelete = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceNoChange = 0xffffffff;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceContinuePending = 0x00000005;
    private const int ServicePausePending = 0x00000006;
    private const int ServicePaused = 0x00000007;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigFailureActions = 2;
    private const int ServiceConfigFailureActionsFlag = 4;
    private const int ScActionRestart = 1;
    private const uint RecoveryResetPeriodSeconds = 24 * 60 * 60;
    private const uint MoveFileDelayUntilReboot = 0x00000004;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceMarkedForDelete = 1072;
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(20);

    private readonly WindowsPaths _paths;

    public WindowsServiceInstaller(WindowsPaths paths)
    {
        _paths = paths;
    }

    public WindowsServiceState GetState()
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceName, ServiceQueryStatus);
        if (service is null)
        {
            return WindowsServiceState.NotInstalled;
        }

        return QueryState(service) switch
        {
            ServiceStopped => WindowsServiceState.Stopped,
            ServiceStartPending => WindowsServiceState.Starting,
            ServiceStopPending => WindowsServiceState.Stopping,
            ServiceRunning => WindowsServiceState.Running,
            ServicePaused => WindowsServiceState.Paused,
            ServiceContinuePending or ServicePausePending => WindowsServiceState.Transitioning,
            _ => WindowsServiceState.Unknown,
        };
    }

    public void ValidateSource()
    {
        var sourceExecutable = GetSourceExecutable();
        if (!PathsEqual(sourceExecutable, _paths.InstalledExecutable) &&
            File.Exists(Path.ChangeExtension(sourceExecutable, ".deps.json")))
        {
            throw new InvalidOperationException("请使用单文件发布版本执行安装");
        }
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ValidateSource();
        var sourceExecutable = GetSourceExecutable();
        WindowsServiceSecurity.EnsureInstallDirectory(_paths.InstallDirectory);
        WindowsServiceSecurity.EnsureLocalServiceCanShutdown();
        EnsureEventSource();

        using var manager = OpenManager(ScManagerConnect | ScManagerCreateService);
        using var existing = TryOpenService(
            manager,
            ServiceName,
            ServiceQueryStatus | ServiceStart | ServiceStop | ServiceChangeConfig);
        if (existing is not null)
        {
            await StopAsync(existing, cancellationToken);
        }

        if (!PathsEqual(sourceExecutable, _paths.InstalledExecutable))
        {
            var temporary = _paths.InstalledExecutable + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(sourceExecutable, temporary, overwrite: false);
                File.Move(temporary, _paths.InstalledExecutable, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        var commandLine = $"\"{_paths.InstalledExecutable}\" --service";
        if (existing is null)
        {
            using var created = CreateServiceW(
                manager,
                ServiceName,
                DisplayName,
                ServiceQueryStatus | ServiceStart | ServiceStop | ServiceChangeConfig,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                commandLine,
                null,
                IntPtr.Zero,
                null,
                ServiceAccount,
                null);
            ThrowIfInvalid(created, "无法创建 Windows Service");
            SetDescription(created);
            SetRecoveryPolicy(created);
        }
        else
        {
            if (!ChangeServiceConfigW(
                    existing,
                    ServiceNoChange,
                    ServiceAutoStart,
                    ServiceErrorNormal,
                    commandLine,
                    null,
                    IntPtr.Zero,
                    null,
                    ServiceAccount,
                    null,
                    DisplayName))
            {
                throw LastWin32Exception("无法更新 Windows Service");
            }

            SetDescription(existing);
            SetRecoveryPolicy(existing);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceName, ServiceQueryStatus | ServiceStart)
            ?? throw new InvalidOperationException("Windows Service 尚未安装");
        var state = QueryState(service);
        if (state == ServiceRunning)
        {
            return;
        }

        if (state != ServiceStartPending && !StartServiceW(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error, "无法启动 Windows Service");
            }
        }

        await WaitForStateAsync(service, ServiceRunning, cancellationToken);
    }

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken)
    {
        using (var manager = OpenManager(ScManagerConnect))
        using (var service = TryOpenService(
                   manager,
                   ServiceName,
                   ServiceQueryStatus | ServiceStop | ServiceDelete))
        {
            if (service is not null)
            {
                await StopAsync(service, cancellationToken);
                if (!DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceMarkedForDelete)
                    {
                        throw new Win32Exception(error, "无法删除 Windows Service");
                    }
                }
            }
        }

        RemoveEventSource();
        return DeleteInstalledFiles();
    }

    private bool DeleteInstalledFiles()
    {
        if (!Directory.Exists(_paths.InstallDirectory))
        {
            return false;
        }

        var runningFromInstalledPath = File.Exists(_paths.InstalledExecutable) &&
                                       PathsEqual(GetSourceExecutable(), _paths.InstalledExecutable);
        if (!runningFromInstalledPath)
        {
            Directory.Delete(_paths.InstallDirectory, recursive: true);
            return false;
        }

        if (!MoveFileExW(_paths.InstalledExecutable, null, MoveFileDelayUntilReboot))
        {
            throw LastWin32Exception("无法安排删除已安装程序");
        }

        if (!MoveFileExW(_paths.InstallDirectory, null, MoveFileDelayUntilReboot))
        {
            throw LastWin32Exception("无法安排删除安装目录");
        }

        return true;
    }

    private static async Task StopAsync(SafeServiceHandle service, CancellationToken cancellationToken)
    {
        var state = QueryState(service);
        if (state == ServiceStopped)
        {
            return;
        }

        if (state != ServiceStopPending && !ControlService(service, ServiceControlStop, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
            {
                throw new Win32Exception(error, "无法停止现有 Windows Service");
            }
        }

        await WaitForStateAsync(service, ServiceStopped, cancellationToken);
    }

    private static async Task WaitForStateAsync(
        SafeServiceHandle service,
        int expectedState,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateChangeTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (QueryState(service) == expectedState)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException("等待 Windows Service 状态变更超时");
    }

    private static int QueryState(SafeServiceHandle service)
    {
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw LastWin32Exception("无法查询 Windows Service 状态");
        }

        return status.CurrentState;
    }

    private static SafeServiceHandle OpenManager(uint access)
    {
        var manager = OpenSCManagerW(null, null, access);
        ThrowIfInvalid(manager, "无法连接 Windows Service Control Manager");
        return manager;
    }

    private static SafeServiceHandle? TryOpenService(
        SafeServiceHandle manager,
        string name,
        uint access)
    {
        var service = OpenServiceW(manager, name, access);
        if (!service.IsInvalid)
        {
            return service;
        }

        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (error == ErrorServiceDoesNotExist)
        {
            return null;
        }

        throw new Win32Exception(error, "无法打开 Windows Service");
    }

    private static void SetDescription(SafeServiceHandle service)
    {
        var descriptionText = Marshal.StringToHGlobalUni("连接 Wollet 服务端并接收远程关机指令");
        try
        {
            var description = new ServiceDescription { Description = descriptionText };
            if (!ChangeServiceConfig2W(service, ServiceConfigDescription, ref description))
            {
                throw LastWin32Exception("无法设置 Windows Service 描述");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptionText);
        }
    }

    private static void SetRecoveryPolicy(SafeServiceHandle service)
    {
        ServiceAction[] actions =
        [
            new() { Type = ScActionRestart, DelayMilliseconds = 5_000 },
            new() { Type = ScActionRestart, DelayMilliseconds = 15_000 },
            new() { Type = ScActionRestart, DelayMilliseconds = 30_000 },
        ];
        var actionSize = Marshal.SizeOf<ServiceAction>();
        var actionPointer = Marshal.AllocHGlobal(checked(actionSize * actions.Length));
        try
        {
            for (var index = 0; index < actions.Length; index++)
            {
                Marshal.StructureToPtr(actions[index], actionPointer + (index * actionSize), fDeleteOld: false);
            }

            var failureActions = new ServiceFailureActions
            {
                ResetPeriodSeconds = RecoveryResetPeriodSeconds,
                ActionCount = (uint)actions.Length,
                Actions = actionPointer,
            };
            if (!ChangeServiceFailureActions(
                    service,
                    ServiceConfigFailureActions,
                    ref failureActions))
            {
                throw LastWin32Exception("无法设置 Windows Service 恢复策略");
            }

            var failureActionsFlag = new ServiceFailureActionsFlag { Enabled = true };
            if (!ChangeServiceFailureActionsFlag(
                    service,
                    ServiceConfigFailureActionsFlag,
                    ref failureActionsFlag))
            {
                throw LastWin32Exception("无法启用 Windows Service 恢复策略");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actionPointer);
        }
    }

    private static void RemoveEventSource()
    {
        if (EventLog.SourceExists(EventSourceName))
        {
            EventLog.DeleteEventSource(EventSourceName);
        }
    }

    private static void EnsureEventSource()
    {
        if (!EventLog.SourceExists(EventSourceName))
        {
            EventLog.CreateEventSource(EventSourceName, "Application");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string GetSourceExecutable() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径");

    private static void ThrowIfInvalid(SafeServiceHandle handle, string message)
    {
        if (handle.IsInvalid)
        {
            throw LastWin32Exception(message);
        }
    }

    private static Win32Exception LastWin32Exception(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceAction
    {
        public int Type;
        public uint DelayMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriodSeconds;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActionsFlag
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enabled;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(
        SafeServiceHandle manager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle CreateServiceW(
        SafeServiceHandle manager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string serviceStartName,
        string? password,
        string displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(
        SafeServiceHandle service,
        int infoLevel,
        ref ServiceDescription info);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceFailureActions(
        SafeServiceHandle service,
        int infoLevel,
        ref ServiceFailureActions info);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceFailureActionsFlag(
        SafeServiceHandle service,
        int infoLevel,
        ref ServiceFailureActionsFlag info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(
        SafeServiceHandle service,
        int argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string? newFileName,
        uint flags);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
