using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Wollet.Client.Core;

namespace Wollet.Client;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        var serviceMode = args.Contains("--service", StringComparer.OrdinalIgnoreCase) ||
                          WindowsServiceHelpers.IsWindowsService();
        if (serviceMode)
        {
            await RunServiceAsync(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        using var installerMutex = new Mutex(false, @"Global\Wollet.Client.Installer");
        bool ownsMutex;
        try { ownsMutex = installerMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            MessageBox.Show("已有客户端管理窗口正在运行，请关闭后重试。", "Wollet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var paths = new WindowsPaths();
        var credentialStore = new WindowsCredentialStore(paths);
        var deviceInfoProvider = new RouteDeviceInfoProvider();
        // LAN requests must not inherit the user's system or environment proxy.
        using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var coordinator = new InstallCoordinator(
            credentialStore,
            new WindowsServiceInstaller(paths),
            deviceInfoProvider,
            new WolletApiClient(httpClient));
        try { Application.Run(new InstallerForm(coordinator)); }
        finally { installerMutex.ReleaseMutex(); }
    }

    private static async Task RunServiceAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceInstaller.ServiceName);
        builder.Services.AddSingleton<WindowsPaths>();
        builder.Services.AddSingleton<WindowsCredentialStore>();
        builder.Services.AddSingleton<IDeviceInfoProvider, RouteDeviceInfoProvider>();
        builder.Services.AddSingleton<IShutdownController, WindowsShutdownController>();
        builder.Services.AddHostedService<WolletWorker>();
        await builder.Build().RunAsync();
    }
}
