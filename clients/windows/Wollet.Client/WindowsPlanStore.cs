using System.Text.Json;
using Wollet.Client.Core;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Wollet.Client;

internal sealed class WindowsPlanStore : IShutdownPlanStore
{
    private readonly string _path;
    public WindowsPlanStore(WindowsPaths paths) => _path = Path.Combine(paths.ConfigDirectory, "plans", "state.json");
    public static void Prepare(WindowsPaths paths)
    {
        var folder = Directory.CreateDirectory(Path.Combine(paths.ConfigDirectory, "plans"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalServiceSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
    }
    public async Task<ShutdownJournal?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<ShutdownJournal>(stream, cancellationToken: token)
            ?? throw new InvalidDataException("关机计划存储为空");
    }
    public async Task SaveAsync(ShutdownJournal journal, CancellationToken token)
    {
        var temporary = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
