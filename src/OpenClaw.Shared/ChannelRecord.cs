using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Aggregate view of one channel — combines the gateway-provided status snapshot
/// with capability flags and metadata. The page renders <see cref="ChannelRecord"/>s,
/// not raw snapshots.
/// </summary>
public sealed class ChannelRecord
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string DetailLabel { get; init; } = "";
    public string? SystemImage { get; init; }

    /// <summary>Raw status JSON from <c>channels.status</c>. Use <see cref="ChannelsStatusParser"/> helpers to extract.</summary>
    public JsonElement RawStatus { get; init; }

    public IReadOnlyList<ChannelAccountSnapshot> Accounts { get; init; } = [];
    public string? DefaultAccountId { get; init; }

    /// <summary>True if this channel has any active configuration/state. Mirrors Mac's <c>channelEnabled</c>.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>
    /// True when the channel is actively running on the gateway right now
    /// (status.running, or any account.Running/Connected). Distinct from
    /// <see cref="IsConfigured"/> which is the broader "has credentials or
    /// any active state" flag. The page uses this to decide whether to offer
    /// the "Start channel" action.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>Capability flags (per-channel — driven by id).</summary>
    public ChannelCapabilities Capabilities { get; init; }

    /// <summary>True if Windows cannot host this channel even when configured (e.g. iMessage).</summary>
    public bool IsUnavailableOnWindows { get; init; }

    /// <summary>Sort order (gateway-provided); lower wins.</summary>
    public int SortOrder { get; init; }

    /// <summary>When we last received a status update for this channel.</summary>
    public DateTime LastUpdatedAt { get; init; }

    /// <summary>Last probe completion (epoch ms → DateTime), when reported.</summary>
    public DateTime? LastProbeAt { get; init; }
}

/// <summary>Per-channel capability flags. Inferred from id; the page uses these to gate action buttons.</summary>
[Flags]
public enum ChannelCapabilities
{
    None = 0,
    /// <summary>Legacy: kept on every record but unused by the page header (a single page-level Refresh-all button covers this).</summary>
    CanRefresh = 1 << 0,
    CanLogout = 1 << 1,
    CanShowQr = 1 << 2,
    CanRelink = 1 << 3,
    /// <summary>Configured-but-not-running channel: offer a <c>channels.start</c> action.</summary>
    CanStart = 1 << 4,
    /// <summary>
    /// Configured-and-running non-QR channel: offer a <c>channels.stop</c>
    /// action (pause without clearing credentials). QR-link channels
    /// (WhatsApp/Signal) instead expose <see cref="CanLogout"/> in the
    /// header — for them "logout" already means "unlink this device",
    /// which is the analogous lightweight pause action.
    /// </summary>
    CanStop = 1 << 5,
}

/// <summary>
/// Merges <see cref="ChannelsStatusSnapshot"/> + a built-in capability/availability catalog
/// into a stable list of <see cref="ChannelRecord"/>s suitable for binding to a list view.
/// </summary>
public static class ChannelsAggregator
{
    /// <summary>Built-in fallback ordering when the gateway returns an empty <c>channelOrder</c>.</summary>
    public static readonly IReadOnlyList<string> BuiltInChannelOrder =
        new[] { "whatsapp", "telegram", "discord", "googlechat", "slack", "signal", "imessage", "nostr" };

    /// <summary>
    /// Pretty labels for the built-in channels. Used when the gateway
    /// hasn't reported a label (typical for "preview" channels — those the
    /// user hasn't configured yet and the gateway doesn't have a plugin
    /// for). Without this we'd render raw lowercase ids ("discord",
    /// "googlechat") which look broken.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> BuiltInChannelLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["whatsapp"]   = "WhatsApp",
            ["telegram"]   = "Telegram",
            ["discord"]    = "Discord",
            ["googlechat"] = "Google Chat",
            ["slack"]      = "Slack",
            ["signal"]     = "Signal",
            ["imessage"]   = "iMessage",
            ["nostr"]      = "Nostr",
        };

    /// <summary>Channels that require a phone-app QR scan.</summary>
    private static readonly HashSet<string> QrLinkChannels =
        new(StringComparer.OrdinalIgnoreCase) { "whatsapp", "signal" };

    /// <summary>Channels that support a per-channel logout/unlink action.
    /// For QR channels (WhatsApp/Signal) Logout = unlink the device (re-scan
    /// to reconnect). For non-QR channels (Telegram) Logout = clear stored
    /// credentials (re-enter them to reconnect).</summary>
    private static readonly HashSet<string> LogoutChannels =
        new(StringComparer.OrdinalIgnoreCase) { "whatsapp", "telegram", "signal" };

    /// <summary>Channels that cannot be hosted on Windows.</summary>
    private static readonly HashSet<string> WindowsUnsupportedChannels =
        new(StringComparer.OrdinalIgnoreCase) { "imessage" };

    /// <summary>
    /// Aggregate a snapshot into <see cref="ChannelRecord"/>s.
    /// Returns records ordered Configured-first, then by gateway/fallback sort order.
    /// </summary>
    /// <param name="snapshot">Latest <c>channels.status</c> response, or null when none has been fetched.</param>
    /// <param name="now">Current time, used to stamp each record's <see cref="ChannelRecord.LastUpdatedAt"/>.</param>
    /// <param name="useBuiltInFallback">
    /// When true, an empty snapshot falls back to <see cref="BuiltInChannelOrder"/>
    /// (useful as a preview when no gateway is connected). When false, an empty
    /// snapshot produces an empty list so the page doesn't fake-list channels
    /// the gateway didn't actually report. Callers should pass true only when
    /// the user isn't connected yet; once connected, the page should show
    /// exactly what the gateway exposes.
    /// </param>
    public static IReadOnlyList<ChannelRecord> Aggregate(
        ChannelsStatusSnapshot? snapshot,
        DateTime now,
        bool useBuiltInFallback = true)
    {
        snapshot ??= new ChannelsStatusSnapshot();

        // Build the channel id list as the *union* of every source the gateway
        // might use to expose channels. Older gateways and plugin-only setups
        // sometimes omit channelOrder while still populating channels/meta —
        // iterating channelOrder alone would silently drop them.
        var order = BuildOrderedIds(snapshot, useBuiltInFallback);
        var records = new List<ChannelRecord>(order.Count);

        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            snapshot.Channels.TryGetValue(id, out var raw);
            var accounts = snapshot.ChannelAccounts.TryGetValue(id, out var accs) ? accs : [];
            snapshot.ChannelDefaultAccountId.TryGetValue(id, out var defaultAccountId);

            var configured = IsChannelConfigured(raw, accounts);
            var running = IsChannelRunning(raw, accounts);

            // Capability gating:
            //   CanRefresh — kept for backcompat, but the page no longer renders
            //                a per-channel Refresh button; one page-level
            //                Refresh-all covers it.
            //   CanShowQr  — QR channels (WhatsApp/Signal): available even when
            //                unconfigured because the QR scan IS how you
            //                configure them. Show-QR is the bootstrap path.
            //   CanRelink  — QR channels, only once already configured: relink
            //                rotates the device link; meaningless before there's
            //                a device to relink.
            //   CanLogout  — only on channels that have a session to end, AND
            //                only when actually configured. Hardcoding logout to
            //                the channel id alone shows a Logout button on
            //                "not configured" rows, which confuses users.
            //   CanStart   — channel is configured but not running. Offers a
            //                channels.start action. Distinct from save-and-start
            //                in the inline form: this is the recovery affordance
            //                for an already-configured channel that didn't come
            //                up on its own.
            var caps = ChannelCapabilities.CanRefresh;
            if (QrLinkChannels.Contains(id))
            {
                caps |= ChannelCapabilities.CanShowQr;
                if (configured) caps |= ChannelCapabilities.CanRelink;
            }
            if (configured && LogoutChannels.Contains(id))
                caps |= ChannelCapabilities.CanLogout;
            if (configured && !running)
                caps |= ChannelCapabilities.CanStart;
            // CanStop: lightweight pause for non-QR running channels.
            // For QR channels (WhatsApp/Signal) we don't set CanStop —
            // "Logout" is the analogous lightweight action there (unlink
            // the device; can scan a fresh QR to reconnect).
            if (configured && running && !QrLinkChannels.Contains(id))
                caps |= ChannelCapabilities.CanStop;

            // Label / DetailLabel fall back to BuiltInChannelLabels when
            // the gateway didn't supply a nice name (typical for preview
            // channels — those we surface from BuiltInChannelOrder for
            // discoverability but that the gateway hasn't reported).
            var gatewayLabel = snapshot.ResolveLabel(id);
            var label = string.Equals(gatewayLabel, id, StringComparison.Ordinal)
                && BuiltInChannelLabels.TryGetValue(id, out var nice)
                ? nice
                : gatewayLabel;
            var gatewayDetailLabel = snapshot.ResolveDetailLabel(id);
            var detailLabel = string.Equals(gatewayDetailLabel, id, StringComparison.Ordinal)
                && BuiltInChannelLabels.TryGetValue(id, out var niceDetail)
                ? niceDetail
                : gatewayDetailLabel;

            records.Add(new ChannelRecord
            {
                Id = id,
                Label = label,
                DetailLabel = detailLabel,
                SystemImage = snapshot.ResolveSystemImage(id),
                RawStatus = raw,
                Accounts = accounts,
                DefaultAccountId = defaultAccountId,
                IsConfigured = configured,
                IsRunning = running,
                Capabilities = caps,
                IsUnavailableOnWindows = WindowsUnsupportedChannels.Contains(id),
                SortOrder = i,
                LastUpdatedAt = now,
                LastProbeAt = ExtractLastProbeAt(raw),
            });
        }

        return records
            .OrderByDescending(r => r.IsConfigured)
            .ThenBy(r => r.SortOrder)
            .ToList();
    }

    /// <summary>
    /// Build the ordered channel id list by unioning every source in the snapshot:
    /// <c>channelOrder</c> (canonical, if provided) → channel ids in <c>channels</c>
    /// (covers older gateways missing channelOrder) → ids in <c>channelMeta</c> /
    /// <c>channelAccounts</c> → optionally <see cref="BuiltInChannelOrder"/>.
    /// </summary>
    /// <param name="useBuiltInFallback">
    /// When true, the built-in catalog is **always** unioned in so the
    /// AVAILABLE section gives the user discoverable options to add — not
    /// just whatever the gateway has already configured. Channels reported
    /// by the gateway dedupe correctly via the OrdinalIgnoreCase HashSet
    /// and keep their gateway-provided position; pure built-in extras
    /// append at the end in BuiltInChannelOrder order.
    /// When false, only what the gateway reported is returned — honest
    /// mode for callers that don't want to surface "preview" channels the
    /// gateway can't host.
    /// </param>
    internal static IReadOnlyList<string> BuildOrderedIds(
        ChannelsStatusSnapshot snapshot,
        bool useBuiltInFallback = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Append(IEnumerable<string> source)
        {
            foreach (var id in source)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (seen.Add(id)) order.Add(id);
            }
        }

        Append(snapshot.ChannelOrder);
        if (snapshot.ChannelMeta is { } meta) Append(meta.Select(m => m.Id));
        Append(snapshot.Channels.Keys);
        Append(snapshot.ChannelAccounts.Keys);

        // Always union the built-in catalog (when requested) so the
        // AVAILABLE section stays populated even when the gateway is
        // only reporting the user's currently-configured channels.
        // Discoverability matters more than the small risk of surfacing
        // a channel whose plugin isn't installed — the Save flow already
        // detects "unknown channel" responses and points the user at
        // openclaw plugins install <pkg>.
        if (useBuiltInFallback) Append(BuiltInChannelOrder);
        return order;
    }

    /// <summary>Mac's <c>channelEnabled</c> rule: configured || running || connected || any-account-active.</summary>
    public static bool IsChannelConfigured(JsonElement raw, IReadOnlyList<ChannelAccountSnapshot>? accounts)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (TryGetBool(raw, "configured")) return true;
            if (TryGetBool(raw, "running")) return true;
            if (TryGetBool(raw, "connected")) return true;
        }
        if (accounts != null)
        {
            foreach (var acc in accounts)
                if (acc.Configured == true || acc.Running == true || acc.Connected == true)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Narrower than <see cref="IsChannelConfigured"/>: true only when the
    /// channel is actively running (status.running, or any account is
    /// running/connected). A channel that's only <c>configured: true</c> but
    /// not yet running returns false, so the page can offer "Start channel".
    /// </summary>
    public static bool IsChannelRunning(JsonElement raw, IReadOnlyList<ChannelAccountSnapshot>? accounts)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (TryGetBool(raw, "running")) return true;
            if (TryGetBool(raw, "connected")) return true;
        }
        if (accounts != null)
        {
            foreach (var acc in accounts)
                if (acc.Running == true || acc.Connected == true)
                    return true;
        }
        return false;
    }

    private static bool TryGetBool(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime? ExtractLastProbeAt(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("lastProbeAt", out var ms) || ms.ValueKind != JsonValueKind.Number) return null;
        if (!ms.TryGetDouble(out var d)) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)d).UtcDateTime;
    }
}
