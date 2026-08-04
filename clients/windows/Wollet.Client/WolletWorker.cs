using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class WolletWorker : BackgroundService
{
    private readonly WindowsCredentialStore _credentialStore;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IShutdownController _shutdownController;
    private readonly ILogger<WolletWorker> _logger;

    public WolletWorker(
        WindowsCredentialStore credentialStore,
        IDeviceInfoProvider deviceInfoProvider,
        IShutdownController shutdownController,
        ILogger<WolletWorker> logger)
    {
        _credentialStore = credentialStore;
        _deviceInfoProvider = deviceInfoProvider;
        _shutdownController = shutdownController;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ClientCredentials? credentials;
        try
        {
            credentials = await _credentialStore.TryLoadAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "无法读取 Wollet 客户端配置，需要重新运行安装程序");
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        if (credentials is null)
        {
            _logger.LogError("未找到 Wollet 客户端配置，需要重新运行安装程序");
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        var runner = new WolletConnectionRunner(
            _deviceInfoProvider,
            _shutdownController,
            new LoggerClientLog(_logger));
        try
        {
            await runner.RunAsync(credentials, stoppingToken);
        }
        catch (DeviceCredentialsRejectedException exception)
        {
            _logger.LogError(exception, "设备凭据已失效，需要使用新 Token 重新绑定");
            await WaitUntilStoppedAsync(stoppingToken);
        }
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
