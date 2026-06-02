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
        catch (Exception fileEx)
        {
            // Crash logger itself crashed (disk full, ACL, etc.). Try a Trace
            // breadcrumb so it's at least visible in attached debuggers.
            try { System.Diagnostics.Trace.WriteLine($"AppCrashLogger.Log: failed to write crash log: {fileEx.GetType().Name}: {fileEx.Message}"); }
            catch (Exception) { /* Trace itself failed — nothing left to call. */ }
        }

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
        catch (Exception logEx)
        {
            // Logger.Error itself crashed (e.g., writer torn down mid-shutdown).
            try { System.Diagnostics.Trace.WriteLine($"AppCrashLogger.Log: failed to log crash via Logger: {logEx.GetType().Name}: {logEx.Message}"); }
            catch (Exception) { /* Trace itself failed — nothing left to call. */ }
        }
    }
}
