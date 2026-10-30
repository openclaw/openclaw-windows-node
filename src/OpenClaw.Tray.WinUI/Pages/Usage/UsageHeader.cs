using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>Usage page header: title, refresh/export/pin, range and scope filters, summary strip and query.</summary>
internal static class UsageHeader
{
    private static readonly UsagePreset[] Presets =
        [UsagePreset.Today, UsagePreset.Last7Days, UsagePreset.Last30Days, UsagePreset.Last90Days, UsagePreset.LastYear, UsagePreset.All];

    private static readonly string[] Facets = ["agent", "channel", "provider", "model", "tool"];

    public static Element Render(
        UsagePageState state, UsagePageController controller, Func<UsageExportFormat, Task> export, double width)
    {
        var filters = state.Filters;

        // Like the web page: title and a one-line description, then one "Reporting range" card holding every control.
        var title = HStack(14,
            Image("ms-appx:///Assets/SidebarIcons/Usage.svg").AccessibilityHidden().Size(36, 36).VAlign(VerticalAlignment.Center),
            VStack(0,
                TextBlock(S("Usage_Title")).FontSize(28).FontWeight(FontWeights.SemiBold)
                    .HeadingLevel(AutomationHeadingLevel.Level1).AutomationId("UsagePageMarker"),
                Note(S("Usage_Subtitle")).AutomationId("UsageSubtitle")));

        var card = VStack(14,
            RangeRow(state, controller),
            OptionsRow(state, controller),
            Border(null).Height(1).Background(CardStroke),
            TotalsRow(state, export, controller, width),
            state.ServerFiltersUnavailable ? null : QueryRow(state, controller),
            state.ServerFiltersUnavailable ? null : FacetRow(state, controller),
            ActiveFilters(state, controller));

        return VStack(16,
                title,
                VStack(8,
                    TextBlock(S("Usage_ReportingRange")).FontSize(14).FontWeight(FontWeights.SemiBold)
                        .HeadingLevel(AutomationHeadingLevel.Level2).Margin(2, 0, 2, 0),
                    Card(card, "UsageFilters")))
            .AutomationId("UsageHeader");
    }

    // ── reporting range card ──

    /// <summary>Row 1: presets, dates and time zone.</summary>
    private static Element RangeRow(UsagePageState state, UsagePageController controller)
    {
        var filters = state.Filters;
        var presetLabels = new[] { "Usage_Preset_Today", "Usage_Preset_7d", "Usage_Preset_30d", "Usage_Preset_90d", "Usage_Preset_1y", "Usage_Preset_All" };
        return Flow(
                SelectorBar(presetLabels.Select(k => SelectorBarItem(S(k))).ToArray(), Array.IndexOf(Presets, filters.Preset),
                        index => { if (index >= 0 && index < Presets.Length) controller.SetPreset(Presets[index]); })
                    .AutomationName(S("Usage_Filter_Range")).AutomationId("UsagePresets")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 16, 6).WithKey("presets"),
                HStack(6,
                        DatePicker(filters.Preset == UsagePreset.All ? null : filters.Start,
                            date => controller.SetRange(date, filters.End), S("Usage_Filter_StartDate"), "UsageStartDate"),
                        TextBlock(S("Usage_Filter_To")).Foreground(SecondaryText).VAlign(VerticalAlignment.Center),
                        DatePicker(filters.End, date => controller.SetRange(filters.Preset == UsagePreset.All ? date.AddDays(-29) : filters.Start, date),
                            S("Usage_Filter_EndDate"), "UsageEndDate"))
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 6).WithKey("dates"),
                ComboBox([S("Usage_TimeZone_Local"), S("Usage_TimeZone_Utc")],
                        filters.TimeZone == UsageTimeZoneMode.Utc ? 1 : 0,
                        index => controller.SetTimeZone(index == 1 ? UsageTimeZoneMode.Utc : UsageTimeZoneMode.Local))
                    .MinWidth(100).ToolTip(S("Usage_Filter_TimeZone"))
                    .AutomationName(S("Usage_Filter_TimeZone")).AutomationId("UsageTimeZone")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 0, 6).WithKey("zone"))
            .AutomationId("UsageFilterRow");
    }

    /// <summary>Row 2: who started the sessions, instance or lineage scope, the shown metric, and refresh.</summary>
    private static Element OptionsRow(UsagePageState state, UsagePageController controller)
    {
        var filters = state.Filters;
        var creators = state.Report?.CreatorOptions ?? Array.Empty<UsageCreator>();
        var creatorLabels = new[] { S("Usage_Creators_All") }.Concat(creators.Select(CreatorLabel)).ToArray();
        var creatorIndex = filters.CreatorKey is null ? 0 : creators.ToList().FindIndex(c => c.Key == filters.CreatorKey) + 1;
        return Flow(
                ComboBox(creatorLabels, Math.Max(0, creatorIndex),
                        index => controller.SetCreator(index <= 0 ? null : creators[index - 1].Key))
                    .MinWidth(170).IsEnabled(creators.Count > 0).ToolTip(S("Usage_Creators_Title"))
                    .AutomationName(S("Usage_Creators_Filter")).AutomationId("UsageCreatorFilter")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 6).WithKey("creator"),
                // Legacy usage.cost has no sessions, so there is no instance or lineage to choose.
                state.Availability == UsageAvailability.Legacy ? null : SelectorBar([SelectorBarItem(S("Usage_Scope_Instance")), SelectorBarItem(S("Usage_Scope_Family"))],
                        filters.Scope == UsageScope.Family ? 1 : 0,
                        index => controller.SetScope(index == 1 ? UsageScope.Family : UsageScope.Instance))
                    .ToolTip(S("Usage_Scope_FamilyHint")).HelpText(S("Usage_Scope_FamilyHint"))
                    .AutomationName(S("Usage_Filter_Scope")).AutomationId("UsageScope")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 6).WithKey("scope"),
                SelectorBar([SelectorBarItem(S("Usage_Metric_Tokens")), SelectorBarItem(S("Usage_Metric_Cost"))],
                        state.Metric == UsageMetric.Cost ? 1 : 0,
                        index => controller.SetMetric(index == 1 ? UsageMetric.Cost : UsageMetric.Tokens))
                    .AutomationName(S("Usage_Filter_Show")).AutomationId("UsageMetric")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 0, 6).WithKey("metric"))
            .AutomationId("UsageOptionsRow");
    }

    private static Element RefreshButton(UsagePageState state, UsagePageController controller) =>
        state.Phase == UsageLoadPhase.Loading
            ? HStack(8, ProgressRing().Size(16, 16).VAlign(VerticalAlignment.Center),
                    TextBlock(S("Usage_Refreshing")).Foreground(SecondaryText).VAlign(VerticalAlignment.Center))
                .AutomationName(S("Usage_Refreshing")).AutomationId("UsageRefreshing")
            : Button(HStack(6,
                        TextBlock("\uE72C").AccessibilityHidden().FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14)
                            .VAlign(VerticalAlignment.Center),
                        TextBlock(S("Usage_Refresh")).VAlign(VerticalAlignment.Center)),
                    controller.Refresh)
                .AccentButton()
                .IsEnabled(state.Availability != UsageAvailability.Disconnected)
                .AutomationName(S("Usage_Refresh")).AutomationId("UsageRefresh");

    /// <summary>The range's totals on the left; when it was updated, pin and export on the right.</summary>
    private static Element TotalsRow(UsagePageState state, Func<UsageExportFormat, Task> export, UsagePageController controller, double width)
    {
        var summary = state.Dashboard?.Summary;
        var lowerBound = summary?.LowerBound == true;
        var totals = Flow(
            Total(S("Usage_Metric_Tokens"), summary is null ? Dash : UsageUi.Tokens(summary.Tokens, lowerBound), GlyphTokens, 0, "UsageSummaryTokens"),
            Total(S("Usage_Metric_Cost"), summary is null ? Dash : UsageUi.Cost(summary.Cost, lowerBound), GlyphCost, 5, "UsageSummaryCost"),
            Total(S("Usage_Overview_Sessions"), summary is null ? Dash : Count(summary.Sessions), GlyphSessions, 4, "UsageSummarySessions"),
            lowerBound || summary?.Partial == true
                ? Pill(lowerBound ? S("Usage_LowerBoundShort") : S("Usage_PartialShort"), "UsageIncompleteChip")
                    .ToolTip(lowerBound ? S("Usage_LowerBoundNote") : S("Usage_PartialNote"))
                    .AutomationName(lowerBound ? S("Usage_LowerBoundNote") : S("Usage_PartialNote"))
                    .VAlign(VerticalAlignment.Center).WithKey("incomplete")
                : null);
        var actions = HStack(8,
            UpdatedText(state),
            RefreshButton(state, controller),
            ToggleButton("\uE718", state.HeaderPinned, controller.SetHeaderPinned)
                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .ToolTip(state.HeaderPinned ? S("Usage_Unpin") : S("Usage_Pin"))
                .AutomationName(state.HeaderPinned ? S("Usage_Unpin") : S("Usage_Pin"))
                .AutomationId("UsagePin"),
            ExportMenu(state, export));
        // Totals need about 560px and actions about 420px; below that the actions move under the totals.
        return width < 1000
            ? VStack(10, totals, actions.HAlign(HorizontalAlignment.Right))
            : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                totals.VAlign(VerticalAlignment.Center).Grid(column: 0),
                actions.VAlign(VerticalAlignment.Center).Grid(column: 1));
    }

    private static Element Total(string label, string value, string glyph, int palette, string automationId) =>
        HStack(10,
                IconBadge(glyph, palette, 36).VAlign(VerticalAlignment.Center),
                VStack(0,
                    TextBlock(value).FontSize(26).FontWeight(FontWeights.SemiBold).AutomationId(automationId + "Value"),
                    TextBlock(label).FontSize(12).Foreground(SecondaryText)))
            .Margin(0, 0, 32, 4)
            .AutomationName($"{label}: {value}")
            .AutomationId(automationId)
            .WithKey(automationId);

    /// <summary>The session filter box with the in-range count beside it, like the web page.</summary>
    private static Element QueryRow(UsagePageState state, UsagePageController controller)
    {
        var count = state.Dashboard?.Sessions.Total;
        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            Component<UsageQueryBox, UsageQueryBoxProps>(new UsageQueryBoxProps(state.Filters.Query, controller.SetQuery)).Grid(column: 0),
            count is { } total
                ? TextBlock(FN("Usage_Filter_SessionsInRangeFormat", total)).FontSize(12).Foreground(SecondaryText)
                    .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).AutomationId("UsageSessionsInRange").Grid(column: 1)
                : null);
    }

    /// <summary>Facet menus followed by a short hint, like the web page's filter chips row.</summary>
    private static Element FacetRow(UsagePageState state, UsagePageController controller) =>
        Flow(FacetMenus(state, controller).WithKey("facets"),
                Note(S("Usage_Filter_Tip")).VAlign(VerticalAlignment.Center).Margin(4, 0, 0, 4).WithKey("tip"))
            .AutomationId("UsageFacetRow");

    public static string RangeCaption(UsageFilters filters)
    {
        var zone = filters.TimeZone == UsageTimeZoneMode.Utc ? S("Usage_TimeZone_Utc") : TimeZoneInfo.Local.StandardName;
        var range = filters.Preset == UsagePreset.All
            ? S("Usage_Range_AllTime")
            : filters.Start == filters.End
                ? UsageFormat.FullDate(filters.Start, Culture)
                : F("Usage_RangeFormat", UsageFormat.FullDate(filters.Start, Culture), UsageFormat.FullDate(filters.End, Culture));
        return F("Usage_RangeCaptionFormat", range, zone);
    }

    private static Element UpdatedText(UsagePageState state) =>
        state.LoadedAt is { } loaded
            ? TextBlock(F("Usage_UpdatedFormat", loaded.ToLocalTime().ToString("t", Culture))).FontSize(12)
                .Foreground(TertiaryText).VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).AutomationId("UsageUpdated")
            : Empty();

    private static Element ExportMenu(UsagePageState state, Func<UsageExportFormat, Task> export)
    {
        var enabled = state.Report is not null && !state.Exporting && state.Availability == UsageAvailability.Connected;
        var target = Button(HStack(6,
                    TextBlock("\uEDE1").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14).VAlign(VerticalAlignment.Center),
                    TextBlock(state.Exporting ? S("Usage_Exporting") : S("Usage_Export")).VAlign(VerticalAlignment.Center),
                    TextBlock("\uE70D").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(10).VAlign(VerticalAlignment.Center)))
            .IsEnabled(enabled)
            .Resources(OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources)
            .AutomationName(S("Usage_Export"))
            .AutomationId("UsageExport");
        return MenuFlyout(target,
            MenuItem(S("Usage_Export_SessionsCsv"), () => _ = export(UsageExportFormat.SessionsCsv)),
            MenuItem(S("Usage_Export_DailyCsv"), () => _ = export(UsageExportFormat.DailyCsv)),
            MenuItem(S("Usage_Export_Json"), () => _ = export(UsageExportFormat.Json)));
    }

    private static Element DatePicker(DateOnly? date, Action<DateOnly> onChanged, string name, string automationId) =>
        CalendarDatePicker(
                date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null,
                value => { if (value is { } picked) onChanged(DateOnly.FromDateTime(picked.Date)); })
            .PlaceholderText(name)
            .Width(140)
            .AutomationName(name)
            .AutomationId(automationId);

    public static string CreatorLabel(UsageCreator creator) =>
        creator.Actor is null
            ? creator.Key == "system" ? S("Usage_Creators_System") : S("Usage_Creators_NotRecorded")
            : CreatorLabel(creator.Key, creator.Actor.Label, creator.Actor.Type);

    // Like upstream: a creator without a recorded actor is unattributed, whatever its opaque key looks like.
    public static string CreatorLabel(string key, string? label, string? type) =>
        !string.IsNullOrWhiteSpace(label) ? label!
            : type == "system" || key == "system" ? S("Usage_Creators_System")
            : type is null or "unknown" || key.StartsWith("unknown", StringComparison.Ordinal) ? S("Usage_Creators_NotRecorded")
            : key;

    private static Element FacetMenus(UsagePageState state, UsagePageController controller)
    {
        var options = state.Dashboard?.FilterOptions;
        if (options is null || options.Count == 0) return Empty();
        var terms = state.Filters.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menus = Facets
            .Where(facet => options.TryGetValue(facet, out var values) && values.Count > 0)
            .Select(facet =>
            {
                var values = options[facet];
                var active = values.Count(v => terms.Contains($"{facet}:{v}"));
                var label = S("Usage_Facet_" + facet) + (active > 0 ? $" ({active})" : "");
                var target = Button(HStack(6,
                            TextBlock(label).FontSize(13).VAlign(VerticalAlignment.Center),
                            TextBlock("\uE70D").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(10).VAlign(VerticalAlignment.Center)))
                    .Padding(10, 4)
                    .Resources(OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources)
                    .AutomationName(S("Usage_Facet_" + facet))
                    .AutomationId("UsageFacet_" + facet);
                return MenuFlyout(target, values.Take(40).Select(v => (MenuFlyoutItemBase)ToggleMenuItem(v, terms.Contains($"{facet}:{v}"),
                        _ => controller.ToggleQueryTerm($"{facet}:{v}"))).ToArray())
                    .Margin(0, 0, 4, 4)
                    .WithKey(facet);
            })
            .ToArray();
        return Flow(menus).AutomationId("UsageFacets");
    }

    private static Element ActiveFilters(UsagePageState state, UsagePageController controller)
    {
        var filters = state.Filters;
        var chips = new List<Element>();
        foreach (var term in filters.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            chips.Add(Removable(term, () => controller.ToggleQueryTerm(term), "UsageChipQuery_" + term));
        foreach (var day in filters.SelectedDays)
        {
            var label = UsageFormat.TryParseIso(day, out var date) ? UsageFormat.DayLabel(date, Culture) : day;
            chips.Add(Removable(label, () => controller.ToggleDay(day, append: true), "UsageChipDay_" + day));
        }
        foreach (var hour in filters.SelectedHours)
            chips.Add(Removable(UsageFormat.Hour(hour, Culture), () => controller.ToggleHour(hour, append: true), "UsageChipHour_" + hour));
        if (filters.SelectedSessions.Count > 0)
            chips.Add(Removable(FN("Usage_SelectedSessionsFormat", filters.SelectedSessions.Count), controller.CloseDetail, "UsageChipSessions"));
        if (filters.CreatorKey is { } creator)
            chips.Add(Removable(CreatorLabel(creator, state.Report?.CreatorOptions.FirstOrDefault(c => c.Key == creator)?.Actor?.Label, null),
                () => controller.SetCreator(null), "UsageChipCreator"));
        if (chips.Count == 0) return Empty();
        chips.Add(SubtleButton(S("Usage_ClearFilters"), controller.ClearAllFilters, "UsageClearFilters").WithKey("clear"));
        return Flow(chips.ToArray()).AutomationId("UsageActiveFilters");
    }

    private static Element Removable(string text, Action remove, string automationId) =>
        Button(HStack(6,
                    TextBlock(text).FontSize(12).VAlign(VerticalAlignment.Center).MaxWidth(240).TextTrimming(TextTrimming.CharacterEllipsis),
                    TextBlock("\uE711").FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(10).VAlign(VerticalAlignment.Center)),
                remove)
            .Padding(10, 3)
            .MinHeight(0)
            .CornerRadius(12)
            .Margin(0, 0, 6, 6)
            .Resources(r =>
            {
                OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources(r);
                r.Set("ButtonBackground", Theme.Ref("AccentFillColorTertiaryBrush"));
            })
            .ToolTip(F("Usage_RemoveFilterFormat", text))
            .AutomationName(F("Usage_RemoveFilterFormat", text))
            .AutomationId(automationId)
            .WithKey(automationId);
}

internal sealed record UsageQueryBoxProps(string Query, Action<string> Submit);

/// <summary>Query input with a local draft so typing is not overwritten by re-renders; submits on Enter.</summary>
internal sealed class UsageQueryBox : Component<UsageQueryBoxProps>
{
    public override Element Render()
    {
        var (draft, setDraft) = UseState(Props.Query);
        var external = Props.Query;
        // An external change (facet chip, clear) replaces the draft.
        UseEffect(() => setDraft(external), external);
        var submit = Props.Submit;
        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                AutoSuggestBox(draft, setDraft, value => submit(value))
                    .PlaceholderText(S("Usage_Query_Placeholder"))
                    .QueryIcon(FontIcon("\uE71C"))
                    .AutomationName(S("Usage_Query_Label"))
                    .HelpText(S("Usage_Query_Help"))
                    .AutomationId("UsageQuery")
                    .Grid(column: 0),
                Button(S("Usage_Query_Apply"), () => submit(draft))
                    .Margin(8, 0, 0, 0)
                    .IsEnabled(draft.Trim() != Props.Query.Trim())
                    .AutomationId("UsageQueryApply")
                    .Grid(column: 1));
    }
}
