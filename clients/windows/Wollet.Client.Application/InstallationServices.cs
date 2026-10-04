using Wollet.Client.Core;

namespace Wollet.Client;

internal interface IInstallCoordinator
{
    ClientUpdateVersion GetVersions();
    Task<StartupInspectionResult> InspectAsync(CancellationToken cancellationToken);
    Task<InstallationResult> InstallAsync(string server, string token, IProgress<string> progress, CancellationToken cancellationToken);
    Task<InstallationResult> UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken);
    Task<UninstallationResult> UninstallAsync(IProgress<string> progress, CancellationToken cancellationToken);
}

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
