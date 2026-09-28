using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client;
using Wollet.Client.Core;

namespace Wollet.Client.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class DesktopPlanServerTests
{
    [TestMethod]
    public async Task ResponseIsNotDiscardedBeforeSlowClientReadsIt()
    {
        var name = "Wollet.Test." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = pipe.WaitForConnectionAsync(timeout.Token);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await wait;
        var response = DesktopPlanServer.WriteResponseAsync(pipe, new(null, Error: new string('x', 2000)), timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.IsFalse(response.IsCompleted, "Server must not recycle the pipe before the client reads");
        var received = await DesktopPlanWire.ReadAsync<DesktopPlanResponse>(client, timeout.Token);
        Assert.AreEqual(2000, received.Error!.Length);
        client.Dispose();
        await response;
    }

    [TestMethod]
    public async Task BrokenClientDoesNotStopListenerAndNextClientGetsResponse()
    {
        var name = "Wollet.Test." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var server = DesktopPlanServer.RunConnectionsAsync(pipe, async token =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.SetResult();
                await closed.Task.WaitAsync(token);
                // A write after the client has closed deterministically marks the pipe broken.
                try { await pipe.WriteAsync(new byte[] { 1 }, token); }
                catch (IOException)
                {
                    Assert.IsFalse(pipe.IsConnected);
                    recovered.SetResult();
                    throw;
                }
                Assert.Fail("Expected the disconnected client to break the pipe");
            }
            else
            {
                await pipe.WriteAsync(new byte[] { 42 }, token);
            }
        }, stop.Token);
        try
        {
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(timeout.Token);
                await entered.Task.WaitAsync(timeout.Token);
            }
            closed.SetResult();
            await recovered.Task.WaitAsync(timeout.Token);
            using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await next.ConnectAsync(timeout.Token);
            var response = new byte[1];
            await next.ReadExactlyAsync(response, timeout.Token);
            Assert.AreEqual((byte)42, response[0]);
            Assert.IsFalse(server.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await server.WaitAsync(timeout.Token);
        }
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForFirstClientExitsCleanly()
    {
        using var pipe = new NamedPipeServerStream("Wollet.Test." + Guid.NewGuid().ToString("N"),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var stop = new CancellationTokenSource();
        var server = DesktopPlanServer.RunConnectionsAsync(pipe, _ => throw new AssertFailedException("No client expected"), stop.Token);
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
