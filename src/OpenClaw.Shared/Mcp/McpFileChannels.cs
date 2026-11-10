using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Mcp;

/// <summary>Authenticated, versioned extension on the existing MCP listener. Not an MCP standard.</summary>
internal sealed class McpFileChannels
{
    internal const string MetadataKey = "openclaw/file-channel";
    internal const string MethodPrefix = "openclaw/files/";
    internal const int MaxLeases = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private long _epoch;
    public long Epoch { get { lock (_gate) return _epoch; } }

    public long Reset()
    {
        lock (_gate)
        {
            _epoch++;
            foreach (var lease in _leases.Values) lease.Cancel();
            _leases.Clear();
            return _epoch;
        }
    }

    public object Control(string method, JsonElement args, long epoch)
    {
        lock (_gate)
        {
            ValidateEpoch(epoch);
            foreach (var expired in _leases.Where(p => p.Value.Expired).ToArray())
            {
                expired.Value.Cancel();
                _leases.Remove(expired.Key);
            }
            if (args.ValueKind != JsonValueKind.Object || args.GetProperty("v").GetInt32() != 1)
                throw Invalid();
            if (method == MethodPrefix + "open")
            {
                var command = args.GetProperty("command").GetString();
                if (command is not ("file.fetch" or "file.create") || _leases.Count >= MaxLeases) throw Invalid();
                var requestId = args.GetProperty("requestId");
                if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw Invalid();
                var timeout = args.GetProperty("timeoutMs").GetInt32();
                if (timeout is < 1 or > 600_000) throw Invalid();
                var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                _leases.Add(id, new Lease(command, McpToolBridge.GetRequestKey(requestId), timeout));
                return new { v = 1, lease = id };
            }
            var leaseId = args.GetProperty("lease").GetString() ?? "";
            if (!_leases.TryGetValue(leaseId, out var lease)) throw Invalid();
            switch (method)
            {
                case MethodPrefix + "input":
                    lease.Input(args.GetProperty("seq").GetInt64(), args.GetProperty("payloadJSON").GetString()!);
                    return new { accepted = true };
                case MethodPrefix + "poll":
                    return lease.Poll(args.GetProperty("ack").GetInt64());
                case MethodPrefix + "close":
                    lease.Cancel();
                    _leases.Remove(leaseId);
                    return new { closed = true };
                default: throw Invalid();
            }
        }
    }

    public Lease Attach(JsonElement metadata, string command, string? requestKey, long epoch)
    {
        lock (_gate)
        {
            ValidateEpoch(epoch);
            if (metadata.GetProperty("v").GetInt32() != 1 ||
                !_leases.TryGetValue(metadata.GetProperty("lease").GetString() ?? "", out var lease) ||
                lease.Command != command || lease.RequestKey != requestKey || lease.Expired || lease.Attached)
                throw Invalid();
            lease.Attached = true;
            return lease;
        }
    }

    private void ValidateEpoch(long epoch)
    {
        if (epoch != _epoch) throw Invalid();
    }

    internal static FileCommandException Invalid() =>
        new("FILE_CHANNEL_INVALID", "Invalid, expired, busy, or already attached file channel.");

    internal sealed class Lease
    {
        private const int OutputFrames = 16;
        private readonly object _gate = new();
        private readonly Queue<OutputFrame> _output = new();
        private TaskCompletionSource _space = Signal();
        private readonly CancellationTokenSource _lifetime;
        private long _nextOutput, _ack, _input;
        private byte[]? _lastInputHash;
        public Lease(string command, string requestKey, int timeout)
        {
            Command = command;
            RequestKey = requestKey;
            _lifetime = new CancellationTokenSource(timeout);
            Channel = new FileFrameChannel(WriteAsync, _lifetime.Token);
        }
        public string Command { get; }
        public string RequestKey { get; }
        public bool Attached { get; set; }
        public bool Expired => _lifetime.IsCancellationRequested;
        public FileFrameChannel Channel { get; }
        public CancellationToken Token => _lifetime.Token;
        public bool Completed { get; private set; }

        private async ValueTask WriteAsync(string payload, CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_output.Count < OutputFrames)
                    {
                        _output.Enqueue(new OutputFrame(_nextOutput++, payload));
                        return;
                    }
                    wait = _space.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public void Input(long seq, string payload)
        {
            lock (_gate)
            {
                if (Expired || Completed || seq < 0 || seq > FileLimits.SafeInteger ||
                    Encoding.UTF8.GetByteCount(payload) > FileFrameChannel.FrameBytes) throw Invalid();
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
                if (seq == _input - 1 && _lastInputHash != null &&
                    CryptographicOperations.FixedTimeEquals(hash, _lastInputHash)) return;
                if (seq != _input) { Cancel(); throw Invalid(); }
                Channel.Receive(payload);
                _lastInputHash = hash;
                _input++;
            }
        }

        public object Poll(long ack)
        {
            lock (_gate)
            {
                if (ack < _ack || ack > _nextOutput) throw Invalid();
                _ack = ack;
                while (_output.TryPeek(out var frame) && frame.Seq < ack) _output.Dequeue();
                var space = _space;
                _space = Signal();
                space.TrySetResult();
                return new { v = 1, frames = _output.ToArray(), completed = Completed };
            }
        }

        public void Complete() { lock (_gate) Completed = true; }
        public void Cancel() { _lifetime.Cancel(); Channel.Abort(); }
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public sealed record OutputFrame(
            [property: JsonPropertyName("seq")] long Seq,
            [property: JsonPropertyName("payloadJSON")] string PayloadJSON);
    }
}
