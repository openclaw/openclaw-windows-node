using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

// ─── Structured JSONL Logger ───

public enum LogLevel { Trace, Debug, Info, Warn, Error }

public sealed partial class SetupLogger : IDisposable
{
    private TextWriter? _writer;
    private readonly string? _filePath;
    private readonly LogLevel _minLevel;
    private readonly string _runId;
    private readonly ConcurrentQueue<LogEntry> _recentEntries = new();
    private readonly object _writeLock = new();
    private const int MaxRecentEntries = 256;
    // The native setup page hands its log to a runtime and host that can outlive it.
    private bool _disposed;
    private bool _fileSinkFailed;

    public event EventHandler<LogEntry>? LogEmitted;
    public string RunId => _runId;
    public string? FilePath => _filePath;

    public SetupLogger(string? filePath, LogLevel minLevel = LogLevel.Trace)
        : this(filePath, OpenFile(filePath), minLevel)
    {
    }

    // Tests inject a writer to simulate a sink that opens and later fails.
    internal SetupLogger(string? filePath, TextWriter? writer, LogLevel minLevel = LogLevel.Trace)
    {
        _minLevel = minLevel;
        _runId = Guid.NewGuid().ToString("N")[..12];
        _filePath = filePath;
        _writer = writer;
    }

    private static StreamWriter? OpenFile(string? filePath)
    {
        if (filePath is null)
            return null;
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        return new StreamWriter(filePath, append: false) { AutoFlush = true };
    }

    public void Trace(string message, object? data = null) => Write(LogLevel.Trace, message, data);
    public void Debug(string message, object? data = null) => Write(LogLevel.Debug, message, data);
    public void Info(string message, object? data = null) => Write(LogLevel.Info, message, data);
    public void Warn(string message, object? data = null) => Write(LogLevel.Warn, message, data);
    public void Error(string message, object? data = null) => Write(LogLevel.Error, message, data);

    public void StepStarted(string stepId, string displayName)
        => Write(LogLevel.Info, $"step.started: {displayName}", new { step_id = stepId });

    public void StepCompleted(string stepId, StepResult result, TimeSpan elapsed)
    {
        Write(LogLevel.Info, $"step.completed: {stepId} → {result.Outcome}", new { step_id = stepId, outcome = result.Outcome.ToString(), message = result.Message, elapsed_ms = elapsed.TotalMilliseconds });
        if (result.Error is not null)
        {
            // StepResult.Fail(message, ex) preserves the original exception so
            // callers don't have to log it inline at every catch site. Surface
            // it here at Error level so the cause is never silently dropped.
            Write(LogLevel.Error, $"step.exception: {stepId}: {result.Error.GetType().Name}: {result.Error.Message}", new { step_id = stepId, exception_type = result.Error.GetType().FullName, exception = result.Error.ToString() });
        }
    }

    public void CommandStarted(string exe, string[] args, TimeSpan timeout)
        => Write(LogLevel.Debug, $"cmd.start: {exe} {Sanitize(string.Join(' ', args))}", new { exe, args = args.Select(Sanitize).ToArray(), timeout_ms = timeout.TotalMilliseconds });

    public void CommandCompleted(string exe, CommandResult result, TimeSpan elapsed)
    {
        var level = result.ExitCode == 0 ? LogLevel.Debug : LogLevel.Warn;
        Write(level, $"cmd.done: {exe} exit={result.ExitCode} ({elapsed.TotalMilliseconds:F0}ms)", new
        {
            exe,
            exit_code = result.ExitCode,
            stdout = Truncate(Sanitize(result.Stdout)),
            stderr = Truncate(Sanitize(result.Stderr)),
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

        var sanitizedMessage = NormalizeLogString(Sanitize(message));
        var entry = new LogEntry(DateTimeOffset.UtcNow, _runId, level, sanitizedMessage, SanitizeData(data));
        Publish(entry);

        var json = JsonSerializer.Serialize(new
        {
            ts = entry.Timestamp.ToString("O"),
            run = entry.RunId,
            level = entry.Level.ToString().ToLowerInvariant(),
            msg = entry.Message,
            data = entry.Data
        }, _jsonOptions);

        Exception? sinkFailure = null;
        lock (_writeLock)
        {
            if (!_disposed && !_fileSinkFailed && _writer is not null)
            {
                try { _writer.WriteLine(json); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    sinkFailure = ex;
                    StopFileSink();
                }
            }
        }
        if (sinkFailure is not null)
            ReportFileSinkFailure(sinkFailure);
    }

    private void Publish(LogEntry entry)
    {
        _recentEntries.Enqueue(entry);
        while (_recentEntries.Count > MaxRecentEntries)
            _recentEntries.TryDequeue(out _);

        LogEmitted?.Invoke(this, entry);

        // Also write to console for headless mode
        var color = entry.Level switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warn => ConsoleColor.Yellow,
            LogLevel.Info => ConsoleColor.White,
            _ => ConsoleColor.DarkGray
        };
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] {entry.Message}");
        Console.ForegroundColor = prev;
    }

    // Caller holds _writeLock. Diagnostics must never control setup cleanup or recovery, so a failed sink is dropped.
    private void StopFileSink()
    {
        _fileSinkFailed = true;
        var writer = _writer;
        _writer = null;
        // slopwatch-ignore: SW003 A dispose failure on an already failed sink has nothing left to report.
        try { writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    // Type and HRESULT only: IO exception messages can carry user-profile paths.
    private void ReportFileSinkFailure(Exception failure)
    {
        var detail = $"{failure.GetType().Name} (0x{failure.HResult:X8})";
        System.Diagnostics.Trace.TraceWarning($"Setup log file writes stopped after {detail}.");
        Publish(new LogEntry(DateTimeOffset.UtcNow, _runId, LogLevel.Warn,
            $"Setup log file writes stopped after {detail}; later entries stay in memory only.", null));
    }

    // ─── Secret Redaction ───

    internal static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Match Tailscale's one-time credentials before the generic URL / token
        // sanitizer rewrites their shape, so diagnostics retain a useful redaction label.
        input = TailscaleAuthorizationUrlPattern().Replace(input, "[REDACTED-TAILSCALE-AUTH-URL]");
        input = TailscaleAuthKeyPattern().Replace(input, "[REDACTED-TAILSCALE-AUTH-KEY]");
        input = TokenSanitizer.SanitizeLogMessage(input);
        if (input == TokenSanitizer.SanitizerTimeoutSentinel)
            return input;

        input = PrivateKeyPattern().Replace(input, "[REDACTED-PRIVATE-KEY]");
        input = BearerPattern().Replace(input, "$1[REDACTED]");
        input = JwtPattern().Replace(input, "[REDACTED-JWT]");
        input = SensitiveKeyPattern().Replace(input, "$1[REDACTED]");
        input = HexTokenPattern().Replace(input, "[REDACTED-HEX]");
        input = Base64TokenPattern().Replace(input, "[REDACTED-SECRET]");
        return input;
    }

    private static object? SanitizeData(object? data)
    {
        if (data == null)
            return null;

        if (data is string value)
            return NormalizeLogString(Sanitize(value));

        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            var sanitized = Sanitize(json);
            var node = JsonNode.Parse(sanitized);
            return NormalizeJsonNode(node);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return NormalizeLogString(Sanitize(data.ToString() ?? string.Empty));
        }
    }

    private static JsonNode? NormalizeJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var normalizedObject = new JsonObject();
                foreach (var property in obj)
                    normalizedObject[property.Key] = NormalizeJsonNode(property.Value);
                return normalizedObject;

            case JsonArray array:
                var normalizedArray = new JsonArray();
                for (var i = 0; i < array.Count; i++)
                    normalizedArray.Add(NormalizeJsonNode(array[i]));
                return normalizedArray;

            case JsonValue value when value.TryGetValue<string>(out var text):
                var normalized = NormalizeLogString(text);
                var trimmed = normalized.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        return NormalizeJsonNode(JsonNode.Parse(normalized));
                    }
                    catch (JsonException)
                    {
                        // Keep malformed JSON-shaped text as a flattened string.
                    }
                }

                return JsonValue.Create(normalized);

            default:
                return node?.DeepClone();
        }
    }

    private static string NormalizeLogString(string value) =>
        value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string Truncate(string input, int max = 4096)
        => input.Length <= max ? input : input[..max] + $"... [truncated {input.Length - max} chars]";

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.Compiled)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"https://login\.tailscale\.com/a/[A-Za-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TailscaleAuthorizationUrlPattern();

    [GeneratedRegex(@"\btskey-[A-Za-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TailscaleAuthKeyPattern();

    [GeneratedRegex(@"((?:""[^""]*(?:token|password|secret|api[_-]?key|setup[_-]?code|authorization)[^""]*""|[A-Za-z0-9_.-]*(?:token|password|secret|api[_-]?key|setup[_-]?code|authorization)[A-Za-z0-9_.-]*)\s*[:=]?\s*""?)[^""\s,}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SensitiveKeyPattern();

    [GeneratedRegex(@"\b[a-fA-F0-9]{32,}\b", RegexOptions.Compiled)]
    private static partial Regex HexTokenPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_-]{48,}={0,2}\b", RegexOptions.Compiled)]
    private static partial Regex Base64TokenPattern();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopFileSink();
        }
    }
}

public sealed record LogEntry(DateTimeOffset Timestamp, string RunId, LogLevel Level, string Message, object? Data);
