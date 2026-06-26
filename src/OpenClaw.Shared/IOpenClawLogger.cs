namespace OpenClaw.Shared;

/// <summary>
/// Simple logger interface for the gateway client.
/// Implementations can write to file, console, debug output, etc.
/// </summary>
public interface IOpenClawLogger
{
    void Info(string message);
    void Debug(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);

    // Verbose diagnostic channel. Default implementation drops the call so
    // existing implementers (tests, console logger, etc.) don't need to be
    // updated. Implementations that have a backing log file can opt in and
    // gate output behind a flag.
    void Trace(string message) { }
}

/// <summary>
/// Default no-op logger for when logging isn't needed.
/// </summary>
public class NullLogger : IOpenClawLogger
{
    public static readonly NullLogger Instance = new();
    public void Info(string message) { }
    public void Debug(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

/// <summary>
/// Console logger for simple debugging.
/// </summary>
public class ConsoleLogger : IOpenClawLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Debug(string message) => Console.WriteLine($"[DEBUG] {message}");
    public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
    public void Error(string message, Exception? ex = null) => 
        Console.WriteLine($"[ERROR] {message}{(ex != null ? $": {ex.Message}" : "")}");
}

