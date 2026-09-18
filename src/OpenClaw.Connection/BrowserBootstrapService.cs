using System.Text.Json;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Connection;

/// <summary>Pairs only the active, connected app-managed local gateway. Never owns a relay or gateway lifecycle.</summary>
public sealed class BrowserBootstrapService(
    Func<GatewayRecord?> getActive,
    Func<GatewayRecord, bool> isAllowed,
    Func<GatewayRecord, CancellationToken, Task<bool>> verifyEndpoint,
    Func<string, CancellationToken, Task<string>> runPairing)
{
    private long _generation;

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<byte[]> HandleAsync(byte[] payload, CancellationToken ct)
    {
        var generation = Interlocked.Read(ref _generation);
        var request = BrowserNativeProtocol.ParseRequest(payload);
        // Local-gateway transport wakes Browser control itself. Arbitrary relay ports must never start a process.
        if (request.Op != "bootstrap") return BrowserNativeProtocol.Failure("manual_required");
        var record = getActive();
        // First-install Chrome approval can precede completion of the Companion setup wizard.
        // Keep that state retryable; manual_required is a durable extension state.
        if (record is null) return BrowserNativeProtocol.Failure("pairing_unavailable");
        if (!IsManagedLocal(record)) return BrowserNativeProtocol.Failure("manual_required");
        var pinned = record!;
        if (generation != Interlocked.Read(ref _generation) || !Current(pinned) ||
            !await verifyEndpoint(pinned, ct) || !Current(pinned) || generation != Interlocked.Read(ref _generation))
            return BrowserNativeProtocol.Failure("pairing_unavailable");
        var json = await runPairing(pinned.SetupManagedDistroName!, ct);
        var pairing = ParsePairing(json, new Uri(pinned.Url).Port);
        // A disconnect, switch, or preference change while the CLI ran wins over returning credentials.
        if (generation != Interlocked.Read(ref _generation) || !Current(pinned) ||
            !await verifyEndpoint(pinned, ct) || !Current(pinned) || generation != Interlocked.Read(ref _generation))
            return BrowserNativeProtocol.Failure("pairing_unavailable");
        return BrowserNativeProtocol.Pairing(request.Nonce, pairing);
    }

    private bool Current(GatewayRecord pinned)
    {
        var current = getActive();
        return current is not null && current.Id == pinned.Id && current.Url == pinned.Url &&
            current.SetupManagedDistroName == pinned.SetupManagedDistroName && IsManagedLocal(current) && isAllowed(current);
    }

    public static bool IsManagedLocal(GatewayRecord? record) =>
        record is { IsLocal: true, SshTunnel: null } &&
        !string.IsNullOrWhiteSpace(record.SetupManagedDistroName) &&
        record.SetupManagedDistroName.Length <= 64 &&
        record.SetupManagedDistroName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "ws" && uri.Host is "127.0.0.1" or "localhost" or "[::1]" &&
        uri.Port is >= 1 and <= 65535 && uri.AbsolutePath == "/" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static string ParsePairing(string json, int gatewayPort)
    {
        if (json.Length > 16384) throw new InvalidDataException("pairing_unavailable");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) ||
            !root.TryGetProperty("remote", out var remote) || remote.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("relayPort", out var relayPort) || !relayPort.TryGetInt32(out var port) || port is < 1 or > 65535 ||
            !root.TryGetProperty("pairingString", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("pairing_unavailable");
        var pairing = value.GetString()!;
        if (!Uri.TryCreate(pairing, UriKind.Absolute, out var uri) ||
            uri.Scheme != "ws" || uri.Host != "127.0.0.1" || uri.Port != gatewayPort ||
            uri.AbsolutePath != "/browser/extension" || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length is < 33 or > 257 ||
            uri.Fragment[1..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("pairing_unavailable");
        var expectedHint = $"ws://127.0.0.1:{gatewayPort}";
        var query = uri.Query;
        if (!query.StartsWith("?gateway=", StringComparison.Ordinal) || query.Contains('&') ||
            Uri.UnescapeDataString(query[9..]) != expectedHint)
            throw new InvalidDataException("pairing_unavailable");
        return pairing;
    }
}
