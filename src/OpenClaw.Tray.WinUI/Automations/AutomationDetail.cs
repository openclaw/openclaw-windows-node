using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationDetailProps(AutomationsHost Host, AutomationsSnapshot State, CronJob Job, AutomationsActions Actions);

/// <summary>Selected automation: actions, status, definition, run history and scratch.</summary>
internal sealed class AutomationDetail : Component<AutomationDetailProps>
{
    private const int RecentRunDots = 20;

    public override Element Render()
    {
        var (host, state, job, actions) = Props;
        var t = host.Text;
        var (tab, setTab) = UseState(0);
        var tabs = new List<SelectorBarItemData>
        {
            SelectorBarItem(t["Tab_Overview"]),
            SelectorBarItem(t["Tab_Runs"]),
        };
        if (state.CanManage)
            tabs.Add(SelectorBarItem(t["Tab_Scratch"]));
        var activeTab = Math.Min(tab, tabs.Count - 1);

        Element body = activeTab switch
        {
            1 => Component<AutomationRuns, AutomationRunsProps>(new(host, state, job)).WithKey($"runs-{job.Id}"),
            2 => Component<AutomationScratch, AutomationScratchProps>(new(host, job)).WithKey($"scratch-{job.Id}"),
            _ => ScrollView(RenderOverview(host, state, job).Padding(PagePadding, 0, PagePadding, PagePadding)).WithKey("overview"),
        };

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
                RenderHeader(host, state, job, actions).Margin(PagePadding, 0, PagePadding, 12).Grid(row: 0),
                SelectorBar(tabs.ToArray(), activeTab, setTab)
                    .Margin(PagePadding - 8, 0, PagePadding, 8)
                    .AutomationId("AutomationDetailTabs")
                    .Grid(row: 1),
                body.Grid(row: 2))
            .AutomationId("AutomationDetail");
    }

    private static Element RenderHeader(AutomationsHost host, AutomationsSnapshot state, CronJob job, AutomationsActions actions)
    {
        var t = host.Text;
        var store = host.Store;
        var running = state.IsRunning(job.Id);
        var busy = state.IsBusy(job.Id);
        var (fg, bg, glyph) = StatusTone(job, running);
        var canAct = state.CanManage && state.Connected && !busy;

        void ConfirmPause() => OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
        {
            if (await host.Confirm(t.Format("ConfirmPause_Title", job.Title), t["ConfirmPause_Body"], t["Pause"]))
                await store.SetEnabledAsync(job.Id, false);
        }, operationName: "Pause automation");

        void ConfirmRemove() => OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
        {
            if (await host.Confirm(t.Format("ConfirmRemove_Title", job.Title), t["ConfirmRemove_Body"], t["Remove"]))
                await store.RemoveAsync(job.Id);
        }, operationName: "Remove automation");

        var tone = Kind(job.Schedule.Kind);
        var title = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            KindTile(job.Schedule.Kind, 52, content: running ? ProgressRing().Size(22, 22).Center() : null)
                .VAlign(VerticalAlignment.Top).Margin(0, 2, 14, 0).Grid(column: 0),
            VStack(6,
                TextBlock(job.Title).FontSize(22).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap)
                    .AutomationId("AutomationDetailTitle"),
                (FlexRow(
                        Pill(t.JobState(job, running), fg, bg, glyph).AutomationId("AutomationDetailState"),
                        HStack(4, Glyph(tone.Glyph, 12, tone.Brush), Secondary(t.Schedule(job.Schedule), 13)).VAlign(VerticalAlignment.Center))
                    with { Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap, ColumnGap = 8, RowGap = 4 })).Grid(column: 1));

        var buttons = new List<Element>();
        if (state.CanManage)
        {
            buttons.Add(SplitButton(running ? t["Running"] : t["RunNow"], () => _ = store.RunNowAsync(job.Id), MenuItems(
                    MenuItem(t["RunIfDue"], () => _ = store.RunNowAsync(job.Id, CronRunMode.Due))))
                .IsEnabled(canAct && !running)
                .AutomationId($"CronRunNow:{job.Id}")
                .AutomationName(t["RunNow"]));
            buttons.Add(job.Enabled
                ? GlyphButton(GlyphPause, t["Pause"], ConfirmPause, $"CronPause:{job.Id}", canAct && !running)
                : GlyphButton(GlyphPlay, t["Resume"], () => _ = store.SetEnabledAsync(job.Id, true), $"CronResume:{job.Id}", canAct && !running));
            buttons.Add(GlyphButton(GlyphEdit, t["Edit"], () => actions.Edit(job), $"CronEdit:{job.Id}", canAct && !running));
            buttons.Add(Button(Glyph(GlyphMore, 14, Theme.PrimaryText), null)
                .WithFlyout(MenuItems(
                    MenuItem(t["Clone"], () => actions.Duplicate(job), GlyphCopyIcon),
                    MenuSeparator(),
                    MenuItem(t["Remove"], ConfirmRemove, GlyphDeleteIcon)))
                .IsEnabled(canAct && !running)
                .Padding(8, 6)
                .ToolTip(t["MoreActions"])
                .AutomationName(t.Format("MoreActionsFor", job.Title))
                .AutomationId($"CronMore:{job.Id}"));
        }

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                title.Grid(column: 0),
                WrapButtons(buttons).VAlign(VerticalAlignment.Top).Margin(12, 0, 0, 0).Grid(column: 1)).Grid(row: 0),
            running
                ? ProgressIndeterminate().Margin(0, 12, 0, 0).AutomationId("AutomationDetailRunning").Grid(row: 1)
                : Empty().Grid(row: 1));
    }

    private const string GlyphCopyIcon = "Copy";
    private const string GlyphDeleteIcon = "Delete";

    private static Element WrapButtons(List<Element> buttons) => HStack(Gap, buttons.ToArray());

    // ─── Overview ───

    private static Element RenderOverview(AutomationsHost host, AutomationsSnapshot state, CronJob job)
    {
        var t = host.Text;
        var sections = new List<Element?>();

        if (job.State.AutoDisabled is { } auto)
        {
            sections.Add((InfoBar(t.AutoDisabled(auto), job.State.LastError ?? t["AutoDisabled_Body"]) with
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = false,
                ActionButtonContent = state.CanManage ? t["Resume"] : null,
                OnActionButtonClick = state.CanManage ? () => _ = host.Store.SetEnabledAsync(job.Id, true) : null,
            }).AutomationId("AutomationAutoDisabled"));
        }
        else if (job.IsFailing && !string.IsNullOrWhiteSpace(job.State.LastError))
        {
            sections.Add((InfoBar(t["LastError_Title"], job.State.LastError) with { Severity = InfoBarSeverity.Error, IsClosable = false })
                .AutomationId("AutomationLastError"));
        }
        if (job.State.StreamError is { Length: > 0 } streamError)
            sections.Add((InfoBar(t["StreamError_Title"], streamError) with { Severity = InfoBarSeverity.Warning, IsClosable = false }));

        sections.Add(RenderPulse(host, state, job));
        sections.Add(RenderWhat(t, job));
        sections.Add(RenderWhen(t, job));
        sections.Add(RenderDelivery(t, job));
        sections.Add(Section(t["Section_Details"], GlyphNote,
            Fact(t["Field_Id"], job.Id, "AutomationFactId"),
            Fact(t["Field_Agent"], job.AgentId ?? state.AgentId, "AutomationFactAgent"),
            Fact(t["Field_Description"], job.Description, "AutomationFactDescription"),
            Fact(t["Field_SessionKey"], job.SessionKey, "AutomationFactSessionKey"),
            Fact(t["Field_Created"], job.CreatedAtMs is { } c ? t.DateTime(c) : null, "AutomationFactCreated"),
            Fact(t["Field_Updated"], job.UpdatedAtMs is { } u ? t.DateTime(u) : null, "AutomationFactUpdated"),
            Fact(t["Field_Revision"], job.ConfigRevision, "AutomationFactRevision")));

        return VStack(SectionGap, sections.ToArray()).MaxWidth(ReadingWidth).HAlign(HorizontalAlignment.Left);
    }

    /// <summary>Next/last run headline and a strip of recent outcomes, newest on the right.</summary>
    private static Element RenderPulse(AutomationsHost host, AutomationsSnapshot state, CronJob job)
    {
        var t = host.Text;
        var running = state.IsRunning(job.Id) || job.IsRunning;
        var next = running
            ? t["State_Running"]
            : job.State.NextRunAtMs is { } n && job.Enabled ? t.Relative(n) : t["NotScheduled"];
        var nextDetail = job.State.NextRunAtMs is { } nd && job.Enabled && !running ? t.DateTime(nd) : "";
        var lastStatus = job.State.LastRunStatus;
        var (lastBrush, lastGlyph) = RunTone(lastStatus);
        var last = job.State.LastRunAtMs is { } l ? t.Relative(l) : t["NeverRun"];
        var lastDetail = job.State.LastRunAtMs is { } ld
            ? string.Join(" · ", new[] { t.RunStatus(lastStatus), job.State.LastDurationMs is { } d ? t.Duration(d) : null, t.DateTime(ld) }.Where(s => !string.IsNullOrEmpty(s)))
            : "";

        Element Metric(string label, string value, string detail, Element? leading, string id) =>
            VStack(2,
                Secondary(label),
                HStack(6, leading ?? Empty(), TextBlock(value).FontSize(20).FontWeight(FontWeights.SemiBold)),
                Secondary(detail)).AutomationId(id);

        var jobRuns = state.RunsQuery.JobId == job.Id ? state.Runs : Array.Empty<CronRunEntry>();
        var recent = jobRuns.Take(RecentRunDots).Reverse().ToArray();
        var longest = Math.Max(1d, recent.Select(r => (double)(r.DurationMs ?? 0)).DefaultIfEmpty(0).Max());
        var dots = recent.Select(run =>
        {
            var (brush, _) = RunTone(run.Status);
            // Bar height follows run duration so slow runs stand out at a glance.
            var height = run.DurationMs is { } ms ? 12 + 22 * Math.Sqrt((double)ms / longest) : 18;
            return Border(Empty())
                .Width(10)
                .Height(Math.Round(height))
                .CornerRadius(3)
                .Background(brush)
                .VAlign(VerticalAlignment.Bottom)
                .ToolTip(string.Join(" · ", new[] { t.RunStatus(run.Status), run.DurationMs is { } d ? t.Duration(d) : null, t.DateTime(run.EffectiveRunAtMs) }.Where(s => !string.IsNullOrEmpty(s))))
                .AutomationName($"{t.RunStatus(run.Status)} {t.DateTime(run.EffectiveRunAtMs)}")
                .WithKey(run.Key);
        }).ToArray();
        Element strip = dots.Length == 0
            ? Secondary(state.RunsLoaded && state.RunsQuery.JobId == job.Id ? t["Runs_EmptyHint"] : t["Loading"])
            : HStack(4, dots).Height(34);

        var finished = recent.Where(r => r.Status is "ok" or "error").ToArray();
        var succeeded = finished.Count(r => r.Status == "ok");
        var streak = jobRuns.TakeWhile(r => r.Status == "ok").Count();
        var durations = recent.Where(r => r.DurationMs is not null).Select(r => r.DurationMs!.Value).ToArray();
        Element score = finished.Length == 0
            ? Empty()
            : VStack(2,
                HStack(6,
                    Glyph(succeeded == finished.Length ? GlyphHeart : GlyphOk, 14, succeeded == finished.Length ? Theme.SystemSuccess : Theme.SecondaryText),
                    TextBlock(t.Format("Runs_SuccessRate", (int)Math.Round(100.0 * succeeded / finished.Length)))
                        .FontSize(14).FontWeight(FontWeights.SemiBold)
                        .Foreground(succeeded == finished.Length ? Theme.SystemSuccess : succeeded * 2 < finished.Length ? Theme.SystemCritical : Theme.PrimaryText)),
                Secondary(durations.Length == 0
                    ? t.Format("Runs_SummaryDetail", succeeded, finished.Length, "—")
                    : t.Format("Runs_SummaryDetail", succeeded, finished.Length, t.Duration((long)durations.Average()))),
                streak >= 3 ? Secondary(t.Format("Runs_Streak", streak)).Foreground(Theme.SystemSuccess) : Empty())
            .HAlign(HorizontalAlignment.Right)
            .AutomationId("AutomationRunScore");

        var condition = job.HasTrigger
            ? Secondary(t.Format("Condition_Summary",
                job.State.TriggerEvalCount ?? 0,
                job.State.LastTriggerEvalAtMs is { } checkedAt ? t.Relative(checkedAt) : t["NotChecked"],
                job.State.LastTriggerFireAtMs is { } firedAt ? t.Relative(firedAt) : t["NeverFired"]))
                .AutomationId("AutomationConditionActivity")
            : Empty();

        var tone = Kind(job.Schedule.Kind);
        return Surface(VStack(16,
                Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                    Metric(t["NextRun"], next, nextDetail, running ? ProgressRing().Size(16, 16) : Glyph(GlyphClock, 16, tone.Brush), "AutomationNextRun").Grid(column: 0),
                    Metric(t["LastRun"], last, lastDetail, Glyph(lastGlyph, 16, lastBrush), "AutomationLastRun").Grid(column: 1)),
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    VStack(6, Secondary(t["RecentRuns"]), strip).AutomationId("AutomationRecentRuns").Grid(column: 0),
                    score.VAlign(VerticalAlignment.Bottom).Margin(16, 0, 0, 0).Grid(column: 1)),
                condition))
            .AutomationId("AutomationPulse");
    }

    private static Element RenderWhat(AutomationsText t, CronJob job)
    {
        var payload = job.Payload;
        var flags = new List<string>();
        if (payload.LightContext == true) flags.Add(t["Flag_LightContext"]);
        if (payload.IncludeReasoning == true) flags.Add(t["Flag_IncludeReasoning"]);
        if (payload.SkipIfScratchEmpty == true) flags.Add(t["Flag_SkipEmptyScratch"]);
        if (job.IdleOnly == true) flags.Add(t["Flag_IdleOnly"]);

        return Section(t["Section_What"], GlyphSparkle,
            Pill(t.PayloadKind(payload.Kind), Theme.AccentText, Theme.SystemAttentionBackground),
            Border(TextBlock(string.IsNullOrWhiteSpace(payload.Text) ? "—" : payload.Text)
                    .FontSize(14).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                    .FontFamily(payload.IsReadOnly ? MonoFont : "Segoe UI Variable Text")
                    .AutomationId("AutomationPrompt"))
                .Padding(12)
                .CornerRadius(6)
                .Background(Theme.SubtleFill),
            payload.IsReadOnly ? Secondary(t["Payload_ReadOnlyHelp"]) : Empty(),
            Fact(t["Field_Model"], payload.Kind == CronPayloadKind.AgentTurn ? payload.Model ?? t["Default"] : null, "AutomationFactModel"),
            Fact(t["Field_Thinking"], payload.Thinking, "AutomationFactThinking"),
            Fact(t["Field_Timeout"], payload.TimeoutSeconds is { } s ? t.Format("Seconds", s.ToString("0.###", CultureInfo.CurrentCulture)) : null, "AutomationFactTimeout"),
            Fact(t["Field_Session"], t.SessionTarget(job.SessionTarget), "AutomationFactSession"),
            Fact(t["Field_Wake"], t.WakeMode(job.WakeMode), "AutomationFactWake"),
            Fact(t["Field_Options"], flags.Count == 0 ? null : string.Join(", ", flags), "AutomationFactOptions"),
            Fact(t["Field_Tools"], payload.ToolsAllow is { Count: > 0 } tools ? string.Join(", ", tools) : null, "AutomationFactTools"));
    }

    private static Element RenderWhen(AutomationsText t, CronJob job)
    {
        var s = job.Schedule;
        var stagger = s.Kind == CronScheduleKind.Cron
            ? s.StaggerMs == 0 ? t["ExactTiming"] : s.StaggerMs is { } ms ? t.Format("StaggerWindow", t.Duration(ms)) : null
            : null;
        var tone = Kind(s.Kind);
        return TintedSection(t["Section_When"], tone.Glyph, tone.Brush, tone.Fill,
            TextBlock(t.Schedule(s)).FontSize(16).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap)
                .AutomationId("AutomationScheduleSummary"),
            Fact(t["Field_Expression"], s.Expr, "AutomationFactExpr"),
            Fact(t["Field_Timezone"], s.Kind == CronScheduleKind.Cron ? s.Tz ?? t["GatewayTimezone"] : null, "AutomationFactTz"),
            Fact(t["Field_Stagger"], stagger, "AutomationFactStagger"),
            Fact(t["Field_EventServer"], s.EventServer, "AutomationFactEventServer"),
            Fact(t["Field_EventName"], s.EventName, "AutomationFactEventName"),
            Fact(t["Field_EventArguments"], s.EventArgumentsJson is { } args && args != "{}" ? args : null, "AutomationFactEventArgs"),
            Fact(t["Field_Command"], s.Command, "AutomationFactCommand"),
            Fact(t["Field_ActiveHours"], job.ActiveHours is { } ah
                ? string.IsNullOrWhiteSpace(ah.Timezone) ? $"{ah.Start} - {ah.End}" : $"{ah.Start} - {ah.End} ({ah.Timezone})"
                : null, "AutomationFactActiveHours"),
            Fact(t["Field_Condition"], job.Trigger is { } trigger
                ? trigger.Once ? t["Condition_Once"] : t["Condition_Every"]
                : null, "AutomationFactCondition"),
            job.Trigger is { } tr
                ? Border(TextBlock(tr.Script).FontFamily(MonoFont).FontSize(12).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true))
                    .Padding(12).CornerRadius(6).Background(Theme.SubtleFill)
                : Empty(),
            Fact(t["Field_DeleteAfterRun"], job.DeleteAfterRun == true ? t["Yes"] : null, "AutomationFactDeleteAfterRun"));
    }

    private static Element RenderDelivery(AutomationsText t, CronJob job)
    {
        var d = job.Delivery;
        var alert = job.FailureAlert switch
        {
            null => t["FailureAlert_Inherit"],
            { Disabled: true } => t["FailureAlert_Disabled"],
            var a => t.Format("FailureAlert_CustomSummary", a.After ?? 1, a.CooldownMs is { } c ? t.Duration(c) : t["Default"]),
        };
        var destination = d is null ? null
            : d.Target == "owner" ? t["Destination_Owner"]
            : string.Join(" · ", new[] { d.Channel, d.To, d.ThreadId is null ? null : t.Format("Thread", d.ThreadId) }.Where(v => !string.IsNullOrWhiteSpace(v)));

        return Section(t["Section_Delivery"], GlyphSend,
            Fact(t["Field_DeliveryMode"], t.DeliveryMode(d?.Mode ?? "none"), "AutomationFactDeliveryMode"),
            Fact(t["Field_Destination"], destination, "AutomationFactDestination"),
            Fact(t["Field_Account"], d?.AccountId, "AutomationFactAccount"),
            Fact(t["Field_DirectPolicy"], d?.DirectPolicy switch { "allow" => t["DirectPolicy_Allow"], "block" => t["DirectPolicy_Block"], _ => null }, "AutomationFactDirectPolicy"),
            Fact(t["Field_BestEffort"], d?.BestEffort == true ? t["Yes"] : null, "AutomationFactBestEffort"),
            Fact(t["Field_Preview"], job.DeliveryPreview is { } p ? string.IsNullOrWhiteSpace(p.Detail) ? p.Label : $"{p.Label} ({p.Detail})" : null, "AutomationFactPreview"),
            Fact(t["Field_LastDelivery"], job.State.LastDeliveryStatus is { } ds ? t.DeliveryStatus(ds) : null, "AutomationFactLastDelivery"),
            Fact(t["Field_Suppressed"], job.State.DeliverySuppressionReason, "AutomationFactSuppressed"),
            Fact(t["Field_DeliveryError"], job.State.LastDeliveryError, "AutomationFactDeliveryError"),
            Fact(t["Field_FailureAlerts"], alert, "AutomationFactFailureAlerts"));
    }
}
