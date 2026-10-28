using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Sessions;

/// <summary>Localized labels and compact formatting for Sessions page rows. No WinUI types.</summary>
internal static class SessionsHubPresentation
{
    internal static string T(string key) => LocalizationHelper.GetString("SessionsHub_" + key);
    internal static string F(string key, params object?[] args) => LocalizationHelper.Format("SessionsHub_" + key, args);

    public static IReadOnlyDictionary<string, string> Titles(IReadOnlyList<SessionInfo> rows)
    {
        var titles = SessionTitleFormatter.FormatUnique(rows);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
            map[rows[i].Key] = titles[i];
        return map;
    }

    public static string RelativeTime(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } value || value <= DateTime.UnixEpoch)
            return T("TimeNever");
        var delta = nowUtc - value;
        if (delta.TotalMinutes < 1) return T("TimeJustNow");
        if (delta.TotalHours < 1) return F("TimeMinutes", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return F("TimeHours", (int)delta.TotalHours);
        if (delta.TotalDays < 30) return F("TimeDays", (int)delta.TotalDays);
        return value.ToLocalTime().ToString("d");
    }

    public static string RelativeTime(long epochMs, DateTime nowUtc) =>
        epochMs > 0 ? RelativeTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime, nowUtc) : T("TimeNever");

    public static string AbsoluteTime(DateTime? utc) =>
        utc is { } value && value > DateTime.UnixEpoch ? value.ToLocalTime().ToString("g") : T("TimeNever");

    public enum StatusTone { Working, Attention, Ready }

    public static (string Label, StatusTone Tone) Status(SessionInfo row)
    {
        if (row.Status?.Equals("queued", StringComparison.OrdinalIgnoreCase) == true)
            return (T("StatusQueued"), StatusTone.Attention);
        return SessionRunState.GetDisplayState(row) switch
        {
            SessionDisplayState.Working => (LocalizationHelper.GetString("SessionsPage_Status_Working"), StatusTone.Working),
            SessionDisplayState.NeedsAttention => (LocalizationHelper.GetString("SessionsPage_Status_NeedsAttention"), StatusTone.Attention),
            _ => (LocalizationHelper.GetString("SessionsPage_Status_Ready"), StatusTone.Ready),
        };
    }

    public static string KindLabel(string kind) => kind switch
    {
        "global" => T("KindGlobal"),
        "unknown" => T("KindUnknown"),
        "group" => T("KindGroup"),
        "direct" => T("KindDirect"),
        "channel" => T("KindChannel"),
        "thread" => T("KindThread"),
        "cron" => T("KindCron"),
        "subagent" => T("KindSubagent"),
        "main" => T("KindMain"),
        _ => T("KindOther"),
    };

    public static string GroupByLabel(SessionsGroupBy mode) => mode switch
    {
        SessionsGroupBy.Category => T("GroupByCategory"),
        SessionsGroupBy.Person => T("GroupByPerson"),
        SessionsGroupBy.Channel => T("GroupByChannel"),
        SessionsGroupBy.Kind => T("GroupByKind"),
        SessionsGroupBy.Agent => T("GroupByAgent"),
        SessionsGroupBy.Date => T("GroupByDate"),
        _ => T("GroupByNone"),
    };

    public static string GroupLabel(
        SessionRowGroup group, SessionsGroupBy mode, IReadOnlyList<SessionsHubAgent> agents)
    {
        if (group.IsUngrouped)
            return mode switch
            {
                SessionsGroupBy.Category => T("Ungrouped"),
                SessionsGroupBy.Person => T("Unassigned"),
                SessionsGroupBy.Date => T("DateNoActivity"),
                _ => T("GroupOther"),
            };
        return mode switch
        {
            SessionsGroupBy.Person => group.Rows.Select(row => row.Owner).FirstOrDefault(owner => owner is not null) is { } owner
                ? OwnerLabel(owner, agents) : group.Id,
            SessionsGroupBy.Channel => ChannelLabel(group.Id),
            SessionsGroupBy.Kind => KindLabel(group.Id),
            SessionsGroupBy.Agent => agents.FirstOrDefault(agent => agent.Id == group.Id)?.Name ?? group.Id,
            SessionsGroupBy.Date => group.Id switch
            {
                "today" => T("DateToday"),
                "yesterday" => T("DateYesterday"),
                "week" => T("DateThisWeek"),
                _ => T("DateOlder"),
            },
            _ => group.Id,
        };
    }

    public static string OwnerLabel(SessionOwnerInfo owner, IReadOnlyList<SessionsHubAgent> agents) =>
        owner.Label
        ?? (owner.Type == "agent" ? agents.FirstOrDefault(agent => agent.Id == owner.Id)?.Name : null)
        ?? owner.Id
        ?? T("Unassigned");

    public static string ChannelLabel(string channel) => channel.ToLowerInvariant() switch
    {
        "imessage" => "iMessage",
        "whatsapp" => "WhatsApp",
        "sms" => "SMS",
        "webchat" => T("ChannelWebchat"),
        { Length: > 0 } value => char.ToUpperInvariant(value[0]) + value[1..],
        _ => T("GroupOther"),
    };

    public static string ArchiveReason(string? reason) => reason switch
    {
        "manual" => T("ArchiveReasonManual"),
        "active-session-cap" => T("ArchiveReasonCap"),
        "age-retention" => T("ArchiveReasonAge"),
        "stale-dashboard" => T("ArchiveReasonStaleDashboard"),
        "restart-recovery" => T("ArchiveReasonRestart"),
        null or "" => "",
        _ => reason,
    };

    public static string Placement(string? state) => state switch
    {
        "requested" or "provisioning" or "syncing" or "starting" => T("PlacementStarting"),
        "active" => T("PlacementActive"),
        "draining" or "reconciling" => T("PlacementStopping"),
        "reclaimed" => T("PlacementReclaimed"),
        "failed" => T("PlacementFailed"),
        _ => T("PlacementLocal"),
    };

    public static string Tokens(SessionInfo row)
    {
        if (row.InputTokens <= 0 && row.OutputTokens <= 0 && row.TotalTokens <= 0)
            return T("NotAvailable");
        var total = row.TotalTokens > 0 ? row.TotalTokens : row.InputTokens + row.OutputTokens;
        return row.ContextTokens > 0
            ? F("TokensWithContext", Compact(total), Compact(row.ContextTokens))
            : Compact(total);
    }

    public static string Compact(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "K",
        _ => value.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    // ── Appearance ──

    public static string ColorLabel(string colorId) => T("Color" + Pascal(colorId));

    public static string IconLabel(string iconId) => T("Icon" + Pascal(iconId));

    public static string IconGlyph(string iconId) => iconId switch
    {
        "braces" => FluentIconCatalog.Develop,
        "book" => "\uE8F1",
        "monitor" => "\uE7F4",
        "bot" => "\uE99A",
        "kanban" => FluentIconCatalog.DashboardView,
        "coins" => FluentIconCatalog.Money,
        _ => FluentIconCatalog.Chat,
    };

    /// <summary>Kinds carry the same hue everywhere (row meta, chips, Kind grouping).</summary>
    public static string? KindColor(string kind) => kind switch
    {
        "direct" or "main" => "blue",
        "group" or "channel" or "thread" => "cyan",
        "cron" => "green",
        "global" => "yellow",
        "subagent" => "purple",
        _ => null,
    };

    public static string ChannelGlyph(string? channel) => channel?.ToLowerInvariant() switch
    {
        "webchat" or "web" => FluentIconCatalog.Browser,
        "discord" or "slack" or "teams" or "matrix" => FluentIconCatalog.People,
        "email" or "gmail" => FluentIconCatalog.MarkUnread,
        "voice" or "phone" => FluentIconCatalog.Voice,
        _ => FluentIconCatalog.Chat,
    };

    public static string Pascal(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];

    public static string WorktreeOwner(WorktreeRecord record, Func<string, string?> sessionTitle) => record.OwnerKind switch
    {
        "session" => record.OwnerId is { } key ? sessionTitle(key) ?? key : T("WorktreeOwnerSession"),
        "workboard" => T("WorktreeOwnerWorkboard"),
        _ => T("WorktreeOwnerManual"),
    };
}
