using DokanNet.Logging;
using LoggerExtensions = Microsoft.Extensions.Logging.LoggerExtensions;

namespace zip2vd.core.Common;

public class DokanLogger : ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public DokanLogger(Microsoft.Extensions.Logging.ILogger logger)
    {
        _logger = logger;
    }

    public void Debug(string message, params object[] args)
    {
        LoggerExtensions.LogDebug(this._logger, message, args);
    }

    public void Info(string message, params object[] args)
    {
        LoggerExtensions.LogInformation(this._logger, message, args);
    }

    public void Warn(string message, params object[] args)
    {
        LoggerExtensions.LogWarning(this._logger, message, args);
    }

    public void Error(string message, params object[] args)
    {
        LoggerExtensions.LogError(this._logger, message, args);
    }

    public void Fatal(string message, params object[] args)
    {
        LoggerExtensions.LogCritical(this._logger, message, args);
    }

    public bool DebugEnabled => true;
}