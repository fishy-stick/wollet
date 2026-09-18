using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

[TestClass]
public sealed class UpdateTests
{
    [TestMethod]
    [DataRow("1.0.4", "1.1.0+abc", ClientUpdateAction.Update)]
    [DataRow("1.1.0+old", "1.1.0+new", ClientUpdateAction.Repair)]
    [DataRow("1.1.0.0", "1.1.0", ClientUpdateAction.Repair)]
    [DataRow("1.10.0", "1.9.0", ClientUpdateAction.Downgrade)]
    [DataRow("1.1.0", "1.0.4", ClientUpdateAction.Downgrade)]
    [DataRow("unknown", "1.1.0", ClientUpdateAction.Unknown)]
    [DataRow("1.1.0", "1.2.0-rc.1", ClientUpdateAction.Update)]
    [DataRow("1.0.4", "1.1.0-dev.1", ClientUpdateAction.Update)]
    [DataRow("1.1.0-dev.2", "1.1.0-dev.10", ClientUpdateAction.Update)]
    [DataRow("1.1.0-dev.10", "1.1.0-dev.2", ClientUpdateAction.Downgrade)]
    [DataRow("1.1.0-dev.10", "1.1.0-rc.1", ClientUpdateAction.Update)]
    [DataRow("1.1.0-rc.1", "1.1.0", ClientUpdateAction.Update)]
    [DataRow("1.1.0", "1.1.0-rc.2", ClientUpdateAction.Downgrade)]
    [DataRow("1.1.0-dev.1+abc", "1.1.0-dev.1+def", ClientUpdateAction.Repair)]
    [DataRow("1.1.0-RC.1", "1.1.0-rc.1", ClientUpdateAction.Repair)]
    [DataRow("1.1.0.1", "1.1.0.0", ClientUpdateAction.Downgrade)]
    [DataRow("1.1.0", "1.1.0-", ClientUpdateAction.Unknown)]
    [DataRow(null, "1.1.0", ClientUpdateAction.Unknown)]
    [DataRow("1.1.0", null, ClientUpdateAction.Unknown)]
    public void ComparesReleaseVersions(string? installed, string? available, ClientUpdateAction expected) =>
        Assert.AreEqual(expected, new ClientUpdateVersion(installed, available, true).Action);

    [TestMethod]
    public async Task UpdateUsesSavedCredentialsWithoutNetworkOrWrites()
    {
        var store = new MemoryStore();
        var installer = new FakeInstaller();
        using var http = new HttpClient(new RejectNetwork());
        var coordinator = new InstallCoordinator(store, installer, new RejectIdentity(), new WolletApiClient(http));
        var result = await coordinator.UpdateAsync(new Progress<string>(), CancellationToken.None);
        Assert.AreEqual("device", result.DeviceId);
        Assert.IsTrue(result.ReusedCredentials);
        Assert.AreEqual(1, installer.Calls);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task MissingCredentialsAndDowngradeDoNotTouchInstallation()
    {
        var store = new MemoryStore { Credentials = null };
        var installer = new FakeInstaller();
        using var http = new HttpClient(new RejectNetwork());
        var coordinator = new InstallCoordinator(store, installer, new RejectIdentity(), new WolletApiClient(http));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.UpdateAsync(new Progress<string>(), CancellationToken.None));
        store.Credentials = new(new Uri("http://server"), "device", "secret");
        installer.Versions = new("1.2.0", "1.1.0", true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.UpdateAsync(new Progress<string>(), CancellationToken.None));
        Assert.AreEqual(0, installer.Calls);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task FailedStartRestoresOldBinaryAndService()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var source = Path.Combine(folder.FullName, "download.exe");
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            var restored = false;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ClientBinaryDeployment.DeployAsync(
                source, destination, _ => Task.CompletedTask,
                _ => { Assert.AreEqual("new", File.ReadAllText(destination)); throw new IOException("start failed"); },
                _ => { Assert.AreEqual("old", File.ReadAllText(destination)); restored = true; return Task.CompletedTask; },
                CancellationToken.None));
            Assert.IsTrue(restored);
            Assert.AreEqual("old", File.ReadAllText(destination));
            Assert.HasCount(2, Directory.GetFiles(folder.FullName));
        }
        finally { folder.Delete(true); }
    }

    [TestMethod]
    public async Task StagingFailureDoesNotStopService()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(destination, "old");
            var stopped = false;
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => ClientBinaryDeployment.DeployAsync(
                Path.Combine(folder.FullName, "missing.exe"), destination,
                _ => { stopped = true; return Task.CompletedTask; },
                _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
            Assert.IsFalse(stopped);
            Assert.AreEqual("old", File.ReadAllText(destination));
        }
        finally { folder.Delete(true); }
    }

    [TestMethod]
    public async Task SuccessfulUpdateRemovesBackup()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var source = Path.Combine(folder.FullName, "download.exe");
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            await ClientBinaryDeployment.DeployAsync(source, destination,
                _ => Task.CompletedTask, _ => Task.CompletedTask,
                _ => throw new AssertFailedException("Recovery was not expected"), CancellationToken.None);
            Assert.AreEqual("new", File.ReadAllText(destination));
            Assert.HasCount(2, Directory.GetFiles(folder.FullName));
        }
        finally { folder.Delete(true); }
    }

    [TestMethod]
    public async Task CancellationAfterStoppingStillRestoresService()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var source = Path.Combine(folder.FullName, "download.exe");
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            using var cancellation = new CancellationTokenSource();
            var restored = false;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ClientBinaryDeployment.DeployAsync(
                source, destination,
                _ => { cancellation.Cancel(); return Task.CompletedTask; },
                _ => throw new AssertFailedException("Cancelled update must not start"),
                token => { Assert.IsFalse(token.IsCancellationRequested); restored = true; return Task.CompletedTask; },
                cancellation.Token));
            Assert.IsTrue(restored);
            Assert.AreEqual("old", File.ReadAllText(destination));
        }
        finally { folder.Delete(true); }
    }

    [TestMethod]
    public async Task FailedRecoveryRetainsBackup()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var source = Path.Combine(folder.FullName, "download.exe");
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            var stops = 0;
            await Assert.ThrowsExactlyAsync<AggregateException>(() => ClientBinaryDeployment.DeployAsync(
                source, destination,
                _ => { if (++stops > 1) throw new IOException("Cannot stop failed service"); return Task.CompletedTask; },
                _ => throw new IOException("Start failed"),
                _ => Task.CompletedTask, CancellationToken.None));
            var backups = Directory.GetFiles(folder.FullName, "*.backup-*");
            Assert.HasCount(1, backups);
            Assert.AreEqual("old", File.ReadAllText(backups[0]));
        }
        finally { folder.Delete(true); }
    }

    [TestMethod]
    public async Task RepairFromInstalledPathDoesNotReplaceBinary()
    {
        var folder = Directory.CreateTempSubdirectory("wollet-update-");
        try
        {
            var destination = Path.Combine(folder.FullName, "installed.exe");
            File.WriteAllText(destination, "old");
            var started = false;
            await ClientBinaryDeployment.DeployAsync(destination, destination,
                _ => Task.CompletedTask, _ => { started = true; return Task.CompletedTask; },
                _ => Task.CompletedTask, CancellationToken.None);
            Assert.IsTrue(started);
            Assert.AreEqual("old", File.ReadAllText(destination));
            Assert.HasCount(1, Directory.GetFiles(folder.FullName));
        }
        finally { folder.Delete(true); }
    }
    private sealed class MemoryStore : ICredentialStore
    {
        public ClientCredentials? Credentials { get; set; } = new(new Uri("http://server"), "device", "secret");
        public int Writes { get; private set; }
        public Task<ClientCredentials?> TryLoadAsync(CancellationToken token) => Task.FromResult(Credentials);
        public Task SaveAsync(ClientCredentials value, CancellationToken token) { Writes++; Credentials = value; return Task.CompletedTask; }
        public void Delete() => throw new AssertFailedException("Credentials must not be deleted");
    }

    private sealed class FakeInstaller : IClientInstaller
    {
        public int Calls { get; private set; }
        public ClientUpdateVersion Versions { get; set; } = new("1.0.4", "1.1.0", true);
        public ClientUpdateVersion GetVersions() => Versions;
        public WindowsServiceState GetState() => WindowsServiceState.Running;
        public void ValidateSource() { }
        public Task InstallOrUpdateAsync(CancellationToken token) { Calls++; return Task.CompletedTask; }
        public Task<bool> UninstallAsync(CancellationToken token) => throw new AssertFailedException("Must not uninstall");
    }

    private sealed class RejectIdentity : IDeviceInfoProvider
    {
        public Task<DeviceIdentity> GetAsync(Uri server, CancellationToken token) => throw new AssertFailedException("Must not rebind");
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new AssertFailedException("Update must work without contacting the server");
    }
}
