namespace Wollet.Client.Core;

public sealed class RetryBackoff
{
    private static readonly TimeSpan Maximum = TimeSpan.FromSeconds(30);
    private int _attempt;

    public TimeSpan NextDelay()
    {
        var exponent = Math.Min(_attempt, 5);
        var seconds = Math.Min(1 << exponent, (int)Maximum.TotalSeconds);
        _attempt++;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset() => _attempt = 0;
}
