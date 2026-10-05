using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

[TestClass]
public sealed class PresentationStateTests
{
    private static readonly ClientCredentials Credentials = new(new Uri("http://server"), "device", "secret");
    private static ShutdownPlan Plan(string state = "scheduled", long revision = 1, long remaining = 10_000, string id = "plan") => new(id, revision, state, remaining);

    [TestMethod]
    public void CountdownUsesMonotonicTimeAndDoesNotExtendOnRepeatedSnapshots()
    {
        var clock = new ManualClock();
        var state = new CountdownState(clock);
        state.Observe(Plan(remaining: 7000));
        clock.Advance(1100);
        Assert.AreEqual("6", state.Display);
        state.Observe(Plan(remaining: 7000));
        Assert.AreEqual(5900d, state.RemainingMilliseconds);
        clock.Advance(5900);
        Assert.AreEqual("正在关机", state.Display);
        Assert.IsFalse(state.CanAct);
    }

    [TestMethod]
    public void TerminalRevisionsCannotBeResurrectedAndNewPlansResetDisplay()
    {
        var state = new CountdownState(new ManualClock());
        state.Observe(Plan("cancelled", 2, 0));
        Assert.IsFalse(state.Observe(Plan()));
        Assert.IsFalse(state.IsVisible);
        state.Observe(Plan(id: "replacement"));
        Assert.IsTrue(state.CanAct);
        Assert.AreEqual("10", state.Display);
        state.Observe(Plan("failed", 2, 0, "replacement"));
        Assert.IsTrue(state.IsFailed);
        Assert.AreEqual("关机失败", state.Display);
    }

    [TestMethod]
    public async Task PollingDoesNotOverlapAndDuplicateActionsAreSuppressed()
    {
        var blocked = new TaskCompletionSource<DesktopPlanResponse>();
        var requests = new List<DesktopPlanRequest>();
        using var controller = new DesktopPlanController((request, _) =>
        {
            requests.Add(request);
            return requests.Count == 1 ? Task.FromResult(new DesktopPlanResponse(Plan())) : blocked.Task;
        });
        await controller.PollAsync();
        var action = controller.RequestActionAsync("cancel");
        await controller.PollAsync();
        await controller.RequestActionAsync("execute");
        Assert.IsFalse(controller.State.CanAct);
        Assert.HasCount(2, requests);
        Assert.AreEqual(new DesktopPlanRequest("cancel", "plan", 1), requests[1]);
        blocked.SetResult(new(Plan("cancelled", 2, 0)));
        await action;
        Assert.IsFalse(controller.State.IsVisible);
    }

    [TestMethod]
    public async Task QueuedActionNeverTargetsAReplacementPlan()
    {
        var blocked = new TaskCompletionSource<DesktopPlanResponse>();
        var calls = 0;
        using var controller = new DesktopPlanController((_, _) => ++calls == 1
            ? Task.FromResult(new DesktopPlanResponse(Plan())) : blocked.Task);
        await controller.PollAsync();
        var poll = controller.PollAsync();
        var action = controller.RequestActionAsync("cancel");
        blocked.SetResult(new(Plan(id: "replacement")));
        await Task.WhenAll(poll, action);
        Assert.AreEqual(2, calls);
        Assert.AreEqual("replacement", controller.State.Plan?.OperationId);
        Assert.IsTrue(controller.State.CanAct);
    }

    [TestMethod]
    public async Task FailedActionKeepsPlanAndDoesNotClaimCancellation()
    {
        using var controller = new DesktopPlanController((request, _) => request.Action == "snapshot"
            ? Task.FromResult(new DesktopPlanResponse(Plan())) : throw new IOException("disconnected"));
        await controller.PollAsync();
        await controller.RequestActionAsync("cancel");
        Assert.AreEqual("scheduled", controller.State.Plan?.State);
        Assert.AreEqual("操作结果未确认，请重试", controller.State.Error);
        Assert.IsTrue(controller.State.CanAct);
        await controller.PollAsync();
        Assert.IsNull(controller.State.Error);
    }

    [TestMethod]
    public async Task RejectedActionStillAppliesAuthoritativeTerminalSnapshot()
    {
        using var controller = new DesktopPlanController((request, _) => Task.FromResult(request.Action == "snapshot"
            ? new DesktopPlanResponse(Plan()) : new DesktopPlanResponse(Plan("executing", 2, 0), false, "too_late")));
        await controller.PollAsync();
        await controller.RequestActionAsync("cancel");
        Assert.AreEqual("executing", controller.State.Plan?.State);
        Assert.IsFalse(controller.State.CanAct);
        Assert.AreEqual("关机已开始，无法取消", controller.State.Error);
    }

    [TestMethod]
    public async Task ConnectionWarningsAreDebouncedAndClearedOnRecovery()
    {
        var fail = true;
        using var controller = new DesktopPlanController((_, _) => fail
            ? throw new IOException("unavailable") : Task.FromResult(new DesktopPlanResponse(null)));
        var warnings = new List<string?>();
        controller.ConnectionWarningChanged += warnings.Add;
        await controller.PollAsync(); await controller.PollAsync();
        Assert.IsEmpty(warnings);
        await controller.PollAsync(); await controller.PollAsync();
        CollectionAssert.AreEqual(new[] { "unavailable" }, warnings);
        fail = false;
        await controller.PollAsync();
        Assert.HasCount(2, warnings);
        Assert.IsNull(warnings[1]);
    }

    [TestMethod]
    public async Task DisposalCancelsIpcAndDiscardsLateResponses()
    {
        var blocked = new TaskCompletionSource<DesktopPlanResponse>();
        CancellationToken sent = default;
        var controller = new DesktopPlanController((_, token) => { sent = token; return blocked.Task; });
        var changes = 0;
        controller.Changed += () => changes++;
        var polling = controller.PollAsync();
        controller.Dispose();
        Assert.IsTrue(sent.IsCancellationRequested);
        blocked.SetResult(new(Plan()));
        await polling;
        Assert.AreEqual(0, changes);
        Assert.IsNull(controller.State.Plan);
        await controller.DisposeAsync();
    }

    [TestMethod]
    public async Task InvalidWireDataBecomesARecoverableConnectionWarning()
    {
        using var controller = new DesktopPlanController((_, _) => throw new InvalidDataException("invalid payload"));
        await controller.PollAsync();
        Assert.AreEqual("invalid payload；正在重试…", controller.State.Error);
    }

    [TestMethod]
    [DataRow(ClientUpdateAction.Update, false, false, true)]
    [DataRow(ClientUpdateAction.Repair, true, true, true)]
    [DataRow(ClientUpdateAction.Downgrade, true, false, false)]
    [DataRow(ClientUpdateAction.Unknown, true, false, false)]
    public async Task InstallerPreservesVersionActionRules(ClientUpdateAction action, bool fields, bool install, bool update)
    {
        var coordinator = new FakeCoordinator { Versions = action switch
        {
            ClientUpdateAction.Update => new("1.1.0", "1.2.0", true),
            ClientUpdateAction.Repair => new("1.2.0", "1.2.0", true),
            ClientUpdateAction.Downgrade => new("1.3.0", "1.2.0", true),
            _ => new("unknown", "1.2.0", true),
        } };
        using var model = new InstallerViewModel(coordinator, new FakeInteraction());
        await model.RefreshAsync(true);
        Assert.AreEqual(fields, model.ShowBindingFields);
        Assert.AreEqual(install, model.InstallCommand.CanExecute(null));
        Assert.AreEqual(update, model.UpdateCommand.CanExecute(null));
        Assert.AreEqual(action == ClientUpdateAction.Repair ? "修复" : "更新", model.UpdateText);
    }

    [TestMethod]
    public async Task InstallerShowsSemanticVersionsWithoutBuildMetadata()
    {
        var coordinator = new FakeCoordinator { Versions = new("1.1.0+abc123", "1.2.0-dev.3+def456", true) };
        using var model = new InstallerViewModel(coordinator, new FakeInteraction());
        await model.RefreshAsync();
        Assert.AreEqual("已安装：1.1.0    当前程序：1.2.0-dev.3", model.Versions);
    }

    [TestMethod]
    public async Task RefreshNeverOverwritesUserInputOrOperationResult()
    {
        var blocked = new TaskCompletionSource<StartupInspectionResult>();
        var coordinator = new FakeCoordinator { Inspect = _ => blocked.Task };
        using var model = new InstallerViewModel(coordinator, new FakeInteraction());
        var refresh = model.RefreshAsync(true);
        model.ServerAddress = "http://edited";
        model.TokenText = "USER-TOKEN";
        blocked.SetResult(new(Credentials, "online", false, true));
        await refresh;
        Assert.AreEqual("http://edited", model.ServerAddress);
        Assert.AreEqual("USER-TOKEN", model.TokenText);
        await model.UpdateCommand.ExecuteAsync();
        Assert.AreEqual("程序已更新／修复，现有配对保持不变。", model.OperationStatus);
        await model.RefreshAsync();
        Assert.AreEqual("程序已更新／修复，现有配对保持不变。", model.OperationStatus);
    }

    [TestMethod]
    public async Task OperationCancelsInspectionAndRejectsItsLateResult()
    {
        var blockedInspection = new TaskCompletionSource<StartupInspectionResult>();
        var blockedUpdate = new TaskCompletionSource<InstallationResult>();
        var coordinator = new FakeCoordinator();
        using var model = new InstallerViewModel(coordinator, new FakeInteraction());
        await model.RefreshAsync();
        CancellationToken inspectionToken = default;
        coordinator.Inspect = token => { inspectionToken = token; return blockedInspection.Task; };
        coordinator.Update = (_, _) => blockedUpdate.Task;
        var refresh = model.RefreshAsync();
        var update = model.UpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.IsBusy);
        Assert.IsTrue(inspectionToken.IsCancellationRequested);
        Assert.IsFalse(model.UninstallCommand.CanExecute(null));
        await model.UpdateCommand.ExecuteAsync();
        Assert.AreEqual(1, coordinator.UpdateCalls);
        blockedInspection.SetResult(new(null, "stale", true, false));
        await refresh;
        Assert.AreEqual("online", model.ConnectionStatus);
        coordinator.Inspect = _ => Task.FromResult(new StartupInspectionResult(Credentials, "recovered", false, true));
        blockedUpdate.SetResult(new("device", true));
        await update;
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual("recovered", model.ConnectionStatus);
    }

    [TestMethod]
    public async Task ClosedInstallerIgnoresInspectionCallbacks()
    {
        var blocked = new TaskCompletionSource<StartupInspectionResult>();
        var coordinator = new FakeCoordinator { Inspect = _ => blocked.Task };
        var model = new InstallerViewModel(coordinator, new FakeInteraction());
        var refreshing = model.RefreshAsync();
        var changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        model.Dispose();
        blocked.SetResult(new(Credentials, "late", false, true));
        await refreshing;
        Assert.AreEqual(0, changes);
        Assert.IsFalse(model.UpdateCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task LateProgressCannotOverwriteSuccessOrUpdateAClosedInstaller()
    {
        var coordinator = new FakeCoordinator();
        using var model = new InstallerViewModel(coordinator, new FakeInteraction());
        await model.RefreshAsync();
        var context = new QueuedContext();
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            coordinator.Update = (progress, _) =>
            {
                progress.Report("queued progress");
                return Task.FromResult(new InstallationResult("device", true));
            };
            var operation = model.UpdateCommand.ExecuteAsync();
            Assert.IsTrue(operation.IsCompletedSuccessfully);
            context.Drain();
            Assert.AreEqual("程序已更新／修复，现有配对保持不变。", model.OperationStatus);
            operation = model.UpdateCommand.ExecuteAsync();
            Assert.IsTrue(operation.IsCompletedSuccessfully);
            var changed = 0;
            model.PropertyChanged += (_, _) => changed++;
            model.Dispose();
            context.Drain();
            Assert.AreEqual(0, changed);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
    }

    [TestMethod]
    public async Task UninstallRequiresConfirmationAndResetsFieldsOnlyAfterSuccess()
    {
        var coordinator = new FakeCoordinator();
        var interaction = new FakeInteraction();
        using var model = new InstallerViewModel(coordinator, interaction) { ServerAddress = "http://server", TokenText = "TOKEN" };
        await model.UninstallCommand.ExecuteAsync();
        Assert.AreEqual(0, coordinator.UninstallCalls);
        Assert.AreEqual("TOKEN", model.TokenText);
        interaction.Confirm = true;
        await model.UninstallCommand.ExecuteAsync();
        Assert.AreEqual(1, coordinator.UninstallCalls);
        Assert.AreEqual("", model.ServerAddress);
        Assert.AreEqual("", model.TokenText);
        Assert.AreEqual(1, interaction.Uninstalled);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(long milliseconds) => _milliseconds += milliseconds;
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue(() => callback(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }
    private sealed class FakeInteraction : IInstallerInteraction
    {
        public bool Confirm;
        public int Uninstalled;
        public bool ConfirmUninstall() => Confirm;
        public void ShowError(string message) => Assert.Fail(message);
        public void ShowUninstalled(string message) => Uninstalled++;
    }
    private sealed class FakeCoordinator : IInstallCoordinator
    {
        public ClientUpdateVersion Versions = new("1.2.0", "1.2.0", true);
        public Func<CancellationToken, Task<StartupInspectionResult>> Inspect = _ => Task.FromResult(new StartupInspectionResult(Credentials, "online", false, true));
        public Func<IProgress<string>, CancellationToken, Task<InstallationResult>> Update = (_, _) => Task.FromResult(new InstallationResult("device", true));
        public int UpdateCalls, UninstallCalls;
        public ClientUpdateVersion GetVersions() => Versions;
        public Task<StartupInspectionResult> InspectAsync(CancellationToken token) => Inspect(token);
        public Task<InstallationResult> InstallAsync(string server, string token, IProgress<string> progress, CancellationToken cancellationToken) => Task.FromResult(new InstallationResult("device", false));
        public Task<InstallationResult> UpdateAsync(IProgress<string> progress, CancellationToken token) { UpdateCalls++; return Update(progress, token); }
        public Task<UninstallationResult> UninstallAsync(IProgress<string> progress, CancellationToken token) { UninstallCalls++; return Task.FromResult(new UninstallationResult(false)); }
    }
}
