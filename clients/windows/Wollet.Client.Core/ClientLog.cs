namespace Wollet.Client.Core;

public interface IClientLog
{
    void Information(string message);

    void Warning(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

public sealed class NullClientLog : IClientLog
{
    public static NullClientLog Instance { get; } = new();

    private NullClientLog()
    {
    }

    public void Information(string message)
    {
    }

    public void Warning(string message, Exception? exception = null)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
