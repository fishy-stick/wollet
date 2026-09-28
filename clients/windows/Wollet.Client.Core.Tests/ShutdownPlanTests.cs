using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

[TestClass]
public sealed class ShutdownPlanTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    private sealed class Store : IShutdownPlanStore
    {
        public ShutdownJournal? Journal;
        public bool Fail;
        public Task<ShutdownJournal?> LoadAsync(CancellationToken token) => Task.FromResult(Journal);
        public Task SaveAsync(ShutdownJournal journal, CancellationToken token)
        {
            if (Fail) throw new IOException("disk unavailable");
            Journal = journal;
            return Task.CompletedTask;
        }
    }
    private sealed class Shutdown : IShutdownController
    {
        public int Calls;
        public bool Fail;
        public Task RequestShutdownAsync(CancellationToken token)
        { Calls++; if (Fail) throw new IOException("OS refused"); return Task.CompletedTask; }
    }
    private static ShutdownCommand Create() => new("shutdown_plan_create", Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
    private static ShutdownCommand Action(string action, ShutdownCommand create, long revision = 1) =>
        new("shutdown_plan_" + action, Guid.NewGuid().ToString(), create.OperationId, revision);

    [TestMethod]
    public async Task DuplicateCreateDoesNotRestartCountdownAndExpiryExecutesOnce()
    {
        var store = new Store(); var shutdown = new Shutdown(); var clock = new Clock();
        var engine = new ShutdownPlanEngine(store, shutdown, clock); await engine.InitializeAsync(default);
        var create = Create(); Assert.IsTrue((await engine.ApplyAsync(create, default)).Accepted);
        clock.Milliseconds = 7000;
        Assert.IsTrue((await engine.ApplyAsync(create, default)).Accepted);
        Assert.AreEqual(3000L, (await engine.SnapshotAsync(default))!.RemainingMilliseconds);
        clock.Milliseconds = 10000;
        await Task.WhenAll(engine.TickAsync(default), engine.TickAsync(default), engine.ApplyAsync(Action("execute", create), default));
        Assert.AreEqual(1, shutdown.Calls);
        Assert.AreEqual("submitted", (await engine.SnapshotAsync(default))!.State);
    }
    [TestMethod]
    public async Task LocalCancellationPreventsExecutionEvenWhenStorageFails()
    {
        var store = new Store(); var shutdown = new Shutdown(); var clock = new Clock();
        var engine = new ShutdownPlanEngine(store, shutdown, clock); await engine.InitializeAsync(default);
        await engine.ApplyAsync(Create(), default); store.Fail = true;
        await engine.CancelLocalAsync("local_user", default); clock.Milliseconds = 20000; await engine.TickAsync(default);
        Assert.AreEqual("cancelled", (await engine.SnapshotAsync(default))!.State);
        Assert.AreEqual(0, shutdown.Calls);
    }
    [TestMethod]
    public async Task ServiceRestartCancelsPendingPlanAndDuplicateCannotReviveIt()
    {
        var store = new Store(); var shutdown = new Shutdown(); var create = Create();
        var engine = new ShutdownPlanEngine(store, shutdown); await engine.InitializeAsync(default); await engine.ApplyAsync(create, default);
        var restored = new ShutdownPlanEngine(store, shutdown); await restored.InitializeAsync(default); await restored.ApplyAsync(create, default);
        var snapshot = await restored.SnapshotAsync(default);
        Assert.AreEqual("cancelled", snapshot!.State); Assert.AreEqual("client_restarted", snapshot.Reason);
        Assert.AreEqual(0, shutdown.Calls);
    }
    [TestMethod]
    public async Task ExecutionCrashRecoversAsIndeterminateWithoutRetry()
    {
        var create = Create(); var store = new Store { Journal = new(new(create.OperationId, 2, "executing", 0), new(), new()) };
        var shutdown = new Shutdown(); var engine = new ShutdownPlanEngine(store, shutdown); await engine.InitializeAsync(default); await engine.TickAsync(default);
        Assert.AreEqual("indeterminate", (await engine.SnapshotAsync(default))!.State); Assert.AreEqual(0, shutdown.Calls);
    }
    [TestMethod]
    public async Task DurableExecutionBoundaryAndSystemFailureNeverRetry()
    {
        var store = new Store(); var shutdown = new Shutdown { Fail = true }; var clock = new Clock();
        var engine = new ShutdownPlanEngine(store, shutdown, clock); await engine.InitializeAsync(default); var create = Create(); await engine.ApplyAsync(create, default);
        var execute = Action("execute", create); await engine.ApplyAsync(execute, default); await engine.ApplyAsync(execute, default); await engine.TickAsync(default);
        Assert.AreEqual("failed", (await engine.SnapshotAsync(default))!.State); Assert.AreEqual(1, shutdown.Calls);
        await engine.ApplyAsync(Create(), default); store.Fail = true; clock.Milliseconds = 10000; await engine.TickAsync(default);
        Assert.AreEqual("failed", (await engine.SnapshotAsync(default))!.State); Assert.AreEqual(1, shutdown.Calls);
    }
    [TestMethod]
    public async Task RevisionConflictAndRejectedCommandRemainIdempotent()
    {
        var store = new Store(); var shutdown = new Shutdown(); var engine = new ShutdownPlanEngine(store, shutdown); await engine.InitializeAsync(default);
        var create = Create(); await engine.ApplyAsync(create, default);
        var other = Create(); var rejected = await engine.ApplyAsync(other, default);
        Assert.AreEqual("active_plan_exists", rejected.Code);
        Assert.AreEqual("revision_conflict", (await engine.ApplyAsync(Action("cancel", create, 99), default)).Code);
        await engine.CancelLocalAsync("local_user", default);
        Assert.AreEqual(rejected, await engine.ApplyAsync(other, default));
        Assert.AreEqual("idempotency_conflict", (await engine.ApplyAsync(create with { DelaySeconds = 11 }, default)).Code);
    }
    [TestMethod]
    public async Task SubmittedHistoryDoesNotBlockNewPlanAfterServiceRestart()
    {
        var store = new Store(); var shutdown = new Shutdown();
        var engine = new ShutdownPlanEngine(store, shutdown); await engine.InitializeAsync(default);
        var create = Create(); await engine.ApplyAsync(create, default); await engine.ApplyAsync(Action("execute", create), default);
        var restored = new ShutdownPlanEngine(store, shutdown); await restored.InitializeAsync(default);
        Assert.IsNull(await restored.SnapshotAsync(default));
        Assert.AreEqual("submitted", (await restored.JournalAsync(default)).Plans![create.OperationId].State);
        Assert.IsTrue((await restored.ApplyAsync(Create(), default)).Accepted);
        Assert.AreEqual(1, shutdown.Calls);
    }

    [TestMethod]
    public async Task PipeRejectsOversizedFrames()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(4097));
        await Assert.ThrowsAsync<InvalidDataException>(() => DesktopPlanWire.ReadAsync<DesktopPlanRequest>(stream, default));
    }
}
