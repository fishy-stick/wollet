using System.IO.Pipes;
using Wollet.Client.Core;

namespace Wollet.Client;

internal static class DesktopPlanClient
{
    public static async Task<DesktopPlanResponse> SendAsync(DesktopPlanRequest request, CancellationToken token)
    {
        var serviceProcessId = DesktopServiceIdentity.RunningProcessId();
        using var pipe = new NamedPipeClientStream(".", DesktopPlanWire.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw; }
        catch (IOException error) { throw new IOException("无法连接后台服务管道", error); }
        DesktopServiceIdentity.Verify(pipe, serviceProcessId);
        await DesktopPlanWire.WriteAsync(pipe, request, token);
        return await DesktopPlanWire.ReadAsync<DesktopPlanResponse>(pipe, token);
    }
}
