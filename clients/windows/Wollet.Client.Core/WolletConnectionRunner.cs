using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace Wollet.Client.Core;

public sealed class WolletConnectionRunner
{
    private const int MaximumMessageBytes = 4 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IShutdownController _shutdownController;
    private readonly IClientLog _log;
    private readonly TimeProvider _timeProvider;

    public WolletConnectionRunner(
        IDeviceInfoProvider deviceInfoProvider,
        IShutdownController shutdownController,
        IClientLog? log = null,
        TimeProvider? timeProvider = null)
    {
        _deviceInfoProvider = deviceInfoProvider;
        _shutdownController = shutdownController;
        _log = log ?? NullClientLog.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(ClientCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var backoff = new RetryBackoff();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var identity = await _deviceInfoProvider.GetAsync(credentials.Server, cancellationToken);
                await RunSessionAsync(credentials, identity, backoff.Reset, cancellationToken);
                return;
            }
            catch (DeviceCredentialsRejectedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var delay = backoff.NextDelay();
                _log.Warning($"连接已断开，将在 {delay.TotalSeconds:0} 秒后重试", exception);
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
        }
    }

    private async Task RunSessionAsync(
        ClientCredentials credentials,
        DeviceIdentity identity,
        Action onReady,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("X-Wollet-Device-ID", credentials.DeviceId);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credentials.DeviceSecret);
        try
        {
            using var connectCancellation = CreateTimeout(cancellationToken, ConnectionTimeout);
            await socket.ConnectAsync(
                ServerAddress.WebSocketEndpoint(credentials.Server),
                connectCancellation.Token);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            throw new DeviceCredentialsRejectedException();
        }

        using var writeLock = new SemaphoreSlim(1, 1);
        await SendAsync(
            socket,
            writeLock,
            new ClientMessage
            {
                Type = "hello",
                ProtocolVersion = ProtocolVersion.Current,
                DeviceName = identity.Name,
                MacAddress = identity.MacAddress,
            },
            cancellationToken);

        using var readyCancellation = CreateTimeout(cancellationToken, ConnectionTimeout);
        var ready = await ReceiveAsync(socket, readyCancellation.Token);
        if (ready.Type != "ready" ||
            ready.ProtocolVersion != ProtocolVersion.Current ||
            ready.HeartbeatIntervalSeconds <= 0 ||
            ready.OfflineAfterSeconds <= ready.HeartbeatIntervalSeconds)
        {
            throw new ClientProtocolException("服务端返回了不支持的 ready 消息");
        }

        var heartbeatInterval = TimeSpan.FromSeconds(ready.HeartbeatIntervalSeconds);
        onReady();
        _log.Information($"设备已连接，心跳间隔 {heartbeatInterval.TotalSeconds:0} 秒");

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatsAsync(socket, writeLock, heartbeatInterval, sessionCancellation.Token);
        var commandTask = ReceiveCommandsAsync(socket, writeLock, sessionCancellation.Token);
        var completed = await Task.WhenAny(heartbeatTask, commandTask);
        sessionCancellation.Cancel();
        var remaining = completed == commandTask ? heartbeatTask : commandTask;
        try
        {
            if (completed == commandTask)
            {
                var shutdownRequested = await commandTask;
                if (shutdownRequested)
                {
                    return;
                }
            }
            else
            {
                await heartbeatTask;
            }
        }
        finally
        {
            await ObserveCompletionAsync(remaining);
        }

        throw new IOException("WebSocket 连接意外结束");
    }

    private async Task<bool> ReceiveCommandsAsync(
        ClientWebSocket socket,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveAsync(socket, cancellationToken);
            if (message.Type != "shutdown" || string.IsNullOrWhiteSpace(message.CommandId))
            {
                throw new ClientProtocolException("服务端发送了不支持的消息");
            }

            await SendAsync(
                socket,
                writeLock,
                new ClientMessage { Type = "shutdown_ack", CommandId = message.CommandId },
                cancellationToken);
            _log.Information("已确认收到关机指令");
            await _shutdownController.RequestShutdownAsync(cancellationToken);
            return true;
        }
    }

    private async Task SendHeartbeatsAsync(
        ClientWebSocket socket,
        SemaphoreSlim writeLock,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SendAsync(
                socket,
                writeLock,
                new ClientMessage { Type = "heartbeat" },
                cancellationToken);
        }
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        SemaphoreSlim writeLock,
        ClientMessage message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        using var operationCancellation = CreateTimeout(cancellationToken, OperationTimeout);
        await writeLock.WaitAsync(operationCancellation.Token);
        try
        {
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                operationCancellation.Token);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<ServerMessage> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumMessageBytes);
        try
        {
            var length = 0;
            while (true)
            {
                if (length == MaximumMessageBytes)
                {
                    throw new ClientProtocolException("服务端消息超过 4 KiB 限制");
                }

                var result = await socket.ReceiveAsync(
                    buffer.AsMemory(length, MaximumMessageBytes - length),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("服务端关闭了 WebSocket 连接");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new ClientProtocolException("服务端发送了非文本消息");
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            try
            {
                return JsonSerializer.Deserialize<ServerMessage>(buffer.AsSpan(0, length), JsonOptions)
                    ?? throw new ClientProtocolException("服务端发送了空消息");
            }
            catch (JsonException exception)
            {
                throw new ClientProtocolException("服务端发送了无效的 JSON 消息", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}
