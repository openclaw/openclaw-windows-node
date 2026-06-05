namespace OpenClawTray.Services;

/// <summary>
/// Writes crash details to a log file and the application logger. Hooked from the WinUI,
/// CLR domain, and TaskScheduler unhandled-exception events, which may fire on the UI
/// thread or background threads.
/// </summary>
internal sealed class AppCrashLogger
{
    private readonly string _path;

    public AppCrashLogger(string path) => _path = path;

    public void Log(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var message = $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n";
            File.AppendAllText(_path, message);
        }
        catch { /* Can't log the crash logger crash */ }

        try
        {
            if (ex != null)
            {
                Logger.Error($"CRASH {source}: {ex}");
            }
            else
            {
                Logger.Error($"CRASH {source}");
            }
        }
        catch { /* Ignore logging failures */ }
    }
}
