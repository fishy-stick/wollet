using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

[TestClass]
public sealed partial class CoreTests
{
    [TestMethod]
    public void NormalizesServerAddresses()
    {
        Assert.AreEqual(
            "http://192.168.1.10:8080/",
            ServerAddress.Normalize(" 192.168.1.10:8080/ ").AbsoluteUri);
        Assert.AreEqual(
            "https://wollet.local/base",
            ServerAddress.Normalize("https://wollet.local/base///").AbsoluteUri);
        Assert.AreEqual(
            "https://wollet.local/base/api/v1/client/bind",
            ServerAddress.ApiEndpoint(
                ServerAddress.Normalize("https://wollet.local/base"),
                "/api/v1/client/bind").AbsoluteUri);
    }

    [TestMethod]
    public void RejectsUnsafeServerAddresses()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ServerAddress.Normalize("ftp://wollet.local"));
        Assert.ThrowsExactly<ArgumentException>(() => ServerAddress.Normalize("http://user@wollet.local"));
        Assert.ThrowsExactly<ArgumentException>(() => ServerAddress.Normalize("http://wollet.local?a=b"));
        Assert.ThrowsExactly<ArgumentException>(() => ServerAddress.Normalize("   "));
    }

    [TestMethod]
    public void BuildsWebSocketEndpoints()
    {
        Assert.AreEqual(
            "ws://wollet.local:8080/api/v1/client/connect",
            ServerAddress.WebSocketEndpoint(ServerAddress.Normalize("http://wollet.local:8080")).AbsoluteUri);
        Assert.AreEqual(
            "wss://wollet.local/api/v1/client/connect",
            ServerAddress.WebSocketEndpoint(ServerAddress.Normalize("https://wollet.local")).AbsoluteUri);
    }

    [TestMethod]
    public void CapsReconnectBackoff()
    {
        var backoff = new RetryBackoff();
        var expected = new[] { 1, 2, 4, 8, 16, 30, 30 };
        foreach (var seconds in expected)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(seconds), backoff.NextDelay());
        }

        backoff.Reset();
        Assert.AreEqual(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [TestMethod]
    public void FormatsMacAddresses()
    {
        Assert.AreEqual(
            "A4:83:E7:19:2C:5A",
            RouteDeviceInfoProvider.FormatMacAddress([0xA4, 0x83, 0xE7, 0x19, 0x2C, 0x5A]));
    }

    [TestMethod]
    public async Task BindsDevicesUsingDocumentedPayload()
    {
        using var handler = new RecordingHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("http://wollet.local:8080/api/v1/client/bind", request.RequestUri?.AbsoluteUri);
            return JsonResponse(
                HttpStatusCode.Created,
                """
                {"deviceId":"device-1","deviceSecret":"secret-1"}
                """);
        });
        using var httpClient = new HttpClient(handler);
        var api = new WolletApiClient(httpClient);
        var credentials = await api.BindAsync(
            ServerAddress.Normalize("http://wollet.local:8080"),
            "ABCDE-FGHIJ-KLMNO-PQRST",
            new DeviceIdentity("Workstation", "A4:83:E7:19:2C:5A"),
            CancellationToken.None);

        Assert.AreEqual("device-1", credentials.DeviceId);
        Assert.AreEqual("secret-1", credentials.DeviceSecret);
        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.AreEqual("ABCDE-FGHIJ-KLMNO-PQRST", root.GetProperty("token").GetString());
        Assert.AreEqual("Workstation", root.GetProperty("deviceName").GetString());
        Assert.AreEqual("A4:83:E7:19:2C:5A", root.GetProperty("macAddress").GetString());
    }

    [TestMethod]
    public async Task AuthenticatesClientCredentialChecks()
    {
        using var handler = new RecordingHandler(request =>
        {
            Assert.AreEqual("device-1", request.Headers.GetValues("X-Wollet-Device-ID").Single());
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("secret-1", request.Headers.Authorization?.Parameter);
            return JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "id":"device-1",
                  "name":"Workstation",
                  "macAddress":"A4:83:E7:19:2C:5A",
                  "status":"offline",
                  "lastSeenAt":null,
                  "createdAt":"2026-08-04T00:00:00Z"
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var api = new WolletApiClient(httpClient);
        var device = await api.GetCurrentDeviceAsync(
            new ClientCredentials(ServerAddress.Normalize("http://wollet.local"), "device-1", "secret-1"),
            CancellationToken.None);

        Assert.AreEqual("device-1", device.Id);
        Assert.AreEqual("Workstation", device.Name);
    }

    [TestMethod]
    public async Task PreservesApiErrorDetails()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            """
            {"error":{"code":"invalid_device_credentials","message":"设备凭据无效"}}
            """));
        using var httpClient = new HttpClient(handler);
        var api = new WolletApiClient(httpClient);
        var exception = await Assert.ThrowsExactlyAsync<WolletApiException>(() => api.GetCurrentDeviceAsync(
            new ClientCredentials(ServerAddress.Normalize("http://wollet.local"), "device-1", "secret-1"),
            CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.AreEqual("invalid_device_credentials", exception.Code);
        Assert.AreEqual("设备凭据无效", exception.Message);
        Assert.IsTrue(exception.IsInvalidDeviceCredentials);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task CompletesShutdownProtocolRoundTrip(bool useUnavailableProxy)
    {
        var originalProxy = HttpClient.DefaultProxy;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            if (useUnavailableProxy)
            {
                // Do not bypass loopback: the connection must explicitly ignore this proxy.
                HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1", false);
                Assert.IsFalse(HttpClient.DefaultProxy.IsBypassed(new Uri("http://127.0.0.1")));
            }

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverTask = HandleWebSocketSessionAsync(listener, timeout.Token);
            var shutdown = new RecordingShutdownController();
            var runner = new WolletConnectionRunner(
                new FixedDeviceInfoProvider(new DeviceIdentity("Workstation", "A4:83:E7:19:2C:5A")),
                shutdown);
            var credentials = new ClientCredentials(
                ServerAddress.Normalize($"http://127.0.0.1:{port}"),
                "device-1",
                "secret-1");

            await Task.WhenAll(runner.RunAsync(credentials, timeout.Token), serverTask);
            Assert.IsTrue(shutdown.Requested);
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
            listener.Stop();
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static async Task HandleWebSocketSessionAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var requestText = await ReadUpgradeRequestAsync(stream, cancellationToken);
        var lines = requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("GET /api/v1/client/connect HTTP/1.1", lines[0]);
        var headers = lines
            .Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("device-1", headers["X-Wollet-Device-ID"]);
        Assert.AreEqual("Bearer secret-1", headers["Authorization"]);
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var webSocketKey) ||
            string.IsNullOrWhiteSpace(webSocketKey))
        {
            Assert.Fail("WebSocket upgrade request did not include Sec-WebSocket-Key");
        }

        var acceptSource = Encoding.ASCII.GetBytes(
            webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        var accept = Convert.ToBase64String(SHA1.HashData(acceptSource));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        using var socket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: Timeout.InfiniteTimeSpan);

        using (var hello = await ReceiveJsonAsync(socket, cancellationToken))
        {
            Assert.AreEqual("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(1, hello.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.AreEqual("Workstation", hello.RootElement.GetProperty("deviceName").GetString());
            Assert.AreEqual("A4:83:E7:19:2C:5A", hello.RootElement.GetProperty("macAddress").GetString());
        }

        await SendJsonAsync(
            socket,
            """
            {"type":"ready","protocolVersion":1,"heartbeatIntervalSeconds":15,"offlineAfterSeconds":45}
            """,
            cancellationToken);
        await SendJsonAsync(
            socket,
            """
            {"type":"shutdown","commandId":"command-1"}
            """,
            cancellationToken);

        using var acknowledgement = await ReceiveJsonAsync(socket, cancellationToken);
        Assert.AreEqual("shutdown_ack", acknowledgement.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("command-1", acknowledgement.RootElement.GetProperty("commandId").GetString());
    }

    private static async Task<string> ReadUpgradeRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (count == 0)
            {
                throw new IOException("WebSocket client closed during HTTP upgrade");
            }

            length += count;
            for (var index = 3; index < length; index++)
            {
                if (buffer[index - 3] == '\r' &&
                    buffer[index - 2] == '\n' &&
                    buffer[index - 1] == '\r' &&
                    buffer[index] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, index + 1);
                }
            }
        }

        throw new IOException("WebSocket upgrade request exceeded 8 KiB");
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
        Assert.IsTrue(result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    private static async Task SendJsonAsync(
        WebSocket socket,
        string value,
        CancellationToken cancellationToken)
    {
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(value),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _responseFactory(request);
        }
    }

    private sealed class FixedDeviceInfoProvider : IDeviceInfoProvider
    {
        private readonly DeviceIdentity _identity;

        public FixedDeviceInfoProvider(DeviceIdentity identity)
        {
            _identity = identity;
        }

        public Task<DeviceIdentity> GetAsync(Uri server, CancellationToken cancellationToken) =>
            Task.FromResult(_identity);
    }

    private sealed class RecordingShutdownController : IShutdownController
    {
        public bool Requested { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken)
        {
            Requested = true;
            return Task.CompletedTask;
        }
    }
}
