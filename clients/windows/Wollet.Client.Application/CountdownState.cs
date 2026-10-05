using Wollet.Client.Core;

namespace Wollet.Client;

// Display time never decides whether to shut down. Only the service owns execution.
internal sealed class CountdownState(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _observed;
    private double _remaining;
    public const double DurationMilliseconds = 10_000;
    public ShutdownPlan? Plan { get; private set; }
    public bool ActionPending { get; set; }
    public string? Error { get; set; }
    public double RemainingMilliseconds => Plan?.State == "scheduled"
        ? Math.Max(0, _remaining - _clock.GetElapsedTime(_observed).TotalMilliseconds) : 0;
    public bool CanAct => Plan?.State == "scheduled" && RemainingMilliseconds > 0 && !ActionPending;
    public bool IsFailed => Plan?.State == "failed";
    public bool IsVisible => Plan?.State is "scheduled" or "executing" or "submitted" or "failed";
    public string Display => Plan?.State == "scheduled" && RemainingMilliseconds > 0
        ? Math.Ceiling(RemainingMilliseconds / 1000).ToString("0") : IsFailed ? "关机失败" : "正在关机";
    public string Status => Error ?? (Plan?.State switch
    {
        "scheduled" when ActionPending => "正在确认操作结果…",
        "scheduled" when RemainingMilliseconds > 0 => "倒计时结束后，此电脑将自动关机",
        "failed" => "关机失败，请在管理页面查看状态",
        _ => "正在关机，请稍候…",
    });

    public bool Observe(ShutdownPlan? plan)
    {
        var same = plan is not null && Plan?.OperationId == plan.OperationId;
        // A delayed snapshot cannot resurrect an older revision or extend a countdown.
        if (same && plan!.Revision < Plan!.Revision) return false;
        var remaining = Math.Clamp((double)(plan?.RemainingMilliseconds ?? 0), 0, DurationMilliseconds);
        if (same && plan!.Revision == Plan!.Revision && Plan.State == "scheduled")
            remaining = Math.Min(remaining, RemainingMilliseconds);
        Plan = plan;
        _remaining = remaining;
        _observed = _clock.GetTimestamp();
        Error = null;
        return true;
    }
}
