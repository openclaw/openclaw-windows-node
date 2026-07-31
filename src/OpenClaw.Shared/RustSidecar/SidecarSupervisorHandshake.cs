using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.RustSidecar;

internal sealed class SidecarSupervisorHandshake
{
    private readonly AuthenticatedSidecarChannel _channel;
    private readonly SidecarProtocolOffer _localOffer;
    private bool _started;

    internal SidecarSupervisorHandshake(
        AuthenticatedSidecarChannel channel,
        SidecarProtocolOffer localOffer)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        try
        {
            if (channel.LocalRole != SidecarPeerRole.Supervisor)
                throw new SidecarProtocolException("Sidecar supervisor handshake requires a supervisor channel.");
            _localOffer = ValidateOffer(localOffer, SidecarPeerRole.Supervisor);
            if (_localOffer.ProtocolMajor != AuthenticatedSidecarChannel.ProtocolMajor ||
                _localOffer.ProtocolMinor > AuthenticatedSidecarChannel.ProtocolMinor)
            {
                throw new SidecarProtocolException("Unsupported local sidecar protocol version.");
            }
        }
        catch
        {
            channel.Retire();
            throw;
        }
    }

    internal bool IsAuthenticated { get; private set; }
    internal SidecarProtocolSelection? Selection { get; private set; }

    internal byte[] Start()
    {
        if (_started || IsAuthenticated)
            return Fail<byte[]>("Sidecar handshake has already started.");
        _started = true;
        try
        {
            return _channel.Seal(SidecarJson.Serialize(new JsonObject
            {
                ["type"] = "offer",
                ["offer"] = OfferJson(_localOffer)
            }));
        }
        catch
        {
            _channel.Retire();
            throw;
        }
    }

    internal void Accept(ReadOnlySpan<byte> frame)
    {
        if (!_started || IsAuthenticated)
        {
            Fail<object>("Unexpected sidecar handshake message.");
            return;
        }

        try
        {
            var message = SidecarJson.Parse(_channel.Open(frame));
            if (SidecarJson.RequiredString(message, "type") != "accept")
                throw new SidecarProtocolException("Runtime did not return a sidecar acceptance.");
            var remote = ParseOffer(SidecarJson.RequiredObject(message, "offer"));
            ValidateOffer(remote, SidecarPeerRole.Runtime);
            var claimed = ParseSelection(SidecarJson.RequiredObject(message, "selection"));
            var negotiated = Negotiate(_localOffer, remote);
            if (claimed != negotiated)
                throw new SidecarProtocolException("Runtime sidecar selection does not match local negotiation.");

            _channel.LowerFrameLimit(negotiated.Limits.MaxFrameBytes);
            Selection = negotiated;
            IsAuthenticated = true;
        }
        catch
        {
            _channel.Retire();
            throw;
        }
    }

    private T Fail<T>(string message)
    {
        _channel.Retire();
        throw new SidecarProtocolException(message);
    }

    private static SidecarProtocolSelection Negotiate(
        SidecarProtocolOffer local,
        SidecarProtocolOffer remote)
    {
        if (local.ProtocolMajor != AuthenticatedSidecarChannel.ProtocolMajor ||
            remote.ProtocolMajor != AuthenticatedSidecarChannel.ProtocolMajor)
        {
            throw new SidecarProtocolException("Unsupported sidecar protocol major version.");
        }
        if (local.ProtocolMinor > AuthenticatedSidecarChannel.ProtocolMinor)
            throw new SidecarProtocolException("Unsupported local sidecar protocol minor version.");

        return new SidecarProtocolSelection(
            AuthenticatedSidecarChannel.ProtocolMajor,
            Math.Min(local.ProtocolMinor, remote.ProtocolMinor),
            local.FeatureBits & remote.FeatureBits,
            new SidecarLimits(
                Math.Min(local.Limits.MaxFrameBytes, remote.Limits.MaxFrameBytes),
                Math.Min(local.Limits.MaxInFlight, remote.Limits.MaxInFlight),
                Math.Min(local.Limits.BootstrapTimeoutMs, remote.Limits.BootstrapTimeoutMs)));
    }

    private static SidecarProtocolOffer ValidateOffer(
        SidecarProtocolOffer offer,
        SidecarPeerRole expectedRole)
    {
        if (offer.Peer.Role != expectedRole ||
            string.IsNullOrWhiteSpace(offer.Peer.Name) ||
            string.IsNullOrWhiteSpace(offer.Peer.Version) ||
            string.IsNullOrWhiteSpace(offer.Peer.ArtifactIdentity) ||
            offer.Limits.MaxFrameBytes < 65 ||
            offer.Limits.MaxInFlight == 0 ||
            offer.Limits.BootstrapTimeoutMs == 0)
        {
            throw new SidecarProtocolException("Invalid sidecar protocol offer.");
        }
        return offer;
    }

    private static JsonObject OfferJson(SidecarProtocolOffer offer) => new()
    {
        ["protocolMajor"] = offer.ProtocolMajor,
        ["protocolMinor"] = offer.ProtocolMinor,
        ["peer"] = new JsonObject
        {
            ["role"] = offer.Peer.Role == SidecarPeerRole.Supervisor ? "supervisor" : "runtime",
            ["name"] = offer.Peer.Name,
            ["version"] = offer.Peer.Version,
            ["artifactIdentity"] = offer.Peer.ArtifactIdentity
        },
        ["featureBits"] = offer.FeatureBits,
        ["limits"] = LimitsJson(offer.Limits)
    };

    private static JsonObject LimitsJson(SidecarLimits limits) => new()
    {
        ["maxFrameBytes"] = limits.MaxFrameBytes,
        ["maxInFlight"] = limits.MaxInFlight,
        ["bootstrapTimeoutMs"] = limits.BootstrapTimeoutMs
    };

    private static SidecarProtocolOffer ParseOffer(JsonElement json)
    {
        var peer = SidecarJson.RequiredObject(json, "peer");
        var role = SidecarJson.RequiredString(peer, "role") switch
        {
            "supervisor" => SidecarPeerRole.Supervisor,
            "runtime" => SidecarPeerRole.Runtime,
            _ => throw new SidecarProtocolException("Unknown sidecar peer role.")
        };
        return new SidecarProtocolOffer(
            checked((ushort)SidecarJson.RequiredUInt64(json, "protocolMajor")),
            checked((ushort)SidecarJson.RequiredUInt64(json, "protocolMinor")),
            new SidecarPeerIdentity(
                role,
                SidecarJson.RequiredString(peer, "name"),
                SidecarJson.RequiredString(peer, "version"),
                SidecarJson.RequiredString(peer, "artifactIdentity")),
            SidecarJson.RequiredUInt64(json, "featureBits"),
            ParseLimits(SidecarJson.RequiredObject(json, "limits")));
    }

    private static SidecarProtocolSelection ParseSelection(JsonElement json) => new(
        checked((ushort)SidecarJson.RequiredUInt64(json, "protocolMajor")),
        checked((ushort)SidecarJson.RequiredUInt64(json, "protocolMinor")),
        SidecarJson.RequiredUInt64(json, "featureBits"),
        ParseLimits(SidecarJson.RequiredObject(json, "limits")));

    private static SidecarLimits ParseLimits(JsonElement json) => new(
        checked((uint)SidecarJson.RequiredUInt64(json, "maxFrameBytes")),
        checked((ushort)SidecarJson.RequiredUInt64(json, "maxInFlight")),
        checked((uint)SidecarJson.RequiredUInt64(json, "bootstrapTimeoutMs")));
}
