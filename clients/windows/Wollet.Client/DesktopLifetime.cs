using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Wollet.Client;

internal static class DesktopLifetime
{
    public static string MutexName(int session) => $@"Global\Wollet.Desktop.Session.{session}";
    public static Mutex Acquire(out bool first)
    {
        var acl = new MutexSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
        using var identity = WindowsIdentity.GetCurrent();
        acl.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
        return MutexAcl.Create(true, MutexName(Process.GetCurrentProcess().SessionId), out first, acl);
    }
    public static async Task WaitForExitAsync(CancellationToken token)
    {
        var sessions = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { sessions.Add(process.SessionId); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        await Task.Run(() =>
        {
            foreach (var session in sessions)
            {
                token.ThrowIfCancellationRequested();
                if (!Mutex.TryOpenExisting(MutexName(session), out var mutex)) continue;
                using (mutex)
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("桌面提示进程尚未退出，请关闭提示后重试更新。");
                    mutex.ReleaseMutex();
                }
            }
        }, token);
    }
}
