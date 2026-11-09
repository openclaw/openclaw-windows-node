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

internal static class UsageSections
{
    public static Element Columns(int columns, IReadOnlyList<Element> items, double gap = PageResponsiveLayout.GapGroup)
    {
        columns = Math.Max(1, columns);
        return Grid(Enumerable.Repeat(GridSize.Star(), columns).ToArray(),
            Enumerable.Repeat(GridSize.Auto, Math.Max(1, (items.Count + columns - 1) / columns)).ToArray(),
            items.Select((item, index) => item.Grid(row: index / columns, column: index % columns)
                .WithKey(item.Key ?? "cell:" + index)).ToArray()) with { ColumnSpacing = gap, RowSpacing = gap };
    }

    public static Element Daily(UsagePageState state, UsageDashboard dashboard, UsagePageProps props, double width) =>
        Component<UsageDailySection, UsageDailyProps>(new(state, dashboard, props.Controller, width));

    public static Element Contributors(UsagePageState state, UsageDashboard dashboard, UsagePageProps props, UsagePageLayout layout) =>
        Component<UsageContributorsSection, UsageContributorsProps>(new(state, dashboard, props, layout));

    public static Element Providers(UsagePageState state, UsagePageController controller) =>
        Component<UsageProvidersSection, UsageProvidersProps>(new(state, controller));

    public static Element Activity(UsageDashboard dashboard, double width)
    {
        var a = dashboard.Activity;
        Element[] stats =
        [
            InlineStat(a.Messages.HasData ? Count(a.Messages.Total) : Dash, S("Usage_Overview_Messages").ToLower(Culture),
                a.Messages.HasData ? F("Usage_Activity_MessageSplitFormat", Count(a.Messages.User), Count(a.Messages.Assistant)) : null, "UsageStatMessages"),
            InlineStat(a.ToolCallsKnown ? Count(a.ToolCalls) : Dash, S("Usage_Overview_ToolCalls").ToLower(Culture), null, "UsageStatToolCalls"),
            InlineStat(a.Messages.HasData ? Count(a.Messages.Errors) : Dash,
                a.Messages.HasData ? F(a.Messages.Errors == 1 ? "Usage_Overview_ErrorsCountFormatOne" : "Usage_Overview_ErrorsCountFormat", "").Trim()
                    : S("Usage_Overview_Errors").ToLower(Culture),
                a.ErrorRate is { } rate ? UsageFormat.Percent(rate, Culture, rate > 0 && rate < 0.01 ? 1 : 0) : null, "UsageStatErrors"),
            InlineStat(a.CacheHitRate is { } hit ? UsageFormat.Percent(hit, Culture, 0) : Dash,
                S("Usage_Overview_CacheHitRate").ToLower(Culture), null, "UsageStatCacheHit"),
        ];
        return Columns(UsagePageLayout.ActivityColumns(width), stats)
            .AutomationId("UsageActivity");
    }

    private static Element InlineStat(string value, string label, string? sub, string id) =>
        Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                TextBlock(value).FontSize(12).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                    .AutomationId(id + "Value").Margin(0, 0, 4, 0).Grid(column: 0),
                TextBlock(label + (sub is null ? "" : $" ({sub})")).FontSize(12).Foreground(SecondaryText)
                    .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(label + (sub is null ? "" : $" ({sub})")).Grid(column: 1))
            .AutomationId(id);

    internal static Element Fact(string label, string value, string? sub, string id) =>
        VStack(3,
            TextBlock(value).FontSize(22).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(value).AutomationId(id + "Value"),
            TextBlock(label).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.WrapWholeWords),
            sub is null ? null : TextBlock(sub).FontSize(11).Foreground(SecondaryText).TextWrapping(TextWrapping.WrapWholeWords))
            .AutomationName($"{label}: {value}. {sub}").AutomationId(id);
}

internal sealed record UsageDailyProps(UsagePageState State, UsageDashboard Dashboard, UsagePageController Controller, double Width);

internal sealed class UsageDailySection : Component<UsageDailyProps>
{
    public override Element Render()
    {
        var (table, setTable) = UseState(false);
        var state = Props.State;
        var chart = Props.Dashboard.Chart;
        var totals = state.Report!.Totals;
        var summary = Props.Dashboard.Summary;
        var compact = PageResponsiveLayout.TierFor(Props.Width) == PageWidthTier.Narrow;
        double? average = chart.Days.Count > 0 && chart.CoversRange && !summary.Partial && !chart.Days.Any(day => day.Pending || day.LowerBound || day.MissingPrice)
            ? chart.Days.Average(day => day.Value) : null;
        string AxisLabel(double value) => state.Metric == UsageMetric.Cost
            ? UsageFormat.Cost(value, Culture, value < 1 ? 2 : 0) : UsageFormat.Tokens((long)value, Culture);
        var metric = SelectorBar([SelectorBarItem(S("Usage_Summary_EstimatedCost")), SelectorBarItem(S("Usage_Metric_Tokens"))],
                state.Metric == UsageMetric.Cost ? 0 : 1,
                index => Props.Controller.SetMetric(index == 0 ? UsageMetric.Cost : UsageMetric.Tokens))
            .AutomationName(S("Usage_Filter_Show")).AutomationId("UsageMetric")
            .HAlign(compact ? HorizontalAlignment.Left : HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center).Margin(0).WithKey("metric");
        var itemPadding = ThemeResource.Thickness("SelectorBarItemPadding");
        // Rebalance native item padding without changing spacing between labels, restoring it on Wide.
        metric = metric.Set(selector =>
        {
            foreach (var item in selector.Items)
                item.Padding = compact
                    ? new Thickness(0, itemPadding.Top, itemPadding.Left + itemPadding.Right, itemPadding.Bottom)
                    : itemPadding;
        });
        var view = SelectorBar([SelectorBarItem(S("Usage_Chart")), SelectorBarItem(S("Usage_Table"))], table ? 1 : 0,
                index => setTable(index == 1))
            .AutomationName(S("Usage_ChartView")).AutomationId("UsageChartView")
            .VAlign(VerticalAlignment.Center).Margin(0).WithKey("view");
        var selectors = Grid(compact ? [GridSize.Star(), GridSize.Auto] : [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                compact ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                TextBlock(S("Usage_PerDay")).FontSize(16).FontWeight(FontWeights.SemiBold)
                    .VAlign(VerticalAlignment.Center).Margin(0).AutomationId("UsagePerDayTitle").Grid(column: 0).WithKey("title"),
                !compact ? metric.Grid(column: 1) : null,
                view.Grid(column: compact ? 1 : 2).WithKey("view-group"),
                compact ? Grid([GridSize.Auto], [GridSize.Auto],
                    metric.Grid(column: 0))
                    .Margin(0, PageResponsiveLayout.GapInner, 0, 0).Grid(row: 1, columnSpan: 2)
                    .AutomationId("UsageDailyMetricRow").WithKey("metric-row") : null);
        var today = DateOnly.FromDateTime(state.Filters.TimeZone == UsageTimeZoneMode.Utc ? DateTime.UtcNow : DateTime.Now);
        var columns = chart.Days.Select(day => new UsageCharts.BarColumn(
            day.Iso, day.Value, day.Date == today ? S("Usage_Preset_Today") : UsageFormat.DayLabel(day.Date, Culture),
            day.Pending ? F("Usage_Daily_PendingFormat", UsageFormat.FullDate(day.Date, Culture))
                : F("Usage_Daily_BarFormat", UsageFormat.FullDate(day.Date, Culture),
                    day.TokensKnown ? Tokens(day.Tokens, day.LowerBound) : Dash, DayCost(day)),
            day.Pending, day.LowerBound, day.Date == today)).ToList();
        Element body = table ? DailyTable(chart) : UsageCharts.Columns(columns, chart.Ceiling,
            AxisLabel, "UsageDailyChart", average: average,
            barWidth: Math.Min(36, Math.Max(1, (Props.Width - UsageCharts.AxisWidth - CardPadding * 2) / Math.Max(1, columns.Count) * 0.6)));
        var inside = Props.Width - CardPadding * 2;
        return Card(VStack(PageResponsiveLayout.GapStack, selectors.AutomationId("UsageDailyControls"), body,
            Border(null).Height(1).Background(CardStroke),
            UsageSections.Columns(UsagePageLayout.DailyFactColumns(inside), [
                UsageSections.Fact(S("Usage_Metric_Tokens"), ReportedTokens(totals, "totalTokens", totals.TotalTokens),
                    F("Usage_Details_TokenSplitFormat", ReportedTokens(totals, "input", totals.Input), ReportedTokens(totals, "output", totals.Output)), "UsageSummaryTokens"),
                UsageSections.Fact(F("Usage_CacheWrittenFormat", ReportedTokens(totals, "cacheWrite", totals.CacheWrite)),
                    ReportedTokens(totals, "cacheRead", totals.CacheRead), null, "UsageSummaryCache"),
                UsageSections.Fact(S("Usage_Overview_Sessions"), summary.SessionsKnown ? AtLeast(Count(summary.Sessions), summary.SessionsAtLeast) : Dash, null, "UsageSummarySessions")])
                .AutomationId("UsageDailyFacts"),
            Border(null).Height(1).Background(CardStroke),
            UsageSections.Activity(Props.Dashboard, inside)), "UsageDaily");
    }

    private static Element DailyTable(UsageDailyChart chart)
    {
        string Value(UsageChartDay day) => day.Pending && !day.TokensKnown && !day.CostKnown
            ? S("Usage_Daily_NotLoaded")
            : chart.Metric == UsageMetric.Cost ? DayCost(day)
            : day.TokensKnown ? Tokens(day.Tokens, day.LowerBound) : Dash;
        var rows = chart.Days.Reverse().Select(day =>
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(UsageFormat.DayLabel(day.Date, Culture)).FontSize(12).TextWrapping(TextWrapping.NoWrap).Grid(column: 0),
                TextBlock(Value(day))
                    .FontSize(12).TextWrapping(TextWrapping.NoWrap).AutomationId("UsageDailyRowValue_" + day.Iso)
                    .Grid(column: 1)).Padding(0, 5)
                .AutomationName(UsageFormat.FullDate(day.Date, Culture) + ": " + Value(day))
                .AutomationId("UsageDailyRow_" + day.Iso).WithKey(day.Iso)).ToArray();
        var header = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            TextBlock(S("Usage_Table_Date")).FontWeight(FontWeights.SemiBold).Grid(column: 0),
            TextBlock(S(chart.Metric == UsageMetric.Cost ? "Usage_Summary_EstimatedCost" : "Usage_Metric_Tokens"))
                .FontWeight(FontWeights.SemiBold).Grid(column: 1)).Padding(0, 5).AutomationId("UsageDailyTableHeader");
        return VStack(2, new[] { header }.Concat(rows).ToArray()).AutomationName(S("Usage_ChartView")).AutomationId("UsageDailyTable");
    }

    private static string DayCost(UsageChartDay day) =>
        !day.CostKnown ? Dash : day.MissingPrice && day.Cost == 0
            ? S("Usage_Overview_Unpriced") : AnalysisCost(day.Cost, day.LowerBound || day.MissingPrice);
}

internal sealed record UsageProvidersProps(UsagePageState State, UsagePageController Controller);

internal sealed class UsageProvidersSection : Component<UsageProvidersProps>
{
    public override Element Render()
    {
        var (all, setAll) = UseState(false);
        var providers = Props.State.Providers;
        var now = DateTimeOffset.Now;
        var attention = UsageInsights.Attention(Props.State, now).ToHashSet();
        var items = providers.Value?.Providers.OrderByDescending(provider => provider.Windows.Count > 0
                ? provider.Windows.Max(window => window.UsedPercent) : -1).ToList() ?? [];
        var rows = items.Take(all ? items.Count : 4).Select(provider => Provider(provider,
            attention.Contains(provider) ? UsageProviderAttention.HighestWindow(provider, now) : null)
            .WithKey(provider.Provider)).ToList();
        if (providers.Phase == UsageLoadPhase.Failed)
            rows.Insert(0, Note(S("Usage_Providers_Unavailable")).WithKey("failed"));
        if (Props.State.Availability == UsageAvailability.Disconnected && providers.Value?.UpdatedAt is { } at)
            rows.Insert(0, Note(F("Usage_LastKnownFormat", at.ToLocalTime().ToString("t", Culture))).WithKey("offline"));
        if (providers.Value?.Refreshing == true) rows.Insert(0, Note(S("Usage_Providers_Refreshing")).WithKey("refreshing"));
        if (items.Count == 0)
            rows.Add(EmptyText(S(providers.IsLoading ? "Usage_Providers_Loading" : "Usage_Providers_Empty"), "UsageProvidersEmpty").WithKey("empty"));
        if (items.Count > 4)
            rows.Add(SubtleButton(all ? S("Usage_ShowLess") : F("Usage_ShowMoreFormat", Count(items.Count - 4)),
                () => setAll(!all), "UsageProvidersMore").WithKey("more"));
        return Section(S("Usage_Providers_Title"), null, null,
            VStack(PageResponsiveLayout.GapInner, rows.ToArray()), "UsageProviders");
    }

    private Element Provider(ProviderUsageSnapshot provider, ProviderUsageWindow? attentionWindow)
    {
        var windows = provider.Windows.OrderByDescending(window => window.UsedPercent).ToList();
        var highest = attentionWindow ?? windows.FirstOrDefault();
        var percent = highest is null ? Dash : UsageFormat.Percent(highest.UsedPercent / 100, Culture, 0);
        var accessible = new List<string> { provider.DisplayName };
        var info = new List<Element>
        {
            TextBlock(provider.DisplayName).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.WrapWholeWords).WithKey("name"),
        };
        if (provider.Plan is { Length: > 0 } plan)
        {
            accessible.Add(plan);
            info.Add(Note(plan).WithKey("plan"));
        }
        foreach (var (window, index) in windows.Select((window, index) => (window, index)))
        {
            var reset = window.ResetAt is { } at ? F("Usage_ResetAtFormat", FriendlyDate(at, olderTime: true)) : "";
            var text = string.Join(" · ", new[] { window.Label, window.GroupLabel,
                UsageFormat.Percent(window.UsedPercent / 100, Culture, 0), reset }.Where(value => !string.IsNullOrEmpty(value)));
            accessible.Add(text);
            info.Add(Note(text).WithKey("window:" + index));
        }
        foreach (var (billing, index) in provider.Billing.Select((billing, index) => (billing, index)))
        {
            var label = billing.Label ?? S("Usage_Providers_Billing_" + billing.Type);
            var amount = Money(billing.Amount, billing.Unit);
            var text = label + ": " + (billing.Limit is { } limit
                ? F("Usage_Providers_BudgetFormat", amount, Money(limit, billing.Unit)) : amount);
            accessible.Add(text);
            info.Add(Note(text).WithKey("billing:" + index));
        }
        if (provider.Summary is { Length: > 0 } summary)
        {
            accessible.Add(summary);
            info.Add(Note(summary).WithKey("summary"));
        }
        if (provider.Error is { Length: > 0 } error)
        {
            accessible.Add(error);
            info.Add(TextBlock(error).Foreground(Critical).TextWrapping(TextWrapping.WrapWholeWords).WithKey("error"));
        }
        else if (windows.Count == 0 && provider.Billing.Count == 0) info.Add(Note(S("Usage_Providers_NoQuota")).WithKey("noquota"));
        if (attentionWindow is not null)
        {
            var reason = F("Usage_AllowanceWarningFormat", provider.DisplayName, percent);
            accessible.Add(S("Usage_NearLimit") + ". " + reason);
            info[0] = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(provider.DisplayName).FontWeight(FontWeights.SemiBold)
                    .AutomationId("UsageProviderName_" + provider.Provider)
                    .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(provider.DisplayName).VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, PageResponsiveLayout.GapInner, 0).Grid(column: 0),
                PageHero.AttentionBadge("UsageNearLimit_" + provider.Provider,
                        S("Usage_NearLimit"), reason, "\uE7BA", PageHeroTone.Caution,
                        !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    .Grid(column: 1))
                .HAlign(HorizontalAlignment.Left)
                .WithKey("name");
            info.Add(Button(TextBlock(S("Usage_SeeUsing")).TextWrapping(TextWrapping.WrapWholeWords),
                    () => Props.Controller.InspectProvider(provider.Provider))
                .IsEnabled(Props.State.Dashboard is not null)
                .AutomationName(F("Usage_SeeUsingProviderFormat", provider.DisplayName))
                .AutomationId("UsageInspectProvider_" + provider.Provider).HAlign(HorizontalAlignment.Left).WithKey("inspect"));
        }
        Element ring = highest is null
            ? Border(TextBlock(Dash).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .Size(56, 56).CornerRadius(28).WithBorder(CardStroke, 2)
            : Grid([GridSize.Auto], [GridSize.Auto],
                ProgressRing(Math.Clamp(highest.UsedPercent, 0, 100)).Size(56, 56)
                    .Foreground(attentionWindow is not null ? Caution : Accent)
                    .AutomationName(F("Usage_AllowanceWarningFormat", provider.DisplayName, percent)),
                TextBlock(percent).FontSize(12).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center).AccessibilityHidden());
        return Border(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                ring.VAlign(VerticalAlignment.Center).Margin(0, 0, PageResponsiveLayout.GapStack, 0).Grid(column: 0),
                VStack(4, info.ToArray()).Grid(column: 1)))
            .Padding(PageResponsiveLayout.GapGroup).CornerRadius(CardRadius).Background(CardFill).WithBorder(CardStroke, 1)
            .AutomationName(string.Join(", ", accessible)).AutomationId("UsageProvider_" + provider.Provider);
    }

    private static string Money(double amount, string unit) =>
        unit == "USD" ? Cost(amount) : F("Usage_Providers_CreditsFormat", amount.ToString("N2", Culture), unit);
}

internal sealed record UsageContributorsProps(UsagePageState State, UsageDashboard Dashboard, UsagePageProps Actions, UsagePageLayout Layout);

internal sealed class UsageContributorsSection : Component<UsageContributorsProps>
{
    private static readonly UsageContributorKind[] Kinds = [UsageContributorKind.Agent, UsageContributorKind.Model, UsageContributorKind.Provider];

    public override Element Render()
    {
        var (kind, setKind) = UseState(UsageContributorKind.Agent);
        var initialExpanded = UseMemo(() => new HashSet<string>(StringComparer.Ordinal), []);
        var (expanded, setExpanded) = UseState(initialExpanded);
        var expandedRef = UseRef(expanded);
        expandedRef.Current = expanded;
        void Expand(string id, bool value)
        {
            var next = new HashSet<string>(expandedRef.Current, StringComparer.Ordinal);
            if (value) next.Add(id); else next.Remove(id);
            expandedRef.Current = next;
            setExpanded(next);
        }
        var sectionRef = UseRef<FrameworkElement?>(null);
        UseEffect(() =>
        {
            if (Props.State.FocusProvider is { } provider)
            {
                setKind(UsageContributorKind.Provider);
                setExpanded(new HashSet<string>(expanded, StringComparer.Ordinal) { provider });
                sectionRef.Current?.StartBringIntoView();
            }
        }, Props.State.FocusProviderRequest);
        var items = Props.Dashboard.Contributors[kind];
        var metric = Props.State.Metric;
        var ordered = items.OrderByDescending(item => metric == UsageMetric.Cost ? item.Cost : item.Tokens).ToList();
        var top = ordered.Take(5).ToList();
        var tail = ordered.Skip(5).ToList();
        var total = metric == UsageMetric.Cost ? ordered.Sum(item => item.Cost) : ordered.Sum(item => (double)item.Tokens);
        var knownShares = UsageInsights.ContributorSharesKnown(Props.Dashboard.Summary, ordered, metric);
        double Share(UsageContributor item) => total > 0 ? (metric == UsageMetric.Cost ? item.Cost : item.Tokens) / total : 0;
        var parts = top.Select(item => (Share(item), Palette(PaletteIndex(item.Label)))).ToList();
        if (tail.Count > 0) parts.Add((tail.Sum(Share), Other));
        Element ContributorRow(UsageContributor item)
        {
            var id = item.Provider is { } provider && kind == UsageContributorKind.Model ? provider + "/" + item.Label : item.Label;
            var open = expanded.Contains(id);
            var loaded = UsageInsights.LoadedSessions(Props.Dashboard.Sessions, kind, item);
            var entries = loaded.Take(3).Select(row =>
                (Element)Button(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock(row.Title).FontSize(12).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                            .ToolTip(row.Title).Margin(0, 0, PageResponsiveLayout.GapInner, 0).Grid(column: 0),
                        TextBlock((metric == UsageMetric.Cost ? row.CostKnown : row.TokensKnown)
                            ? metric == UsageMetric.Cost && row.CostIncomplete && row.Cost == 0 ? S("Usage_Overview_Unpriced")
                                : Metric(metric, row.Tokens, row.Cost, row.LowerBound || metric == UsageMetric.Cost && row.CostIncomplete)
                            : Dash).FontSize(12).TextWrapping(TextWrapping.NoWrap).Grid(column: 1)),
                    () => Props.Actions.Controller.SelectSession(row.Key, row.AgentId))
                    .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
                    .SubtleButton().AutomationName(row.Title).AutomationId("UsageContributorSession_" + row.Key)
                    .WithKey(row.Key)).ToList();
            if (entries.Count == 0) entries.Add(Note(S("Usage_NoLoadedSessions")).WithKey("empty"));
            if (kind == UsageContributorKind.Agent && !Props.State.ServerFiltersUnavailable)
                entries.Add(SubtleButton(F("Usage_ShowOnlyFormat", item.Label),
                    () => Props.Actions.Controller.SetQuery("agent:" + item.Label), "UsageContributorFilter_" + item.Label).WithKey("filter"));
            var value = (!(metric == UsageMetric.Cost ? item.CostKnown : item.TokensKnown) ? Dash
                : item.Unpriced && metric == UsageMetric.Cost ? S("Usage_Overview_Unpriced")
                : Metric(metric, item.Tokens, item.Cost, item.LowerBound || metric == UsageMetric.Cost && item.CostIncomplete))
                + (knownShares ? " · " + UsageFormat.Percent(Share(item), Culture, 0) : "");
            var narrow = Props.Layout.Narrow;
            var header = Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto],
                narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                Border(null).Size(10, 10).CornerRadius(2).Background(Palette(PaletteIndex(item.Label)))
                    .Margin(0, 0, 10, 0).VAlign(VerticalAlignment.Center).Grid(column: 0),
                TextBlock(item.Label).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(item.Label).Grid(column: 1, columnSpan: narrow ? 2 : 1),
                TextBlock(value).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(value)
                    .Margin(narrow ? 0 : PageResponsiveLayout.GapGroup, narrow ? 4 : 0, 0, 0)
                    .Grid(row: narrow ? 1 : 0, column: narrow ? 1 : 2));
            return (Element)Expander(item.Label + " · " + value,
                    VStack(4, new[] { Note(S("Usage_LoadedSessions")) }.Concat(entries).ToArray()).Padding(12),
                    open, value => Expand(id, value))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch).HAlign(HorizontalAlignment.Stretch)
                .HeaderTemplate(header)
                .AutomationName(item.Label + ": " + value)
                .AutomationId("UsageContributor_" + kind + "_" + item.Label)
                .WithKey(id);
        }
        var rows = top.Select(ContributorRow).ToList();
        if (kind == UsageContributorKind.Provider && Props.State.FocusProvider is { } focus && !items.Any(item => item.Label == focus))
            rows.Insert(0, Note(F("Usage_NoProviderUsageFormat", focus)).WithKey("missing-provider"));
        if (tail.Count > 0)
            rows.Add(Expander(F("Usage_OtherFormat", Count(tail.Count)), VStack(4,
                    tail.Select(ContributorRow).ToArray()),
                expanded.Contains("$other") || tail.Any(item => expanded.Contains(kind == UsageContributorKind.Model ? item.Provider + "/" + item.Label : item.Label)),
                value => Expand("$other", value))
                .WithKey("other"));
        if (rows.Count == 0) rows.Add(Note(S("Usage_Overview_NoAgentData")).WithKey("empty"));
        var highlight = Highlights(Props.Dashboard, metric, Props.Actions);
        return Section(S("Usage_Contributors_Title"), null,
            SelectorBar(Kinds.Select(value => SelectorBarItem(S("Usage_Contributors_" + value))).ToArray(),
                    Array.IndexOf(Kinds, kind), index => { if (index >= 0 && index < Kinds.Length) { setKind(Kinds[index]); setExpanded(new HashSet<string>(StringComparer.Ordinal)); } })
                .AutomationName(S("Usage_Contributors_Label")).AutomationId("UsageContributorsKind"),
            VStack(16,
                knownShares ? StackedBar(parts).AutomationId("UsageContributorShares") : null,
                Pair(Props.Layout.TwoColumns,
                    VStack(6, rows.ToArray()), highlight, "UsageContributorLayout")),
            "UsageContributors", stackActions: Props.Layout.Narrow).OnMountAdd(control => sectionRef.Current = control);
    }

    private static Element Highlights(UsageDashboard dashboard, UsageMetric metric, UsagePageProps actions)
    {
        var entries = new List<Element> { TextBlock(S("Usage_Highlights")).FontWeight(FontWeights.SemiBold).WithKey("title") };
        var costly = UsageInsights.CostliestLoadedSession(dashboard);
        if (costly is not null)
            entries.Add(VStack(4,
                Note(S("Usage_CostliestLoaded")),
                Button(TextBlock(costly.Title).TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2)
                        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(costly.Title), () => actions.Controller.SelectSession(costly.Key, costly.AgentId))
                    .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Left)
                    .SubtleButton().AutomationName(costly.Title),
                Note(AnalysisCost(costly.Cost, costly.LowerBound))).WithKey("costliest"));
        var peak = dashboard.Chart.Days.Where(day => !day.Pending).OrderByDescending(day => day.Value).FirstOrDefault();
        if (peak is not null && peak.Value > 0 && dashboard.Chart.CoversRange && !dashboard.Summary.Partial
            && dashboard.Chart.Days.All(day => !day.Pending && !day.LowerBound && !day.MissingPrice))
            entries.Add(Note(F("Usage_BusiestDayFormat", UsageFormat.DayLabel(peak.Date, Culture),
                Metric(metric, peak.Tokens, peak.Cost, peak.LowerBound))).WithKey("peak"));
        if (dashboard.Summary.MissingCostEntries > 0)
            entries.Add(Note(FN("Usage_MissingCostNoteFormat", dashboard.Summary.MissingCostEntries)).WithKey("missing"));
        return VStack(12, entries.ToArray()).AutomationId("UsageHighlights");
    }
}
