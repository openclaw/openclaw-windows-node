using System;
using System.IO;

namespace OpenClawTray.Services;

/// <summary>
/// Simple file logger for the tray application.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory;
    private static readonly string _logFilePath;

    static Logger()
    {
        // OPENCLAW_TRAY_DATA_DIR keeps test instances out of the user's log file.
        _logDirectory = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray");
        
        Directory.CreateDirectory(_logDirectory);
        _logFilePath = Path.Combine(_logDirectory, "openclaw-tray.log");
        
        // Rotate log if too large (> 5MB)
        try
        {
            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Exists && fileInfo.Length > 5 * 1024 * 1024)
            {
                var backupPath = Path.Combine(_logDirectory, "openclaw-tray.log.old");
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(_logFilePath, backupPath);
            }
        }
        catch { }
    }

    public static string LogFilePath => _logFilePath;

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }
}
