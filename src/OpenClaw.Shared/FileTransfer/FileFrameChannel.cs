using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>
/// The upstream v1 node-duplex JSON framing protocol, narrowed to file-sized messages.
/// Receive never awaits a consumer. The bounded queue includes incomplete and active delivery.
/// </summary>
public sealed class FileFrameChannel : IFileInvocationChannel, IAsyncDisposable
{
    public const int FragmentBytes = 8192;
    public const int FrameBytes = 16 * 1024;
    public const int SendWindowFrames = 16;
    private const int PendingMessages = 8;
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, ValueTask> _send;
    private readonly Func<IReadOnlyList<string>, CancellationToken, ValueTask>? _sendBatch;
    private readonly bool _requireReady;
    private readonly bool _announceReady;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly Channel<byte[]> _messages = Channel.CreateBounded<byte[]>(PendingMessages);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _incoming = new();
    private long _incomingMessage, _outgoingMessage;
    private long _fragment;
    private int _pendingBytes, _pendingCount;
    private bool _receivedReady, _receivedData, _listened, _sentReady;
    private Task _delivery = Task.CompletedTask;
    private CancellationTokenSource? _deliveryStop;
    private Task? _disposeTask;
    private int _aborted;

    public FileFrameChannel(Func<string, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken = default, bool requireReady = false, bool announceReady = true,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? sendBatch = null)
    {
        _send = send;
        _sendBatch = sendBatch;
        _requireReady = requireReady;
        _announceReady = announceReady;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _lifetime.Token;
    }

    public CancellationToken Token => _token;
    public Task Ready => _ready.Task;

    public void Receive(string frame)
    {
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            try
            {
                if (frame.Length == 0) return; // Progress heartbeat, not a framed message.
                if (Encoding.UTF8.GetByteCount(frame) > FrameBytes) throw Invalid();
                using var doc = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 4 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Invalid();
                var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
                if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                    !root.TryGetProperty("v", out var version) || !version.TryGetDouble(out var v) || v != 1 ||
                    !root.TryGetProperty("kind", out var kind)) throw Invalid();
                if (kind.GetString() == "ready")
                {
                    if (keys.Length != 2 || _receivedReady || _receivedData) throw Invalid();
                    _receivedReady = true;
                    _ready.TrySetResult();
                    return;
                }
                if (kind.GetString() != "data" || keys.Length != 6 ||
                    (_requireReady && !_receivedReady)) throw Invalid();
                var message = Counter(root, "message");
                var index = Counter(root, "index");
                if (message != _incomingMessage || index != _fragment) throw Invalid();
                var last = root.GetProperty("last").GetBoolean();
                var encoded = root.GetProperty("data").GetString() ?? throw Invalid();
                if (encoded.Length > (FragmentBytes + 2) / 3 * 4) throw Invalid();
                var bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded || bytes.Length > FragmentBytes ||
                    (!last && bytes.Length != FragmentBytes) || (last && bytes.Length == 0 && index != 0))
                    throw Invalid();
                if (_incoming.Length + bytes.Length > FileLimits.MessageBytes ||
                    _pendingBytes + bytes.Length > (_listened ? 2 * FileLimits.MessageBytes : FileLimits.MessageBytes) ||
                    (index == 0 && _pendingCount == PendingMessages)) throw Invalid();
                _receivedData = true;
                if (index == 0) _pendingCount++;
                _pendingBytes += bytes.Length;
                _incoming.Write(bytes);
                _fragment++;
                if (last)
                {
                    if (!_messages.Writer.TryWrite(_incoming.ToArray())) throw Invalid();
                    _incoming.SetLength(0);
                    _fragment = 0;
                    _incomingMessage++;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or FileCommandException)
            {
                Abort();
                throw Invalid();
            }
        }
    }

    private static long Counter(JsonElement root, string name)
    {
        return SafeInteger(root.GetProperty(name));
    }

    internal static long SafeInteger(JsonElement value)
    {
        // The peer parses JSON numbers as IEEE-754 doubles before Number.isSafeInteger.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 0 || number > FileLimits.SafeInteger ||
            Math.Truncate(number) != number) throw Invalid();
        return (long)number;
    }

    public async ValueTask<IAsyncDisposable> ListenAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            if (_listened) throw Invalid();
            _listened = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        try
        {
            // Do not run buffered callbacks until the readiness RPC has been acknowledged.
            if (_announceReady)
            {
                await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (_sentReady || _outgoingMessage != 0) throw Invalid();
                    await _send("{\"v\":1,\"kind\":\"ready\"}", linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    _sentReady = true;
                }
                finally { _sendGate.Release(); }
            }
            lock (_gate)
            {
                _deliveryStop = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
                _delivery = Task.Run(() => DeliverAsync(onMessage, _deliveryStop.Token), CancellationToken.None);
            }
            return new Registration(this);
        }
        catch { Abort(); throw; }
    }

    private async Task DeliverAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> callback, CancellationToken ct)
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await callback(message, ct).ConfigureAwait(false);
                lock (_gate) { _pendingBytes -= message.Length; _pendingCount--; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { Abort(); }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.Length > FileLimits.MessageBytes) { Abort(); throw Invalid(); }
        message = message.ToArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if ((_announceReady && !_sentReady) || _outgoingMessage > FileLimits.SafeInteger) throw Invalid();
            var index = 0;
            var offset = 0;
            var batch = _sendBatch is null ? null : new List<string>(SendWindowFrames);
            do
            {
                var length = Math.Min(FragmentBytes, message.Length - offset);
                var frame = JsonSerializer.Serialize(new
                {
                    v = 1, kind = "data", message = _outgoingMessage, index,
                    last = offset + length == message.Length,
                    data = Convert.ToBase64String(message.Span.Slice(offset, length))
                }, FileTransportJson.Options);
                if (batch is null)
                    await _send(frame, linked.Token).ConfigureAwait(false);
                else
                {
                    batch.Add(frame);
                    if (batch.Count == SendWindowFrames || offset + length == message.Length)
                    {
                        await _sendBatch!(batch, linked.Token).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
                linked.Token.ThrowIfCancellationRequested();
                offset += length;
                index++;
            } while (offset < message.Length);
            _outgoingMessage++;
        }
        catch { Abort(); throw; }
        finally { _sendGate.Release(); }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        _lifetime.Cancel();
        _messages.Writer.TryComplete();
        _ready.TrySetCanceled(Token);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Abort();
        await StopListeningAsync().ConfigureAwait(false);
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _incoming.Dispose();
                _deliveryStop?.Dispose();
            }
            _lifetime.Dispose();
        }
        finally
        {
            _sendGate.Release();
            _sendGate.Dispose();
        }
    }

    private async ValueTask StopListeningAsync()
    {
        Task delivery;
        lock (_gate)
        {
            _deliveryStop?.Cancel();
            delivery = _delivery;
        }
        await delivery.ConfigureAwait(false);
    }

    private sealed class Registration(FileFrameChannel owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.StopListeningAsync();
    }

    private static FileCommandException Invalid() => new("INVALID_FILE_FRAME", "Invalid or over-capacity file channel frame.");
}

public static class FileTransportJson
{
    // JSON is transported as data, never inserted into HTML. Avoid expanding every '+' in base64.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
    public const int EnvelopeBytes = FileLimits.RequestBytes + 6 * FileLimits.MetadataBytes;
    public const int ResultEnvelopeBytes = FileLimits.ResultBytes + 6 * FileLimits.MetadataBytes;
    public const string EnvelopeHeader = "X-OpenClaw-File-Command";
}
