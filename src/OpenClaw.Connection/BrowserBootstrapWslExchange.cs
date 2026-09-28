using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection;

// Private framing only. A missing terminal acknowledgment never releases request ownership.
internal sealed class BrowserBootstrapWslExchange(string requestId, string unitPrefix)
{
    internal const int ControlLimit = 2048, ResultLimit = 90000, WireLimit = ResultLimit + 2 * ControlLimit + 12;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _invocation, _challenge, _result, _outcome;
    private int _permitAttempted, _phase, _revoked;
    internal void Revoke() { Volatile.Write(ref _revoked,1); _result = null; }
    private bool _invalid;
    internal Task Ready => _ready.Task;
    // A transport failure must NOT complete this task. Missing acknowledgment retains ownership.
    internal Task Settlement => _settled.Task;
    internal bool Acknowledged => _settled.Task.IsCompletedSuccessfully;
    internal string Result => Volatile.Read(ref _revoked) == 0 && !_invalid && Acknowledged && _outcome == "completed" && _result is not null
        ? _result : throw Invalid();

    internal byte[] Permit()
    {
        if (Volatile.Read(ref _revoked) != 0 || !_ready.Task.IsCompletedSuccessfully || _invalid || Interlocked.Exchange(ref _permitAttempted, 1) != 0)
            throw Invalid();
        return Encode(new { v = 1, type = "permit", requestId, invocationId = _invocation, challenge = _challenge });
    }
    internal byte[] Cancel()
    {
        if (!_ready.Task.IsCompletedSuccessfully || _invalid) throw Invalid();
        return Encode(new { v = 1, type = "cancel", requestId, invocationId = _invocation });
    }
    internal async Task ReadToEndAsync(Stream stream)
    {
        var total = 0; var frames = 0;
        try
        {
            for (;;)
            {
                var header = new byte[4];
                var count = await stream.ReadAsync(header);
                if (count == 0) { if (!Acknowledged) throw Invalid(); return; }
                if (count < 4) await stream.ReadExactlyAsync(header.AsMemory(count));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (size is 0 or > ResultLimit || ++frames > 3 || (total += checked((int)size + 4)) > WireLimit) throw Invalid();
                var bytes = new byte[(int)size]; await stream.ReadExactlyAsync(bytes);
                Accept(bytes);
            }
        }
        catch { _invalid = true; _result = null; throw; }
    }
    internal void Accept(byte[] bytes)
    {
        try
        {
            if (_invalid || _phase == 3 || bytes.Length > ResultLimit || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
            _ = Utf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                root.GetProperty("v").GetRawText() != "1" || Text(root, "requestId") != requestId || !Hex(requestId)) throw Invalid();
            var type = Text(root, "type");
            var expected = type switch
            {
                "ready" => "v,type,requestId,invocationId,challenge,unit,bootId,pid,start,cgroup,environmentClean",
                "result" => "v,type,requestId,invocationId,payload",
                "settled" => "v,type,requestId,invocationId,outcome,startSealed,gateExited,cgroupEmpty,startJobSettled",
                _ => throw Invalid()
            };
            if (!properties.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Split(',').Order(StringComparer.Ordinal)) ||
                type != "result" && bytes.Length > ControlLimit) throw Invalid();
            var invocation = Text(root, "invocationId"); if (!Hex(invocation)) throw Invalid();
            if (type == "ready")
            {
                if (_phase != 0 || Text(root, "unit") != unitPrefix + requestId + ".service" || !True(root,"environmentClean") ||
                    !root.GetProperty("pid").TryGetInt32(out var pid) || pid <= 1 || !root.GetProperty("start").TryGetInt64(out var start) || start <= 0)
                    throw Invalid();
                var boot = Text(root,"bootId");
                if (!Guid.TryParseExact(boot,"D",out var bootId) || bootId.ToString("D") != boot || bootId == Guid.Empty) throw Invalid();
                var group = Text(root,"cgroup");
                if (!group.StartsWith("/user.slice/",StringComparison.Ordinal) || group.Split('/').Contains("..") ||
                    !group.EndsWith("/" + unitPrefix + requestId + ".service",StringComparison.Ordinal)) throw Invalid();
                _challenge = Text(root,"challenge"); if (!Hex(_challenge)) throw Invalid();
                _invocation = invocation; _phase = 1; _ready.TrySetResult(); return;
            }
            if (_phase is not (1 or 2) || invocation != _invocation) throw Invalid();
            if (type == "result")
            {
                if (_phase != 1 || Volatile.Read(ref _permitAttempted) != 1) throw Invalid();
                var encoded = Text(root,"payload"); var payload = Convert.FromBase64String(encoded);
                if (payload.Length > 65536 || Convert.ToBase64String(payload) != encoded) throw Invalid();
                var result = Utf8.GetString(payload); if (result.Length > 16384) throw Invalid();
                _result = result; if (Volatile.Read(ref _revoked) != 0) _result = null;
                _phase = 2; return;
            }
            foreach (var key in new[] { "startSealed", "gateExited", "cgroupEmpty", "startJobSettled" }) if (!True(root,key)) throw Invalid();
            _outcome = Text(root,"outcome");
            if (_outcome is not ("completed" or "cancelled" or "failed") || _outcome == "completed" && _phase != 2) throw Invalid();
            if (_outcome != "completed") _result = null;
            _phase = 3; _settled.TrySetResult();
        }
        catch { _invalid = true; _result = null; throw Invalid(); }
    }
    private static bool Hex(string value) => value.Length == 32 && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static bool True(JsonElement value,string key) => value.GetProperty(key).ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value,string key) => value.GetProperty(key).ValueKind == JsonValueKind.String ? value.GetProperty(key).GetString()! : throw Invalid();
    private static InvalidDataException Invalid() => new("pairing_unavailable");
    private static byte[] Encode(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message); if (body.Length > ControlLimit) throw Invalid();
        var bytes = new byte[4 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes,body.Length); body.CopyTo(bytes,4); return bytes;
    }
}
