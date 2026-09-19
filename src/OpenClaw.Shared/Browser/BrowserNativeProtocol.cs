using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Browser;

/// <summary>The v1 Chrome bootstrap contract shared with the browser plugin.</summary>
public static class BrowserNativeProtocol
{
    public const string HostName = "ai.openclaw.browser_bootstrap";
    public const string ExtensionId = "kcdjddhmeafeomebliikmbpblkmkfoig";
    public const string Origin = "chrome-extension://" + ExtensionId + "/";
    public const string PipeName = "OpenClawTray.BrowserBootstrap.v1";
    public const int RequestLimit = 4096;
    public const int ResponseLimit = 1024 * 1024 - 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record Request(string Op, string Nonce, int? RelayPort = null);

    public static bool IsAllowedCaller(string[] args) =>
        args.Length is 1 or 2 && args[0] == Origin &&
        (args.Length == 1 || (args[1].StartsWith("--parent-window=", StringComparison.Ordinal) &&
         ulong.TryParse(args[1]["--parent-window=".Length..],
             System.Globalization.NumberStyles.None,
             System.Globalization.CultureInfo.InvariantCulture, out _)));

    public static Request ParseRequest(byte[] payload)
    {
        if (payload.Length is 0 or > RequestLimit)
            throw new InvalidDataException("invalid_frame");
        string json;
        try { json = StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException) { throw new InvalidDataException("invalid_utf8"); }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("invalid_request");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                !root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var v) || v != 1 ||
                !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String ||
                !IsNonce(nonce.GetString()!))
                throw new InvalidDataException("invalid_request");
            var operation = op.GetString();
            string[] expected = operation == "ensure_relay" ? ["v", "op", "nonce", "relayPort"] : ["v", "op", "nonce"];
            if (properties.Length != expected.Length || properties.Any(p => !expected.Contains(p.Name)))
                throw new InvalidDataException("invalid_request");
            if (operation == "bootstrap") return new(operation, nonce.GetString()!);
            if (operation == "ensure_relay" && root.GetProperty("relayPort").TryGetInt32(out var port) && port is >= 1 and <= 65535)
                return new(operation, nonce.GetString()!, port);
            throw new InvalidDataException("invalid_request");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("invalid_request");
        }
    }

    private static bool IsNonce(string nonce)
    {
        if (nonce.Length is < 22 or > 43 || nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return false;
        try
        {
            var padded = nonce.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(padded);
            return bytes.Length is >= 16 and <= 32 &&
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == nonce;
        }
        catch (FormatException) { return false; }
    }

    // Windows x64/ARM64 both use little-endian lengths. Raw streams never perform CR/LF conversion.
    public static async Task<byte[]> ReadAsync(Stream input, int limit, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await input.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length == 0 || length > limit) throw new InvalidDataException("invalid_frame");
            var payload = new byte[(int)length];
            await input.ReadExactlyAsync(payload, ct);
            return payload;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("invalid_frame"); }
    }

    public static async Task WriteAsync(Stream output, byte[] payload, CancellationToken ct)
    {
        if (payload.Length is 0 or > ResponseLimit) throw new InvalidDataException("invalid_frame");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await output.WriteAsync(header, ct);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }

    public static byte[] Failure(string code) => JsonSerializer.SerializeToUtf8Bytes(new { v = 1, ok = false, code });
    public static byte[] Pairing(string nonce, string pairingString) =>
        JsonSerializer.SerializeToUtf8Bytes(new { v = 1, ok = true, nonce, pairingString });
}
