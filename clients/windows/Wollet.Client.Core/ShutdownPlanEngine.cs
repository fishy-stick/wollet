namespace Wollet.Client.Core;

public sealed record ShutdownPlan(string OperationId, long Revision, string State,
    long RemainingMilliseconds, string? Reason = null);
public sealed record ShutdownCommand(string Type, string CommandId, string OperationId,
    long ExpectedRevision = 0, int DelaySeconds = 10);
public sealed record ShutdownResult(string CommandId, string OperationId, bool Accepted,
    ShutdownPlan? Plan, string? Code = null);
public sealed record ShutdownJournal(ShutdownPlan? Plan, Dictionary<string, ShutdownCommand> Commands,
    Dictionary<string, ShutdownResult> Results, Dictionary<string, ShutdownPlan>? Plans = null);
public interface IShutdownPlanStore
{
    Task<ShutdownJournal?> LoadAsync(CancellationToken token);
    Task SaveAsync(ShutdownJournal journal, CancellationToken token);
}

// This engine belongs to the worker lifetime, not a WebSocket session or UI window.
public sealed class ShutdownPlanEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IShutdownPlanStore _store;
    private readonly IShutdownController _shutdown;
    private readonly TimeProvider _clock;
    private ShutdownJournal _journal = new(null, new(), new());
    private long _started;
    private bool _ready;
    private bool _submittedThisProcess;
    public ShutdownPlanEngine(IShutdownPlanStore store, IShutdownController shutdown, TimeProvider? clock = null)
    { _store = store; _shutdown = shutdown; _clock = clock ?? TimeProvider.System; }

    public async Task InitializeAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _journal = await _store.LoadAsync(token) ?? _journal;
            if (_journal.Commands is null || _journal.Results is null || _journal.Commands.Keys.Any(k => !_journal.Results.ContainsKey(k)))
                throw new InvalidDataException("关机计划记录不完整");
            if (_journal.Plan is { State: "scheduled" or "executing" } plan)
            {
                _journal = _journal with { Plan = plan with { Revision = plan.Revision + 1,
                    State = plan.State == "scheduled" ? "cancelled" : "indeterminate", RemainingMilliseconds = 0,
                    Reason = plan.State == "scheduled" ? "client_restarted" : "execution_interrupted" } };
                await SaveAsync(token);
            }
            else if (_journal.Plan is { State: "submitted" } submitted)
            {
                var history = new Dictionary<string, ShutdownPlan>(_journal.Plans ?? new()) { [submitted.OperationId] = submitted };
                _journal = _journal with { Plan = null, Plans = history };
                await SaveAsync(token);
            }
            _ready = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ShutdownPlan?> SnapshotAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return Snapshot(); }
        finally { _gate.Release(); }
    }

    public async Task<ShutdownJournal> JournalAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return new(Snapshot(), new(_journal.Commands), new(_journal.Results), new(_journal.Plans ?? new())); }
        finally { _gate.Release(); }
    }

    private ShutdownPlan? Snapshot() => _journal.Plan is { State: "scheduled" } plan
        ? plan with { RemainingMilliseconds = Math.Max(0, 10000 - (long)_clock.GetElapsedTime(_started).TotalMilliseconds) }
        : _journal.Plan;

    private Task SaveAsync(CancellationToken token)
    {
        var plans = new Dictionary<string, ShutdownPlan>(_journal.Plans ?? new());
        if (_journal.Plan is { } plan) plans[plan.OperationId] = plan;
        _journal = _journal with { Plans = plans };
        return _store.SaveAsync(_journal, token);
    }

    public async Task<ShutdownResult> ApplyAsync(ShutdownCommand command, CancellationToken token, string actor = "remote_user")
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            // Once admitted, persist the decision independently of the transport lifetime.
            token = CancellationToken.None;
            ShutdownResult Reject(string code) => new(command.CommandId, command.OperationId, false, Snapshot(), code);
            if (!_ready) return Reject("storage_unavailable");
            if (!Guid.TryParse(command.OperationId, out _) || !Guid.TryParse(command.CommandId, out _)) return Reject("invalid_request");
            if (_journal.Commands.TryGetValue(command.CommandId, out var previous))
                return previous == command ? _journal.Results[command.CommandId] : Reject("idempotency_conflict");
            async Task<ShutdownResult> RememberRejection(string code)
            {
                var rejected = Reject(code);
                var old = _journal;
                _journal = new(old.Plan, new(old.Commands) { [command.CommandId] = command }, new(old.Results) { [command.CommandId] = rejected }, old.Plans);
                try { await SaveAsync(token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { _journal = old; return Reject("storage_unavailable"); }
                return rejected;
            }
            var before = _journal;
            var current = Snapshot();
            ShutdownResult result;
            if (command.Type == "shutdown_plan_create")
            {
                if (command.DelaySeconds != 10) return await RememberRejection("invalid_delay");
                if (_journal.Commands.Values.Any(c => c.OperationId == command.OperationId)) return await RememberRejection("idempotency_conflict");
                if (current?.State is "scheduled" or "executing" || _submittedThisProcess) return await RememberRejection("active_plan_exists");
                _started = _clock.GetTimestamp();
                current = new(command.OperationId, 1, "scheduled", 10000);
            }
            else
            {
                if (current?.OperationId != command.OperationId) return await RememberRejection("unknown_operation");
                if (!(command.Type == "shutdown_plan_cancel" && current.State == "cancelled"))
                {
                if (current.State != "scheduled") return await RememberRejection("too_late");
                if (command.ExpectedRevision != current.Revision) return await RememberRejection("revision_conflict");
                if (command.Type is not ("shutdown_plan_cancel" or "shutdown_plan_execute")) return await RememberRejection("invalid_request");
                current = current with { Revision = current.Revision + 1, RemainingMilliseconds = 0,
                    State = command.Type == "shutdown_plan_cancel" ? "cancelled" : "executing", Reason = actor };
                }
            }
            result = new(command.CommandId, command.OperationId, true, current);
            _journal = new(current, new(before.Commands) { [command.CommandId] = command },
                new(before.Results) { [command.CommandId] = result }, before.Plans);
            try { await SaveAsync(token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (current.State != "cancelled") _journal = before;
                if (current.State != "cancelled") return Reject("storage_unavailable");
                // Cancellation remains effective even if durable storage is temporarily unavailable.
            }
            if (current.State == "executing") await ExecuteLockedAsync(token);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelLocalAsync(string reason, CancellationToken token)
    {
        var plan = await SnapshotAsync(token);
        if (plan?.State == "scheduled")
            await ApplyAsync(new("shutdown_plan_cancel", Guid.NewGuid().ToString(), plan.OperationId, plan.Revision), token, reason);
    }

    public async Task TickAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_ready || Snapshot() is not { State: "scheduled", RemainingMilliseconds: 0 } plan) return;
            _journal = _journal with { Plan = plan with { Revision = plan.Revision + 1, State = "executing" } };
            try { await SaveAsync(token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _journal = _journal with { Plan = _journal.Plan! with { State = "failed", Reason = "storage_error", Revision = _journal.Plan!.Revision + 1 } };
                return;
            }
            await ExecuteLockedAsync(token);
        }
        finally { _gate.Release(); }
    }

    private async Task ExecuteLockedAsync(CancellationToken token)
    {
        var state = "submitted";
        string? reason = _journal.Plan!.Reason;
        try { await _shutdown.RequestShutdownAsync(token); }
        catch (Exception) { state = "failed"; reason = "system_error"; }
        _journal = _journal with { Plan = _journal.Plan! with { State = state, Reason = reason, Revision = _journal.Plan!.Revision + 1 } };
        _submittedThisProcess = state == "submitted";
        try { await SaveAsync(CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Persisted executing recovers as indeterminate. */ }
    }
}
