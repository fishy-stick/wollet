using Wollet.Client.Core;

namespace Wollet.Client;

internal interface ICredentialStore
{
    Task<ClientCredentials?> TryLoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ClientCredentials credentials, CancellationToken cancellationToken);
    void Delete();
}

internal enum WindowsServiceState
{
    NotInstalled, Stopped, Starting, Stopping, Running, Paused, Transitioning, Unknown,
}

internal interface IClientInstaller
{
    WindowsServiceState GetState();
    ClientUpdateVersion GetVersions();
    void ValidateSource();
    Task InstallOrUpdateAsync(CancellationToken cancellationToken);
    Task<bool> UninstallAsync(CancellationToken cancellationToken);
}
