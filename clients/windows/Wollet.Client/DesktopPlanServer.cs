using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Wollet.Client.Core;

namespace Wollet.Client;

[SupportedOSPlatform("windows")]
internal sealed class DesktopPlanServer(ShutdownPlanEngine engine, ConnectionCompatibility compatibility)
{
    public async Task RunAsync(CancellationToken token)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // A single server instance prevents another process from joining this pipe as a server.
        using var pipe = NamedPipeServerStreamAcl.Create(DesktopPlanWire.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
        await RunConnectionsAsync(pipe, async requestToken =>
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return;
            using var process = Process.GetProcessById(checked((int)pid));
            if (process.SessionId != WTSGetActiveConsoleSessionId()) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var request = await DesktopPlanWire.ReadAsync<DesktopPlanRequest>(pipe, deadline.Token);
            DesktopPlanResponse response;
            if (request.Action == "snapshot") response = new(await engine.SnapshotAsync(deadline.Token));
            else if (request.Action == "compatibility") response = new(null, Compatibility: compatibility.Snapshot);
            else if (request.Action is "cancel" or "execute")
            {
                var result = await engine.ApplyAsync(new("shutdown_plan_" + request.Action, Guid.NewGuid().ToString(),
                    request.OperationId ?? "", request.Revision), deadline.Token, "local_user");
                response = new(await engine.SnapshotAsync(deadline.Token), result.Accepted, result.Code);
            }
            else response = new(null, false, "invalid_request");
            await DesktopPlanWire.WriteAsync(pipe, response, deadline.Token);
        }, token);
    }

    internal static async Task RunConnectionsAsync(NamedPipeServerStream pipe,
        Func<CancellationToken, Task> handleRequest, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                await handleRequest(token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
            { if (token.IsCancellationRequested) return; }
            finally
            {
                // Broken pipes report IsConnected == false but still need Disconnect
                // before this exclusively owned server instance can accept again.
                try { pipe.Disconnect(); }
                catch (InvalidOperationException) { } // No client connected before cancellation.
            }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
