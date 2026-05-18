using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Strongly-typed snapshot returned by <c>channels.status</c>.
/// Mirrors the shape used by the macOS app (<c>ChannelsStatusSnapshot</c>) and the
/// web UI (<c>ChannelsStatusSnapshot</c> in <c>ui/src/ui/types.ts</c>).
///
/// The gateway is the source of truth for channel metadata: <see cref="ChannelOrder"/>,
/// <see cref="ChannelLabels"/>, <see cref="ChannelDetailLabels"/>,
/// <see cref="ChannelSystemImages"/>, and <see cref="ChannelMeta"/> all come from the
/// gateway's plugin registry. The tray UI renders whatever is reported; it does not
/// maintain its own list.
/// </summary>
public sealed class ChannelsStatusSnapshot
{
    /// <summary>Server timestamp (epoch seconds, fractional).</summary>
    public double Ts { get; init; }

    /// <summary>Canonical ordered list of channel ids the gateway is aware of.</summary>
    public IReadOnlyList<string> ChannelOrder { get; init; } = [];

    /// <summary>Sidebar label per id (e.g. <c>"whatsapp" → "WhatsApp"</c>).</summary>
    public IReadOnlyDictionary<string, string> ChannelLabels { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Detail-pane title per id (falls back to <see cref="ChannelLabels"/>).</summary>
    public IReadOnlyDictionary<string, string>? ChannelDetailLabels { get; init; }

    /// <summary>SF Symbol name per id. Windows maps these to Fluent glyphs.</summary>
    public IReadOnlyDictionary<string, string>? ChannelSystemImages { get; init; }

    /// <summary>Richer per-channel UI metadata. When present, takes precedence over the *Labels maps.</summary>
    public IReadOnlyList<ChannelUiMetaEntry>? ChannelMeta { get; init; }

    /// <summary>
    /// Per-channel raw status JSON. Keys are channel ids; values are arbitrary channel-specific
    /// status documents (typed records like <see cref="WhatsAppChannelStatus"/> available for
    /// built-ins; plugin channels use the generic shape).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Channels { get; init; }
        = new Dictionary<string, JsonElement>();

    /// <summary>Per-channel account list. Channels with multi-account support (e.g. WhatsApp Business).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChannelAccountSnapshot>> ChannelAccounts { get; init; }
        = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>();

    /// <summary>Per-channel default account id (when multi-account).</summary>
    public IReadOnlyDictionary<string, string> ChannelDefaultAccountId { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Resolve the sidebar label for a channel id, falling back through the precedence list Mac uses.</summary>
    public string ResolveLabel(string id)
    {
        if (ChannelMeta is { } meta)
        {
            var entry = meta.FirstOrDefault(e => e.Id == id);
            if (entry != null && !string.IsNullOrEmpty(entry.Label)) return entry.Label;
        }
        if (ChannelLabels.TryGetValue(id, out var label) && !string.IsNullOrEmpty(label)) return label;
        return id;
    }

    /// <summary>Resolve the detail-pane title for a channel id.</summary>
    public string ResolveDetailLabel(string id)
    {
        if (ChannelMeta is { } meta)
        {
            var entry = meta.FirstOrDefault(e => e.Id == id);
            if (entry != null && !string.IsNullOrEmpty(entry.DetailLabel)) return entry.DetailLabel;
        }
        if (ChannelDetailLabels is { } details && details.TryGetValue(id, out var detail) && !string.IsNullOrEmpty(detail))
            return detail;
        return ResolveLabel(id);
    }

    /// <summary>Resolve the SF Symbol name for a channel id (callers map to Fluent glyphs).</summary>
    public string? ResolveSystemImage(string id)
    {
        if (ChannelMeta is { } meta)
        {
            var entry = meta.FirstOrDefault(e => e.Id == id);
            if (entry != null && !string.IsNullOrEmpty(entry.SystemImage)) return entry.SystemImage;
        }
        if (ChannelSystemImages is { } sys && sys.TryGetValue(id, out var symbol) && !string.IsNullOrEmpty(symbol))
            return symbol;
        return null;
    }
}

/// <summary>Per-channel UI metadata entry from <c>channelMeta</c>.</summary>
public sealed class ChannelUiMetaEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string DetailLabel { get; init; } = "";
    public string? SystemImage { get; init; }
}

/// <summary>One account belonging to a channel (for channels with multi-account support).</summary>
public sealed class ChannelAccountSnapshot
{
    public string Id { get; init; } = "";
    public bool? Configured { get; init; }
    public bool? Running { get; init; }
    public bool? Connected { get; init; }
    /// <summary>Last inbound message timestamp (epoch ms).</summary>
    public double? LastInboundAt { get; init; }
    /// <summary>Last outbound message timestamp (epoch ms).</summary>
    public double? LastOutboundAt { get; init; }
}

/// <summary>Generic channel status fields surfaced by virtually every channel plugin.</summary>
public sealed class GenericChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public bool Connected { get; init; }
    public bool Linked { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    /// <summary>Last probe completion timestamp (epoch ms).</summary>
    public double? LastProbeAt { get; init; }
    /// <summary>Transport mode, e.g. "polling" / "webhook" — channel plugins set this to declare how they receive events.</summary>
    public string? Mode { get; init; }
    /// <summary>Epoch ms when the channel last booted (entered the running state).</summary>
    public double? LastStartAt { get; init; }
    /// <summary>Epoch ms of the channel's last upstream event (message, presence push, …) — useful as a "freshness" indicator.</summary>
    public double? LastEventAt { get; init; }
    /// <summary>Epoch ms of the last transport-layer activity (HTTP poll round-trip, WS heartbeat). Often equals <see cref="LastEventAt"/> when traffic is flowing.</summary>
    public double? LastTransportActivityAt { get; init; }
    /// <summary>Number of times the plugin has reconnected since it last booted. Non-zero is a soft caution signal.</summary>
    public int ReconnectAttempts { get; init; }
    /// <summary>True when the plugin is in the middle of a graceful restart (channels.start has been queued but hasn't completed).</summary>
    public bool RestartPending { get; init; }
}

/// <summary>Probe sub-record common to most channel statuses.</summary>
public sealed class ChannelProbe
{
    public bool? Ok { get; init; }
    public int? Status { get; init; }
    public double? ElapsedMs { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
}

// ─── Typed status records for built-in channels ─────────────────────────────
// These cover the fields Mac surfaces in its detail-pane "details line"; plugin
// channels (e.g. nostr) use the generic shape above.

public sealed class WhatsAppChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public bool Connected { get; init; }
    public bool Linked { get; init; }
    public string? LastError { get; init; }
    public WhatsAppSelf? Self { get; init; }
    public double? AuthAgeMs { get; init; }
    public double? LastConnectedAt { get; init; }
    public double? LastMessageAt { get; init; }
    public int ReconnectAttempts { get; init; }
    public WhatsAppLastDisconnect? LastDisconnect { get; init; }
}

public sealed class WhatsAppSelf
{
    public string? E164 { get; init; }
    public string? Jid { get; init; }
}

public sealed class WhatsAppLastDisconnect
{
    public double? At { get; init; }
    public int? Status { get; init; }
    public string? Error { get; init; }
    public bool? LoggedOut { get; init; }
}

public sealed class TelegramChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    public double? LastProbeAt { get; init; }
    public string? BotUsername { get; init; }
}

public sealed class DiscordChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    public double? LastProbeAt { get; init; }
    public string? WebhookUrl { get; init; }
}

public sealed class GoogleChatChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    public double? LastProbeAt { get; init; }
    public string? WebhookUrl { get; init; }
}

public sealed class SignalChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    public double? LastProbeAt { get; init; }
    public string? BaseUrl { get; init; }
}

public sealed class IMessageChannelStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public string? LastError { get; init; }
    public ChannelProbe? Probe { get; init; }
    public double? LastProbeAt { get; init; }
    public string? CliPath { get; init; }
    public string? DbPath { get; init; }
}

// ─── Web login (QR linking) ──────────────────────────────────────────────────

/// <summary>Result of <c>web.login.start</c> — a QR or status for a channel.</summary>
public sealed class WebLoginStartResult
{
    public string? Message { get; init; }
    public string? QrDataUrl { get; init; }
    public bool Connected { get; init; }

    /// <summary>Gateway-side error message when the call failed (ok=false), or transport exception. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Raw JSON of the gateway response (or stringified exception). Used by the diagnostic disclosure in the UI.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>Result of <c>web.login.wait</c> — long-poll outcome.</summary>
public sealed class WebLoginWaitResult
{
    public string? Message { get; init; }
    public string? QrDataUrl { get; init; }
    public bool Connected { get; init; }

    /// <summary>Gateway-side error message when the call failed (ok=false), or transport exception. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Raw JSON of the gateway response (or stringified exception). Used by the diagnostic disclosure in the UI.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// Result of <c>channels.start</c>. Mirrors the gateway response shape
/// (<c>{ channel, accountId, started }</c>) and adds error/raw fields so the
/// page can surface the gateway's actual error message — including the
/// telltale "unknown channel: foo" which means the channel plugin isn't
/// loaded on the gateway host and the operator needs to run
/// <c>openclaw plugins install @openclaw/&lt;id&gt;</c> on that machine.
/// </summary>
public sealed class ChannelStartResult
{
    /// <summary>Channel id the gateway acted on (echoes the request param).</summary>
    public string? Channel { get; init; }

    /// <summary>Account id the gateway started (default if none was requested).</summary>
    public string? AccountId { get; init; }

    /// <summary>True when the gateway reports the channel transitioned to started.</summary>
    public bool Started { get; init; }

    /// <summary>Overall wire-level success — false on transport failure or gateway ok:false.</summary>
    public bool Ok { get; init; }

    /// <summary>Gateway-side error message when <see cref="Ok"/> is false. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Raw JSON of the gateway response (or stringified exception). Used by the page diagnostic disclosure.</summary>
    public string? RawResponse { get; init; }

    /// <summary>
    /// True when the gateway responded with "unknown channel" — a strong signal
    /// the channel plugin isn't loaded on the gateway host. Used to upgrade
    /// the page's hint to "install the plugin on your gateway first".
    /// </summary>
    public bool LooksLikeMissingPlugin =>
        Error != null &&
        Error.Contains("unknown channel", System.StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of <see cref="OpenClawGatewayClient.PatchConfigDetailedAsync"/>.
///
/// Distinguishes "patch dispatched" (the older fire-and-forget bool) from
/// "patch was accepted by the gateway". Lets the inline channel save flow
/// surface the gateway's actual error message — including the wire-format
/// validation errors that fail silently with the legacy
/// <c>config.set { path, value }</c> path on newer gateways.
/// </summary>
public sealed class ConfigPatchResult
{
    /// <summary>True when the gateway accepted the patch (responded ok:true).</summary>
    public bool Ok { get; init; }

    /// <summary>Gateway-side error message when <see cref="Ok"/> is false. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Raw JSON of the gateway response (or stringified exception). For diagnostic disclosure.</summary>
    public string? RawResponse { get; init; }

    /// <summary>
    /// True when the gateway rejected the patch because our baseHash was
    /// stale (someone else changed the config out from under us). Pages
    /// should refresh the cached config and prompt the user to retry.
    ///
    /// The bare word "conflict" is too generic to match on its own (Hanselman
    /// review MEDIUM-3) — a JSON schema error like "property 'conflict_mode'
    /// is invalid" would otherwise trigger a spurious refresh-and-retry loop.
    /// We require "conflict" to co-occur with "hash" or "baseHash" before
    /// treating it as a stale-hash signal.
    /// </summary>
    public bool LooksLikeStaleBaseHash =>
        Error != null &&
        (Error.Contains("baseHash", System.StringComparison.OrdinalIgnoreCase) ||
         Error.Contains("stale", System.StringComparison.OrdinalIgnoreCase) ||
         (Error.Contains("conflict", System.StringComparison.OrdinalIgnoreCase) &&
          Error.Contains("hash", System.StringComparison.OrdinalIgnoreCase)));
}
