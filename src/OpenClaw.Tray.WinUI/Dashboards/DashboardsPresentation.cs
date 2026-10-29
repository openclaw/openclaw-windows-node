using System.Collections.Immutable;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Dashboards;

/// <summary>Localized text projections for the dashboards surface.</summary>
internal static class DashboardsPresentation
{
    public static string RowTitle(DashboardSessionRow row) => SessionTitleFormatter.Format(row.Session);

    /// <summary>Largest Unix millisecond value <see cref="DateTimeOffset"/> can represent (year 9999).</summary>
    private static readonly long s_maxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static string UpdatedText(DashboardSessionRow row, DateTimeOffset now)
    {
        // A Gateway sending seconds-as-micros or garbage must not break rendering: treat it as unknown.
        if (row.UpdatedAtMs is not { } ms || ms <= 0 || ms > s_maxUnixMs)
            return LocalizationHelper.GetString("Dashboards_UpdatedUnknown");
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return LocalizationHelper.Format("Dashboards_UpdatedFormat", RelativeTime(updated, now));
    }

    public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var age = now - value;
        if (age.TotalMinutes < 1)
            return LocalizationHelper.GetString("TimeAgo_JustNow");
        if (age.TotalHours < 1)
            return LocalizationHelper.Format("TimeAgo_MinutesFormat", (int)age.TotalMinutes);
        if (age.TotalHours < 48)
            return LocalizationHelper.Format("TimeAgo_HoursFormat", (int)age.TotalHours);
        if (age.TotalDays < 30)
            return LocalizationHelper.Format("TimeAgo_DaysFormat", (int)age.TotalDays);
        return value.ToLocalTime().ToString("d");
    }

    public static string KindLabel(BoardWidget widget, IReadOnlyList<PluginWidgetKind> kinds) => widget.ContentKind switch
    {
        BoardWidgetContentKind.McpApp => LocalizationHelper.GetString("Dashboards_Widget_KindMcp"),
        BoardWidgetContentKind.Plugin => BoardWidgetBodyPolicy.AdvertisedLabel(widget, kinds)
            ?? widget.KindLabel
            ?? LocalizationHelper.GetString("Dashboards_Widget_KindPlugin"),
        _ => LocalizationHelper.GetString("Dashboards_Widget_KindHtml"),
    };

    /// <summary>Accessible capability lines for a widget's declared access.</summary>
    public static ImmutableArray<string> CapabilityLines(BoardWidget widget) =>
        [
            .. widget.Declared.NetOrigins.Select(origin => LocalizationHelper.Format("Dashboards_Widget_NetworkCapability", origin)),
            .. widget.Declared.Tools.Select(tool => LocalizationHelper.Format("Dashboards_Widget_ToolCapability", tool)),
        ];
}
