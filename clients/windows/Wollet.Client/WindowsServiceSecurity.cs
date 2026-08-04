using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Wollet.Client;

internal static class WindowsServiceSecurity
{
    private const uint PolicyCreateAccount = 0x00000010;
    private const uint PolicyLookupNames = 0x00000800;
    private const string ShutdownPrivilege = "SeShutdownPrivilege";

    public static void EnsureInstallDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AddRule(
            security,
            WellKnownSidType.LocalServiceSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        AddRule(
            security,
            WellKnownSidType.BuiltinUsersSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        directory.SetAccessControl(security);
    }

    public static void EnsureLocalServiceCanShutdown()
    {
        var attributes = new LsaObjectAttributes
        {
            Length = (uint)Marshal.SizeOf<LsaObjectAttributes>(),
        };
        var status = LsaOpenPolicy(
            IntPtr.Zero,
            ref attributes,
            PolicyCreateAccount | PolicyLookupNames,
            out var policy);
        ThrowIfLsaError(status, "无法打开本地安全策略");
        using (policy)
        {
            var sid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);
            var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
            var privilegePointer = Marshal.StringToHGlobalUni(ShutdownPrivilege);
            try
            {
                Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
                var privilege = new LsaUnicodeString
                {
                    Length = checked((ushort)(ShutdownPrivilege.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((ShutdownPrivilege.Length + 1) * sizeof(char))),
                    Buffer = privilegePointer,
                };
                status = LsaAddAccountRights(policy, sidPointer, [privilege], 1);
                ThrowIfLsaError(status, "无法授予 Windows Service 关机权限");
            }
            finally
            {
                Marshal.FreeHGlobal(privilegePointer);
                Marshal.FreeHGlobal(sidPointer);
            }
        }
    }

    private static void AddRule(
        DirectorySecurity security,
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void ThrowIfLsaError(uint status, string message)
    {
        if (status == 0)
        {
            return;
        }

        throw new Win32Exception((int)LsaNtStatusToWinError(status), message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private sealed class SafeLsaPolicyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLsaPolicyHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => LsaClose(handle) == 0;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(
        IntPtr systemName,
        ref LsaObjectAttributes objectAttributes,
        uint desiredAccess,
        out SafeLsaPolicyHandle policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(
        SafeLsaPolicyHandle policyHandle,
        IntPtr accountSid,
        [In] LsaUnicodeString[] userRights,
        uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policyHandle);
}
