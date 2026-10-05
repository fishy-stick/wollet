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
    private sealed class TestUptime(bool fails) : ISystemUptimeProvider
    {
        public long? ReadMilliseconds() => fails ? throw new InvalidOperationException("sampling failed") : 86_400_123;
    }

    [TestMethod]
    [DataRow("127.0.0.1", true, false, false)]
    [DataRow("localhost", true, false, false)]
    [DataRow("::1", true, false, false)]
    [DataRow("127.0.0.1", true, true, false)]
    [DataRow("127.0.0.1", false, false, false)]
    [DataRow("127.0.0.1", false, true, false)]
    [DataRow("127.0.0.1", true, false, true)]
    public async Task SystemUptimeUsesIndependentNegotiationWithoutReportingIp(
        string host, bool statusNegotiated, bool plansNegotiated, bool samplingFails)
    {
        if (host == "::1" && !Socket.OSSupportsIPv6) Assert.Inconclusive("IPv6 unavailable");
        using var listener = new TcpListener(host == "::1" ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var shutdown = new RecordingShutdownController();
        ShutdownPlanEngine? engine = plansNegotiated ? new(new PlanMemoryStore(), shutdown) : null;
        if (engine is not null) await engine.InitializeAsync(timeout.Token);
        var runner = new WolletConnectionRunner(
            new FixedDeviceInfoProvider(new DeviceIdentity("Workstation", "A4:83:E7:19:2C:5A")),
            shutdown, plans: engine, systemUptimeProvider: new TestUptime(samplingFails));
        var server = new Uri($"http://{(host == "::1" ? "[::1]" : host)}:{port}");
        var run = runner.RunAsync(new(server, "device-1", "secret-1"), timeout.Token);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            var request = await ReadUpgradeRequestAsync(stream, timeout.Token);
            var key = request.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            using (var hello = await ReceiveJsonAsync(socket, timeout.Token))
            {
                var caps = hello.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
                CollectionAssert.Contains(caps, "device-status.v1");
                Assert.AreEqual(plansNegotiated, caps.Contains("shutdown-plan.v1"));
            }
            var session = Guid.NewGuid().ToString();
            var capabilities = new List<string>();
            if (statusNegotiated) capabilities.Add("device-status.v1");
            if (plansNegotiated) capabilities.Add("shutdown-plan.v1");
            // Advertising support alone must not cause telemetry to be sent.
            await SendJsonAsync(socket, System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "ready", protocolVersion = 1, heartbeatIntervalSeconds = 1, offlineAfterSeconds = 3,
                sessionId = plansNegotiated ? session : null, capabilities,
                supportedCapabilities = new[] { "protocol.v1", "shutdown-plan.v1", "device-status.v1" },
            }), timeout.Token);
            if (plansNegotiated)
            {
                while (true)
                {
                    using var sync = await ReceiveJsonAsync(socket, timeout.Token);
                    Assert.AreEqual("shutdown_plan_sync", sync.RootElement.GetProperty("type").GetString());
                    if (sync.RootElement.GetProperty("complete").GetBoolean()) break;
                }
                await SendJsonAsync(socket, $$"""{"type":"shutdown_plan_synced","sessionId":"{{session}}"}""", timeout.Token);
            }
            while (true)
            {
                using var heartbeat = await ReceiveJsonAsync(socket, timeout.Token);
                if (heartbeat.RootElement.GetProperty("type").GetString() == "shutdown_plan_state") continue;
                Assert.AreEqual("heartbeat", heartbeat.RootElement.GetProperty("type").GetString());
                Assert.AreEqual(statusNegotiated, heartbeat.RootElement.TryGetProperty("deviceStatus", out var sample));
                if (statusNegotiated)
                {
                    Assert.IsFalse(sample.TryGetProperty("localIpAddress", out _));
                    if (samplingFails) Assert.AreEqual(System.Text.Json.JsonValueKind.Null, sample.GetProperty("systemUptimeMs").ValueKind);
                    else Assert.AreEqual(86_400_123L, sample.GetProperty("systemUptimeMs").GetInt64());
                }
                break;
            }
            if (plansNegotiated)
            {
                var operation = Guid.NewGuid().ToString();
                var command = Guid.NewGuid().ToString();
                await SendJsonAsync(socket, $$"""{"type":"shutdown_plan_create","sessionId":"{{session}}","operationId":"{{operation}}","commandId":"{{command}}","delaySeconds":10}""", timeout.Token);
                while (true)
                {
                    using var message = await ReceiveJsonAsync(socket, timeout.Token);
                    if (message.RootElement.GetProperty("type").GetString() != "shutdown_plan_result") continue;
                    Assert.IsTrue(message.RootElement.GetProperty("accepted").GetBoolean()); break;
                }
                Assert.IsFalse(shutdown.Requested);
                await engine!.CancelLocalAsync("test_done", timeout.Token);
            }
            else
            {
                await SendJsonAsync(socket, """{"type":"shutdown","commandId":"command-1"}""", timeout.Token);
                while (true)
                {
                    using var message = await ReceiveJsonAsync(socket, timeout.Token);
                    if (message.RootElement.GetProperty("type").GetString() == "heartbeat") continue;
                    Assert.AreEqual("shutdown_ack", message.RootElement.GetProperty("type").GetString()); break;
                }
                await run;
                Assert.IsTrue(shutdown.Requested);
            }
        }
        finally
        {
            timeout.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public void NativeSystemUptimeIsAvailableOnWindows()
    {
        var provider = new WindowsSystemUptimeProvider();
        var first = provider.ReadMilliseconds();
        var second = provider.ReadMilliseconds();
        if (!OperatingSystem.IsWindows()) { Assert.IsNull(first); return; }
        Assert.IsNotNull(first);
        Assert.IsTrue(first >= 0 && second >= first && second <= 9_007_199_254_740_991L);
    }
}
