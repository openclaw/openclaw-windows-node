using System.Text;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>Bounded, incremental output remains readable while the process owner finishes cleanup.</summary>
internal sealed class CommandOutputCapture(long byteBudget)
{
    private readonly object _exitLock = new();
    private Task? _rootExit;
    private Func<(int Code, bool TimedOut)>? _readExit;
    private (int Code, bool TimedOut)? _exit;

    internal BoundedStream StandardOutput { get; } = new(byteBudget);
    internal BoundedStream StandardError { get; } = new(byteBudget);

    internal void BindRootExit(Task rootExit, Func<(int, bool)> readExit)
    {
        lock (_exitLock) { _rootExit = rootExit; _readExit = readExit; }
    }

    // Cache before disposing the process, including when its caller already returned a deadline snapshot.
    internal void CaptureRootExit()
    {
        lock (_exitLock)
            if (_exit is null && _rootExit?.IsCompletedSuccessfully == true)
                _exit = _readExit!();
    }

    internal CommandResult Snapshot(NodeToolExecutionMode mode, bool deadlineExpired = false)
    {
        (int Code, bool TimedOut)? exit;
        lock (_exitLock)
        {
            CaptureRootExit();
            exit = _exit;
        }
        var timedOut = exit?.TimedOut ?? deadlineExpired;
        var code = exit?.Code ?? -1;
        return new()
        {
            Stdout = StandardOutput.Snapshot(deadlineExpired),
            Stderr = StandardError.Snapshot(deadlineExpired),
            ExitCode = code, TimedOut = timedOut, ExecutionMode = mode,
            ErrorCategory = timedOut ? NodeToolErrorCategory.Timeout :
                code == 0 ? NodeToolErrorCategory.None : NodeToolErrorCategory.CommandFailed,
        };
    }

    internal static async Task WaitForExitOrInterruptionAsync(Task exit, Task output, Task error, CancellationToken stop)
    {
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Register(() => interrupted.TrySetResult());
        foreach (var reader in new[] { output, error })
            _ = reader.ContinueWith(_ => interrupted.TrySetResult(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await Task.WhenAny(exit, interrupted.Task).ConfigureAwait(false);
    }

    internal static async Task DrainAfterExitAsync(Task drains, CancellationToken stop)
    {
        // The same stop token ends this grace at the original command deadline.
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(stop);
        grace.CancelAfter(500);
        try { await drains.WaitAsync(grace.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (grace.IsCancellationRequested) { }
    }

    internal sealed class BoundedStream(long byteBudget)
    {
        private const string Marker = "\n[truncated]";
        private readonly object _gate = new();
        private readonly int _cap = (int)Math.Clamp(byteBudget, 0, int.MaxValue - 16);
        private readonly StringBuilder _text = new((int)Math.Min(Math.Max(byteBudget, 0), 8192));
        private readonly byte[] _prefix = new byte[4];
        private readonly char[] _chars = new char[8194];
        private int _prefixLength;
        private Decoder? _decoder;
        private int _retained;
        private bool _truncated;
        private bool _completed;
        private bool _interrupted;
        private char? _pendingHigh;

        internal async Task CollectAsync(Stream? stream, CancellationToken stop = default)
        {
            if (stream is null) { Complete(); return; }
            var bytes = new byte[8192];
            try
            {
                while (true)
                {
                    // Public FileStream cancellation also interrupts synchronous Windows pipe handles
                    // through the runtime's AsyncOverSyncWithIoCancellation, unlike Dispose alone.
                    var count = await stream.ReadAsync(bytes.AsMemory(), stop).ConfigureAwait(false);
                    if (count == 0) { Complete(); return; }
                    Append(bytes.AsSpan(0, count));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Interrupt(); }
        }

        internal void Collect(Stream? stream)
        {
            if (stream is null) { Complete(); return; }
            var bytes = new byte[8192];
            try
            {
                while (true)
                {
                    // The SDK native stream implements synchronous Read only. Its owner must use
                    // IMxcStreamCloser to interrupt it, not a token or concurrent Dispose.
                    var count = stream.Read(bytes, 0, bytes.Length);
                    if (count == 0) { Complete(); return; }
                    Append(bytes.AsSpan(0, count));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                lock (_gate) { if (!_interrupted) throw; }
            }
        }

        internal void Interrupt()
        {
            lock (_gate) { if (!_completed) _interrupted = true; }
        }

        internal string Snapshot(bool incomplete = false)
        {
            lock (_gate)
            {
                var value = _text.ToString();
                if (!(_truncated || _interrupted || (incomplete && !_completed)) || _cap < Marker.Length)
                    return value;
                var limit = _cap - Marker.Length;
                var bytes = 0;
                var length = 0;
                foreach (var rune in value.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > limit) break;
                    bytes += rune.Utf8SequenceLength;
                    length += rune.Utf16SequenceLength;
                }
                return value[..length] + Marker;
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                while (_decoder is null && !bytes.IsEmpty)
                {
                    _prefix[_prefixLength++] = bytes[0];
                    bytes = bytes[1..];
                    SelectEncoding(eof: false);
                }
                if (_decoder is not null) Decode(bytes, eof: false);
            }
        }

        private void Complete()
        {
            lock (_gate)
            {
                // Interrupted native reads can report EOF. Do not manufacture replacement characters
                // for a partial encoding sequence at a deadline or post-exit cutoff.
                if (!_interrupted)
                {
                    if (_decoder is null) SelectEncoding(eof: true);
                    Decode([], eof: true);
                }
                _completed = true;
            }
        }

        private void SelectEncoding(bool eof)
        {
            var prefix = _prefix.AsSpan(0, _prefixLength);
            Encoding encoding = new UTF8Encoding(false, false);
            var bom = 0;
            if (prefix.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
            { encoding = new UTF32Encoding(false, false); bom = 4; }
            else if (prefix.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            { encoding = new UTF32Encoding(true, false); bom = 4; }
            else if (prefix.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
            else if (prefix.StartsWith(new byte[] { 0xfe, 0xff }))
            { encoding = Encoding.BigEndianUnicode; bom = 2; }
            else if (prefix.StartsWith(new byte[] { 0xff, 0xfe }) &&
                (eof || _prefixLength == 4 || (_prefixLength == 3 && prefix[2] != 0)))
            { encoding = Encoding.Unicode; bom = 2; }
            else if (!eof && (IsPrefixOf([0xff, 0xfe, 0, 0]) || IsPrefixOf([0, 0, 0xfe, 0xff]) ||
                IsPrefixOf([0xef, 0xbb, 0xbf]) || IsPrefixOf([0xfe, 0xff])))
                return;

            _decoder = encoding.GetDecoder();
            Decode(prefix[bom..], eof: false);
        }

        private bool IsPrefixOf(ReadOnlySpan<byte> bom) =>
            _prefixLength < bom.Length && bom[.._prefixLength].SequenceEqual(_prefix.AsSpan(0, _prefixLength));

        private void Decode(ReadOnlySpan<byte> bytes, bool eof)
        {
            var count = _decoder!.GetChars(bytes, _chars, flush: eof);
            var offset = 0;
            if (_pendingHigh is { } high && (count > 0 || eof))
            {
                if (count > 0 && char.IsLowSurrogate(_chars[0]))
                { AppendChars([high, _chars[0]]); offset = 1; }
                else AppendChars("\uFFFD");
                _pendingHigh = null;
            }
            for (var i = offset; i < count;)
            {
                if (i == count - 1 && char.IsHighSurrogate(_chars[i]))
                { _pendingHigh = _chars[i]; break; }
                var width = char.IsHighSurrogate(_chars[i]) && i + 1 < count && char.IsLowSurrogate(_chars[i + 1]) ? 2 : 1;
                AppendChars(_chars.AsSpan(i, width));
                i += width;
            }
        }

        private void AppendChars(ReadOnlySpan<char> value)
        {
            var cost = Encoding.UTF8.GetByteCount(value);
            if (!_truncated && cost <= _cap - _retained)
            { _text.Append(value); _retained += cost; }
            else _truncated = true;
        }
    }
}
