using System.Collections.Generic;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Parses the JSON payload of a <c>channels.status</c> response into a
/// <see cref="ChannelsStatusSnapshot"/>. Tolerant of missing optional fields —
/// older gateways or plugin-only responses still parse.
/// </summary>
public static class ChannelsStatusParser
{
    public static ChannelsStatusSnapshot Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new ChannelsStatusSnapshot();

        var snap = new ChannelsStatusSnapshot
        {
            Ts = GetDouble(payload, "ts") ?? 0,
            ChannelOrder = ParseStringArray(payload, "channelOrder"),
            ChannelLabels = ParseStringMap(payload, "channelLabels"),
            ChannelDetailLabels = ParseOptionalStringMap(payload, "channelDetailLabels"),
            ChannelSystemImages = ParseOptionalStringMap(payload, "channelSystemImages"),
            ChannelMeta = ParseChannelMeta(payload),
            Channels = ParseChannels(payload),
            ChannelAccounts = ParseChannelAccounts(payload),
            ChannelDefaultAccountId = ParseStringMap(payload, "channelDefaultAccountId"),
        };
        return snap;
    }

    // ─── Typed extractors for individual channels ────────────────────────────

    public static WhatsAppChannelStatus? ExtractWhatsApp(JsonElement channel) =>
        channel.ValueKind == JsonValueKind.Object
            ? new WhatsAppChannelStatus
            {
                Configured = GetBool(channel, "configured") ?? false,
                Running = GetBool(channel, "running") ?? false,
                Connected = GetBool(channel, "connected") ?? false,
                Linked = GetBool(channel, "linked") ?? false,
                LastError = GetString(channel, "lastError"),
                Self = channel.TryGetProperty("self", out var s) && s.ValueKind == JsonValueKind.Object
                    ? new WhatsAppSelf { E164 = GetString(s, "e164"), Jid = GetString(s, "jid") }
                    : null,
                AuthAgeMs = GetDouble(channel, "authAgeMs"),
                LastConnectedAt = GetDouble(channel, "lastConnectedAt"),
                LastMessageAt = GetDouble(channel, "lastMessageAt"),
                ReconnectAttempts = (int)(GetDouble(channel, "reconnectAttempts") ?? 0),
                LastDisconnect = channel.TryGetProperty("lastDisconnect", out var d) && d.ValueKind == JsonValueKind.Object
                    ? new WhatsAppLastDisconnect
                    {
                        At = GetDouble(d, "at"),
                        Status = (int?)GetDouble(d, "status"),
                        Error = GetString(d, "error"),
                        LoggedOut = GetBool(d, "loggedOut"),
                    }
                    : null,
            }
            : null;

    public static GenericChannelStatus? ExtractGeneric(JsonElement channel) =>
        channel.ValueKind == JsonValueKind.Object
            ? new GenericChannelStatus
            {
                Configured = GetBool(channel, "configured") ?? false,
                Running = GetBool(channel, "running") ?? false,
                Connected = GetBool(channel, "connected") ?? false,
                Linked = GetBool(channel, "linked") ?? false,
                LastError = GetString(channel, "lastError") ?? GetString(channel, "error"),
                Probe = ExtractProbe(channel),
                LastProbeAt = GetDouble(channel, "lastProbeAt"),
                Mode = GetString(channel, "mode"),
                LastStartAt = GetDouble(channel, "lastStartAt"),
                LastEventAt = GetDouble(channel, "lastEventAt"),
                LastTransportActivityAt = GetDouble(channel, "lastTransportActivityAt"),
                ReconnectAttempts = (int)(GetDouble(channel, "reconnectAttempts") ?? 0),
                RestartPending = GetBool(channel, "restartPending") ?? false,
            }
            : null;

    public static ChannelProbe? ExtractProbe(JsonElement channel)
    {
        if (!channel.TryGetProperty("probe", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return new ChannelProbe
        {
            Ok = GetBool(p, "ok"),
            Status = (int?)GetDouble(p, "status"),
            ElapsedMs = GetDouble(p, "elapsedMs"),
            Version = GetString(p, "version"),
            Error = GetString(p, "error"),
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ParseStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list;
    }

    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement parent, string name)
    {
        var map = new Dictionary<string, string>();
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in obj.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } v)
                map[prop.Name] = v;
        return map;
    }

    private static IReadOnlyDictionary<string, string>? ParseOptionalStringMap(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, string>();
        foreach (var prop in obj.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } v)
                map[prop.Name] = v;
        return map;
    }

    private static IReadOnlyList<ChannelUiMetaEntry>? ParseChannelMeta(JsonElement parent)
    {
        if (!parent.TryGetProperty("channelMeta", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<ChannelUiMetaEntry>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new ChannelUiMetaEntry
            {
                Id = GetString(item, "id") ?? "",
                Label = GetString(item, "label") ?? "",
                DetailLabel = GetString(item, "detailLabel") ?? GetString(item, "label") ?? "",
                SystemImage = GetString(item, "systemImage"),
            });
        }
        return list;
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseChannels(JsonElement parent)
    {
        var map = new Dictionary<string, JsonElement>();
        if (!parent.TryGetProperty("channels", out var obj) || obj.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = prop.Value.Clone();
        return map;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ChannelAccountSnapshot>> ParseChannelAccounts(JsonElement parent)
    {
        var map = new Dictionary<string, IReadOnlyList<ChannelAccountSnapshot>>();
        if (!parent.TryGetProperty("channelAccounts", out var obj) || obj.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<ChannelAccountSnapshot>();
            foreach (var acc in prop.Value.EnumerateArray())
            {
                if (acc.ValueKind != JsonValueKind.Object) continue;
                list.Add(new ChannelAccountSnapshot
                {
                    Id = GetString(acc, "id") ?? "",
                    Configured = GetBool(acc, "configured"),
                    Running = GetBool(acc, "running"),
                    Connected = GetBool(acc, "connected"),
                    LastInboundAt = GetDouble(acc, "lastInboundAt"),
                    LastOutboundAt = GetDouble(acc, "lastOutboundAt"),
                });
            }
            map[prop.Name] = list;
        }
        return map;
    }

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return null;
    }
}
