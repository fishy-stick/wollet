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

        var engine = new ShutdownPlanEngine(new WindowsPlanStore(new WindowsPaths()), _shutdownController);
        try { await engine.InitializeAsync(stoppingToken); }
        catch (Exception exception) { _logger.LogError(exception, "无法恢复关机计划，暂停连接"); await WaitUntilStoppedAsync(stoppingToken); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var compatibility = new ConnectionCompatibility();
        var localServer = new DesktopPlanServer(engine, compatibility);
        var localTask = localServer.RunAsync(lifetime.Token);
        var clockTask = RunPlanClockAsync(engine, lifetime.Token);
        var runner = new WolletConnectionRunner(
            _deviceInfoProvider,
            _shutdownController,
            new LoggerClientLog(_logger), plans: engine, compatibility: compatibility);
        Task? connectionTask = null;
        try
        {
            connectionTask = runner.RunAsync(credentials, lifetime.Token);
            var completed = await Task.WhenAny(connectionTask, localTask, clockTask);
            await completed;
            if (completed != connectionTask) throw new IOException("本地关机服务意外退出");
        }
        catch (DeviceCredentialsRejectedException exception)
        {
            _logger.LogError(exception, "设备凭据已失效，需要使用新 Token 重新绑定");
            await engine.CancelLocalAsync("credentials_rejected", CancellationToken.None);
            await WaitUntilStoppedAsync(stoppingToken);
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(localTask, clockTask, connectionTask ?? Task.CompletedTask); }
            catch (Exception exception) { _logger.LogDebug(exception, "关机后台任务已结束"); }
            finally { await engine.CancelLocalAsync("client_restarted", CancellationToken.None); }
        }
    }

    private static async Task RunPlanClockAsync(ShutdownPlanEngine engine, CancellationToken token)
    {
        using var power = new PowerResumeMonitor();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(token))
        {
            if (power.ConsumeChange()) await engine.CancelLocalAsync("system_resumed", token);
            await engine.TickAsync(token);
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
