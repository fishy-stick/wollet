using System.Diagnostics;
using System.Security.Principal;
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
        if (args.Contains("--desktop", StringComparer.OrdinalIgnoreCase))
        {
            using var desktopMutex = DesktopLifetime.Acquire(out var first);
            if (first) Application.Run(new DesktopPlanContext());
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated-installer") { UseShellExecute = true, Verb = "runas" });
                // Keep the unelevated launcher alive so a completed installation can start
                // its desktop companion without inheriting the administrator token.
                while (elevated is not null && !elevated.WaitForExit(1000)) TryStartDesktop();
                TryStartDesktop();
            }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show("安装和管理客户端需要管理员权限。", "Wollet"); }
            return;
        }
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
        if (!args.Contains("--elevated-installer", StringComparer.OrdinalIgnoreCase)) TryStartDesktop();
    }

    private static void TryStartDesktop()
    {
        var paths = new WindowsPaths();
        if (!File.Exists(paths.InstalledExecutable) || File.Exists(Path.Combine(paths.InstallDirectory, "desktop.stop"))) return;
        using var run = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        if (run?.GetValue("WolletDesktop") is null) return;
        if (Mutex.TryOpenExisting(DesktopLifetime.MutexName(Process.GetCurrentProcess().SessionId), out var existing)) { existing.Dispose(); return; }
        using var desktop = Process.Start(new ProcessStartInfo(paths.InstalledExecutable, "--desktop") { UseShellExecute = false });
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
