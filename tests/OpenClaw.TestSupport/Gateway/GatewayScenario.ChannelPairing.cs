using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    private readonly HashSet<string>? _pendingChannelSenders;
    private static readonly string[] ChannelPairingMethods =
        ["channels.pairing.list", "channels.pairing.approve", "channels.pairing.dismiss"];

    /// <summary>Opt-in DM-access records only. Never contacts a provider or grants real access.</summary>
    public static GatewayScenario CreateChannelPairing()
    {
        var browse = CreateBrowse();
        return new(browse._sessions, browse._reads, channelPairing: true);
    }

    private object RespondChannelPairing(string method, JsonElement parameters)
    {
        var pending = _pendingChannelSenders!;
        lock (pending)
        {
            if (method == "channels.pairing.list")
            {
                ValidateProperties(parameters);
                return new
                {
                    accounts = new[]
                    {
                        new { channel = "telegram", channelLabel = "Telegram", accountId = "work", accountLabel = "Fixture work", notifySupported = false }
                    },
                    requests = pending.Order(StringComparer.Ordinal).Select(id => new
                    {
                        requestId = id, channel = "telegram", channelLabel = "Telegram",
                        accountId = "work", accountLabel = "Fixture work", senderId = $"sender-{id}",
                        senderLabel = $"Synthetic sender {id}", createdAt = "2026-10-23T12:00:00Z",
                        lastSeenAt = "2026-10-23T12:01:00Z", expiresAt = "2099-10-23T13:00:00Z", notifySupported = false
                    }).ToArray(),
                    commandOwnerConfigured = true, limits = new { pendingPerAccount = 3, ttlMs = 3600000 }
                };
            }
            ValidateProperties(parameters, "channel", "accountId", "requestId");
            var id = RequiredString(parameters, "requestId");
            if (RequiredString(parameters, "channel") != "telegram" ||
                RequiredString(parameters, "accountId") != "work" || !pending.Remove(id))
                throw new FixtureRequestException("INVALID_PARAMS", "Unknown synthetic pending DM request.");
            return method == "channels.pairing.approve"
                ? new { requestId = id, senderId = $"sender-{id}", notification = "not-requested", commandOwnerBootstrap = "not-requested" }
                : new { requestId = id, senderId = $"sender-{id}" };
        }
    }
}
