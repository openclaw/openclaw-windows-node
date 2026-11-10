using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClaw.Shared.Usage;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

internal static class UsageSessionsSection
{
    public const int PreviewRows = 10;

    public static Element Render(UsagePageState state, UsageDashboard dashboard, UsagePageProps props, UsagePageLayout layout) =>
        Component<UsageSessionsList, UsageSessionsListProps>(new(state, dashboard.Sessions, props, layout));

    public static string SortLabel(UsageSort sort) => S("Usage_Sort_" + sort);

    internal static Element Details(UsagePageState state, UsageDetailState detail, UsagePageLayout layout, string suffix = "")
    {
        var row = detail.Row.Value ?? state.Report?.Sessions.FirstOrDefault(row => row.Key == detail.Key);
        var usage = row?.Usage;
        var totals = usage?.Totals;
        var counts = usage?.MessageCounts;
        var title = row?.Label ?? detail.Key;
        var cost = totals is null || !totals.HasValue("totalCost") ? Dash
            : totals.TotalCost == 0 && totals.MissingCostEntries > 0 ? S("Usage_Overview_Unpriced")
            : AnalysisCost(totals.TotalCost, totals.IsLowerBound || totals.MissingCostEntries > 0);
        var share = UsageSessionDetailProjection.CostShare(state.Report, row) is { } costShare
                ? F("Usage_Detail_ShareFormat", UsageFormat.Percent(costShare, Culture, 0), UsageHeader.RangeWords(state.Filters))
                : null;
        var padding = ThemeResource.Thickness("ExpanderContentPadding");
        var inner = layout.ContentWidth - padding.Left - padding.Right;
        var valueWidth = layout.TwoColumns ? (inner - PageResponsiveLayout.GapStack) / 2 : inner;
        var values = new List<Element>
        {
            DetailValue(S("Usage_Facet_model"), row?.EffectiveModel ?? Dash, "UsageDetailModel" + suffix, valueWidth),
            DetailValue(S("Usage_Detail_InputOutput"), totals is null ? Dash : $"{ReportedTokens(totals, "input", totals.Input)} · {ReportedTokens(totals, "output", totals.Output)}", "UsageDetailTokens" + suffix, valueWidth),
            DetailValue(S("Usage_Detail_CacheSplit"), totals is null ? Dash : $"{ReportedTokens(totals, "cacheRead", totals.CacheRead)} · {ReportedTokens(totals, "cacheWrite", totals.CacheWrite)}", "UsageDetailCache" + suffix, valueWidth),
            DetailValue(S("Usage_Overview_Messages"), counts is null || !counts.HasData ? Dash : Count(counts.Total), "UsageDetailMessages" + suffix, valueWidth),
            DetailValue(S("Usage_Details_Duration"), usage?.DurationMs is { } ms ? UsageFormat.Duration(ms) : Dash, "UsageDetailDuration" + suffix, valueWidth),
            DetailValue(S("Usage_Overview_ToolCalls"), usage?.ToolUsage is { HasData: true } tools
                ? F("Usage_Detail_ToolsFormat", Count(tools.TotalCalls), Count(tools.UniqueTools))
                : counts is null || !counts.HasData ? Dash : Count(counts.ToolCalls), "UsageDetailTools" + suffix, valueWidth),
            DetailValue(S("Usage_Overview_Errors"), counts is null || !counts.HasData ? Dash : Count(counts.Errors), "UsageDetailErrors" + suffix, valueWidth),
        };
        var facts = Pair(layout.TwoColumns,
            VStack(8,
                TextBlock(S("Usage_Summary_EstimatedCost")).FontWeight(FontWeights.SemiBold),
                TextBlock(cost).FontSize(28).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                    .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(cost).AutomationId("UsageDetailCostValue" + suffix),
                share is null ? null : Note(share).AutomationId("UsageDetailCostShare" + suffix),
                UsageInsights.IsAutomation(detail.Key) ? Note(S("Usage_FromAutomation")) : null,
                totals?.MissingCostEntries > 0 ? Note(FN("Usage_Details_MissingCostFormat", totals.MissingCostEntries)) : null),
            VStack(8,
                TextBlock(S("Usage_Metric_Tokens")).FontWeight(FontWeights.SemiBold),
                VStack(8, values.ToArray())), "UsageDetailLayout" + suffix);
        return VStack(12,
                detail.Row.Phase == UsageLoadPhase.Failed
                    ? Banner(S("Usage_Details_LoadFailedTitle"),
                        F("Usage_Details_LoadFailedFormat", S("Usage_Details_Summary"), ReadError(detail.Row)),
                        Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "UsageDetailRowError") : null,
                detail.Row.IsLoading && row is null ? Note(S("Usage_Loading")) : facts)
            .AutomationName(F("Usage_Details_AccessibleFormat", title)).AutomationId("UsageSessionDetails" + suffix);
    }

    private static Element DetailValue(string label, string value, string id, double width) =>
        Grid(width < UsagePageLayout.FactStackWidth ? [GridSize.Star()] : [GridSize.Px(140), GridSize.Star()],
            width < UsagePageLayout.FactStackWidth ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
            TextBlock(label).FontSize(12).Foreground(SecondaryText).Grid(column: 0),
            TextBlock(value).FontSize(12).TextWrapping(TextWrapping.WrapWholeWords).TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(2).ToolTip(value).Margin(0, width < UsagePageLayout.FactStackWidth ? 4 : 0, 0, 0)
                .AutomationId(id + "Value").Grid(row: width < UsagePageLayout.FactStackWidth ? 1 : 0,
                    column: width < UsagePageLayout.FactStackWidth ? 0 : 1));

    internal static Element Row(UsageSessionRowView row, UsagePageProps props, UsagePageLayout layout)
    {
        var raw = props.Controller.State.Report?.Sessions.FirstOrDefault(r => r.Key == row.Key);
        var unpriced = raw?.Usage?.Totals is { TotalCost: 0, MissingCostEntries: > 0 };
        var cost = row.Computing ? S("Usage_Sessions_Computing")
            : raw?.Usage is null || !raw.Usage.Totals.HasValue("totalCost") ? Dash : unpriced ? S("Usage_Overview_Unpriced")
            : AnalysisCost(row.Cost, row.LowerBound || raw.Usage.Totals.MissingCostEntries > 0);
        var subtitle = string.Join(" · ", new[]
        {
            row.AgentId, row.Model,
            row.LastActivity is { } last ? FriendlyDate(last) : null,
            row.Errors > 0 ? FN("Usage_Overview_ErrorsCountFormat", row.Errors) : null,
        }.Where(value => !string.IsNullOrEmpty(value)));
        var meta = HStack(PageResponsiveLayout.GapInner,
            TextBlock(cost).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(cost).AutomationId("UsageRowCost_" + row.Key),
            TextBlock(raw?.Usage is null ? Dash : ReportedTokens(raw.Usage.Totals, "totalTokens", row.Tokens)).FontSize(12).Foreground(SecondaryText)
                .TextWrapping(TextWrapping.NoWrap)).AutomationId("UsageRowMeta_" + row.Key);
        var copy = VStack(4,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(row.Title).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                    .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(row.Title).VAlign(VerticalAlignment.Center)
                    .AutomationId("UsageRowTitle_" + row.Key).Grid(column: 0),
                layout.Tier == PageWidthTier.Wide && UsageInsights.IsAutomation(row.Key)
                    ? Pill(S("Usage_Automation"), "UsageAutomation_" + row.Key).VAlign(VerticalAlignment.Center)
                        .Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 1)
                    : null).MinHeight(28),
            TextBlock(subtitle).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(subtitle).AutomationId("UsageRowSubtitle_" + row.Key),
            layout.Narrow ? meta.HAlign(HorizontalAlignment.Left) : null).AutomationId("UsageRowCopy_" + row.Key);
        return Grid(layout.Narrow ? [GridSize.Auto, GridSize.Star()] : [GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                Avatar(row.AgentId ?? row.Title, row.AgentId, 32).VAlign(VerticalAlignment.Center).Grid(column: 0),
                copy
                    .Margin(12, 0, 12, 0).Grid(column: 1),
                layout.Narrow ? null : VStack(2, TextBlock(cost).FontWeight(FontWeights.SemiBold)
                        .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(160).ToolTip(cost),
                    TextBlock(raw?.Usage is null ? Dash : ReportedTokens(raw.Usage.Totals, "totalTokens", row.Tokens)).FontSize(12).Foreground(SecondaryText))
                    .VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Right).AutomationId("UsageRowMeta_" + row.Key).Grid(column: 2))
            .HAlign(HorizontalAlignment.Stretch)
            .Padding(0, PageResponsiveLayout.GapInner)
            .AutomationId("UsageSession_" + row.Key);
    }

    internal static Element ChatButton(string key, string title, UsagePageProps props) =>
        IconButton("\uE8BD", F("Usage_OpenChatForFormat", title), () => props.OpenConversation(key),
                "UsageChat_" + key, props.Controller.State.Availability == UsageAvailability.Connected)
            .ToolTip(S("Usage_OpenChat"));
}

internal sealed record UsageSessionsListProps(UsagePageState State, UsageSessionsView Sessions, UsagePageProps Props, UsagePageLayout Layout);

internal sealed class UsageSessionsList : Component<UsageSessionsListProps>
{
    private static readonly UsageSort[] Sorts = [UsageSort.Cost, UsageSort.Tokens, UsageSort.Recent, UsageSort.Errors, UsageSort.Messages];

    public override Element Render()
    {
        var (shown, setShown) = UseState(UsageSessionsSection.PreviewRows);
        var state = Props.State;
        var sessions = Props.Sessions;
        var controller = Props.Props.Controller;
        UseEffect(() => setShown(UsageSessionsSection.PreviewRows), state.Filters.Start, state.Filters.End, state.Filters.Query);
        var rows = sessions.Rows;
        var visible = UsageInsights.VisibleSessions(sessions, shown, state.Detail?.Key);
        var body = new List<Element>();
        if (!sessions.ServerPaged)
        {
            body.Add(Note(S("Usage_Sessions_NewestFirst")).AutomationId("UsageSessionsNewestFirst").WithKey("order"));
            if (sessions.Truncated)
                body.Add(Note(F(state.Dashboard!.Summary.Partial || state.Dashboard.Summary.LowerBound
                        ? "Usage_Sessions_PartialLoadedFormat" : "Usage_Sessions_TruncatedFormat", Count(rows.Count), Count(sessions.Total)))
                    .AutomationId("UsageSessionsTruncated").WithKey("truncated"));
            else if (sessions.MayHaveMore)
                body.Add(Note(F("Usage_Sessions_LimitReachedFormat", Count(rows.Count)))
                    .AutomationId("UsageSessionsLimitReached").WithKey("limit"));
        }
        else
        {
            body.Add(HStack(PageResponsiveLayout.GapInner,
                    ComboBox(Sorts.Select(UsageSessionsSection.SortLabel).ToArray(), Math.Max(0, Array.IndexOf(Sorts, state.Filters.Sort)),
                        index => controller.SetSort(Sorts[Math.Clamp(index, 0, Sorts.Length - 1)]))
                        .AutomationName(S("Usage_Sessions_Sort")).AutomationId("UsageSessionsSort"),
                    IconButton(state.Filters.SortDescending ? "\uE74B" : "\uE74A", S("Usage_Sessions_Sort"),
                        controller.ToggleSortDirection, "UsageSessionsDirection"))
                .HAlign(Props.Layout.Narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right).WithKey("sort"));
        }
        foreach (var row in visible)
        {
            var reveal = !sessions.ServerPaged && row.Key == state.Detail?.Key
                && !rows.Take(shown).Any(candidate => candidate.Key == row.Key);
            body.Add(Component<UsageSessionCard, UsageSessionCardProps>(new(row, state, Props.Props, Props.Layout, reveal))
                .WithKey("row:" + row.Key));
        }

        if (rows.Count == 0) body.Add(EmptyText(S(state.Report?.SessionsKnown == true && sessions.Total == 0
            ? "Usage_Sessions_NoneInRange" : "Usage_Daily_NotLoaded"), "UsageSessionsEmpty").WithKey("empty"));
        if (!sessions.ServerPaged && rows.Count > visible.Count)
            body.Add(Button(TextBlock(S("Usage_ShowMoreLoaded")).TextWrapping(TextWrapping.NoWrap), () => setShown(shown + UsageSessionsSection.PreviewRows))
                .AutomationName(S("Usage_ShowMoreLoaded")).AutomationId("UsageSessionsShowAll").WithKey("more"));
        if (sessions.ServerPaged && sessions.Total > sessions.Limit)
        {
            body.Add(HStack(8,
                TextBlock(F("Usage_Sessions_PageFormat", Count(sessions.Offset + 1),
                    Count(Math.Min(sessions.Offset + rows.Count, sessions.Total)), Count(sessions.Total))).TextWrapping(TextWrapping.NoWrap).AutomationId("UsageSessionsPage"),
                IconButton("\uE76B", S("Usage_Sessions_PreviousPage"), () => controller.SetPage(sessions.Offset - sessions.Limit),
                    "UsageSessionsPrevious", sessions.Offset > 0),
                IconButton("\uE76C", S("Usage_Sessions_NextPage"), () => controller.SetPage(sessions.Offset + sessions.Limit),
                    "UsageSessionsNext", sessions.Offset + sessions.Limit < sessions.Total)).WithKey("paging"));
        }
        return VStack(12,
            HStack(8, TextBlock(S("Usage_Sessions_Title")).FontSize(20).FontWeight(FontWeights.SemiBold),
                Pill(Count(rows.Count), "UsageSessionsCount").HelpText(S("Usage_LoadedSessions"))),
            Note(F("Usage_Sessions_ShownLoadedFormat", Count(visible.Count), Count(rows.Count)))
                .AutomationId("UsageSessionsShownLoaded"),
            VStack(PageResponsiveLayout.GapInner, body.ToArray())).AutomationId("UsageSessions");
    }
}

internal sealed record UsageSessionCardProps(UsageSessionRowView Row, UsagePageState State, UsagePageProps Actions,
    UsagePageLayout Layout, bool Reveal);

/// <summary>Independent native expansion using loaded summaries, with only the active detail read in the controller.</summary>
internal sealed class UsageSessionCard : Component<UsageSessionCardProps>
{
    private Action? _cancelReveal;

    public override Element Render()
    {
        var (open, setOpen) = UseState(false);
        var row = Props.Row;
        UseEffect((Func<Action>)(() => () => _cancelReveal?.Invoke()), row.Key);
        var selected = Props.State.Detail?.Key == row.Key;
        UseEffect(() => { if (selected) setOpen(true); }, selected);
        void Expand(bool value)
        {
            setOpen(value);
            var activeKey = Props.Actions.Controller.State.Detail?.Key;
            if (value && activeKey != row.Key) Props.Actions.Controller.SelectSession(row.Key, row.AgentId);
            else if (!value && activeKey == row.Key) Props.Actions.Controller.CloseDetail();
        }
        var detail = selected ? Props.State.Detail!
            : new UsageDetailState(row.Key, row.AgentId, UsageRead<SessionUsageRow>.Idle);
        var expander = Expander(row.Title,
                UsageSessionsSection.Details(Props.State, detail, Props.Layout, selected ? "" : "_" + row.Key),
                open, Expand)
            .HeaderTemplate(UsageSessionsSection.Row(row, Props.Actions, Props.Layout))
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .ItemStatus(S(open ? "Usage_Selected" : "Usage_Sessions_SelectHint"))
            .AutomationName(row.Title).AutomationId("UsageSessionExpander_" + row.Key);
        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            expander.Grid(column: 0),
            UsageSessionsSection.ChatButton(row.Key, row.Title, Props.Actions)
                .VAlign(open ? VerticalAlignment.Top : VerticalAlignment.Center)
                .Margin(PageResponsiveLayout.GapInner, open ? CardPadding : 0, 0, 0).Grid(column: 1))
            .AutomationId("UsageSessionCard_" + row.Key)
            .OnMountAdd(control => AttachReveal((Microsoft.UI.Xaml.Controls.Grid)control, row.Key));
    }

    private void AttachReveal(Microsoft.UI.Xaml.Controls.Grid control, string key)
    {
        _cancelReveal?.Invoke();
        if (!Props.Reveal) return;
        void Cancel()
        {
            control.LayoutUpdated -= OnLayout;
            control.Unloaded -= OnUnloaded;
            _cancelReveal = null;
        }
        void OnUnloaded(object sender, RoutedEventArgs args) => Cancel();
        void OnLayout(object? sender, object args)
        {
            if (!Props.Reveal || Props.Actions.Controller.State.Detail?.Key != key)
            {
                Cancel();
                return;
            }
            if (!control.IsLoaded || control.ActualWidth <= 0 ||
                !control.Children.OfType<Microsoft.UI.Xaml.Controls.Expander>()
                    .Any(expander => expander.IsExpanded && expander.ActualHeight > 0))
                return;
            Cancel();
            control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        }
        _cancelReveal = Cancel;
        control.LayoutUpdated += OnLayout;
        control.Unloaded += OnUnloaded;
    }
}
