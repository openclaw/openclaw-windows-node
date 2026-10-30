using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>Overview sections of the native Usage page, in the upstream web page's order.</summary>
internal static class UsageSections
{
    public const int CreatorPreviewCount = 6;

    /// <summary>Lays items out row-major in <paramref name="columns"/> equal columns.</summary>
    public static Element Columns(int columns, IReadOnlyList<Element> items, double gap = 12)
    {
        columns = Math.Max(1, Math.Min(columns, Math.Max(1, items.Count)));
        var rows = (items.Count + columns - 1) / columns;
        return Grid(Enumerable.Range(0, columns).Select(_ => GridSize.Star()).ToArray(),
            Enumerable.Range(0, Math.Max(rows, 1)).Select(_ => GridSize.Auto).ToArray(),
            items.Select((item, i) => item
                    .Margin(i % columns == 0 ? 0 : gap / 2, i / columns == 0 ? 0 : gap, i % columns == columns - 1 ? 0 : gap / 2, 0)
                    .Grid(row: i / columns, column: i % columns)
                    .WithKey(item.Key ?? "cell:" + i))
                .ToArray());
    }

    public static int ColumnsFor(double width, double minColumnWidth, int max) =>
        Math.Clamp((int)(width / minColumnWidth), 1, max);

    // ── daily trend ──

    public static Element Daily(UsagePageState state, UsageDashboard dashboard, UsagePageController controller, double width)
    {
        var chart = dashboard.Chart;
        var metric = state.Metric;
        var title = metric == UsageMetric.Cost ? S("Usage_Daily_CostTitle") : S("Usage_Daily_TokensTitle");
        var modes = SelectorBar(
                [SelectorBarItem(S("Usage_Daily_Total")), SelectorBarItem(S("Usage_Daily_ByType")), SelectorBarItem(S("Usage_Daily_ByProvider"))],
                (int)state.ChartMode,
                index => controller.SetChartMode((UsageDailyChartMode)Math.Clamp(index, 0, 2)))
            .AutomationName(S("Usage_Daily_ModeLabel"))
            .AutomationId("UsageDailyMode");

        var columns = chart.Days.Select(day =>
        {
            var label = UsageFormat.DayLabel(day.Date, Culture);
            var text = day.Pending
                ? F("Usage_Daily_PendingFormat", UsageFormat.FullDate(day.Date, Culture))
                : F("Usage_Daily_BarFormat", UsageFormat.FullDate(day.Date, Culture),
                    UsageUi.Tokens(day.Tokens, day.LowerBound), AnalysisCost(day.Cost, day.LowerBound));
            return new UsageCharts.BarColumn(day.Iso, day.Total, day.Segments, label, text, day.Selected, day.Pending, day.LowerBound);
        }).ToList();

        Element body = chart.IsEmpty && !chart.Days.Any(d => d.Pending)
            ? EmptyText(S("Usage_Daily_Empty"), "UsageDailyEmpty")
            : VStack(12,
                UsageCharts.Columns(columns, chart.Series, chart.Ceiling,
                    value => metric == UsageMetric.Cost ? UsageFormat.Cost(value, Culture, value < 1 ? 2 : 0) : UsageFormat.Tokens((long)value, Culture),
                    state.ServerFiltersUnavailable ? null : (string iso, bool append) => controller.ToggleDay(iso, append), "UsageDailyChart"),
                UsageCharts.Legend(chart.Series),
                state.ServerFiltersUnavailable ? null : Note(S("Usage_Daily_Hint")));

        return Section(title, UsageHeader.RangeCaption(state.Filters), modes, VStack(16,
                body,
                Border(null).Height(1).Background(CardStroke),
                Breakdown(dashboard, metric),
                Component<UsageDailyTable, UsageDailyTableProps>(new UsageDailyTableProps(chart))),
            "UsageDaily");
    }

    private static Element Breakdown(UsageDashboard dashboard, UsageMetric metric)
    {
        var parts = dashboard.Breakdown;
        var lowerBound = dashboard.Summary.LowerBound;
        return VStack(8,
                TextBlock(metric == UsageMetric.Cost ? S("Usage_Breakdown_CostTitle") : S("Usage_Breakdown_TokensTitle"))
                    .FontSize(13).FontWeight(FontWeights.SemiBold),
                StackedBar(parts.Select(p => (p.Share, KindBrush(p.Kind))).ToList()),
                Flow(parts.Select(p => LegendItem(
                        SeriesLabel(new UsageSeries("", p.Kind, null, 0)),
                        KindBrush(p.Kind),
                        $"{Metric(metric, p.Tokens, p.Cost, lowerBound)} ({UsageFormat.Percent(p.Share, Culture, 0)})")
                    .Margin(0, 0, 20, 4)
                    .AutomationName($"{SeriesLabel(new UsageSeries("", p.Kind, null, 0))}: {Metric(metric, p.Tokens, p.Cost, lowerBound)}")
                    .WithKey(p.Kind.ToString())).ToArray()))
            .AutomationId("UsageBreakdown");
    }

    // ── cost windows ──

    public static Element CostWindows(UsageDashboard dashboard, UsageFilters filters, double width)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tiles = dashboard.CostWindows.Select(w =>
        {
            var label = w.IsSelectedRange ? S("Usage_CostWindows_SelectedRange")
                : w.Days == 1 ? (w.EndDate == today ? S("Usage_Preset_Today") : UsageFormat.DayLabel(w.EndDate, Culture))
                : F("Usage_CostWindows_LastDaysFormat", w.Days);
            var lower = w.Totals.IsLowerBound;
            var sub = UsageUi.Tokens(w.Totals.TotalTokens, lower) + " " + S("Usage_Metric_TokensLower")
                + (w.Days > 1 ? " · " + F("Usage_CostWindows_PerDayFormat", AnalysisCost(w.Totals.TotalCost / w.Days, lower)) : "");
            return Stat(label, AnalysisCost(w.Totals.TotalCost, lower), sub, "UsageCostWindow_" + w.Days,
                glyph: w.IsSelectedRange ? GlyphCalendar : GlyphCost, palette: w.IsSelectedRange ? 0 : 5).WithKey("window:" + w.Days);
        }).ToList();
        return Section(S("Usage_CostWindows_Title"), F("Usage_CostWindows_SubtitleFormat", UsageFormat.FullDate(filters.End, Culture)), null,
            Columns(ColumnsFor(width, 190, tiles.Count), tiles), "UsageCostWindows");
    }

    // ── creators ──

    public static Element Creators(UsagePageState state, UsageDashboard dashboard, UsagePageController controller) =>
        Component<UsageCreatorsSection, UsageCreatorsProps>(new UsageCreatorsProps(dashboard.Creators, state.Metric, controller.SetCreator));

    // ── insights ──

    public static Element Insights(UsagePageState state, UsageDashboard dashboard, double width)
    {
        var i = dashboard.Insights;
        var empty = Dash;
        var stats = new List<Element>
        {
            Stat(S("Usage_Overview_Messages"), Count(i.Messages.Total),
                F("Usage_Overview_MessagesSubFormat", Count(i.Messages.User), Count(i.Messages.Assistant)), "UsageStatMessages",
                glyph: GlyphMessages, palette: 0),
            Stat(S("Usage_Overview_Throughput"),
                i.ThroughputTokensPerMinute is { } tpm ? F("Usage_Overview_TokensPerMinuteFormat", UsageUi.Tokens((long)Math.Round(tpm))) : empty,
                i.ThroughputCostPerMinute is { } cpm ? F("Usage_Overview_CostPerMinuteFormat", AnalysisCost(cpm)) : empty,
                "UsageStatThroughput", tooltip: S("Usage_Overview_ThroughputHint"), glyph: GlyphSpeed, palette: 3),
            Stat(S("Usage_Overview_ToolCalls"), Count(i.Tools.TotalCalls),
                FN("Usage_Overview_ToolsUsedFormat", i.Tools.UniqueTools), "UsageStatToolCalls", glyph: GlyphTools, palette: 1),
            Stat(S("Usage_Overview_AvgTokens"), i.AverageTokensPerMessage is { } avg ? UsageUi.Tokens(avg) : empty,
                FN("Usage_Overview_AcrossMessagesFormat", i.Messages.Total), "UsageStatAvgTokens", glyph: GlyphTokens, palette: 2),
            Stat(S("Usage_Overview_CacheHitRate"), i.CacheHitRate is { } hit ? UsageFormat.Percent(hit, Culture) : empty,
                F("Usage_Overview_CacheSubFormat", UsageUi.Tokens(i.CacheRead, i.Partial), UsageUi.Tokens(i.CacheBase, i.Partial)),
                "UsageStatCacheHit",
                i.Partial || i.CacheHitRate is null ? null : i.CacheHitRate > 0.6 ? Success : i.CacheHitRate > 0.3 ? Caution : Critical,
                S("Usage_Overview_CacheHint"), GlyphCache, 1),
            Stat(S("Usage_Overview_ErrorRate"), UsageFormat.Percent(i.ErrorRate, Culture, 2),
                F("Usage_Overview_ErrorSubFormat", Count(i.Messages.Errors),
                    i.AverageSessionDurationMs is { } d ? UsageFormat.Duration(d) : empty),
                "UsageStatErrorRate",
                i.ErrorRate > 0.05 ? Critical : i.ErrorRate > 0.01 ? Caution : Success,
                S("Usage_Overview_ErrorHint"), GlyphErrors),
            Stat(S("Usage_Overview_AvgCost"), i.AverageCostPerMessage is { } c ? AnalysisCost(c) : empty,
                F("Usage_Overview_TotalCostFormat", AnalysisCost(i.TotalCost, i.Partial)), "UsageStatAvgCost",
                tooltip: i.MissingCost ? S("Usage_Overview_AvgCostHintMissing") : S("Usage_Overview_AvgCostHint"), glyph: GlyphCost, palette: 5),
            Stat(S("Usage_Overview_Sessions"), Count(i.Sessions),
                F("Usage_Overview_SessionsInRangeFormat", Count(i.TotalSessions)), "UsageStatSessions", glyph: GlyphSessions, palette: 4),
            Stat(S("Usage_Overview_Errors"), Count(i.Messages.Errors),
                F("Usage_Overview_ToolResultsFormat", Count(i.Messages.ToolResults)), "UsageStatErrors",
                i.Messages.Errors > 0 ? Critical : Success, glyph: GlyphErrors),
        }.Select((e, index) => e.WithKey("stat:" + index)).ToList();

        var lists = new[]
        {
            (UsageInsightList.TopModels, "Usage_Overview_TopModels", "Usage_Overview_NoModelData"),
            (UsageInsightList.TopProviders, "Usage_Overview_TopProviders", "Usage_Overview_NoProviderData"),
            (UsageInsightList.TopTools, "Usage_Overview_TopTools", "Usage_Overview_NoToolCalls"),
            (UsageInsightList.TopAgents, "Usage_Overview_TopAgents", "Usage_Overview_NoAgentData"),
            (UsageInsightList.TopChannels, "Usage_Overview_TopChannels", "Usage_Overview_NoChannelData"),
            (UsageInsightList.PeakErrorDays, "Usage_Overview_PeakErrorDays", "Usage_Overview_NoErrorData"),
        }.Select(l => Tile(InsightList(l.Item1, S(l.Item2), S(l.Item3), i.Lists[l.Item1])).WithKey(l.Item1.ToString())).ToList();
        // Hourly error counts come from the server overview, which released Gateways do not return.
        var errorHours = state.BasicGateway
            ? null
            : Tile(InsightList(UsageInsightList.PeakErrorHours, S("Usage_Overview_PeakErrorHours"), S("Usage_Overview_NoErrorData"),
                i.Lists[UsageInsightList.PeakErrorHours])).WithKey("error-hours");

        // Like the web page: a grid of stat cards, then one card per ranked list, then peak error hours full width.
        return Section(S("Usage_Overview_Title"), null, null, VStack(12,
                Columns(ColumnsFor(width, 220, 3), stats.Select(s => Tile(s).WithKey(s.Key ?? "stat")).ToList(), 12),
                Columns(ColumnsFor(width, 300, 3), lists, 12),
                errorHours),
            "UsageInsights", framed: false);
    }

    private static Element InsightList(UsageInsightList kind, string title, string emptyText, IReadOnlyList<UsageInsightItem> items)
    {
        var isError = kind is UsageInsightList.PeakErrorDays or UsageInsightList.PeakErrorHours;
        var rows = items.Select((item, index) =>
        {
            var label = kind switch
            {
                UsageInsightList.PeakErrorDays when UsageFormat.TryParseIso(item.Label, out var date) => UsageFormat.DayLabel(date, Culture),
                UsageInsightList.PeakErrorHours when int.TryParse(item.Label, out var hour) => UsageFormat.Hour(hour, Culture),
                _ when string.IsNullOrWhiteSpace(item.Label) => S("Usage_Unknown"),
                _ => item.Label,
            };
            var value = kind switch
            {
                UsageInsightList.TopTools => FN("Usage_Overview_CallsFormat", item.Count ?? 0),
                _ when isError => UsageFormat.Percent(item.Rate ?? 0, Culture, 2),
                _ when item.Unpriced => S("Usage_Overview_Unpriced"),
                _ => AnalysisCost(item.Cost, item.LowerBound),
            };
            var subParts = new List<string>();
            if (item.Source is { Length: > 0 } source) subParts.Add(source);
            if (isError)
            {
                subParts.Add(FN("Usage_Overview_ErrorsCountFormat", item.Errors ?? 0));
                subParts.Add(FN("Usage_Overview_MessagesCountFormat", item.Messages ?? 0));
            }
            else if (kind != UsageInsightList.TopTools)
            {
                subParts.Add(UsageUi.Tokens(item.Tokens, item.LowerBound));
                if (item.Count is { } count) subParts.Add(FN("Usage_Overview_MessagesCountFormat", count));
            }
            var sub = string.Join(" · ", subParts);
            // Each item keeps its own palette color (the same agent, model or provider is the same color everywhere).
            var colorKey = kind == UsageInsightList.TopModels ? item.Source ?? item.Label : item.Label;
            var fill = isError ? Critical : Palette(PaletteIndex(colorKey));
            Element? mark = kind switch
            {
                UsageInsightList.TopModels => ProviderMark(item.Source, 24),
                UsageInsightList.TopProviders => ProviderMark(item.Label, 24),
                UsageInsightList.TopAgents or UsageInsightList.TopChannels => Avatar(label, item.Label, 24),
                UsageInsightList.TopTools => IconBadge(GlyphTools, PaletteIndex(item.Label), 24),
                _ => null,
            };
            var valueText = TextBlock(value).FontSize(13).FontWeight(FontWeights.SemiBold).Margin(8, 0, 0, 0);
            Element text = VStack(4,
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(label).FontSize(13).TextTrimming(TextTrimming.CharacterEllipsis).Grid(column: 0),
                    (isError ? valueText.Foreground(Critical) : valueText).Grid(column: 1)),
                ShareBar(item.Weight, fill, 6),
                sub.Length == 0 ? null : TextBlock(sub).FontSize(11).Foreground(TertiaryText).TextTrimming(TextTrimming.CharacterEllipsis));
            return (mark is null
                    ? text
                    : Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                        mark.VAlign(VerticalAlignment.Top).Margin(0, 0, 10, 0).Grid(column: 0),
                        text.Grid(column: 1)))
                .AutomationName($"{label}: {value}{(sub.Length == 0 ? "" : ". " + sub)}")
                .AutomationId($"UsageInsight_{kind}_{index}")
                .WithKey(kind + ":" + (item.Source is { } keySource ? keySource + "/" : "") + item.Label);
        }).ToArray();
        return VStack(10,
                TextBlock(title).FontSize(13).FontWeight(FontWeights.SemiBold).HeadingLevel(AutomationHeadingLevel.Level3),
                rows.Length == 0 ? EmptyText(emptyText, $"UsageInsightEmpty_{kind}") : VStack(10, rows))
            .AutomationId("UsageInsight_" + kind);
    }

    // ── activity ──

    public static Element Activity(UsagePageState state, UsageDashboard dashboard, UsagePageController controller)
    {
        var activity = dashboard.Activity;
        var zone = state.Filters.TimeZone == UsageTimeZoneMode.Utc ? S("Usage_TimeZone_Utc") : TimeZoneInfo.Local.StandardName;
        if (!activity.HasTimelineData)
            return Section(S("Usage_Mosaic_Title"), null, null, EmptyText(S("Usage_Mosaic_NoTimeline"), "UsageMosaicEmpty"), "UsageActivity");

        var weekdays = activity.Weekdays;
        var peak = new List<string>();
        if (activity.PeakHour is { } peakHour) peak.Add(F("Usage_Mosaic_PeakHourFormat", UsageFormat.Hour(peakHour, Culture)));
        if (activity.PeakWeekday is { } peakDay) peak.Add(F("Usage_Mosaic_PeakDayFormat", Culture.DateTimeFormat.GetDayName(peakDay)));
        return Section(S("Usage_Mosaic_Title"), F("Usage_Mosaic_SubtitleFormat", zone), null, VStack(16,
                TextBlock(S("Usage_Mosaic_Weekdays")).FontSize(12).Foreground(SecondaryText),
                UsageCharts.ActivityStrip(weekdays, d => UsageFormat.ShortWeekday((DayOfWeek)d, Culture), _ => true, null, "UsageWeekdays", 64,
                    valuesInside: true),
                TextBlock(S("Usage_Mosaic_Hours")).FontSize(12).Foreground(SecondaryText),
                UsageCharts.ActivityStrip(activity.Hours, h => UsageFormat.Hour(h, Culture), h => h % 4 == 0,
                    (hour, append) => controller.ToggleHour(hour, append), "UsageHours", 40),
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    Note(string.Join(" · ", peak.Prepend(S("Usage_Mosaic_Hint")))).Grid(column: 0),
                    HStack(4,
                        TextBlock(S("Usage_Heatmap_Less")).FontSize(11).Foreground(TertiaryText),
                        Border(null).Size(12, 12).CornerRadius(2).Background(Accent).Opacity(0.2),
                        Border(null).Size(12, 12).CornerRadius(2).Background(Accent).Opacity(0.6),
                        Border(null).Size(12, 12).CornerRadius(2).Background(Accent),
                        TextBlock(S("Usage_Heatmap_More")).FontSize(11).Foreground(TertiaryText))
                        .AccessibilityView(AccessibilityView.Raw).Grid(column: 1))),
            "UsageActivity");
    }

    public static Element HeatmapSection(UsageHeatmap heatmap)
    {
        var active = heatmap.Weeks.SelectMany(w => w).Count(c => c.InRange && c.Tokens > 0);
        return Section(S("Usage_Heatmap_Title"), F("Usage_Heatmap_SubtitleFormat", Count(active)), null,
            UsageCharts.Heatmap(heatmap), "UsageHeatmap");
    }

    // ── provider plans ──

    public static Element Providers(UsagePageState state)
    {
        var providers = state.Providers;
        Element body;
        if (providers.Value is null)
        {
            body = providers.Phase switch
            {
                UsageLoadPhase.Loading => HStack(12, ProgressRing().Size(18, 18),
                    TextBlock(S("Usage_Providers_Loading")).Foreground(SecondaryText).VAlign(VerticalAlignment.Center)).AutomationId("UsageProvidersLoading"),
                UsageLoadPhase.Failed => EmptyText(S("Usage_Providers_Unavailable"), "UsageProvidersUnavailable"),
                _ => EmptyText(S("Usage_Providers_Empty"), "UsageProvidersEmpty"),
            };
        }
        else if (providers.Value.Providers.Count == 0)
        {
            body = EmptyText(providers.Value.Refreshing ? S("Usage_Providers_Refreshing") : S("Usage_Providers_Empty"), "UsageProvidersEmpty");
        }
        else
        {
            body = Flow(providers.Value.Providers.Select(p => ProviderCard(p).Width(300).Margin(0, 0, 12, 12).WithKey(p.Provider)).ToArray())
                .AutomationId("UsageProviderCards");
        }
        var note = providers.Phase == UsageLoadPhase.Failed && providers.Value is not null ? S("Usage_Providers_Stalled")
            : providers.Value?.Refreshing == true ? S("Usage_Providers_Refreshing") : S("Usage_Providers_Subtitle");
        return Section(S("Usage_Providers_Title"), note, null, body, "UsageProviders");
    }

    private static Element ProviderCard(ProviderUsageSnapshot provider)
    {
        var rows = new List<Element>();
        foreach (var (window, index) in provider.Windows.Select((w, i) => (w, i)))
        {
            var label = window.GroupLabel is { Length: > 0 } group ? $"{window.Label} · {group}" : window.Label;
            var used = Math.Clamp(window.UsedPercent / 100, 0, 1);
            var reset = window.ResetAt is { } at
                ? at <= DateTimeOffset.Now ? S("Usage_Providers_ResetNow") : F("Usage_Providers_ResetsFormat", Relative(at))
                : null;
            rows.Add(VStack(4,
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock(label).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
                        TextBlock(F("Usage_Providers_UsedFormat", UsageFormat.Percent(used, Culture, 0))).FontSize(12).FontWeight(FontWeights.SemiBold).Grid(column: 1)),
                    ShareBar(used, used >= 0.9 ? Critical : used >= 0.7 ? Caution : Accent),
                    reset is null ? null : TextBlock(reset).FontSize(11).Foreground(TertiaryText))
                .AutomationName($"{label}: {UsageFormat.Percent(used, Culture, 0)}{(reset is null ? "" : ". " + reset)}")
                .WithKey("window:" + index));
        }
        foreach (var (billing, index) in provider.Billing.Select((b, i) => (b, i)))
        {
            var label = billing.Label ?? S("Usage_Providers_Billing_" + billing.Type);
            var amount = Money(billing.Amount, billing.Unit);
            Element content = billing.Type == "budget" && billing.Limit is > 0 and var limit
                ? VStack(4,
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock(label).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
                        TextBlock(F("Usage_Providers_BudgetFormat", amount, Money(limit, billing.Unit))).FontSize(12).FontWeight(FontWeights.SemiBold).Grid(column: 1)),
                    ShareBar(billing.Amount / limit, billing.Amount / limit >= 0.9 ? Critical : Accent))
                : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(label).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
                    TextBlock(amount).FontSize(12).FontWeight(FontWeights.SemiBold).Grid(column: 1));
            rows.Add(content.AutomationName($"{label}: {amount}").WithKey("billing:" + index));
        }
        if (provider.CostHistory is { } history)
        {
            var total = history.Daily.Sum(d => d.Amount);
            rows.Add(Note(F("Usage_Providers_CostHistoryFormat", Money(total, history.Unit), history.PeriodDays)).WithKey("history"));
        }
        if (provider.Summary is { Length: > 0 } summary) rows.Add(Note(summary).WithKey("summary"));
        if (provider.Error is { Length: > 0 } error)
        {
            rows.Add(Border(TextBlock(error).FontSize(12).TextWrapping(TextWrapping.Wrap))
                .Padding(8).CornerRadius(4).Background(CautionBackground).WithKey("error"));
        }
        if (rows.Count == 0) rows.Add(Note(S("Usage_Providers_NoQuota")).WithKey("none"));

        return Card(VStack(10,
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    ProviderMark(provider.Provider, 32).VAlign(VerticalAlignment.Center).Margin(0, 0, 10, 0).Grid(column: 0),
                    VStack(0,
                        TextBlock(provider.DisplayName).FontSize(14).FontWeight(FontWeights.SemiBold),
                        TextBlock(provider.AccountEmail ?? provider.Provider).FontSize(11).Foreground(TertiaryText)
                            .TextTrimming(TextTrimming.CharacterEllipsis)).VAlign(VerticalAlignment.Center).Grid(column: 1),
                    provider.Plan is { Length: > 0 } plan ? Pill(plan, "UsageProviderPlan_" + provider.Provider).VAlign(VerticalAlignment.Center).Grid(column: 2) : null),
                VStack(10, rows.ToArray())),
            "UsageProvider_" + provider.Provider);
    }

    private static string Money(double amount, string unit) =>
        unit.Length == 3 && unit.All(char.IsUpper)
            ? amount.ToString("C", CurrencyFormat(unit))
            : F("Usage_Providers_CreditsFormat", amount.ToString("N2", Culture), unit);

    private static System.Globalization.NumberFormatInfo CurrencyFormat(string isoCode)
    {
        var format = (System.Globalization.NumberFormatInfo)Culture.NumberFormat.Clone();
        format.CurrencySymbol = isoCode == "USD" ? "$" : isoCode + " ";
        return format;
    }

    private static string Relative(DateTimeOffset at) =>
        UsageFormat.Duration(Math.Max(0, (at - DateTimeOffset.Now).TotalMilliseconds));
}

internal sealed record UsageDailyTableProps(UsageDailyChart Chart);

/// <summary>Text equivalent of the daily chart, rendered only when expanded.</summary>
internal sealed class UsageDailyTable : Component<UsageDailyTableProps>
{
    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(false);
        var rows = expanded
            ? Props.Chart.Days.Where(d => d.Tokens > 0 || d.Cost > 0 || d.Pending).Reverse().Select(d =>
                    (Element)Grid([GridSize.Star(), GridSize.Px(110), GridSize.Px(110)], [GridSize.Auto],
                            TextBlock(UsageFormat.FullDate(d.Date, Culture)).FontSize(12).Grid(column: 0),
                            TextBlock(d.Pending ? S("Usage_Daily_NotLoaded") : UsageUi.Tokens(d.Tokens, d.LowerBound)).FontSize(12)
                                .HAlign(HorizontalAlignment.Right).Grid(column: 1),
                            TextBlock(d.Pending ? "" : AnalysisCost(d.Cost, d.LowerBound)).FontSize(12)
                                .HAlign(HorizontalAlignment.Right).Grid(column: 2))
                        .Padding(0, 2)
                        .WithKey(d.Iso))
                .ToArray()
            : [];
        Element content = expanded
            ? VStack(4, new Element[]
                {
                    Grid([GridSize.Star(), GridSize.Px(110), GridSize.Px(110)], [GridSize.Auto],
                        TextBlock(S("Usage_Table_Date")).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
                        TextBlock(S("Usage_Metric_Tokens")).FontSize(12).Foreground(SecondaryText).HAlign(HorizontalAlignment.Right).Grid(column: 1),
                        TextBlock(S("Usage_Metric_Cost")).FontSize(12).Foreground(SecondaryText).HAlign(HorizontalAlignment.Right).Grid(column: 2))
                        .WithKey("header"),
                }.Concat(rows).ToArray())
            : Empty();
        return Expander(S("Usage_Daily_ShowTable"), content, expanded, setExpanded)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("UsageDailyTable");
    }
}

internal sealed record UsageCreatorsProps(IReadOnlyList<UsageCreatorRow> Creators, UsageMetric Metric, Action<string?> Select);

/// <summary>"Started by" attribution with a short preview and an expand action.</summary>
internal sealed class UsageCreatorsSection : Component<UsageCreatorsProps>
{
    public override Element Render()
    {
        var (showAll, setShowAll) = UseState(false);
        var creators = Props.Creators;
        var visible = showAll ? creators : creators.Take(UsageSections.CreatorPreviewCount).ToList();
        var select = Props.Select;
        var metric = Props.Metric;
        // Table layout like the web page: identity (with share bar), tokens, cost, sessions. The shown metric is bold.
        GridSize[] columns = [GridSize.Px(3), GridSize.Px(42), GridSize.Star(), GridSize.Px(96), GridSize.Px(96), GridSize.Px(80)];
        Element Cell(string text, int column, bool primary) =>
            TextBlock(text).FontSize(13).FontWeight(primary ? FontWeights.SemiBold : FontWeights.Normal)
                .Foreground(primary ? Theme.Ref("TextFillColorPrimaryBrush") : SecondaryText)
                .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center).Grid(column: column);
        var header = Grid(columns, [GridSize.Auto],
                TextBlock(S("Usage_Creators_Identity")).FontSize(12).Foreground(SecondaryText).Grid(column: 2),
                TextBlock(S("Usage_Metric_Tokens")).FontSize(12).Foreground(SecondaryText).HAlign(HorizontalAlignment.Right).Grid(column: 3),
                TextBlock(S("Usage_Metric_Cost")).FontSize(12).Foreground(SecondaryText).HAlign(HorizontalAlignment.Right).Grid(column: 4),
                TextBlock(S("Usage_Overview_Sessions")).FontSize(12).Foreground(SecondaryText).HAlign(HorizontalAlignment.Right).Grid(column: 5))
            .Padding(8, 0, 8, 6)
            .AccessibilityView(AccessibilityView.Raw)
            .WithKey("header");
        var rows = visible.Select(c =>
        {
            var name = UsageHeader.CreatorLabel(c.Key, c.Label, c.ActorType);
            var recorded = c.ActorType is "human" or "agent" or "system";
            var tokens = UsageUi.Tokens(c.Tokens, c.LowerBound);
            var cost = AnalysisCost(c.Cost, c.LowerBound);
            var type = S("Usage_Creators_Type_" + (recorded ? c.ActorType : "unknown"));
            var tone = PaletteIndex(c.Key);
            return (Element)Button(Grid(columns, [GridSize.Auto],
                        // Non-color selection cue that also survives high contrast, matching session rows.
                        Border(null).Width(3).CornerRadius(1.5).Background(c.Selected ? Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                            .VAlign(VerticalAlignment.Stretch).Grid(column: 0),
                        (recorded
                            ? Avatar(name, c.Key, 30)
                            : Border(TextBlock("?").AccessibilityHidden().FontSize(13).FontWeight(FontWeights.SemiBold).Foreground(SecondaryText)
                                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                                .Size(30, 30).CornerRadius(15).Background(Track).AccessibilityView(AccessibilityView.Raw))
                            .VAlign(VerticalAlignment.Center).Margin(6, 0, 0, 0).Grid(column: 1),
                        VStack(4,
                            HStack(6,
                                TextBlock(name).FontSize(13).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                                TextBlock(type).FontSize(11).Foreground(TertiaryText).VAlign(VerticalAlignment.Bottom)),
                            ShareBar(c.Weight, recorded ? Palette(tone) : Other, 4).MaxWidth(320).HAlign(HorizontalAlignment.Left))
                            .VAlign(VerticalAlignment.Center).Grid(column: 2),
                        Cell(tokens, 3, metric == UsageMetric.Tokens),
                        Cell(cost, 4, metric == UsageMetric.Cost),
                        Cell(Count(c.Sessions), 5, false)),
                    () => select(c.Key))
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Padding(8, 8)
                .Resources(r =>
                {
                    OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources(r);
                    if (c.Selected) r.Set("ButtonBackground", PaletteTint(tone));
                })
                .ToolTip(c.Selected ? S("Usage_Creators_ClearFilter") : S("Usage_Creators_Filter"))
                .AutomationName($"{name}, {type}: {tokens} {S("Usage_Metric_TokensLower")}, {cost}, {FN("Usage_Creators_SessionsFormat", c.Sessions)}")
                .ItemStatus(c.Selected ? S("Usage_Selected") : "")
                .AutomationId("UsageCreator_" + c.Key)
                .WithKey(c.Key);
        }).Prepend(header).ToList();
        if (creators.Count > UsageSections.CreatorPreviewCount)
        {
            rows.Add(SubtleButton(showAll ? S("Usage_Creators_ShowFewer") : F("Usage_Creators_ShowMoreFormat", creators.Count - UsageSections.CreatorPreviewCount),
                () => setShowAll(!showAll), "UsageCreatorsMore").WithKey("more"));
        }
        return Section(S("Usage_Creators_Title"), S("Usage_Creators_Subtitle"), null, VStack(2, rows.ToArray()), "UsageCreators");
    }
}
