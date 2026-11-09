using System.Globalization;
using System.Text.Json;

namespace OpenClaw.TestSupport;

public enum NativeUiExceptionSource { Unknown, WinUiUnhandled, DispatchCatch, ChatUnhandled }
public enum NativeUiPhase
{
    Unknown, Idle, UiStarting, UiReady, UiStopping, UiExited,
    ChatSetup, ChatBody, ChatShowingFlyout, ChatFlyoutShown, ChatClosingPopups,
    ChatDisposingWindow, ChatDisposingHost, ChatClosingWindow, ChatWindowClosed,
    ChatDisposingProvider, ChatClosed, ChatScopeEnded
}

/// <summary>
/// Testhost-only metadata: opt-in file capture with bounded, content-free stderr
/// fallback. A scope/last-started dispatch is an observation,
/// not attribution of a late exception to that operation. No exception or UI object
/// is retained. This does not read stowed/restricted error records or handle errors.
/// </summary>
public sealed class NativeUiExceptionDiagnostics : IDisposable
{
    public const string PathVariable = "OPENCLAW_UI_EXCEPTION_METADATA_PATH";
    public const string InvocationVariable = "OPENCLAW_UI_EXCEPTION_INVOCATION_ID";
    public const int MaxRecords = 4096;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRecordBytes = 1024;
    public const int MaxTypeCharacters = 128;
    public const int MaxFallbackRecords = 16;

    private readonly object _gate = new();
    private readonly int _processId = Environment.ProcessId;
    private readonly string? _invocationId;
    private readonly string? _configuredSourceRevision;
    private readonly Action _failureSignal;
    private readonly Action<string> _fallbackWriter;
    private readonly bool _captureRequested;
    private bool _fileOpened;
    private int _fallbackRecords;
    private Stream? _stream;
    private bool _failed, _overflowed, _disposed;
    private int _records, _bytes;
    private long _nextScope, _scope, _nextDispatch, _lastDispatch;
    private string _lastDispatchState = "none";
    private NativeUiPhase _phase = NativeUiPhase.Idle;

    private NativeUiExceptionDiagnostics(string? invocationId, string? sourceRevision, Action? failureSignal,
        bool captureRequested, Action<string>? fallbackWriter)
    {
        _captureRequested = captureRequested;
        _fallbackWriter = fallbackWriter ?? (line => Console.Error.WriteLine(line));
        _invocationId = Guid.TryParseExact(invocationId, "N", out var id) ? id.ToString("N") : null;
        _configuredSourceRevision = sourceRevision is { Length: 40 } && sourceRevision.All(char.IsAsciiHexDigit)
            ? sourceRevision.ToLowerInvariant() : null;
        _failureSignal = failureSignal ?? (() => Console.Error.WriteLine(
            $"{{\"schema\":1,\"source\":\"writeFailure\",\"processId\":{_processId}}}"));
    }

    public static NativeUiExceptionDiagnostics FromEnvironment() => Open(
        Environment.GetEnvironmentVariable(PathVariable),
        Environment.GetEnvironmentVariable(InvocationVariable),
        Environment.GetEnvironmentVariable("OPENCLAW_RELEASE_SOURCE_SHA"));

    // The optional writer avoids process-global Console changes in pure fallback tests.
    // The stream factory is a fault-injection seam for pure tests; the harness uses
    // CreateNew, one writer, read-only sharing and synchronous durable flushes.
    public static NativeUiExceptionDiagnostics Open(string? path, string? invocationId, string? sourceRevision,
        Action? failureSignal = null, Func<string, Stream>? createStream = null, Action<string>? fallbackWriter = null)
    {
        var sink = new NativeUiExceptionDiagnostics(invocationId, sourceRevision, failureSignal,
            !string.IsNullOrEmpty(path), fallbackWriter);
        if (string.IsNullOrEmpty(path)) return sink; // Explicitly disabled outside the invocation wrapper.
        try
        {
            if (!Path.IsPathFullyQualified(path) || sink._invocationId is null)
                throw new ArgumentException("Invalid diagnostic destination or invocation identity.");
            sink._stream = createStream is null
                ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)
                : createStream(path);
            if (sink._stream is null) throw new IOException("Diagnostic stream unavailable.");
            sink._fileOpened = true;
            sink.Write("ready"); // Flushed before the UI thread/bootstrap starts.
        }
        catch { sink.Fail(); }
        return sink;
    }

    public bool WriteFailed { get { lock (_gate) return _failed; } }

    public ChatScope BeginChatScope()
    {
        lock (_gate)
        {
            _scope = ++_nextScope;
            _phase = NativeUiPhase.ChatSetup;
            Write("lifecycle");
            return new ChatScope(this, _scope);
        }
    }

    public void SetPhase(NativeUiPhase phase)
    {
        lock (_gate)
        {
            _phase = Enum.IsDefined(phase) ? phase : NativeUiPhase.Unknown;
            Write("lifecycle");
        }
    }

    public long BeginDispatch()
    {
        lock (_gate)
        {
            _lastDispatch = ++_nextDispatch;
            _lastDispatchState = "running";
            return _lastDispatch;
        }
    }

    public void CompleteDispatch(long id)
    {
        lock (_gate)
            if (_lastDispatch == id) _lastDispatchState = "completed";
    }

    // The getter is evaluated synchronously inside the guard, including WinRT's
    // projected args.Exception getter. Neither it nor the exception is stored.
    public void RecordException(NativeUiExceptionSource source, Func<Exception?> readException, long sourceDispatchId = 0)
    {
        lock (_gate)
        {
            if (_disposed || ((_stream is null || _failed || _overflowed) && _fallbackRecords >= MaxFallbackRecords)) return;
            Exception? exception = null;
            var readFailed = false;
            try { exception = readException(); }
            catch { readFailed = true; }
            if (source == NativeUiExceptionSource.DispatchCatch && _lastDispatch == sourceDispatchId)
                _lastDispatchState = "faulted";
            var name = Enum.IsDefined(source) ? source.ToString() : "Unknown";
            if (!Write(name, exception, readFailed, sourceDispatchId))
                WriteFallback(name, exception, readFailed);
        }
    }

    private bool Write(string source, Exception? exception = null, bool readFailed = false, long sourceDispatchId = 0)
    {
        if (_disposed || _stream is null || _failed || _overflowed) return false;
        try
        {
            var (type, hresult) = ProjectException(exception);
            var record = Serialize(source, type, hresult, readFailed, sourceDispatchId);
            if (record.Length > MaxRecordBytes) { Fail(); return false; }
            // Reserve one bounded record for explicit truncation. No silent overwrite/ring.
            if (_records >= MaxRecords - 1 || _bytes + record.Length > MaxBytes - MaxRecordBytes)
            {
                record = Serialize("overflow", null, null, false, 0);
                WriteBytes(record);
                _overflowed = true;
                return false;
            }
            WriteBytes(record);
            return true;
        }
        catch { Fail(); return false; }
    }

    // File capture is optional; exception reporting is not. This separate budget
    // includes a final truncation record and counts failed writer attempts too.
    private void WriteFallback(string source, Exception? exception, bool readFailed)
    {
        if (_fallbackRecords >= MaxFallbackRecords) return;
        var truncated = ++_fallbackRecords == MaxFallbackRecords;
        try
        {
            var (type, hresult) = ProjectException(truncated ? null : exception);
            var line = JsonSerializer.Serialize(new
            {
                schema = 1, source = truncated ? "fallbackOverflow" : source, processId = _processId,
                fallbackSequence = _fallbackRecords,
                fileCapture = _overflowed ? "overflow" : _fileOpened ? "failed" : _captureRequested ? "unavailable" : "disabled",
                exceptionAvailable = type is not null, exceptionReadFailed = !truncated && readFailed,
                exceptionType = type, reportedHResult = hresult,
                reportedHResultHex = hresult is { } value ? "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture) : null
            });
            if (System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length <= MaxRecordBytes)
                _fallbackWriter(line);
        }
        catch { /* A fallback writer/projection failure must not replace the original exception. */ }
    }

    private static (string? Type, int? HResult) ProjectException(Exception? exception)
    {
        // Only non-virtual, content-free metadata. This is not a stowed ResultCode.
        var type = exception?.GetType().Name;
        if (type is not null)
            type = new string(type.Take(MaxTypeCharacters)
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' ? c : '_').ToArray());
        return (type, exception?.HResult);
    }

    private byte[] Serialize(string source, string? type, int? hresult, bool readFailed, long sourceDispatchId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, sequence = _records + 1, source, processId = _processId,
            managedThreadId = Environment.CurrentManagedThreadId, invocationId = _invocationId,
            configuredSourceRevision = _configuredSourceRevision,
            scopeId = _scope, phase = _phase.ToString(), lastStartedDispatchId = _lastDispatch,
            lastStartedDispatchState = _lastDispatchState, sourceDispatchId,
            exceptionAvailable = type is not null, exceptionReadFailed = readFailed,
            exceptionType = type, reportedHResult = hresult,
            reportedHResultHex = hresult is { } value ? "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture) : null
        });
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    private void WriteBytes(byte[] bytes)
    {
        var stream = _stream!;
        stream.Write(bytes);
        if (stream is FileStream file) file.Flush(flushToDisk: true);
        else stream.Flush();
        _records++;
        _bytes += bytes.Length;
    }

    private void Fail()
    {
        if (_failed) return;
        _failed = true;
        // A failing diagnostic destination, fallback console, or test observer must
        // never replace the original exception. The fixed sentinel contains no path.
        try { _failureSignal(); }
        catch { /* Diagnostic failure reporting is itself best-effort. */ }
        var stream = _stream;
        _stream = null;
        try { stream?.Dispose(); }
        catch { /* Already signalled; do not recurse or replace the original error. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Write("closed");
            _disposed = true;
            var stream = _stream;
            _stream = null;
            try { stream?.Dispose(); }
            catch { Fail(); }
        }
    }

    public sealed class ChatScope : IDisposable
    {
        private readonly NativeUiExceptionDiagnostics _owner;
        private readonly long _id;
        internal ChatScope(NativeUiExceptionDiagnostics owner, long id) { _owner = owner; _id = id; }
        public void SetPhase(NativeUiPhase phase)
        {
            lock (_owner._gate)
            {
                if (_owner._scope != _id) return;
                _owner._phase = Enum.IsDefined(phase) ? phase : NativeUiPhase.Unknown;
                _owner.Write("lifecycle");
            }
        }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._scope != _id) return;
                _owner._phase = NativeUiPhase.ChatScopeEnded;
                _owner.Write("lifecycle");
                _owner._scope = 0;
                _owner._phase = NativeUiPhase.Idle;
            }
        }
    }
}
