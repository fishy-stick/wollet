using NuGet.Versioning;

namespace Wollet.Client.Core;

public enum ClientUpdateAction { Install, Update, Repair, Downgrade, Unknown }

public sealed record ClientUpdateVersion(string? Installed, string? Available, bool IsInstalled)
{
    public ClientUpdateAction Action
    {
        get
        {
            if (!IsInstalled) return ClientUpdateAction.Install;
            if (!NuGetVersion.TryParse(Installed, out var installed) || !NuGetVersion.TryParse(Available, out var available))
                return ClientUpdateAction.Unknown;
            // Compare release precedence, including prerelease labels, but not build metadata.
            return VersionComparer.VersionRelease.Compare(available, installed) switch
            {
                > 0 => ClientUpdateAction.Update,
                < 0 => ClientUpdateAction.Downgrade,
                _ => ClientUpdateAction.Repair,
            };
        }
    }
}
