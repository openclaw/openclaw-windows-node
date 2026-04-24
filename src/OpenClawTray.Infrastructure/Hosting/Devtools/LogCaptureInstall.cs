using System.Diagnostics;
using System.Text;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// TextWriter that forks every write into a <see cref="LogCaptureBuffer"/> and
/// (optionally) the original Console stream. In stdio MCP transport the
/// original Console.Out is the JSON-RPC frame — we must not pass text through
/// or we'd corrupt the agent's message stream, so the host installs us with
/// <c>forward: false</c>.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter? _forward;
    private readonly LogCaptureBuffer _buffer;
    private readonly LogSource _source;
    private readonly StringBuilder _lineBuf = new();
    private readonly object _lock = new();

    public TeeTextWriter(TextWriter? forward, LogCaptureBuffer buffer, LogSource source)
    {
        _forward = forward;
        _buffer = buffer;
        _source = source;
    }

    public override Encoding Encoding => _forward?.Encoding ?? Encoding.UTF8;

    public override void Write(char value)
    {
        _forward?.Write(value);
        AppendChar(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        _forward?.Write(value);
        AppendString(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _forward?.Write(buffer, index, count);
        AppendString(new string(buffer, index, count));
    }

    public override void WriteLine()
    {
        _forward?.WriteLine();
        AppendChar('\n');
    }

    public override void WriteLine(string? value)
    {
        _forward?.WriteLine(value);
        if (value is not null) AppendString(value);
        AppendChar('\n');
    }

    public override void Flush()
    {
        _forward?.Flush();
        FlushLineBuffer(force: true);
    }

    private void AppendChar(char c)
    {
        lock (_lock)
        {
            if (c == '\n') FlushLineLocked();
            else if (c != '\r') _lineBuf.Append(c);
        }
    }

    private void AppendString(string s)
    {
        lock (_lock)
        {
            foreach (var c in s)
            {
                if (c == '\n') FlushLineLocked();
                else if (c != '\r') _lineBuf.Append(c);
            }
        }
    }

    private void FlushLineBuffer(bool force)
    {
        lock (_lock)
        {
            if (force && _lineBuf.Length > 0) FlushLineLocked();
        }
    }

    private void FlushLineLocked()
    {
        var line = _lineBuf.ToString();
        _lineBuf.Clear();
        _buffer.Append(_source, level: null, line);
    }
}

/// <summary>
/// TraceListener that mirrors Debug/Trace output into a
/// <see cref="LogCaptureBuffer"/>. Debug.WriteLine is one newline-terminated
/// write; Debug.Write can stream a partial line, so we accumulate until the
/// listener is Flush()ed or a newline arrives.
/// </summary>
internal sealed class BufferTraceListener : TraceListener
{
    private readonly LogCaptureBuffer _buffer;
    private readonly LogSource _source;
    private readonly StringBuilder _pending = new();
    private readonly object _lock = new();

    public BufferTraceListener(LogCaptureBuffer buffer)
    {
        _buffer = buffer;
        _source = LogSource.Debug;
        Name = "ReactorDevtoolsCapture";
    }

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (_lock)
        {
            foreach (var c in message)
            {
                if (c == '\n') FlushLocked();
                else if (c != '\r') _pending.Append(c);
            }
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(message)) _pending.Append(message);
            FlushLocked();
        }
    }

    public override void Flush()
    {
        lock (_lock) { if (_pending.Length > 0) FlushLocked(); }
    }

    private void FlushLocked()
    {
        var line = _pending.ToString();
        _pending.Clear();
        _buffer.Append(_source, level: null, line);
    }
}

/// <summary>
/// One-shot install of Debug/Trace listener + Console.Out/Err tee into a
/// shared <see cref="LogCaptureBuffer"/>. Idempotent — repeat calls are no-ops.
/// </summary>
internal static class LogCaptureInstall
{
    private static readonly object _installLock = new();
    private static LogCaptureBuffer? _shared;

    /// <summary>Process-wide shared buffer, lazily created on first install.</summary>
    public static LogCaptureBuffer? Shared => Volatile.Read(ref _shared);

    /// <summary>
    /// Installs capture. When <paramref name="forwardConsole"/> is false
    /// (stdio MCP transport), Console.Out writes land in the buffer but are
    /// NOT passed through to the underlying stream. Safe because
    /// <c>DevtoolsMcpServer</c> writes stdio responses via
    /// <c>Console.OpenStandardOutput</c>, bypassing <c>Console.Out</c>.
    /// Console.Error always forwards; stderr is never the MCP channel.
    /// </summary>
    public static LogCaptureBuffer Install(long capacityBytes, bool forwardConsole)
    {
        // Lock on install so concurrent callers don't see _shared null after
        // another thread has latched _installed. Install runs once per process
        // from TryRunDevtools today, but a lock is cheaper than a spin-wait
        // and closes the race regardless of call site.
        lock (_installLock)
        {
            if (_shared is { } existing) return existing;

            var buf = new LogCaptureBuffer(capacityBytes);

            // Debug/Trace share a single listener collection
            // (Trace.Listeners == Debug.Listeners). One add covers both
            // Debug.WriteLine and Trace.WriteLine. Add rather than clear so
            // the debugger's DefaultTraceListener still routes to the VS
            // Output window.
            Trace.Listeners.Add(new BufferTraceListener(buf));

            // Console tee. Wrap whatever was set previously so later installers
            // (test loggers, xunit capture) still see output.
            Console.SetOut(new TeeTextWriter(
                forward: forwardConsole ? Console.Out : null,
                buffer: buf,
                source: LogSource.Stdout));
            Console.SetError(new TeeTextWriter(
                forward: Console.Error,
                buffer: buf,
                source: LogSource.Stderr));

            Volatile.Write(ref _shared, buf);
            return buf;
        }
    }

    /// <summary>Test hook: reset the shared buffer. Do not call in product code.</summary>
    internal static void ResetForTests()
    {
        lock (_installLock) Volatile.Write(ref _shared, null);
    }
}
