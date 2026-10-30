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

/// <summary>Sessions list and the selected session's details (model mix, timeline, context, conversation).</summary>
internal static class UsageSessionsSection
{
    public const int PreviewRows = 10;
    public const double LogListHeight = 460;


    public static Element Render(UsagePageState state, UsageDashboard dashboard, UsagePageController controller, double width)
    {
        var list = Component<UsageSessionsList, UsageSessionsListProps>(new UsageSessionsListProps(state, dashboard.Sessions, controller));
        if (state.Detail is not { } detail)
            return list;
        var details = Details(state, detail, dashboard, controller).WithKey("details:" + detail.Key);
        return width >= ReactorUsagePage.WideLayoutBreakpoint
            ? Grid([GridSize.Star(1), GridSize.Star(1.25)], [GridSize.Auto],
                list.VAlign(VerticalAlignment.Top).Margin(0, 0, 8, 0).Grid(column: 0),
                details.VAlign(VerticalAlignment.Top).Margin(8, 0, 0, 0).Grid(column: 1))
            : VStack(SectionGap, list, details);
    }

    public static string SortLabel(UsageSort sort) => S("Usage_Sort_" + sort);

    // ── details ──

    private static Element Details(UsagePageState state, UsageDetailState detail, UsageDashboard dashboard, UsagePageController controller)
    {
        var filtered = state.Filters.SelectedDays.Count > 0 || state.Filters.SelectedHours.Count > 0;
        var overviewRow = state.Report?.Sessions.FirstOrDefault(s => s.Key == detail.Key)
            ?? state.Report?.Overview?.SelectedRows.FirstOrDefault(s => s.Key == detail.Key);
        // Day and hour filters narrow the overview row's totals, but the detail read covers the whole range. A
        // filtered view keeps the overview totals so they match the heading, with context from the detail read.
        var row = UsageSessionDetailProjection.SummaryRow(overviewRow, detail.Row.Value, filtered);
        var view = row is null ? null : UsageDashboardProjection.Row(row, true);
        var title = view?.Title ?? detail.Row.Value?.Label ?? detail.Key;

        var header = Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            (row?.EffectiveProvider is { } headerProvider
                    ? ProviderMark(headerProvider, 40)
                    : Avatar(row?.AgentId ?? title, row?.AgentId, 40))
                .VAlign(VerticalAlignment.Top).Margin(0, 0, 12, 0).Grid(column: 0),
            VStack(2,
                TextBlock(title + (filtered ? " " + S("Usage_Details_Filtered") : "")).FontSize(18).FontWeight(FontWeights.SemiBold)
                    .TextWrapping(TextWrapping.Wrap).HeadingLevel(AutomationHeadingLevel.Level2).AutomationId("UsageDetailTitle"),
                TextBlock(detail.Key).FontSize(11).Foreground(TertiaryText).IsTextSelectionEnabled().TextTrimming(TextTrimming.CharacterEllipsis))
                .VAlign(VerticalAlignment.Center).Grid(column: 1),
            HStack(2,
                IconButton("\uE8C8", S("Usage_Sessions_CopyKey"), () => ClipboardHelper.TryCopyText(detail.Key), "UsageDetailCopyKey"),
                IconButton("\uE711", S("Usage_Details_Close"), controller.CloseDetail, "UsageDetailClose"))
                .VAlign(VerticalAlignment.Top).Grid(column: 2));

        var sections = new List<Element> { header.WithKey("header") };
        if (row?.HistoricalInstanceCount is > 1 && row.Scope == "family")
            sections.Add(Note(F("Usage_Details_FamilyFormat", Count(row.HistoricalInstanceCount.Value))).WithKey("family"));
        if (detail.Row.Phase == UsageLoadPhase.Failed)
            sections.Add(Banner(S("Usage_Details_LoadFailedTitle"), F("Usage_Details_LoadFailedFormat", S("Usage_Details_Summary"), ReadError(detail.Row)),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "UsageDetailRowError").WithKey("row-error"));
        if (row is not null)
        {
            sections.Add(Summary(row, state.Metric).WithKey("summary"));
            sections.Add(ModelMix(row, state.Metric).WithKey("models"));
        }
        else if (filtered)
            sections.Add(EmptyText(S("Usage_Details_NoMatchingUsage"), "UsageDetailNoMatchingUsage").WithKey("no-matching-usage"));
        sections.Add(Timeline(detail, state.Metric, controller).WithKey("timeline"));
        if (row is not null)
            sections.Add(Context(row, detail.Row.IsLoading).WithKey("context"));
        sections.Add(Component<UsageConversation, UsageConversationProps>(new UsageConversationProps(detail.Logs))
            .WithKey("conversation:" + detail.Key));

        return Card(VStack(20, sections.ToArray()), "UsageSessionDetails")
            .AutomationName(F("Usage_Details_AccessibleFormat", title));
    }

    private static Element Summary(SessionUsageRow row, UsageMetric metric)
    {
        var usage = row.Usage;
        var totals = usage?.Totals ?? UsageTotals.Empty;
        var lower = totals.IsLowerBound;
        var counts = usage?.MessageCounts;
        var stats = new List<Element>
        {
            Stat(S("Usage_Metric_Tokens"), UsageUi.Tokens(totals.TotalTokens, lower),
                F("Usage_Details_TokenSplitFormat", UsageUi.Tokens(totals.Input, lower), UsageUi.Tokens(totals.Output, lower)), "UsageDetailTokens",
                glyph: GlyphTokens, palette: 0),
            Stat(S("Usage_Metric_Cost"), AnalysisCost(totals.TotalCost, lower),
                totals.MissingCostEntries > 0 ? FN("Usage_Details_MissingCostFormat", totals.MissingCostEntries) : null, "UsageDetailCost",
                glyph: GlyphCost, palette: 5),
            Stat(S("Usage_Overview_Messages"), Count(counts?.Total ?? 0),
                counts is null ? null : F("Usage_Overview_MessagesSubFormat", Count(counts.User), Count(counts.Assistant)), "UsageDetailMessages",
                glyph: GlyphMessages, palette: 2),
            Stat(S("Usage_Overview_ToolCalls"), Count(usage?.ToolUsage?.TotalCalls ?? counts?.ToolCalls ?? 0),
                usage?.ToolUsage is { } tools ? FN("Usage_Overview_ToolsUsedFormat", tools.UniqueTools) : null, "UsageDetailTools",
                glyph: GlyphTools, palette: 1),
            Stat(S("Usage_Overview_Errors"), Count(counts?.Errors ?? 0), null, "UsageDetailErrors",
                (counts?.Errors ?? 0) > 0 ? Critical : Success, glyph: GlyphErrors),
            Stat(S("Usage_Details_Duration"), usage?.DurationMs is { } ms ? UsageFormat.Duration(ms) : Dash,
                usage?.FirstActivity is { } first && usage.LastActivity is { } last
                    ? F("Usage_Details_ActivityFormat", first.ToLocalTime().ToString("g", Culture), last.ToLocalTime().ToString("g", Culture))
                    : null,
                "UsageDetailDuration", glyph: GlyphCalendar, palette: 4),
        };
        if (usage?.Latency is { } latency)
        {
            stats.Add(Stat(S("Usage_Details_Latency"), UsageFormat.Duration(latency.AvgMs),
                F("Usage_Details_LatencySubFormat", UsageFormat.Duration(latency.P95Ms)), "UsageDetailLatency", glyph: GlyphSpeed, palette: 3));
        }
        var chips = new List<Element>();
        if (row.AgentId is { } agent) chips.Add(Pill(F("Usage_Sessions_AgentFormat", agent), "UsageDetailAgent").WithKey("agent"));
        if (row.Channel is { } channel) chips.Add(Pill(channel, "UsageDetailChannel").WithKey("channel"));
        if (row.EffectiveProvider is { } provider) chips.Add(Pill(provider, "UsageDetailProvider").WithKey("provider"));
        if (row.EffectiveModel is { } model) chips.Add(Pill(model, "UsageDetailModel").WithKey("model"));
        if (row.CreatedActor is { } actor)
            chips.Add(Pill(F("Usage_Details_StartedByFormat", UsageHeader.CreatorLabel(row.CreatorKey ?? "", actor.Label, actor.Type)), "UsageDetailCreator").WithKey("creator"));
        return VStack(12,
            Flow(chips.Select(c => c.Margin(0, 0, 6, 6).WithKey(c.Key ?? "chip")).ToArray()),
            UsageSections.Columns(2, stats.Select((s, i) => s.WithKey("detail-stat:" + i)).ToList(), 16));
    }

    private static Element ModelMix(SessionUsageRow row, UsageMetric metric)
    {
        var mix = UsageSessionDetailProjection.ModelMix(row);
        var rows = mix.Select((m, i) => VStack(4,
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(m.Model ?? S("Usage_Unknown")).FontSize(13).TextTrimming(TextTrimming.CharacterEllipsis).Grid(column: 0),
                    TextBlock(Metric(metric, m.Tokens, m.Cost, m.LowerBound)).FontSize(13).FontWeight(FontWeights.SemiBold).Grid(column: 1)),
                m.Share is { } share ? ShareBar(share, Palette(PaletteIndex(m.Provider ?? m.Model)), 6) : null,
                TextBlock(string.Join(" · ", new[] { m.Provider, FN("Usage_Overview_MessagesCountFormat", m.Count),
                        m.Share is { } percent ? UsageFormat.Percent(percent, Culture, 0) : null }
                    .Where(p => !string.IsNullOrEmpty(p)))).FontSize(11).Foreground(TertiaryText))
            .AutomationName(string.Join(", ", new[] { $"{m.Model}: {Metric(metric, m.Tokens, m.Cost, m.LowerBound)}",
                m.Share is { } accessibleShare ? UsageFormat.Percent(accessibleShare, Culture, 0) : null }.Where(p => p is not null)))
            .WithKey($"{m.Provider}/{m.Model}")).ToArray();
        return VStack(10,
            SubHeading(S("Usage_Details_ModelMix")),
            rows.Length == 0 ? EmptyText(S("Usage_Overview_NoModelData"), "UsageDetailModelsEmpty") : VStack(10, rows));
    }

    private static Element SubHeading(string text) =>
        TextBlock(text).FontSize(14).FontWeight(FontWeights.SemiBold).HeadingLevel(AutomationHeadingLevel.Level3);

    private static Element Timeline(UsageDetailState detail, UsageMetric metric, UsagePageController controller)
    {
        var controls = HStack(8,
            SelectorBar([SelectorBarItem(S("Usage_Details_Cumulative")), SelectorBarItem(S("Usage_Details_PerTurn"))],
                    detail.TimelineMode == UsageTimelineMode.PerTurn ? 1 : 0,
                    index => controller.SetTimelineMode(index == 1 ? UsageTimelineMode.PerTurn : UsageTimelineMode.Cumulative))
                .AutomationName(S("Usage_Details_TimelineMode")).AutomationId("UsageTimelineMode"),
            detail.TimelineMode == UsageTimelineMode.PerTurn && metric == UsageMetric.Tokens
                ? ToggleButton(S("Usage_Daily_ByType"), detail.TimelineByType, controller.SetTimelineByType).AutomationId("UsageTimelineByType")
                : null);

        Element body;
        var series = detail.Timeseries;
        if (series.Value is null)
        {
            body = series.Phase switch
            {
                UsageLoadPhase.Loading => HStack(12, ProgressRing().Size(16, 16), Note(S("Usage_Details_LoadingTimeline"))),
                UsageLoadPhase.Failed => EmptyText(F("Usage_Details_LoadFailedFormat", S("Usage_Details_UsageOverTime"), ReadError(series)), "UsageTimelineError"),
                _ => Empty(),
            };
        }
        else if (series.Value.Points.Count == 0)
        {
            body = EmptyText(S("Usage_Details_NoTimeline"), "UsageTimelineEmpty");
        }
        else
        {
            var timeline = UsageSessionDetailProjection.Timeline(series.Value, metric, detail.TimelineMode, detail.TimelineByType);
            var typeSeries = detail.TimelineByType && detail.TimelineMode == UsageTimelineMode.PerTurn && metric == UsageMetric.Tokens
                ? new List<UsageSeries>
                {
                    new("output", UsageSeriesKind.Output, null, 0), new("input", UsageSeriesKind.Input, null, 1),
                    new("cacheWrite", UsageSeriesKind.CacheWrite, null, 2), new("cacheRead", UsageSeriesKind.CacheRead, null, 3),
                }
                : new List<UsageSeries> { new("total", UsageSeriesKind.Total, null, 0) };
            // Sessions spanning several days label points with their date so repeated clock times stay distinct.
            var multiDay = timeline.Bars.Count > 1
                && timeline.Bars[0].Timestamp.ToLocalTime().Date != timeline.Bars[^1].Timestamp.ToLocalTime().Date;
            string PointLabel(DateTimeOffset at) => multiDay
                ? UsageFormat.DayLabel(DateOnly.FromDateTime(at.ToLocalTime().Date), Culture) + " " + at.ToLocalTime().ToString("t", Culture)
                : at.ToLocalTime().ToString("t", Culture);
            var columns = timeline.Bars.Select((bar, i) => new UsageCharts.BarColumn(
                bar.Timestamp.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + i,
                bar.Total, bar.Segments, PointLabel(bar.Timestamp),
                F("Usage_Details_PointFormat", bar.Timestamp.ToLocalTime().ToString("g", Culture), UsageUi.Tokens(bar.Tokens), AnalysisCost(bar.Cost)),
                false, false, timeline.LowerBound)).ToList();
            body = VStack(8,
                timeline.LowerBound ? Banner(S("Usage_LowerBoundShort"), S("Usage_Details_Unavailable"),
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "UsageTimelineLowerBound") : null,
                UsageCharts.Columns(columns, typeSeries, timeline.Ceiling,
                    v => metric == UsageMetric.Cost ? UsageFormat.Cost(v, Culture) : UsageFormat.Tokens((long)v, Culture),
                    null, "UsageTimelineChart", 140),
                UsageCharts.Legend(typeSeries),
                Note(FN("Usage_Details_TurnsFormat", columns.Count)));
        }
        return VStack(10,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                SubHeading(S("Usage_Details_UsageOverTime")).VAlign(VerticalAlignment.Center).Grid(column: 0),
                controls.Grid(column: 1)),
            body);
    }

    private static Element Context(SessionUsageRow row, bool loading)
    {
        var context = UsageSessionDetailProjection.Context(row);
        if (context is null)
        {
            return VStack(10,
                SubHeading(S("Usage_Details_Context")),
                loading ? HStack(12, ProgressRing().Size(16, 16), Note(S("Usage_Details_LoadingContext")))
                        : EmptyText(S("Usage_Details_NoContext"), "UsageContextEmpty"));
        }
        ThemeRef Brush(UsageContextCategory category) => category switch
        {
            UsageContextCategory.System => Accent,
            UsageContextCategory.Skills => Success,
            UsageContextCategory.Tools => Caution,
            _ => CacheRead,
        };
        var description = context.InputShare is { } share
            ? F("Usage_Details_ContextOfInputFormat", UsageFormat.Percent(share, Culture, 0))
            : S("Usage_Details_BaseContextPerMessage");
        return VStack(10,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                SubHeading(S("Usage_Details_Context")).Grid(column: 0),
                TextBlock(F("Usage_Details_ApproxTokensFormat", UsageUi.Tokens(context.TotalTokens))).FontSize(12).Foreground(SecondaryText).Grid(column: 1)),
            Note(description),
            StackedBar(context.Groups.Select(g => (g.Share, Brush(g.Category))).ToList(), 12),
            VStack(4, context.Groups.Select(g =>
            {
                var label = S("Usage_Details_Context_" + g.Category);
                var value = F("Usage_Details_ApproxTokensFormat", UsageUi.Tokens(g.Tokens));
                Element head = LegendItem(label, Brush(g.Category), $"{value} ({UsageFormat.Percent(g.Share, Culture, 0)})");
                if (g.Entries.Count == 0)
                    return head.AutomationName($"{label}: {value}").WithKey(g.Category.ToString());
                var entries = VStack(2, g.Entries.Select(e => Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                            TextBlock(e.Name).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis).Grid(column: 0),
                            TextBlock(e.Chars is { } chars ? F("Usage_Details_ApproxTokensFormat", UsageUi.Tokens(UsageSessionDetailProjection.CharsToTokens(chars))) : S("Usage_Details_Unmeasured"))
                                .FontSize(12).Foreground(SecondaryText).Grid(column: 1))
                        .WithKey(e.Name)).ToArray());
                return Expander($"{label}: {value} ({g.Entries.Count})", entries)
                    .HAlign(HorizontalAlignment.Stretch)
                    .AutomationName($"{label}: {value}")
                    .AutomationId("UsageContext_" + g.Category)
                    .WithKey(g.Category.ToString());
            }).ToArray()));
    }
}

internal sealed record UsageSessionsListProps(UsagePageState State, UsageSessionsView Sessions, UsagePageController Controller);

/// <summary>Sessions card: All/Recently viewed, sort, preview/expand, paging and multi-selection.</summary>
internal sealed class UsageSessionsList : Component<UsageSessionsListProps>
{
    public override Element Render()
    {
        var (showAll, setShowAll) = UseState(false);
        var state = Props.State;
        var sessions = Props.Sessions;
        var controller = Props.Controller;
        var filters = state.Filters;
        var metric = state.Metric;

        var sortIndex = Array.IndexOf(new[] { UsageSort.Cost, UsageSort.Errors, UsageSort.Messages, UsageSort.Recent, UsageSort.Tokens }, filters.Sort);
        var sorts = new[] { UsageSort.Cost, UsageSort.Errors, UsageSort.Messages, UsageSort.Recent, UsageSort.Tokens };
        var clearSelection = filters.SelectedSessions.Count > 0
            ? SubtleButton(S("Usage_Sessions_ClearSelection"), controller.CloseDetail, "UsageSessionsClearSelection", "\uE711").WithKey("clear")
            : null;
        // Released Gateways cannot sort or limit to recent sessions; their rows are shown newest first.
        var toolbar = state.BasicGateway
            ? Flow(Note(S("Usage_Sessions_NewestFirst")).AutomationId("UsageSessionsNewestFirst").Margin(0, 0, 12, 4).WithKey("newest"), clearSelection)
            : Flow(
            SelectorBar([SelectorBarItem(S("Usage_Sessions_All")), SelectorBarItem(S("Usage_Sessions_Recent"))],
                    filters.RecentOnly ? 1 : 0, index => controller.SetRecentOnly(index == 1))
                .AutomationName(S("Usage_Sessions_View")).AutomationId("UsageSessionsView").Margin(0, 0, 12, 4).WithKey("view"),
            ComboBox(sorts.Select(UsageSessionsSection.SortLabel).ToArray(), Math.Max(0, sortIndex), index => controller.SetSort(sorts[Math.Clamp(index, 0, sorts.Length - 1)]))
                .MinWidth(130).AutomationName(S("Usage_Sessions_Sort")).AutomationId("UsageSessionsSort").Margin(0, 0, 4, 4).WithKey("sort"),
            IconButton(filters.SortDescending ? "\uE74B" : "\uE74A",
                    filters.SortDescending ? S("Usage_Sessions_Descending") : S("Usage_Sessions_Ascending"),
                    controller.ToggleSortDirection, "UsageSessionsDirection").Margin(0, 0, 12, 4).WithKey("direction"),
            clearSelection);

        var rows = sessions.Rows;
        var visible = showAll ? rows : rows.Take(UsageSessionsSection.PreviewRows).ToList();
        var count = rows.Count;
        var avg = count == 0 ? 0 : metric == UsageMetric.Cost
            ? sessions.TableCost / Math.Max(1, sessions.TableSessions)
            : sessions.TableTokens / (double)Math.Max(1, sessions.TableSessions);
        var subtitle = F("Usage_Sessions_SubtitleFormat",
            Count(sessions.Total),
            metric == UsageMetric.Cost ? AnalysisCost(avg) : UsageUi.Tokens((long)avg),
            Count(sessions.TableErrors));

        var body = new List<Element> { toolbar.WithKey("toolbar") };
        if (sessions.SelectedRows.Count > 1)
        {
            body.Add(Note(F("Usage_Sessions_SelectedFormat", Count(sessions.SelectedRows.Count))).WithKey("selected-caption"));
            body.Add(VStack(4, sessions.SelectedRows.Select(r => Row(r, metric, controller, "UsageSelectedSession_").WithKey("selected:" + r.Key)).ToArray()).WithKey("selected"));
            body.Add(Border(null).Height(1).Background(CardStroke).WithKey("selected-divider"));
        }
        if (visible.Count == 0)
        {
            body.Add(EmptyText(filters.RecentOnly ? S("Usage_Sessions_NoRecent") : S("Usage_Sessions_NoneInRange"), "UsageSessionsEmpty").WithKey("empty"));
        }
        else
        {
            body.Add(VStack(4, visible.Select(r => Row(r, metric, controller, "UsageSession_").WithKey(r.Key)).ToArray()).AutomationId("UsageSessionsRows").WithKey("rows"));
            if (rows.Count > UsageSessionsSection.PreviewRows)
            {
                body.Add(SubtleButton(showAll ? F("Usage_Sessions_ShowTopFormat", UsageSessionsSection.PreviewRows) : F("Usage_Sessions_ShowAllFormat", rows.Count),
                    () => setShowAll(!showAll), "UsageSessionsShowAll").WithKey("show-all"));
            }
        }
        if (sessions.Total > sessions.Limit && !filters.RecentOnly)
        {
            var from = sessions.Offset + 1;
            var to = Math.Min(sessions.Offset + sessions.Limit, sessions.Total);
            body.Add(HStack(8,
                    IconButton("\uE76B", S("Usage_Sessions_PreviousPage"), () => controller.SetPage(sessions.Offset - sessions.Limit), "UsageSessionsPrevious", sessions.Offset > 0),
                    TextBlock(F("Usage_Sessions_PageFormat", Count(from), Count(to), Count(sessions.Total))).FontSize(12).Foreground(SecondaryText)
                        .VAlign(VerticalAlignment.Center).AutomationId("UsageSessionsPage"),
                    IconButton("\uE76C", S("Usage_Sessions_NextPage"), () => controller.SetPage(sessions.Offset + sessions.Limit), "UsageSessionsNext", to < sessions.Total))
                .HAlign(HorizontalAlignment.Right).WithKey("paging"));
        }
        return Section(S("Usage_Sessions_Title"), subtitle, null, VStack(10, body.ToArray()), "UsageSessions");
    }

    private static Element Row(UsageSessionRowView row, UsageMetric metric, UsagePageController controller, string idPrefix)
    {
        var value = row.Computing ? S("Usage_Sessions_Computing") : Metric(metric, row.Tokens, row.Cost, row.LowerBound);
        var stats = string.Join(" · ", new[]
        {
            FN("Usage_Overview_MessagesCountFormat", row.Messages),
            FN("Usage_Sessions_ToolsFormat", row.ToolCalls),
            row.Errors > 0 ? FN("Usage_Overview_ErrorsCountFormat", row.Errors) : null,
            row.DurationMs is { } ms ? UsageFormat.Duration(ms) : null,
        }.Where(s => s is not null));
        var tags = new List<Element>();
        if (row.AgentId is { } agent) tags.Add(Pill(agent, idPrefix + "Agent_" + row.Key).WithKey("agent"));
        if (row.Channel is { } channel) tags.Add(Pill(channel, idPrefix + "Channel_" + row.Key).WithKey("channel"));
        if (row.Model is { } model)
        {
            tags.Add(HStack(4, ProviderMark(row.Provider, 18).VAlign(VerticalAlignment.Center),
                    Pill(row.Provider is { } p ? $"{p}/{model}" : model, idPrefix + "Model_" + row.Key))
                .WithKey("model"));
        }
        if (row.InstanceCount is { } instances) tags.Add(Pill(FN("Usage_Sessions_InstancesFormat", instances), idPrefix + "Instances_" + row.Key).WithKey("instances"));
        var accessible = $"{row.Title}: {value}. {stats}";
        return Button(Grid([GridSize.Px(3), GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    Border(null).Width(3).CornerRadius(1.5).Background(row.Selected ? Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                        .VAlign(VerticalAlignment.Stretch).Grid(column: 0),
                    Avatar(row.AgentId ?? row.Title, row.AgentId, 32).VAlign(VerticalAlignment.Top).Margin(8, 2, 0, 0).Grid(column: 1),
                    VStack(4,
                        TextBlock(row.Title).FontSize(13).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                        Flow(tags.Select(t => t.Margin(0, 0, 4, 2).WithKey(t.Key ?? "tag")).ToArray()),
                        TextBlock(stats).FontSize(11).Foreground(TertiaryText))
                        .Margin(10, 0, 8, 0).Grid(column: 2),
                    VStack(2,
                        TextBlock(value).FontSize(14).FontWeight(FontWeights.SemiBold).HAlign(HorizontalAlignment.Right),
                        row.LastActivity is { } last
                            ? TextBlock(last.ToLocalTime().ToString("g", Culture)).FontSize(11).Foreground(TertiaryText).HAlign(HorizontalAlignment.Right)
                            : null)
                        .VAlign(VerticalAlignment.Center).Grid(column: 3)),
                () => controller.SelectSession(row.Key, row.AgentId, ControlPressed()))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(6, 8)
            .Resources(r =>
            {
                OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources(r);
                if (row.Selected) r.Set("ButtonBackground", Theme.Ref("SubtleFillColorSecondaryBrush"));
            })
            .ToolTip(S("Usage_Sessions_SelectHint"))
            .AutomationName(accessible)
            .ItemStatus(row.Selected ? S("Usage_Selected") : "")
            .AutomationId(idPrefix + row.Key)
            .WithKey(row.Key);
    }
}

internal sealed record UsageConversationProps(UsageRead<IReadOnlyList<UsageLogView>> Logs);

/// <summary>
/// Conversation log with role/tool/search filters. The filter is view state kept in this component, so typing re-renders
/// only the conversation; the parent keys it by session so each session starts unfiltered. Content is display-only;
/// it is never logged or exported.
/// </summary>
internal sealed class UsageConversation : Component<UsageConversationProps>
{
    private static readonly string?[] Roles = [null, "user", "assistant", "tool", "toolResult"];
    private static readonly UsageLogFilter NoFilter = new();

    public override Element Render()
    {
        var (filter, setFilter) = UseState(NoFilter);
        var logs = Props.Logs;
        var heading = TextBlock(S("Usage_Details_Conversation")).FontSize(14).FontWeight(FontWeights.SemiBold)
            .HeadingLevel(AutomationHeadingLevel.Level3);
        if (logs.Value is null)
        {
            return VStack(10, heading, logs.Phase switch
            {
                UsageLoadPhase.Loading => HStack(12, ProgressRing().Size(16, 16), Note(S("Usage_Details_LoadingConversation"))),
                UsageLoadPhase.Failed => EmptyText(F("Usage_Details_LoadFailedFormat", S("Usage_Details_Conversation"), ReadError(logs)), "UsageConversationError"),
                _ => Empty(),
            });
        }

        var all = logs.Value;
        var tools = all.SelectMany(l => l.Tools.Select(t => t.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var visible = UsageSessionDetailProjection.FilterLogs(all, filter);
        var roleIndex = Math.Max(0, Array.IndexOf(Roles, filter.Role));
        var toolIndex = filter.Tool is null ? 0 : Array.IndexOf(tools, filter.Tool) + 1;

        var filters = Flow(
            ComboBox([S("Usage_Details_Role_All"), S("Usage_Details_Role_user"), S("Usage_Details_Role_assistant"), S("Usage_Details_Role_tool"), S("Usage_Details_Role_toolResult")],
                    roleIndex, index => setFilter(filter with { Role = Roles[Math.Clamp(index, 0, Roles.Length - 1)] }))
                .MinWidth(130).AutomationName(S("Usage_Details_RoleFilter")).AutomationId("UsageConversationRole").Margin(0, 0, 8, 6).WithKey("role"),
            ComboBox(new[] { S("Usage_Details_AllTools") }.Concat(tools).ToArray(), Math.Max(0, toolIndex),
                    index => setFilter(filter with { Tool = index <= 0 ? null : tools[index - 1] }))
                .MinWidth(130).IsEnabled(tools.Length > 0).AutomationName(S("Usage_Details_ToolFilter")).AutomationId("UsageConversationTool").Margin(0, 0, 8, 6).WithKey("tool"),
            CheckBox(filter.HasToolsOnly, value => setFilter(filter with { HasToolsOnly = value }), S("Usage_Details_HasTools"))
                .AutomationId("UsageConversationHasTools").Margin(0, 0, 8, 6).WithKey("has-tools"),
            TextBox(filter.Search, value => setFilter(filter with { Search = value }), S("Usage_Details_SearchPlaceholder"))
                .MinWidth(200).AutomationName(S("Usage_Details_Search")).AutomationId("UsageConversationSearch").Margin(0, 0, 8, 6).WithKey("search"),
            filter.IsActive ? SubtleButton(S("Usage_ClearFilters"), () => setFilter(NoFilter), "UsageConversationClear").WithKey("clear") : null);

        Element list = visible.Count == 0
            ? EmptyText(all.Count == 0 ? S("Usage_Details_NoMessages") : S("Usage_Details_NoMessagesMatch"), "UsageConversationEmpty")
            : ItemsView(visible.Select((log, index) => (Log: log, Index: index)).ToList(),
                    entry => entry.Index + ":" + entry.Log.Timestamp.ToUnixTimeMilliseconds() + ":" + entry.Log.Role,
                    (entry, _) => LogItem(entry.Log))
                .Height(Math.Min(UsageSessionsSection.LogListHeight, 72 + visible.Count * 90))
                .AutomationName(S("Usage_Details_Conversation"))
                .AutomationId("UsageConversationList");

        return VStack(10,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                heading.Grid(column: 0),
                TextBlock(F("Usage_Details_MessageCountFormat", Count(visible.Count), Count(all.Count))).FontSize(12).Foreground(SecondaryText)
                    .AutomationId("UsageConversationCount").Grid(column: 1)),
            filters,
            list);
    }

    // ItemsView requires an ItemContainer root. The log is read-only, so container hover and selection visuals stay
    // transparent, as in the chat timeline.
    private static Element LogItem(UsageLogView log)
    {
        var (entry, accessible) = Entry(log);
        return ItemContainer(entry)
            .Resources(resources => resources
                .Set("ItemContainerPointerOverBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerPointerOverBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerPressedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerPressedBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerSelectedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerSelectedInnerBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(accessible);
    }

    private static (Element Entry, string Accessible) Entry(UsageLogView log)
    {
        var role = S("Usage_Details_Role_" + (log.Role is "user" or "assistant" or "tool" or "toolResult" ? log.Role : "assistant"));
        var meta = string.Join(" · ", new[]
        {
            log.Timestamp.ToLocalTime().ToString("g", Culture),
            log.Tokens is { } t ? UsageUi.Tokens(t) : null,
            log.Cost is { } c ? AnalysisCost(c) : null,
        }.Where(s => s is not null));
        var entry = Border(VStack(4,
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(role).FontSize(12).FontWeight(FontWeights.SemiBold)
                        .Foreground(log.Role == "user" ? AccentText : SecondaryText).Grid(column: 0),
                    TextBlock(meta).FontSize(11).Foreground(TertiaryText).Grid(column: 1)),
                TextBlock(log.Content).FontSize(13).TextWrapping(TextWrapping.Wrap).MaxLines(6).IsTextSelectionEnabled(),
                log.Tools.Count == 0 ? null : Flow(log.Tools.Select(t =>
                    Pill(t.Count > 1 ? $"{t.Name} ×{t.Count}" : t.Name, "UsageLogTool_" + t.Name).Margin(0, 0, 4, 0).WithKey(t.Name)).ToArray())))
            .Padding(10, 8)
            .Margin(0, 0, 0, 6)
            .CornerRadius(6)
            .Background(log.Role == "user" ? Theme.Ref("SubtleFillColorSecondaryBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
            .WithBorder(CardStroke, 1);
        return (entry, $"{role}, {meta}: {log.Content}");
    }
}
