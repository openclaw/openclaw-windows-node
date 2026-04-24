using System.Globalization;
using System.Text;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Log verbosity for the devtools observability stream. <c>Off</c> writes
/// nothing; <c>Error</c> writes only failed tool calls; <c>Call</c> writes
/// every tool call line (default); <c>Trace</c> adds per-call argument
/// previews for deeper debugging.
/// </summary>
internal enum DevtoolsLogLevel
{
    Off,
    Error,
    Call,
    Trace,
}

/// <summary>
/// Rolling text logger for <c>reactor.*</c> tool calls. Lands in
/// <c>%LOCALAPPDATA%/Reactor/devtools/&lt;pid&gt;.log</c> on Windows
/// (<c>$XDG_STATE_HOME/reactor/devtools/</c> on non-Windows). Every call
/// writes one line: ISO timestamp, tool name, selector (truncated),
/// latency ms, result code. Rotation at 10 MB; keep the newest 5 files.
/// Thread-safe.
/// </summary>
internal sealed class DevtoolsLogger : IDisposable
{
    private const long MaxBytesPerFile = 10L * 1024 * 1024;
    private const int MaxRotations = 5;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly DevtoolsLogLevel _level;
    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _disposed;

    public string Path => _path;
    public DevtoolsLogLevel Level => _level;

    public DevtoolsLogger(string directory, int pid, DevtoolsLogLevel level)
    {
        _level = level;
        _path = global::System.IO.Path.Combine(directory, $"{pid}.log");
        if (_level != DevtoolsLogLevel.Off)
        {
            Directory.CreateDirectory(directory);
            OpenWriter();
        }
    }

    /// <summary>Default directory per-platform. Callers override for tests.</summary>
    public static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return global::System.IO.Path.Combine(local, "Reactor", "devtools");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(xdg))
            xdg = global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return global::System.IO.Path.Combine(xdg, "reactor", "devtools");
    }

    /// <summary>Parses <c>--devtools-log-level</c>; defaults to <see cref="DevtoolsLogLevel.Call"/>.</summary>
    public static DevtoolsLogLevel ParseLevel(string? raw) => raw?.ToLowerInvariant() switch
    {
        "off" => DevtoolsLogLevel.Off,
        "error" => DevtoolsLogLevel.Error,
        "call" or null or "" => DevtoolsLogLevel.Call,
        "trace" => DevtoolsLogLevel.Trace,
        _ => DevtoolsLogLevel.Call,
    };

    /// <summary>
    /// Writes one structured call record. <paramref name="success"/> controls
    /// whether the record passes the Error-level filter. <paramref name="resultCode"/>
    /// is the JSON-RPC error code ('0' for success).
    /// </summary>
    public void LogCall(string tool, string? selector, long latencyMs, bool success, int resultCode)
    {
        if (_level == DevtoolsLogLevel.Off) return;
        if (_level == DevtoolsLogLevel.Error && success) return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var selPart = string.IsNullOrEmpty(selector) ? "-" : Truncate(selector!, 80);
        var status = success ? "ok" : "err";
        var line = $"{ts}\t{tool}\t{selPart}\t{latencyMs}ms\t{status}\t{resultCode}";
        WriteLine(line);
    }

    /// <summary>
    /// Free-form error line. Respects Off/Error floors. Emits the same 6-TSV-column
    /// shape as <see cref="LogCall"/> so downstream parsers can treat every log
    /// line uniformly — the human message rides in the selector column (which is
    /// always a free-form string for structured calls) rather than a trailing
    /// 7th column that would break fixed-width parsing.
    /// </summary>
    public void LogError(string message)
    {
        if (_level == DevtoolsLogLevel.Off) return;
        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var messagePart = string.IsNullOrEmpty(message) ? "-" : Truncate(message, 500);
        WriteLine($"{ts}\t!error\t{messagePart}\t-\terr\t-");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_bytesWritten + bytes > MaxBytesPerFile) Rotate();
            _writer.WriteLine(line);
            _writer.Flush();
            _bytesWritten += bytes;
        }
    }

    private void OpenWriter()
    {
        // Truncate on open — each pid gets a fresh log. Rotation preserves
        // history. Append mode would merge logs from reloaded pids that happen
        // to reuse the same pid (rare but possible), making per-run diagnosis
        // harder.
        var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _bytesWritten = 0;
    }

    private void Rotate()
    {
        _writer?.Dispose();

        // Shift .1 → .2 → .3 ... dropping anything past MaxRotations.
        // We rotate in reverse so nothing overwrites before being moved.
        for (int i = MaxRotations - 1; i >= 1; i--)
        {
            var src = $"{_path}.{i}";
            var dst = $"{_path}.{i + 1}";
            if (File.Exists(src))
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
        }
        var firstArchive = $"{_path}.1";
        if (File.Exists(firstArchive)) File.Delete(firstArchive);
        File.Move(_path, firstArchive);

        OpenWriter();
    }

    private static string Truncate(string s, int max)
    {
        // Log lines must not contain literal newlines — collapse any that leak
        // through and cap length to keep rotations predictable.
        var collapsed = s.Replace('\n', ' ').Replace('\r', ' ');
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }
}
