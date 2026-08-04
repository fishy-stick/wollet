using Microsoft.Extensions.Logging;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class LoggerClientLog : IClientLog
{
    private readonly ILogger _logger;

    public LoggerClientLog(ILogger logger)
    {
        _logger = logger;
    }

    public void Information(string message) => _logger.LogInformation("{Message}", message);

    public void Warning(string message, Exception? exception = null) =>
        _logger.LogWarning(exception, "{Message}", message);

    public void Error(string message, Exception? exception = null) =>
        _logger.LogError(exception, "{Message}", message);
}
