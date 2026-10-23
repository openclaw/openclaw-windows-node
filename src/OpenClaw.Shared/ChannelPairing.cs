using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record ChannelPairingAccount(string Channel, string ChannelLabel, string AccountId, string? AccountLabel);

public sealed record ChannelPairingRequest(
    string RequestId, string Channel, string ChannelLabel, string AccountId, string? AccountLabel,
    string SenderId, string SenderLabel, string CreatedAt, string ExpiresAt);

public sealed record ChannelPairingSnapshot(
    IReadOnlyList<ChannelPairingAccount> Accounts, IReadOnlyList<ChannelPairingRequest> Requests);

public interface IChannelPairingGateway
{
    long? ConnectionEpoch { get; }
    Task<ChannelPairingSnapshot> ListAsync(long epoch);
    Task ResolveAsync(ChannelPairingRequest request, bool approve, long epoch);
}

/// <summary>One observed pending list, bound to its authenticated Gateway and UI lifetime.</summary>
public sealed class ChannelPairingSession
{
    private readonly IChannelPairingGateway _gateway;
    private readonly Func<bool> _isCurrent;
    private readonly long _epoch;
    public ChannelPairingSnapshot? Snapshot { get; private set; }
    public bool Busy { get; private set; }
    public bool RequiresRefresh { get; private set; } = true;

    public ChannelPairingSession(IChannelPairingGateway gateway, Func<bool> isCurrent)
    {
        _gateway = gateway;
        _isCurrent = isCurrent;
        _epoch = gateway.ConnectionEpoch ?? throw new InvalidOperationException("Gateway is disconnected.");
    }

    public bool IsCurrent => _isCurrent() && _gateway.ConnectionEpoch == _epoch;

    private void RequireCurrent()
    {
        if (!IsCurrent) throw new InvalidOperationException("The Gateway context changed.");
    }

    public async Task RefreshAsync()
    {
        RequireCurrent();
        if (Busy) throw new InvalidOperationException("A channel pairing operation is already in progress.");
        Busy = true;
        RequiresRefresh = true;
        try
        {
            var snapshot = await _gateway.ListAsync(_epoch);
            RequireCurrent();
            Snapshot = snapshot;
            RequiresRefresh = false;
        }
        finally { Busy = false; }
    }

    public bool CanResolve(ChannelPairingRequest request) =>
        IsCurrent && !Busy && !RequiresRefresh &&
        Snapshot?.Requests.Any(row => ReferenceEquals(row, request)) == true;

    public async Task ResolveAsync(ChannelPairingRequest request, bool approve)
    {
        RequireCurrent();
        if (!CanResolve(request)) throw new InvalidOperationException("Refresh pending requests before continuing.");
        Busy = true;
        // After dispatch, timeout/cancellation/disconnect is not proof of rollback.
        RequiresRefresh = true;
        try
        {
            await _gateway.ResolveAsync(request, approve, _epoch);
            RequireCurrent();
            Snapshot = Snapshot! with
            {
                Requests = Array.AsReadOnly(Snapshot.Requests.Where(row => !ReferenceEquals(row, request)).ToArray())
            };
        }
        finally { Busy = false; }
    }
}

internal sealed class GatewayChannelPairingClient : IChannelPairingGateway
{
    private readonly Func<long?> _epoch;
    private readonly Func<string, object, long, Task<JsonElement>> _request;
    public GatewayChannelPairingClient(Func<long?> epoch, Func<string, object, long, Task<JsonElement>> request)
    {
        _epoch = epoch;
        _request = request;
    }

    public long? ConnectionEpoch => _epoch();

    public async Task<ChannelPairingSnapshot> ListAsync(long epoch)
    {
        var payload = await _request("channels.pairing.list", new { }, epoch).ConfigureAwait(false);
        RequireEpoch(epoch);
        return ParseSnapshot(payload);
    }

    public async Task ResolveAsync(ChannelPairingRequest request, bool approve, long epoch)
    {
        var method = approve ? "channels.pairing.approve" : "channels.pairing.dismiss";
        var payload = await _request(method, new
        {
            channel = request.Channel, accountId = request.AccountId, requestId = request.RequestId
        }, epoch).ConfigureAwait(false);
        RequireEpoch(epoch);
        if (Text(payload, "requestId") != request.RequestId || Text(payload, "senderId") != request.SenderId)
            throw InvalidPayload();
        if (approve && (Text(payload, "notification") != "not-requested" ||
                        Text(payload, "commandOwnerBootstrap") != "not-requested"))
            throw InvalidPayload();
    }

    private void RequireEpoch(long epoch)
    {
        if (ConnectionEpoch != epoch) throw new InvalidOperationException("The Gateway context changed.");
    }

    internal static ChannelPairingSnapshot ParseSnapshot(JsonElement root)
    {
        foreach (var account in Array(root, "accounts")) RequireBoolean(account, "notifySupported");
        foreach (var request in Array(root, "requests"))
        {
            RequireBoolean(request, "notifySupported");
            _ = Text(request, "lastSeenAt");
        }
        var accounts = Array(root, "accounts").Select(row => new ChannelPairingAccount(
            Text(row, "channel"), Text(row, "channelLabel"), Text(row, "accountId"), OptionalText(row, "accountLabel"))).ToArray();
        var requests = Array(root, "requests").Select(row => new ChannelPairingRequest(
            Text(row, "requestId"), Text(row, "channel"), Text(row, "channelLabel"), Text(row, "accountId"),
            OptionalText(row, "accountLabel"), Text(row, "senderId"), Text(row, "senderLabel"),
            Text(row, "createdAt"), Text(row, "expiresAt"))).ToArray();
        if (!root.TryGetProperty("commandOwnerConfigured", out var owner) ||
            owner.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Object ||
            !limits.TryGetProperty("pendingPerAccount", out var pending) || pending.ValueKind != JsonValueKind.Number || !pending.TryGetInt64(out var pendingCount) || pendingCount < 0 ||
            !limits.TryGetProperty("ttlMs", out var ttl) || ttl.ValueKind != JsonValueKind.Number || !ttl.TryGetInt64(out var ttlMs) || ttlMs < 0 ||
            accounts.Select(row => (row.Channel, row.AccountId)).Distinct().Count() != accounts.Length ||
            requests.Select(row => (row.Channel, row.AccountId, row.RequestId)).Distinct().Count() != requests.Length ||
            requests.Any(row => !accounts.Any(account => account.Channel == row.Channel && account.AccountId == row.AccountId)))
            throw InvalidPayload();
        return new(System.Array.AsReadOnly(accounts), System.Array.AsReadOnly(requests));
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : throw InvalidPayload();
    private static string Text(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw InvalidPayload();
    private static string? OptionalText(JsonElement root, string key) =>
        root.TryGetProperty(key, out _) ? Text(root, key) : null;
    private static void RequireBoolean(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidPayload();
    }
    private static InvalidDataException InvalidPayload() => new("Invalid channel pairing response.");
}
