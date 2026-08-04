using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed record InstallationResult(string DeviceId, bool ReusedCredentials);

internal sealed class InstallCoordinator
{
    private readonly WindowsCredentialStore _credentialStore;
    private readonly WindowsServiceInstaller _serviceInstaller;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly WolletApiClient _apiClient;

    public InstallCoordinator(
        WindowsCredentialStore credentialStore,
        WindowsServiceInstaller serviceInstaller,
        IDeviceInfoProvider deviceInfoProvider,
        WolletApiClient apiClient)
    {
        _credentialStore = credentialStore;
        _serviceInstaller = serviceInstaller;
        _deviceInfoProvider = deviceInfoProvider;
        _apiClient = apiClient;
    }

    public async Task<ClientCredentials?> TryLoadExistingAsync(CancellationToken cancellationToken) =>
        await _credentialStore.TryLoadAsync(cancellationToken);

    public async Task<InstallationResult> InstallAsync(
        string serverValue,
        string token,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var server = ServerAddress.Normalize(serverValue);
        progress.Report("正在验证安装程序…");
        _serviceInstaller.ValidateSource();
        progress.Report("正在检查现有配置…");
        ClientCredentials? existing;
        try
        {
            existing = await _credentialStore.TryLoadAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            progress.Report("现有配置不可用，将使用新 Token 重新绑定…");
            existing = null;
        }
        ClientCredentials? credentials = null;
        var reusedCredentials = false;

        if (existing is not null && ServerAddress.AreEquivalent(existing.Server, server))
        {
            try
            {
                await _apiClient.GetCurrentDeviceAsync(existing, cancellationToken);
                credentials = existing;
                reusedCredentials = true;
            }
            catch (WolletApiException exception) when (exception.IsInvalidDeviceCredentials)
            {
            }
        }

        DeviceIdentity? identity = null;
        if (credentials is null)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("当前没有有效配置，请输入绑定 Token", nameof(token));
            }

            progress.Report("正在确定设备网卡…");
            identity = await _deviceInfoProvider.GetAsync(server, cancellationToken);
        }

        if (credentials is null)
        {
            progress.Report("正在绑定设备…");
            credentials = await _apiClient.BindAsync(server, token, identity!, cancellationToken);
            progress.Report("正在安全保存设备凭据…");
            await _credentialStore.SaveAsync(credentials, cancellationToken);
        }

        progress.Report("正在准备 Windows Service…");
        await _serviceInstaller.PrepareAsync(cancellationToken);
        progress.Report("正在启动 Windows Service…");
        await _serviceInstaller.StartAsync(cancellationToken);
        return new InstallationResult(credentials.DeviceId, reusedCredentials);
    }
}
