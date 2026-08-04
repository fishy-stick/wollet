using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class WindowsCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly WindowsPaths _paths;

    public WindowsCredentialStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public async Task<ClientCredentials?> TryLoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ConfigFile))
        {
            return null;
        }

        await using var stream = new FileStream(
            _paths.ConfigFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        StoredConfiguration stored;
        try
        {
            stored = await JsonSerializer.DeserializeAsync<StoredConfiguration>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("本地客户端配置为空");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本地客户端配置不是有效的 JSON", exception);
        }

        if (stored.Version != 1 ||
            string.IsNullOrWhiteSpace(stored.Server) ||
            string.IsNullOrWhiteSpace(stored.DeviceId) ||
            string.IsNullOrWhiteSpace(stored.ProtectedDeviceSecret))
        {
            throw new InvalidDataException("本地客户端配置不完整");
        }

        try
        {
            return new ClientCredentials(
                ServerAddress.Normalize(stored.Server),
                stored.DeviceId,
                Dpapi.Unprotect(stored.ProtectedDeviceSecret));
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            throw new InvalidDataException("本地客户端配置包含无效内容", exception);
        }
    }

    public async Task SaveAsync(ClientCredentials credentials, CancellationToken cancellationToken)
    {
        EnsureSecureDirectory();
        var stored = new StoredConfiguration(
            1,
            credentials.Server.AbsoluteUri.TrimEnd('/'),
            credentials.DeviceId,
            Dpapi.Protect(credentials.DeviceSecret));
        var temporary = _paths.ConfigFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _paths.ConfigFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Delete()
    {
        if (Directory.Exists(_paths.ConfigDirectory))
        {
            Directory.Delete(_paths.ConfigDirectory, recursive: true);
        }
    }

    private void EnsureSecureDirectory()
    {
        var directory = Directory.CreateDirectory(_paths.ConfigDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AddRule(
            security,
            WellKnownSidType.LocalServiceSid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        directory.SetAccessControl(security);
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

    private sealed record StoredConfiguration(
        int Version,
        string Server,
        string DeviceId,
        string ProtectedDeviceSecret);
}
