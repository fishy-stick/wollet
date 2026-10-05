using System.ComponentModel;
using System.Text.Json;
using Wollet.Client.Core;

namespace Wollet.Client;

// The caller schedules polling and owns windows; this controller serializes IPC.
internal sealed class DesktopPlanController : IDisposable, IAsyncDisposable
{
    private readonly Func<DesktopPlanRequest, CancellationToken, Task<DesktopPlanResponse>> _send;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private Task _cleanup = Task.CompletedTask;
    private int _failures;
    private string? _warning;
    public DesktopPlanController(Func<DesktopPlanRequest, CancellationToken, Task<DesktopPlanResponse>> send, TimeProvider? clock = null)
    {
        _send = send;
        _token = _lifetime.Token;
        State = new(clock);
    }
    public CountdownState State { get; }
    public event Action? Changed;
    public event Action<string?>? ConnectionWarningChanged;

    public async Task PollAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            var response = await SendAsync(new("snapshot"));
            if (_disposed) return;
            _failures = 0;
            if (_warning is not null) { _warning = null; ConnectionWarningChanged?.Invoke(null); }
            Apply(response);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) when (IsTransportError(error))
        {
            if (_disposed) return;
            var message = error is OperationCanceledException ? "连接后台服务超时" : error.Message;
            State.Error = message + "；正在重试…";
            Changed?.Invoke();
            if (++_failures >= 3 && _warning != message)
            {
                _warning = message;
                ConnectionWarningChanged?.Invoke(message);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RequestActionAsync(string action)
    {
        if (_disposed || action is not ("cancel" or "execute") || !State.CanAct || State.Plan is not { } plan) return;
        State.ActionPending = true;
        Changed?.Invoke();
        try
        {
            await _gate.WaitAsync(_token);
            try
            {
                if (_disposed) return;
                // Do not retarget a queued action to a replacement plan or revision.
                if (State.Plan?.OperationId != plan.OperationId || State.Plan.Revision != plan.Revision || State.Plan.State != "scheduled") return;
                Apply(await SendAsync(new(action, plan.OperationId, plan.Revision)));
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) when (IsTransportError(error))
        {
            if (!_disposed) State.Error = "操作结果未确认，请重试";
        }
        finally
        {
            State.ActionPending = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    private async Task<DesktopPlanResponse> SendAsync(DesktopPlanRequest request)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return await _send(request, deadline.Token);
    }
    private void Apply(DesktopPlanResponse response)
    {
        if (_disposed || !State.Observe(response.Plan)) return;
        if (!response.Accepted)
            State.Error = response.Error == "too_late" ? "关机已开始，无法取消" : "操作未成功，请重试";
        Changed?.Invoke();
    }
    private static bool IsTransportError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or ArgumentException or OperationCanceledException or Win32Exception or InvalidOperationException;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Changed = null;
        ConnectionWarningChanged = null;
        _cleanup = CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        // Cancel waiters first, then wait for the in-flight request to unwind.
        await _gate.WaitAsync();
        _gate.Dispose();
        _lifetime.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return new(_cleanup); }
}
