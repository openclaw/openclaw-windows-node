using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

internal static class UsageHeader
{
    private static readonly UsagePreset[] Presets =
        [UsagePreset.Today, UsagePreset.Last7Days, UsagePreset.Last30Days, UsagePreset.Custom];

    public static Element Render(UsagePageState state, UsagePageProps actions, UsagePageLayout layout, bool highContrast)
    {
        var summary = state.Dashboard?.Summary;
        var unsupported = state.Availability == UsageAvailability.Unsupported;
        var forbidden = state.Availability == UsageAvailability.Forbidden;
        var amount = summary is null || !summary.CostKnown ? Dash
            : summary.Cost == 0 && summary.MissingCostEntries > 0 ? S("Usage_Overview_Unpriced")
            : Cost(summary.Cost, summary.LowerBound || summary.MissingCostEntries > 0);
        Element headline = unsupported || forbidden
            ? TextBlock(S(unsupported ? "Usage_Unsupported_Title" : "Usage_Forbidden_Title"))
                .FontSize(14).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.WrapWholeWords)
                .MaxLines(2).TextTrimming(TextTrimming.CharacterEllipsis)
            : Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                TextBlock(amount).FontSize(14).FontWeight(FontWeights.SemiBold).Foreground(AccentText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .MaxWidth(layout.ContentWidth / 2).ToolTip(amount)
                    .AutomationId("UsageSummaryCostValue").Margin(0, 0, PageResponsiveLayout.GapInner, 0).Grid(column: 0),
                TextBlock(HeadlineRange(state)).FontSize(14).Foreground(SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .ToolTip(HeadlineCaption(state)).HelpText(HeadlineCaption(state))
                    .AutomationId("UsageSummaryRange").Grid(column: 1));
        var incompleteReason = IncompleteReason(state);
        if (incompleteReason is not null)
            headline = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                headline.Grid(column: 0),
                Pill(S("Usage_PartialShort"), "UsageIncompleteChip")
                    .HelpText(incompleteReason).ToolTip(incompleteReason)
                    .Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 1));
        var subtitle = unsupported ? S("Usage_Unsupported_Message")
            : forbidden ? S("Usage_Forbidden_Message") : ContributorText(state);
        var detail = VStack(4,
            Note(subtitle).AutomationId("UsageSubtitle"),
            incompleteReason is null ? null : Note(incompleteReason).AutomationId("UsageIncompleteReason"),
            summary?.MissingCostEntries > 0
                ? Note(FN("Usage_MissingCostNoteFormat", summary.MissingCostEntries))
                    .AutomationId("UsageMissingCostChip") : null)
            .AutomationId("UsageMastText");
        var buttons = HStack(PageResponsiveLayout.GapInner,
                OpenWebButton(state, actions.OpenWebUsage).WithKey("web"),
                ExportMenu(state, actions.Export).WithKey("export"));
        var mast = PageHero.Header("UsageMast", "\uE9D2", S("Usage_Title"), "UsagePageMarker",
            headline.AutomationId("UsageHeadline"), detail,
            unsupported || forbidden ? Empty() : buttons.AutomationId("UsageMastActions"),
            layout.Narrow, !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled, highContrast, pageTag: "usage");
        return VStack(PageResponsiveLayout.GapStack,
            mast,
            unsupported || forbidden ? null : RangeRow(state, actions.Controller, layout))
            .AutomationId("UsageHeader");
    }

    public static string RangeWords(UsageFilters filters) => filters.Preset switch
    {
        UsagePreset.Today => S("Usage_Mast_Today"),
        UsagePreset.Last7Days => S("Usage_Mast_7d"),
        UsagePreset.Last30Days => S("Usage_Mast_30d"),
        _ => UsageFormat.CompactRange(filters.Start, filters.End, Culture, S("Usage_RangeFormat")),
    };

    private static string HeadlineRange(UsagePageState state)
    {
        if (state.Filters.Preset != UsagePreset.Last30Days) return RangeWords(state.Filters);
        return state.Dashboard?.Chart.Days is { Count: > 0 } days
            ? UsageFormat.CompactRange(days[0].Date, days[^1].Date, Culture, S("Usage_RangeFormat"))
            : S("Usage_NoReportedDates");
    }

    private static string HeadlineCaption(UsagePageState state) =>
        state.Filters.Preset == UsagePreset.Last30Days && state.Dashboard?.Chart.Days is { Count: > 0 } days
            ? RangeCaption(state.Filters with { Start = days[0].Date, End = days[^1].Date })
            : RangeCaption(state.Filters);

    private static string? IncompleteReason(UsagePageState state)
    {
        if (state.Report?.Partial is { } partial)
            return string.IsNullOrWhiteSpace(partial.Message) ? S("Usage_Partial_Message") : partial.Message;
        if (state.Report?.CacheStatus?.Partial is { } cachePartial)
            return string.IsNullOrWhiteSpace(cachePartial.Message) ? S("Usage_Partial_Message") : cachePartial.Message;
        if (state.Report?.CacheStatus?.IsIncomplete == true || state.Report?.Sessions.Any(row => row.Computing) == true)
            return S("Usage_PartialNote");
        return state.Dashboard?.Summary.LowerBound == true ? S("Usage_Partial_Message") : null;
    }

    private static string SummaryText(UsagePageState state)
    {
        var summary = state.Dashboard?.Summary;
        if (summary is null) return S("Usage_Loading");
        var count = summary.SessionsKnown ? AtLeast(Count(summary.Sessions), summary.SessionsAtLeast) : Dash;
        var text = F(summary.Sessions == 1 && !summary.SessionsAtLeast
            ? "Usage_Filter_SessionsInRangeFormatOne" : "Usage_Filter_SessionsInRangeFormat", count);
        if (state.Filters.Preset == UsagePreset.Last30Days && state.Dashboard?.Chart is { } chart)
            text += " · " + F("Usage_ReportedDaysFormat", Count(chart.Days.Count));
        if (state.Availability == UsageAvailability.Disconnected && state.LoadedAt is { } loaded)
            text += " · " + F("Usage_LastKnownFormat", loaded.ToLocalTime().ToString("t", Culture));
        return text;
    }

    private static string ContributorText(UsagePageState state)
    {
        var parts = new List<string>();
        if (state.Dashboard is { } dashboard)
        {
            var (agent, share, model) = UsageInsights.HeroContributors(dashboard);
            if (agent is not null && share is { } value)
                parts.Add(F("Usage_Hero_MostlyFormat", agent)
                    + " (" + UsageFormat.Percent(value, Culture, 0) + ")");
            if (model is not null) parts.Add(F("Usage_Hero_TopModelFormat", model));
        }
        parts.Add(SummaryText(state));
        return string.Join(" · ", parts);
    }

    private static Element RangeRow(UsagePageState state, UsagePageController controller, UsagePageLayout layout)
    {
        var filters = state.Filters;
        var advanced = !state.ServerFiltersUnavailable && state.SupportsOverview == true;
        var segments = SelectorBar(new[] { "Usage_Preset_Today", "Usage_Preset_7d", "Usage_Preset_30d", "Usage_Preset_Custom" }
                    .Select(key => SelectorBarItem(S(key))).ToArray(), Array.IndexOf(Presets, filters.Preset),
                    index => { if (index >= 0 && index < Presets.Length) controller.SetPreset(Presets[index]); })
                .HAlign(HorizontalAlignment.Left).AutomationName(S("Usage_Filter_Range")).AutomationId("UsagePresets");
        var controls = HStack(PageResponsiveLayout.GapInner,
            advanced ? AgentMenu(state, controller).WithKey("agent") : null,
            OptionsMenu(state, controller).WithKey("options"),
            state.Phase == UsageLoadPhase.Loading
                ? ProgressRing().Size(20, 20).AutomationName(S("Usage_Refreshing")).AutomationId("UsageRefreshing").WithKey("refreshing")
                : IconButton("\uE72C", S("Usage_Refresh"), controller.Refresh, "UsageRefresh",
                    state.Availability != UsageAvailability.Disconnected).WithKey("refresh"))
            .HAlign(layout.Narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center).AutomationId("UsageFilterOptions");
        var wide = layout.Tier == PageWidthTier.Wide;
        var custom = filters.Preset == UsagePreset.Custom;
        var inlineDates = wide && custom;
        var inlineSearch = wide && !custom;
        var searchRow = wide ? inlineDates ? 1 : 0 : layout.Narrow ? 2 : 1;
        var dateRow = inlineDates ? 0 : (advanced ? searchRow : layout.Narrow ? 1 : 0) + 1;
        var columns = wide && (advanced || custom) ? 3 : layout.Narrow ? 1 : 2;
        var rows = Math.Max(advanced ? searchRow + 1 : layout.Narrow ? 2 : 1, custom ? dateRow + 1 : 1);
        return Grid(columns == 3 ? [GridSize.Auto, GridSize.Star(), GridSize.Auto]
                : layout.Narrow ? [GridSize.Star()] : [GridSize.Star(), GridSize.Auto],
            Enumerable.Repeat(GridSize.Auto, rows).ToArray(),
            segments.VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0).WithKey("presets"),
            controls.Margin(layout.Narrow ? 0 : PageResponsiveLayout.GapGroup, layout.Narrow ? PageResponsiveLayout.GapGroup : 0,
                    inlineSearch && advanced ? PageResponsiveLayout.GapGroup : 0, 0)
                .Grid(row: layout.Narrow ? 1 : 0, column: inlineDates ? 2 : layout.Narrow ? 0 : 1).WithKey("controls"),
            advanced
                ? Component<UsageQueryBox, UsageQueryBoxProps>(new(filters.Query, controller.SetQuery))
                    .VAlign(VerticalAlignment.Center)
                    .MinWidth(inlineSearch ? 240 : 0).MaxWidth(inlineSearch ? 360 : double.PositiveInfinity)
                    .Width(inlineSearch ? Math.Clamp(layout.ContentWidth / 3, 240, 360) : double.NaN)
                    .Margin(0, inlineSearch ? 0 : PageResponsiveLayout.GapGroup, 0, 0)
                    .Grid(row: searchRow, column: inlineSearch ? 2 : 0, columnSpan: inlineSearch ? 1 : columns).WithKey("search")
                : null,
            custom
                ? Grid([GridSize.Star(), GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    DatePicker(filters.Start, date => controller.SetRange(date, filters.End), S("Usage_Filter_StartDate"), "UsageStartDate").Grid(column: 0),
                    TextBlock(S("Usage_Filter_To")).VAlign(VerticalAlignment.Center).Margin(PageResponsiveLayout.GapInner, 0)
                        .Grid(column: 1),
                    DatePicker(filters.End, date => controller.SetRange(filters.Start, date), S("Usage_Filter_EndDate"), "UsageEndDate").Grid(column: 2))
                    .Width(inlineDates ? double.NaN : Math.Min(layout.ContentWidth, 360)).MaxWidth(360)
                    .HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center)
                    .Margin(inlineDates ? PageResponsiveLayout.GapGroup : 0, inlineDates ? 0 : PageResponsiveLayout.GapGroup, 0, 0)
                    .Grid(row: dateRow, column: inlineDates ? 1 : 0, columnSpan: inlineDates ? 1 : columns)
                    .AutomationId("UsageCustomDates").WithKey("dates")
                : null)
            .AutomationId("UsageFilterRow");
    }

    private static Element AgentMenu(UsagePageState state, UsagePageController controller)
    {
        var values = state.Dashboard?.FilterOptions.TryGetValue("agent", out var agents) == true ? agents : [];
        return MenuFlyout(DropDownButton(S("Usage_Facet_agent")).AutomationName(S("Usage_Facet_agent")).AutomationId("UsageFacet_agent"),
            values.Select(agent => (MenuFlyoutItemBase)ToggleMenuItem(agent,
                state.Filters.Query.Split(' ').Contains("agent:" + agent),
                _ => controller.ToggleQueryTerm("agent:" + agent))).ToArray());
    }

    private static Element OptionsMenu(UsagePageState state, UsagePageController controller)
    {
        var zone = state.Filters.TimeZone;
        var scope = state.Filters.Scope;
        var local = F("Usage_Options_LocalFormat", TimeZoneInfo.Local.StandardName);
        return MenuFlyout(UsageMenuButtonElement.DropDownButton(content: HStack(6,
                    TextBlock("\uE713").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).AccessibilityHidden(),
                    TextBlock(S("Usage_Options")).TextWrapping(TextWrapping.NoWrap)))
                .AutomationName(S("Usage_Options"))
                .ToolTip($"{(zone == UsageTimeZoneMode.Utc ? S("Usage_TimeZone_Utc") : local)} · {S(scope == UsageScope.Family ? "Usage_Scope_Family" : "Usage_Scope_Instance")}")
                .AutomationId("UsageOptions"),
            MenuItem(S("Usage_Filter_TimeZone")) with { IsEnabled = false },
            RadioMenuItem(local, "usage-zone", zone == UsageTimeZoneMode.Local, () => controller.SetTimeZone(UsageTimeZoneMode.Local)),
            RadioMenuItem(S("Usage_TimeZone_Utc"), "usage-zone", zone == UsageTimeZoneMode.Utc, () => controller.SetTimeZone(UsageTimeZoneMode.Utc)),
            MenuSeparator(),
            MenuItem(S("Usage_Filter_Scope")) with { IsEnabled = false },
            RadioMenuItem(S("Usage_Scope_Instance"), "usage-scope", scope == UsageScope.Instance, () => controller.SetScope(UsageScope.Instance)),
            RadioMenuItem(S("Usage_Scope_Family"), "usage-scope", scope == UsageScope.Family, () => controller.SetScope(UsageScope.Family)));
    }

    public static Element OpenWebButton(UsagePageState state, Action openWebUsage, string automationId = "UsageOpenWeb") =>
        Button(HStack(6, TextBlock(S("Usage_OpenWeb")).TextWrapping(TextWrapping.NoWrap),
                TextBlock("\uE8A7").AccessibilityHidden().FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14)), openWebUsage)
            .IsEnabled(state.Availability != UsageAvailability.Disconnected)
            .ToolTip(S("Usage_OpenWebHint")).HelpText(S("Usage_OpenWebHint"))
            .AutomationName(S("Usage_OpenWeb")).AutomationId(automationId);

    public static string RangeCaption(UsageFilters filters) =>
        F("Usage_RangeCaptionFormat",
            F("Usage_RangeFormat", UsageFormat.FullDate(filters.Start, Culture), UsageFormat.FullDate(filters.End, Culture)),
            filters.TimeZone == UsageTimeZoneMode.Utc ? S("Usage_TimeZone_Utc") : TimeZoneInfo.Local.StandardName);

    private static Element ExportMenu(UsagePageState state, Func<UsageExportFormat, Task> export) =>
        MenuFlyout(UsageMenuButtonElement.DropDownButton(content: HStack(6,
                    TextBlock("\uE896").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).AccessibilityHidden(),
                    TextBlock(S(state.Exporting ? "Usage_Exporting" : "Usage_Export")).TextWrapping(TextWrapping.NoWrap)))
                .IsEnabled(state.Report is not null && !state.Exporting && !state.ExportInteraction && state.Availability == UsageAvailability.Connected)
                .AutomationName(S("Usage_Export")).AutomationId("UsageExport"),
            MenuItem(S("Usage_Export_SessionsCsv"), () => _ = export(UsageExportFormat.SessionsCsv)),
            MenuItem(S("Usage_Export_DailyCsv"), () => _ = export(UsageExportFormat.DailyCsv)));

    private static Element DatePicker(DateOnly date, Action<DateOnly> changed, string name, string id) =>
        CalendarDatePicker(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)),
                value => { if (value is { } picked) changed(DateOnly.FromDateTime(picked.Date)); })
            .MinWidth(0).HAlign(HorizontalAlignment.Stretch).PlaceholderText(name).AutomationName(name).AutomationId(id);
}

// The built-in Reactor factory accepts only a string label. This exposes native Content without replacing the template.
[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.DropDownButton),
    AutoDiscover = false, RegisterAssembly = false)]
[Microsoft.UI.Reactor.Wrappers.WrapContent("Content")]
internal partial record UsageMenuButtonElement;

internal sealed record UsageQueryBoxProps(string Query, Action<string> Submit);

internal sealed class UsageQueryBox : Component<UsageQueryBoxProps>
{
    public override Element Render()
    {
        var (draft, setDraft) = UseState(Props.Query);
        UseEffect(() => setDraft(Props.Query), Props.Query);
        return AutoSuggestBox(draft, setDraft, Props.Submit)
            .PlaceholderText(S("Usage_Query_Placeholder")).QueryIcon(FontIcon("\uE71C"))
            .AutomationName(S("Usage_Query_Label")).AutomationId("UsageQuery");
    }
}
