using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class DesktopCompatibilityReader : ICompatibilityReader
{
    public async Task<CompatibilityResult?> ReadAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var pipe = new NamedPipeClientStream(".", DesktopPlanWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return null;
            using var process = Process.GetProcessById((int)pid);
            using var handle = OpenProcess(0x1000, false, pid);
            var name = new System.Text.StringBuilder(32768); uint length = (uint)name.Capacity;
            if (process.SessionId != 0 || handle.IsInvalid || !QueryFullProcessImageName(handle, 0, name, ref length) ||
                !string.Equals(name.ToString(), new WindowsPaths().InstalledExecutable, StringComparison.OrdinalIgnoreCase)) return null;
            await DesktopPlanWire.WriteAsync(pipe, new DesktopPlanRequest("compatibility"), deadline.Token);
            return (await DesktopPlanWire.ReadAsync<DesktopPlanResponse>(pipe, deadline.Token)).Compatibility;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder name, ref uint size);
}
