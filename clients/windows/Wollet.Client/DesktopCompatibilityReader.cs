using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class DesktopCompatibilityReader : ICompatibilityReader
{
    public async Task<CompatibilityResult?> ReadAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            return (await DesktopPlanClient.SendAsync(new("compatibility"), deadline.Token)).Compatibility;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }
}
