using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray;

internal sealed class AppLogger : IOpenClawLogger
{
    public void Info(string message) => Logger.Info(message);
    public void Debug(string message) => Logger.Debug(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message, Exception? ex = null) =>
        Logger.Error(ex != null ? $"{message}: {ex}" : message);
    public void Trace(string message) => Logger.Trace(message);
}
