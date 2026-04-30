using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// File logger for the tray. Calls return immediately: writes are queued onto
/// a bounded <see cref="Channel{T}"/> and a single background task drains
/// them in batches. UI / dispatcher / MCP threads no longer block on disk
/// I/O. Drop-oldest is the overflow policy — losing the oldest queued line
/// is preferable to back-pressuring callers, since the recent context around
/// a failure is what matters at debug time.
///
/// Write failures are not silent: the most recent error is exposed via
/// <see cref="LastWriteError"/> and mirrored to <see cref="System.Diagnostics.Trace"/>
/// so a release build can be diagnosed via DebugView/ETW.
/// </summary>
public static class Logger
{
    private const long RotateThresholdBytes = 5 * 1024 * 1024;
    // Sample the file size after this many flushes — File.Length on Windows
    // requires a metadata round-trip and we don't need byte-precise rotation.
    private const int RotateCheckInterval = 64;
    // Cap. Drop-oldest on overflow keeps memory bounded under a logging
    // storm while preserving the most recent lines.
    private const int ChannelCapacity = 4096;

    private static readonly string _logDirectory;
    private static readonly string _logFilePath;
    private static readonly Channel<string> s_channel;
    private static readonly Task s_writerTask;
    private static int _writesSinceRotateCheck;

    /// <summary>Most recent write/rotate failure, or null if the writer is healthy.</summary>
    public static string? LastWriteError { get; private set; }

    static Logger()
    {
        // OPENCLAW_TRAY_DATA_DIR keeps test instances out of the user's log file.
        _logDirectory = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray");

        try { Directory.CreateDirectory(_logDirectory); }
        catch (Exception ex) { ReportWriteFailure($"create log dir: {ex.Message}"); }
        _logFilePath = Path.Combine(_logDirectory, "openclaw-tray.log");

        // Initial rotation pass picks up anything left from a previous run that
        // exceeded the threshold. The append path also rotates periodically.
        TryRotate();

        s_channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        s_writerTask = Task.Run(WriterLoopAsync);

        // Best-effort flush on process exit. The runtime gives us a few seconds
        // of grace; missing the deadline is preferable to losing logs entirely.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                s_channel.Writer.TryComplete();
                s_writerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* shutdown — nothing to do */ }
        };
    }

    public static string LogFilePath => _logFilePath;

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {TokenSanitizer.Sanitize(message)}";

        // TryWrite is non-blocking. With DropOldest semantics the call should
        // never fail unless the writer has been completed (process shutdown).
        s_channel.Writer.TryWrite(line);

#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }

    private static async Task WriterLoopAsync()
    {
        var reader = s_channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            int wrote = 0;
            try
            {
                // Open once per drained batch so high-volume bursts don't pay
                // a full File.AppendAllText (open/seek/close) per line.
                using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs);
                while (reader.TryRead(out var pending))
                {
                    sw.WriteLine(pending);
                    wrote++;
                    if (wrote >= 256) break; // bound batch so rotation can kick in promptly
                }
                sw.Flush();
                LastWriteError = null;
            }
            catch (Exception ex)
            {
                ReportWriteFailure(ex.Message);
            }

            if (wrote > 0)
            {
                _writesSinceRotateCheck += wrote;
                if (_writesSinceRotateCheck >= RotateCheckInterval)
                {
                    _writesSinceRotateCheck = 0;
                    TryRotate();
                }
            }
        }
    }

    private static void TryRotate()
    {
        try
        {
            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Exists && fileInfo.Length > RotateThresholdBytes)
            {
                var backupPath = Path.Combine(_logDirectory, "openclaw-tray.log.old");
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(_logFilePath, backupPath);
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure($"rotate: {ex.Message}");
        }
    }

    private static void ReportWriteFailure(string detail)
    {
        LastWriteError = detail;
        try { System.Diagnostics.Trace.WriteLine($"[OpenClaw Logger] {detail}"); } catch { }
#if DEBUG
        try { System.Diagnostics.Debug.WriteLine($"[OpenClaw Logger] {detail}"); } catch { }
#endif
    }
}
