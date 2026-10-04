using System.IO.Pipes;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Windows.Tests;

public sealed partial class WindowTests
{
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("execute")]
    [DataRow("expiry")]
    [DataRow("failure")]
    public Task NamedPipeEngineAndWindowCompletePlanWithoutRealShutdown(string scenario) => OnUi(async () =>
    {
        var clock = new PipeClock();
        var shutdown = new RecordingShutdown { Fail = scenario == "failure" };
        var engine = new ShutdownPlanEngine(new MemoryJournal(), shutdown, clock);
        await engine.InitializeAsync(default);
        var pipeName = "Wollet.WpfTest." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = DesktopPlanServer.RunConnectionsAsync(pipe, async token =>
        {
            var request = await DesktopPlanWire.ReadAsync<DesktopPlanRequest>(pipe, token);
            DesktopPlanResponse response;
            if (request.Action == "snapshot") response = new(await engine.SnapshotAsync(token));
            else
            {
                var result = await engine.ApplyAsync(new("shutdown_plan_" + request.Action,
                    Guid.NewGuid().ToString(), request.OperationId!, request.Revision), token, "local_user");
                response = new(await engine.SnapshotAsync(token), result.Accepted, result.Code);
            }
            await DesktopPlanServer.WriteResponseAsync(pipe, response, token);
        }, lifetime.Token);
        await using var controller = new DesktopPlanController(async (request, token) =>
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(token);
            await DesktopPlanWire.WriteAsync(client, request, token);
            return await DesktopPlanWire.ReadAsync<DesktopPlanResponse>(client, token);
        }, clock);
        await using var desktop = new DesktopPresentation(controller,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stop"), () => Assert.Fail("Unexpected exit"));
        try
        {
            await engine.ApplyAsync(new("shutdown_plan_create", Guid.NewGuid().ToString(), Guid.NewGuid().ToString()), default);
            await controller.PollAsync();
            await Rendered();
            var window = _application.Windows.OfType<CountdownWindow>().Single();
            if (scenario is "cancel" or "execute")
            {
                ((Button)window.FindName(scenario == "cancel" ? "CancelAction" : "ExecuteAction"))
                    .RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
                // The real window event starts an async pipe request; allow its acknowledgement to arrive.
                for (var i = 0; i < 100 && controller.State.ActionPending; i++) await Task.Delay(10);
                Assert.IsFalse(controller.State.ActionPending, "Pipe action was not acknowledged.");
            }
            else
            {
                clock.Milliseconds = 10001;
                await engine.TickAsync(default);
                await controller.PollAsync();
            }
            var expected = scenario == "cancel" ? "cancelled" : scenario == "failure" ? "failed" : "submitted";
            Assert.AreEqual(expected, controller.State.Plan!.State);
            Assert.AreEqual(scenario == "cancel" ? 0 : 1, shutdown.Calls);
            await engine.TickAsync(default);
            await controller.PollAsync();
            Assert.AreEqual(scenario == "cancel" ? 0 : 1, shutdown.Calls, "A later poll/tick must not execute twice.");
            if (scenario == "cancel")
            {
                Assert.IsFalse(window.IsVisible);
                await engine.ApplyAsync(new("shutdown_plan_create", Guid.NewGuid().ToString(), Guid.NewGuid().ToString()), default);
                await controller.PollAsync();
                Assert.AreEqual(1, _application.Windows.OfType<CountdownWindow>().Count());
            }
            if (scenario == "failure")
            {
                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(((Button)window.FindName("ExecuteAction")).IsEnabled);
                Assert.AreEqual("关闭", ((Button)window.FindName("CancelAction")).Content);
            }
        }
        finally
        {
            await desktop.DisposeAsync();
            lifetime.Cancel();
            await serving;
        }
    });

    private sealed class PipeClock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    private sealed class MemoryJournal : IShutdownPlanStore
    {
        private ShutdownJournal? _journal;
        public Task<ShutdownJournal?> LoadAsync(CancellationToken token) => Task.FromResult(_journal);
        public Task SaveAsync(ShutdownJournal journal, CancellationToken token)
        { _journal = journal; return Task.CompletedTask; }
    }
    private sealed class RecordingShutdown : IShutdownController
    {
        public int Calls;
        public bool Fail;
        public Task RequestShutdownAsync(CancellationToken token)
        { Calls++; return Fail ? Task.FromException(new IOException("Isolated OS failure")) : Task.CompletedTask; }
    }
}
