using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

/// <summary><paramref name="Job"/> is null for the agent-wide Run history view.</summary>
internal sealed record AutomationRunsProps(AutomationsHost Host, AutomationsSnapshot State, CronJob? Job);

internal sealed record TranscriptState(string RunKey, bool Loading, string? Error, IReadOnlyList<CronTranscriptMessage> Messages, string? NextCursor);

/// <summary>Filterable run history with delivery outcomes and inline run transcripts.</summary>
internal sealed class AutomationRuns : Component<AutomationRunsProps>
{
    private static readonly string[] Statuses = ["ok", "error", "skipped"];
    private static readonly string[] DeliveryStatuses = ["delivered", "not-delivered", "unknown", "not-requested"];

    public override Element Render()
    {
        var (host, state, job) = Props;
        var t = host.Text;
        var store = host.Store;
        var (search, setSearch) = UseState(state.RunsQuery.Query);
        var (transcript, setTranscript) = UseState<TranscriptState?>(null, threadSafe: true);
        var (expanded, setExpanded) = UseState<string?>(null);
        // Newest transcript request; replies to older ones (or to a hidden transcript) are dropped.
        var transcriptRequest = UseRef(0);
        var reduceMotion = UseReducedMotion();
        var query = state.RunsQuery;
        var scopeMatches = job is null ? !query.IsJobScope : query.JobId == job.Id;

        UseEffect((Func<Action>)(() =>
        {
            if (!scopeMatches)
                store.ShowRuns(new CronRunsQuery { JobId = job?.Id });
            return static () => { };
        }), job?.Id ?? "all");

        async Task LoadTranscriptAsync(CronRunEntry run, string? cursor)
        {
            var client = host.Client();
            if (client is null) return;
            var request = ++transcriptRequest.Current;
            var previous = transcript is { } current && current.RunKey == run.Key ? current : null;
            setTranscript(new TranscriptState(run.Key, true, null, previous?.Messages ?? Array.Empty<CronTranscriptMessage>(), previous?.NextCursor));
            try
            {
                var page = await client.GetCronRunTranscriptAsync(run, cursor);
                if (request != transcriptRequest.Current) return;
                // Cursor pages are older messages and go before what is already shown.
                var messages = cursor is null ? page.Messages : page.Messages.Concat(previous?.Messages ?? Array.Empty<CronTranscriptMessage>()).ToList();
                var next = page.NextCursor is { } c && c != cursor ? c : null;
                setTranscript(new TranscriptState(run.Key, false, null, messages, next));
            }
            catch (Exception ex)
            {
                if (request != transcriptRequest.Current) return;
                setTranscript(new TranscriptState(run.Key, false, ex.Message, previous?.Messages ?? Array.Empty<CronTranscriptMessage>(), previous?.NextCursor));
            }
        }

        void ToggleTranscript(CronRunEntry run)
        {
            if (transcript?.RunKey == run.Key)
            {
                transcriptRequest.Current++;
                setTranscript(null);
                return;
            }
            _ = LoadTranscriptAsync(run, null);
        }

        void OnSearch(string text)
        {
            setSearch(text);
            store.SetRunsFilter(query with { Query = text });
        }

        IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string value, bool on) =>
            on ? list.Append(value).Distinct().ToList() : list.Where(v => v != value).ToList();

        var statusMenu = DropDownButton(query.Statuses.Count == 0 ? t["Runs_AllStatuses"] : t.Format("Runs_StatusCount", query.Statuses.Count),
                MenuItems(Statuses.Select(s => ToggleMenuItem(t.RunStatus(s), query.Statuses.Contains(s),
                    on => store.SetRunsFilter(query with { Statuses = Toggle(query.Statuses, s, on) }))).ToArray<Microsoft.UI.Reactor.Core.MenuFlyoutItemBase>()))
            .AutomationId("AutomationRunsStatusFilter");
        var deliveryMenu = DropDownButton(query.DeliveryStatuses.Count == 0 ? t["Runs_AllDelivery"] : t.Format("Runs_DeliveryCount", query.DeliveryStatuses.Count),
                MenuItems(DeliveryStatuses.Select(s => ToggleMenuItem(t.DeliveryStatus(s), query.DeliveryStatuses.Contains(s),
                    on => store.SetRunsFilter(query with { DeliveryStatuses = Toggle(query.DeliveryStatuses, s, on) }))).ToArray<Microsoft.UI.Reactor.Core.MenuFlyoutItemBase>()))
            .AutomationId("AutomationRunsDeliveryFilter");
        var sortMenu = DropDownButton(query.OldestFirst ? t["Runs_OldestFirst"] : t["Runs_NewestFirst"], MenuItems(
                RadioMenuItem(t["Runs_NewestFirst"], "runSort", !query.OldestFirst, () => store.SetRunsFilter(query with { OldestFirst = false })),
                RadioMenuItem(t["Runs_OldestFirst"], "runSort", query.OldestFirst, () => store.SetRunsFilter(query with { OldestFirst = true }))))
            .AutomationId("AutomationRunsSort");

        var filters = VStack(Gap,
            job is null ? VStack(4, SectionTitle(t["RunHistory_Title"]), Secondary(t.Format("RunHistory_Subtitle", state.AgentId), 13),
                scopeMatches ? ScoreLine(t, state.Runs) : Empty()) : Empty(),
            (AutoSuggestBox(search, OnSearch, OnSearch) with
            {
                PlaceholderText = t["Runs_SearchPlaceholder"],
                QueryIcon = FontIcon(GlyphSearch),
            }).AutomationName(t["Runs_Search"]).AutomationId("AutomationRunsSearch"),
            HStack(4,
                statusMenu,
                deliveryMenu,
                sortMenu,
                query.HasFilters
                    ? Button(t["Runs_Clear"], () =>
                    {
                        setSearch("");
                        store.SetRunsFilter(query with { Query = "", Statuses = Array.Empty<string>(), DeliveryStatuses = Array.Empty<string>() });
                    }).AutomationName(t["Runs_Clear"]).AutomationId("AutomationRunsClear")
                    : Empty()));

        var rows = new List<Element>();
        if (!scopeMatches || (state.RunsLoading && state.Runs.Count == 0))
        {
            rows.Add(HStack(Gap, ProgressRing().Size(16, 16), Secondary(t["Runs_Loading"], 13)).AutomationId("AutomationRunsLoading"));
        }
        else if (state.RunsError is { } error)
        {
            rows.Add((InfoBar(t["Runs_ErrorTitle"], error) with
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = false,
                ActionButtonContent = t["Retry"],
                OnActionButtonClick = () => _ = store.LoadRunsAsync(),
            }).AutomationId("AutomationRunsError"));
        }
        else if (state.Runs.Count == 0)
        {
            rows.Add(job is { HasTrigger: true }
                ? Hero(GlyphBranch, t["Runs_EmptyConditionTitle"], ConditionHint(t, job), "AutomationRunsEmpty")
                : Hero(GlyphHistory, query.HasFilters ? t["Runs_NoMatch"] : t["Runs_EmptyTitle"], t["Runs_EmptyHint"], "AutomationRunsEmpty"));
        }
        else
        {
            for (var i = 0; i < state.Runs.Count; i++)
            {
                var run = state.Runs[i];
                var last = i == state.Runs.Count - 1 && !state.RunsHasMore;
                rows.Add(RenderRun(host, run, job is null, expanded == run.Key,
                    () => setExpanded(expanded == run.Key ? null : run.Key),
                    () => ToggleTranscript(run),
                    transcript?.RunKey == run.Key ? transcript : null,
                    cursor => _ = LoadTranscriptAsync(run, cursor),
                    first: i == 0,
                    last: last).Enter(reduceMotion).WithKey(run.Key));
            }
            if (state.RunsHasMore)
            {
                rows.Add(Button(state.RunsLoadingMore ? t["Loading"] : t["Runs_LoadMore"], () => _ = store.LoadRunsAsync(append: true)).AutomationName(t["Runs_LoadMore"])
                    .IsEnabled(!state.RunsLoadingMore)
                    .HAlign(HorizontalAlignment.Stretch)
                    .AutomationId("AutomationRunsLoadMore"));
            }
            rows.Add(Secondary(t.Format("Runs_Count", state.Runs.Count, state.RunsTotal)).AutomationId("AutomationRunsCount"));
        }

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                filters.Padding(PagePadding, job is null ? 0 : 4, PagePadding, 12).MaxWidth(ReadingWidth + 2 * PagePadding).HAlign(HorizontalAlignment.Left).Grid(row: 0),
                ScrollView(VStack(Gap, rows.ToArray()).Stagger(StaggerStep).Padding(PagePadding, 0, PagePadding, PagePadding).MaxWidth(ReadingWidth + 2 * PagePadding).HAlign(HorizontalAlignment.Left))
                    .AutomationId("AutomationRunsList")
                    .Grid(row: 1))
            .AutomationId("AutomationRuns");
    }

    private static string ConditionHint(AutomationsText t, CronJob job) => job.State.TriggerEvalCount switch
    {
        null or 0 => t["Runs_ConditionUnchecked"],
        1 => t["Runs_ConditionCheckedOnce"],
        var count => t.Format("Runs_ConditionChecked", count),
    };

    /// <summary>Success rate of the loaded runs, with average duration.</summary>
    private static Element ScoreLine(AutomationsText t, IReadOnlyList<CronRunEntry> runs)
    {
        var finished = runs.Where(r => r.Status is "ok" or "error").ToArray();
        if (finished.Length == 0)
            return Empty();
        var ok = finished.Count(r => r.Status == "ok");
        var perfect = ok == finished.Length;
        var durations = runs.Where(r => r.DurationMs is not null).Select(r => r.DurationMs!.Value).ToArray();
        var average = durations.Length == 0 ? "—" : t.Duration((long)durations.Average());
        return HStack(Gap,
                Pill(t.Format("Runs_SuccessRate", (int)Math.Round(100.0 * ok / finished.Length)),
                    perfect ? Theme.SystemSuccess : ok * 2 < finished.Length ? Theme.SystemCritical : Theme.SystemCaution,
                    perfect ? Theme.SystemSuccessBackground : ok * 2 < finished.Length ? Theme.SystemCriticalBackground : Theme.SystemCautionBackground,
                    perfect ? GlyphHeart : GlyphOk),
                Secondary(t.Format("Runs_SummaryDetail", ok, finished.Length, average)).VAlign(VerticalAlignment.Center))
            .Margin(0, 6, 0, 0)
            .AutomationId("AutomationRunsScore");
    }

    private static Element RenderRun(
        AutomationsHost host,
        CronRunEntry run,
        bool showJob,
        bool expanded,
        Action toggleExpanded,
        Action toggleTranscript,
        TranscriptState? transcript,
        Action<string> loadOlder,
        bool first,
        bool last)
    {
        var t = host.Text;
        var (brush, glyph) = RunTone(run.Status);
        var isError = run.Status == "error";
        var display = CronRunHistoryDisplay.ExtractText(run.Raw, isError);
        var text = string.IsNullOrWhiteSpace(display.PreviewText) ? t["Runs_NoSummary"] : display.PreviewText;
        var full = expanded && display.HasExpandableFullText ? display.FullText! : text;

        var headline = HStack(Gap,
            Glyph(glyph, 14, brush),
            TextBlock(t.RunStatus(run.Status)).FontWeight(FontWeights.SemiBold).Foreground(brush),
            showJob ? TextBlock(run.JobName ?? run.JobId).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis) : Empty(),
            Secondary(t.DateTime(run.EffectiveRunAtMs), 12).VAlign(VerticalAlignment.Center).ToolTip(t.Relative(run.EffectiveRunAtMs)));

        var chips = new List<Element>();
        if (run.DurationMs is { } duration)
            chips.Add(Pill(t.Duration(duration), Theme.SecondaryText, Theme.SystemNeutralBackground, GlyphClock));
        if (run.DeliveryStatus is { } delivery)
            chips.Add(Pill(t.DeliveryStatus(delivery),
                delivery == "delivered" ? Theme.SystemSuccess : delivery == "not-delivered" ? Theme.SystemCritical : Theme.SecondaryText,
                delivery == "delivered" ? Theme.SystemSuccessBackground : delivery == "not-delivered" ? Theme.SystemCriticalBackground : Theme.SystemNeutralBackground,
                GlyphSend).AutomationId($"AutomationRunDelivery:{run.Key}"));
        if (!string.IsNullOrWhiteSpace(run.Model))
            chips.Add(Pill(string.IsNullOrWhiteSpace(run.Provider) ? run.Model! : $"{run.Provider}/{run.Model}", Theme.SecondaryText, Theme.SystemNeutralBackground, GlyphSparkle));
        if (run.InputTokens is not null || run.OutputTokens is not null)
            chips.Add(Pill(t.Format("Runs_Tokens", run.InputTokens ?? 0, run.OutputTokens ?? 0), Theme.SecondaryText, Theme.SystemNeutralBackground));
        if (run.TriggerFired == true)
            chips.Add(Pill(t["Runs_TriggerFired"], Theme.AccentText, Theme.SystemAttentionBackground, GlyphBranch));

        var details = new List<Element?>
        {
            TextBlock(full).FontSize(13).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                .Foreground(isError ? Theme.SystemCritical : Theme.PrimaryText)
                .MaxLines(expanded ? 0 : 3)
                .AutomationId($"AutomationRunSummary:{run.Key}"),
            run.DiagnosticsSummary is { Length: > 0 } diag ? Secondary(diag) : null,
            run.DeliverySuppressionReason is { Length: > 0 } reason ? Secondary(t.Format("Runs_Suppressed", reason)) : null,
            run.DeliveryError is { Length: > 0 } deliveryError ? ErrorText(t.Format("Runs_DeliveryError", deliveryError), $"AutomationRunDeliveryError:{run.Key}") : null,
            run.NextRunAtMs is { } nextRun ? Secondary(t.Format("Runs_NextRun", t.DateTime(nextRun))) : null,
        };

        var actions = new List<Element>();
        if (display.HasExpandableFullText || (display.FullText?.Length ?? 0) > 200)
            actions.Add(HyperlinkButton(expanded ? t["Runs_ShowLess"] : t["Runs_ShowMore"], null, toggleExpanded).AutomationId($"AutomationRunExpand:{run.Key}"));
        if (run.CanOpenTranscript)
            actions.Add(HyperlinkButton(transcript is null ? t["Runs_ViewTranscript"] : t["Runs_HideTranscript"], null, toggleTranscript)
                .AutomationId($"AutomationRunTranscript:{run.Key}"));
        if (!string.IsNullOrWhiteSpace(run.SessionKey))
            actions.Add(HyperlinkButton(t["Runs_OpenChat"], null, () => host.OpenChat(run.SessionKey!)).AutomationId($"AutomationRunChat:{run.Key}"));

        var card = Surface(VStack(Gap,
                headline,
                chips.Count == 0 ? Empty() : (FlexRow(chips.ToArray()) with { Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap, ColumnGap = 6, RowGap = 6 }),
                VStack(4, details.ToArray()),
                actions.Count == 0 ? Empty() : HStack(4, actions.ToArray()),
                transcript is null ? Empty() : RenderTranscript(t, transcript, loadOlder)),
                padding: 12)
            .AutomationId($"AutomationRun:{run.Key}");

        // Timeline rail: a continuous line through status nodes, bridging the list gap.
        var rail = Border(Empty()).Width(2).Background(Theme.Ref("AutomationsRailBrush"))
            .HAlign(HorizontalAlignment.Center)
            .Margin(0, first ? 16 : 0, 0, last ? 0 : -Gap);
        var node = Border(Glyph(glyph, 11, brush).Center())
            .Size(24, 24).CornerRadius(12)
            .Background(RunFill(run.Status))
            .BorderBrush(Theme.CardBackground).BorderThickness(2)
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Top).Margin(0, 10, 0, 0);
        Element railElement = first && last ? Empty()
            : last ? rail.VAlign(VerticalAlignment.Top).Height(22)
            : rail;
        return Grid([GridSize.Px(32), GridSize.Star()], [GridSize.Auto],
            railElement.Grid(column: 0),
            node.Grid(column: 0),
            card.Margin(8, 0, 0, 0).Grid(column: 1));
    }

    private static ThemeRef RunFill(string? status) => status switch
    {
        "ok" => Theme.SystemSuccessBackground,
        "error" => Theme.SystemCriticalBackground,
        "skipped" => Theme.SystemCautionBackground,
        _ => Theme.SystemNeutralBackground,
    };

    private static Element RenderTranscript(AutomationsText t, TranscriptState transcript, Action<string> loadOlder)
    {
        var items = new List<Element>();
        if (transcript.NextCursor is { } cursor)
            items.Add(HyperlinkButton(t["Transcript_LoadOlder"], null, () => loadOlder(cursor)).IsEnabled(!transcript.Loading));
        foreach (var (message, index) in transcript.Messages.Select((m, i) => (m, i)))
        {
            var isUser = message.Role == "user";
            var label = message.Role switch
            {
                "user" => t["Transcript_User"],
                "assistant" => t["Transcript_Assistant"],
                "tool" => t.Format("Transcript_Tool", message.ToolName ?? "tool"),
                "system" => t["Transcript_System"],
                _ => message.Role,
            };
            items.Add(Border(VStack(4,
                    HStack(Gap, TextBlock(label).FontSize(12).FontWeight(FontWeights.SemiBold),
                        message.TimestampMs is { } ts ? Secondary(t.DateTime(ts)) : Empty()),
                    message.Text.Length == 0 ? Empty() : TextBlock(message.Text).FontSize(13).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                        .FontFamily(message.Role == "tool" ? MonoFont : "Segoe UI Variable Text")))
                .Padding(10, 8)
                .CornerRadius(6)
                .Background(isUser ? Theme.SystemAttentionBackground : Theme.SubtleFill)
                .WithKey($"m{index}"));
        }
        if (transcript.Loading)
            items.Add(HStack(Gap, ProgressRing().Size(14, 14), Secondary(t["Transcript_Loading"])));
        else if (transcript.Error is { } error)
            items.Add(ErrorText(t.Format("Transcript_Unavailable", error), "AutomationTranscriptError"));
        else if (transcript.Messages.Count == 0)
            items.Add(Secondary(t["Transcript_Empty"]));

        return Border(VStack(6,
                HStack(Gap, Glyph(GlyphChat, 14), SectionTitle(t["Transcript_Title"])),
                VStack(6, items.ToArray())))
            .Padding(12)
            .CornerRadius(6)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(1)
            .AutomationId("AutomationTranscript");
    }
}
