using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

// ─── Structured JSONL Logger ───

public enum LogLevel { Trace, Debug, Info, Warn, Error }

public sealed partial class SetupLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly string? _filePath;
    private readonly LogLevel _minLevel;
    private readonly string _runId;
    private readonly ConcurrentQueue<LogEntry> _recentEntries = new();
    private readonly object _writeLock = new();
    private const int MaxRecentEntries = 256;

    public event EventHandler<LogEntry>? LogEmitted;
    public string RunId => _runId;
    public string? FilePath => _filePath;

    public SetupLogger(string? filePath, LogLevel minLevel = LogLevel.Trace)
    {
        _minLevel = minLevel;
        _runId = Guid.NewGuid().ToString("N")[..12];

        if (filePath != null)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
        }
    }

    public void Trace(string message, object? data = null) => Write(LogLevel.Trace, message, data);
    public void Debug(string message, object? data = null) => Write(LogLevel.Debug, message, data);
    public void Info(string message, object? data = null) => Write(LogLevel.Info, message, data);
    public void Warn(string message, object? data = null) => Write(LogLevel.Warn, message, data);
    public void Error(string message, object? data = null) => Write(LogLevel.Error, message, data);

    public void StepStarted(string stepId, string displayName)
        => Write(LogLevel.Info, $"step.started: {displayName}", new { step_id = stepId });

    public void StepCompleted(string stepId, StepResult result, TimeSpan elapsed)
        => Write(LogLevel.Info, $"step.completed: {stepId} → {result.Outcome}", new { step_id = stepId, outcome = result.Outcome.ToString(), message = result.Message, elapsed_ms = elapsed.TotalMilliseconds });

    public void CommandStarted(string exe, string[] args, TimeSpan timeout)
        => Write(LogLevel.Debug, $"cmd.start: {exe} {Redact(string.Join(' ', args))}", new { exe, args = args.Select(Redact).ToArray(), timeout_ms = timeout.TotalMilliseconds });

    public void CommandCompleted(string exe, CommandResult result, TimeSpan elapsed)
    {
        var level = result.ExitCode == 0 ? LogLevel.Debug : LogLevel.Warn;
        Write(level, $"cmd.done: {exe} exit={result.ExitCode} ({elapsed.TotalMilliseconds:F0}ms)", new
        {
            exe,
            exit_code = result.ExitCode,
            stdout = Truncate(Redact(result.Stdout)),
            stderr = Truncate(Redact(result.Stderr)),
            elapsed_ms = elapsed.TotalMilliseconds,
            timed_out = result.TimedOut
        });
    }

    public void Decision(string description, string chosen)
        => Write(LogLevel.Info, $"decision: {description} → {chosen}");

    public void StateChange(string key, string? from, string? to)
        => Write(LogLevel.Debug, $"state: {key} [{from ?? "null"}] → [{to ?? "null"}]");

    private void Write(LogLevel level, string message, object? data = null)
    {
        if (level < _minLevel) return;

        var entry = new LogEntry(DateTimeOffset.UtcNow, _runId, level, message, data);
        _recentEntries.Enqueue(entry);
        while (_recentEntries.Count > MaxRecentEntries)
            _recentEntries.TryDequeue(out _);

        LogEmitted?.Invoke(this, entry);

        var json = JsonSerializer.Serialize(new
        {
            ts = entry.Timestamp.ToString("O"),
            run = entry.RunId,
            level = entry.Level.ToString().ToLowerInvariant(),
            msg = entry.Message,
            data = entry.Data
        }, _jsonOptions);

        lock (_writeLock)
        {
            _writer?.WriteLine(json);
        }

        // Also write to console for headless mode
        var color = level switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warn => ConsoleColor.Yellow,
            LogLevel.Info => ConsoleColor.White,
            _ => ConsoleColor.DarkGray
        };
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{level}] {message}");
        Console.ForegroundColor = prev;
    }

    // ─── Secret Redaction ───

    private static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        input = TokenPattern().Replace(input, "$1[REDACTED]");
        input = HexTokenPattern().Replace(input, "[REDACTED-HEX]");
        return input;
    }

    private static string Truncate(string input, int max = 4096)
        => input.Length <= max ? input : input[..max] + $"... [truncated {input.Length - max} chars]";

    [GeneratedRegex(@"(token[=:\s]+)[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\b[a-fA-F0-9]{64}\b")]
    private static partial Regex HexTokenPattern();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose() => _writer?.Dispose();
}

public sealed record LogEntry(DateTimeOffset Timestamp, string RunId, LogLevel Level, string Message, object? Data);
