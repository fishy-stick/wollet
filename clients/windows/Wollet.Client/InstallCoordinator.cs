using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed record InstallationResult(string DeviceId, bool ReusedCredentials);

internal sealed record UninstallationResult(bool RebootRequired);

internal sealed record StartupInspectionResult(
    ClientCredentials? Credentials,
    string Message,
    bool IsError,
    bool IsSuccess);

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

    public async Task<StartupInspectionResult> InspectAsync(CancellationToken cancellationToken)
    {
        WindowsServiceState serviceState;
        try
        {
            serviceState = _serviceInstaller.GetState();
        }
        catch (Exception exception)
        {
            return new StartupInspectionResult(
                null,
                "后台服务状态检查失败：" + exception.Message,
                IsError: true,
                IsSuccess: false);
        }

        ClientCredentials? credentials;
        try
        {
            credentials = await _credentialStore.TryLoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new StartupInspectionResult(
                null,
                DescribeServiceState(serviceState) + "；本地配置读取失败：" + exception.Message,
                IsError: true,
                IsSuccess: false);
        }

        if (serviceState != WindowsServiceState.Running)
        {
            var configurationHint = credentials is null
                ? string.Empty
                : "；检测到现有绑定配置，可点击“安装并绑定”修复";
            return new StartupInspectionResult(
                credentials,
                DescribeServiceState(serviceState) + configurationHint + "。",
                IsError: serviceState is not WindowsServiceState.NotInstalled,
                IsSuccess: false);
        }

        if (credentials is null)
        {
            return new StartupInspectionResult(
                null,
                "后台服务：已安装且正在运行，但本地绑定配置缺失；请使用 Token 重新绑定。",
                IsError: true,
                IsSuccess: false);
        }

        try
        {
            var device = await _apiClient.GetCurrentDeviceAsync(credentials, cancellationToken);
            if (string.Equals(device.Status, "online", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupInspectionResult(
                    credentials,
                    "后台服务：已安装、正在运行，并已连接服务端。",
                    IsError: false,
                    IsSuccess: true);
            }

            var reportedStatus = string.IsNullOrWhiteSpace(device.Status) ? "未知" : device.Status;
            return new StartupInspectionResult(
                credentials,
                $"后台服务：已安装且正在运行，但服务端显示设备未连接（状态：{reportedStatus}）。",
                IsError: true,
                IsSuccess: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ConnectionError(credentials, "连接服务端超时");
        }
        catch (HttpRequestException exception)
        {
            return ConnectionError(credentials, "无法连接服务端：" + exception.Message);
        }
        catch (WolletApiException exception) when (exception.IsInvalidDeviceCredentials)
        {
            return ConnectionError(credentials, "设备凭据已失效，请使用新 Token 重新绑定");
        }
        catch (WolletApiException exception)
        {
            return ConnectionError(credentials, "服务端状态检查失败：" + exception.Message);
        }
        catch (ClientProtocolException exception)
        {
            return ConnectionError(credentials, "服务端响应无效：" + exception.Message);
        }
        catch (Exception exception)
        {
            return ConnectionError(credentials, "状态检查发生错误：" + exception.Message);
        }
    }

    private static StartupInspectionResult ConnectionError(ClientCredentials credentials, string detail) =>
        new(
            credentials,
            "后台服务：已安装且正在运行，但" + detail + "。",
            IsError: true,
            IsSuccess: false);

    private static string DescribeServiceState(WindowsServiceState state) => state switch
    {
        WindowsServiceState.NotInstalled => "后台服务：未安装",
        WindowsServiceState.Stopped => "后台服务：已安装但未运行",
        WindowsServiceState.Starting => "后台服务：正在启动",
        WindowsServiceState.Stopping => "后台服务：正在停止",
        WindowsServiceState.Running => "后台服务：已安装且正在运行",
        WindowsServiceState.Paused => "后台服务：已暂停",
        WindowsServiceState.Transitioning => "后台服务：正在切换状态",
        _ => "后台服务：状态未知",
    };

    public async Task<UninstallationResult> UninstallAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        progress.Report("正在停止并删除 Windows Service…");
        var rebootRequired = await _serviceInstaller.UninstallAsync(cancellationToken);
        progress.Report("正在删除本地设备凭据…");
        _credentialStore.Delete();
        return new UninstallationResult(rebootRequired);
    }

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
