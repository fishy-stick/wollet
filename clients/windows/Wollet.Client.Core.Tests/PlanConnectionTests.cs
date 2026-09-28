using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

public sealed partial class CoreTests
{
    private sealed class PlanMemoryStore : IShutdownPlanStore
    {
        public ShutdownJournal? Journal;
        public Task<ShutdownJournal?> LoadAsync(CancellationToken token) => Task.FromResult(Journal);
        public Task SaveAsync(ShutdownJournal journal, CancellationToken token) { Journal = journal; return Task.CompletedTask; }
    }
    private sealed class PlanClock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    [TestMethod]
    public async Task NegotiatedPlanSurvivesWebSocketDisconnectAndExecutesLocally()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var shutdown = new RecordingShutdownController(); var clock = new PlanClock();
        var engine = new ShutdownPlanEngine(new PlanMemoryStore(), shutdown, clock); await engine.InitializeAsync(default);
        var runner = new WolletConnectionRunner(new FixedDeviceInfoProvider(new DeviceIdentity("Workstation", "A4:83:E7:19:2C:5A")), shutdown, plans: engine);
        var run = runner.RunAsync(new ClientCredentials(new Uri($"http://127.0.0.1:{port}"), "device-1", "secret-1"), timeout.Token);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            var request = await ReadUpgradeRequestAsync(stream, timeout.Token);
            var key = request.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':',2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            using var hello = await ReceiveJsonAsync(socket, timeout.Token);
            Assert.IsTrue(hello.RootElement.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "shutdown-plan.v1"));
            Assert.IsTrue(hello.RootElement.TryGetProperty("clientVersion", out _));
            var session = Guid.NewGuid().ToString();
            await SendJsonAsync(socket, $$"""{"type":"ready","protocolVersion":1,"heartbeatIntervalSeconds":15,"offlineAfterSeconds":45,"sessionId":"{{session}}","capabilities":["shutdown-plan.v1"]}""", timeout.Token);
            long sequence = 0;
            while (true)
            {
                using var sync = await ReceiveJsonAsync(socket, timeout.Token);
                Assert.AreEqual("shutdown_plan_sync", sync.RootElement.GetProperty("type").GetString());
                var next = sync.RootElement.GetProperty("sequence").GetInt64(); Assert.IsGreaterThan(sequence, next); sequence = next;
                if (sync.RootElement.GetProperty("complete").GetBoolean()) break;
            }
            await SendJsonAsync(socket, $$"""{"type":"shutdown_plan_synced","sessionId":"{{session}}"}""", timeout.Token);
            var operation = Guid.NewGuid().ToString(); var command = Guid.NewGuid().ToString();
            await SendJsonAsync(socket, $$"""{"type":"shutdown_plan_create","sessionId":"{{session}}","operationId":"{{operation}}","commandId":"{{command}}","delaySeconds":10}""", timeout.Token);
            using var result = await ReceiveJsonAsync(socket, timeout.Token);
            Assert.AreEqual("shutdown_plan_result", result.RootElement.GetProperty("type").GetString());
            Assert.IsTrue(result.RootElement.GetProperty("accepted").GetBoolean());
            Assert.AreEqual(operation, result.RootElement.GetProperty("plan").GetProperty("operationId").GetString());
            socket.Abort();
            Assert.AreEqual("scheduled", (await engine.SnapshotAsync(default))!.State);
            clock.Milliseconds = 10000; await engine.TickAsync(default);
            Assert.IsTrue(shutdown.Requested);
            Assert.AreEqual("submitted", (await engine.SnapshotAsync(default))!.State);
        }
        finally
        {
            timeout.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }
}
