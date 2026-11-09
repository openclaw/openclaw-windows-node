using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationDetailProps(AutomationsHost Host, AutomationsSnapshot State, CronJob Job, AutomationsActions Actions)
{
    public bool Narrow { get; init; }
}

/// <summary>Inline definition and recent results. Mutations retain store permission/revision checks.</summary>
internal sealed class AutomationDetail : Component<AutomationDetailProps>
{
    public override Element Render()
    {
        var (host, state, job, actions) = Props;
        var t = host.Text;
        var (copied, setCopied) = UseState<bool?>(null);
        var (expandedRun, setExpandedRun) = UseState<string?>(null);
        var runs = state.RunsQuery.JobId == job.Id ? state.Runs : Array.Empty<CronRunEntry>();
        var left = VStack(16,
            Section(!state.Connected && state.LastSeenAtMs is { } synced
                    ? t.Format("Designer_ScheduleAsOf", t.Clock(synced)) : t["Section_Schedule"],
                TextBlock(t.Schedule(job.Schedule)).FontSize(16).FontWeight(FontWeights.SemiBold)
                    .TextWrapping(TextWrapping.WrapWholeWords).AutomationId("AutomationScheduleSummary"),
                Fact(t["Field_Timezone"], job.Schedule.Kind == CronScheduleKind.Cron ? job.Schedule.Tz ?? t["GatewayTimezone"] : null, "AutomationFactTz"),
                Fact(t["NextRun"], !job.Enabled ? t["NotScheduled"]
                    : job.State.NextRunAtMs is { } next ? t.FriendlyDateTime(next) : AutomationsText.Unavailable, "AutomationNextRun"),
                IsAdvanced(job) ? Fact(t["Field_Expression"], job.Schedule.Expr, "AutomationFactExpr") : Empty(),
                IsAdvanced(job) ? Secondary(t["Schedule_WebManaged"]) : Empty(),
                job.Schedule.Kind is CronScheduleKind.Event or CronScheduleKind.Stream or CronScheduleKind.OnExit && runs.Count == 0
                    ? Secondary(job.HasTrigger && job.State.TriggerEvalCount is { } evaluations
                        ? t.Format("Runs_ConditionChecked", evaluations) : t["Redesign_EventNoRuns"]) : Empty()),
            Section(t.PayloadField(job.Payload.Kind),
                Border(TextBlock(job.Payload.Text).TextWrapping(TextWrapping.WrapWholeWords).IsTextSelectionEnabled(true)
                    .AutomationId("AutomationPrompt")).Padding(12).CornerRadius(8).Background(Theme.SubtleFill),
                Fact(t["Field_Session"], job.SessionTarget is { } target ? t.SessionTarget(target) : null, "AutomationFactSession"),
                job.Payload.IsReadOnly ? Secondary(t["Payload_WebManaged"]) : Empty()),
            Section(t["Section_Delivery"],
                Fact(t["Field_DeliveryMode"], job.Delivery is { } delivery ? t.DeliveryMode(delivery.Mode) : t["Default"], "AutomationFactDeliveryMode"),
                Fact(t["Field_Destination"], job.Delivery?.Target == "owner" ? t["Destination_Owner"]
                    : string.Join(" · ", new[] { job.Delivery?.Channel, job.Delivery?.To }.Where(v => !string.IsNullOrWhiteSpace(v))), "AutomationFactDestination"),
                Fact(t["Field_LastDelivery"], job.State.LastDeliveryStatus is { } ds ? t.DeliveryStatus(ds) : null, "AutomationFactLastDelivery"),
                ErrorText(job.State.LastDeliveryError, "AutomationFactDeliveryError")));
        var finished = runs.Where(r => r.Status is "ok" or "error").ToArray();
        var right = VStack(12,
            TextBlock(t["RecentRuns"]).FontSize(16).FontWeight(FontWeights.SemiBold),
            finished.Length > 0 ? Secondary(t.Format("Redesign_SuccessCount", finished.Count(r => r.Status == "ok"), finished.Length))
                .AutomationId("AutomationRunScore") : Empty(),
            HStack(4, runs.Take(20).Reverse().Select(run =>
            {
                var (brush, _) = RunTone(run.Status);
                return (Element)Border(Empty()).Size(12, 8).CornerRadius(4).Background(brush).ToolTip(t.RunStatus(run.Status)).WithKey(run.Key);
            }).ToArray()).AutomationId("AutomationRecentRuns"),
            state.RunsError is { } error ? ErrorText(error, "AutomationRunsError")
                : !state.RunsLoaded || state.RunsQuery.JobId != job.Id ? Secondary(t["Runs_Loading"])
                : runs.Count == 0 ? Secondary(t["Runs_EmptyHint"]) : Empty(),
            VStack(8, runs.Take(3).Select((run, index) => AutomationRunResult.Render(host, run, expandedRun == run.Key,
                () => setExpandedRun(expandedRun == run.Key ? null : run.Key), runOrder: index).WithKey(run.Key)).ToArray()),
            GlyphButton(GlyphOpenInBrowser, t["Redesign_AllRunsWeb"], () => host.OpenWeb(job.Id), $"AutomationAllRunsWeb:{job.Id}", state.Connected));
        Element details = (Props.Narrow ? (Element)VStack(16, left, right)
            : Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto], left.Margin(0, 0, 8, 0).Grid(column: 0), right.Margin(8, 0, 0, 0).Grid(column: 1)))
            .AutomationId("AutomationDetailColumns");
        return VStack(16,
            RenderCommands(host, state, job, actions, () => setCopied(host.CopyText(job.Id)), runs.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SessionKey))?.SessionKey),
            copied is { } ok ? Secondary(ok ? t.Format("CopyId_Done", job.Id) : t["CopyId_Failed"]).AutomationId("AutomationCopyIdResult") : Empty(),
            state.SelectedError is { } selectedError ? ErrorText(selectedError, "AutomationSelectedError") : Empty(),
            job.IsAutoDisabled || job.IsFailing
                ? (InfoBar(job.IsAutoDisabled ? t["Redesign_AutoDisabled"]
                    : job.State.ConsecutiveErrors is { } count ? t.Format("Redesign_FailureStreak", count) : t["Redesign_FailedLast"],
                    job.State.AutoDisabled?.ConsecutiveErrors is { } disabledCount
                        ? string.Join(". ", new[] { t.Format("Redesign_FailureStreak", disabledCount), job.State.LastError }.Where(s => !string.IsNullOrWhiteSpace(s)))
                        : job.State.LastError) with
                { Severity = InfoBarSeverity.Error, IsClosable = false }).AutomationId(job.IsAutoDisabled ? "AutomationAutoDisabled" : "AutomationLastError") : Empty(),
            job.State.StreamError is { } streamError ? ErrorText(streamError, "AutomationStreamError") : Empty(),
            details).AutomationId("AutomationDetail");
    }

    private static Element Section(string title, params Element?[] children) =>
        VStack(8, TextBlock(title).FontSize(16).FontWeight(FontWeights.SemiBold), VStack(8, children));

    private static Element RenderCommands(AutomationsHost host, AutomationsSnapshot state, CronJob job,
        AutomationsActions actions, Action copyId, string? lastSession)
    {
        var t = host.Text;
        var store = host.Store;
        var canAct = state.CanManage && state.Connected && !state.IsBusy(job.Id) && !state.IsRunning(job.Id) && !job.IsRunning;
        void Pause() => OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
        {
            if (await host.Confirm(new(t.Format("ConfirmPause_Title", job.Title), t["ConfirmPause_Body"], t["Pause"]) { Glyph = GlyphPause }))
                await store.SetEnabledAsync(job.Id, false);
        }, operationName: "Pause automation");
        void Remove() => OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
        {
            if (await host.Confirm(new(t.Format("Popup_RemoveTitle", job.Title),
                t.Format("Popup_RemoveBody", host.GatewayDisplayName() ?? t["Popup_ConnectedGateway"]), t["Remove"])
            {
                Glyph = GlyphDelete,
                Destructive = true,
                Facts =
                [
                    new(t["Field_Agent"], job.Owner ?? t["Unknown"]),
                    new(t["Section_Schedule"], t.Schedule(job.Schedule)),
                    new(t["NextRun"], job.Enabled && job.State.NextRunAtMs is { } next ? t.DateTime(next) : t["NotScheduled"]),
                ],
            }))
                await store.RemoveAsync(job.Id);
        }, operationName: "Remove automation");
        var primary = new List<AppBarItemBase>();
        if (state.CanManage || (!state.Connected && state.LastKnownCanManage))
        {
            primary.Add(AppBarButton(t["RunNow"], () => _ = store.RunNowAsync(job.Id), "Play") with { IsEnabled = canAct });
            primary.Add(AppBarButton(job.Enabled ? t["Pause"] : t["Resume"],
                job.Enabled ? Pause : () => _ = store.SetEnabledAsync(job.Id, true), job.Enabled ? "Pause" : "Play") with
            { IsEnabled = canAct });
            primary.Add(AppBarSeparator());
            primary.Add(AppBarButton(t[IsAdvanced(job) ? "Redesign_EditWeb" : "Edit"],
                () => { if (IsAdvanced(job)) host.OpenWeb(job.Id); else actions.Edit(job); }, "Edit") with
            { IsEnabled = canAct });
        }
        else
            primary.Add(AppBarButton(t["CopyId"], copyId, "Copy"));
        if (lastSession is not null)
            primary.Add(AppBarButton(t["Redesign_LastRunChat"], () => host.OpenChat(lastSession), "Message"));
        var secondary = new List<AppBarItemBase>();
        secondary.Add(AppBarButton(t["OpenInWeb"], () => host.OpenWeb(job.Id), "OpenInNewWindow") with { IsEnabled = state.Connected });
        secondary.Add(AppBarButton(t["CopyId"], copyId, "Copy"));
        if (state.CanManage || (!state.Connected && state.LastKnownCanManage))
        {
            secondary.Add(AppBarButton(t["Redesign_CopyAsNew"], () => actions.Duplicate(job), "Copy") with { IsEnabled = canAct });
            secondary.Add(AppBarSeparator());
            secondary.Add(AppBarButton(t["Remove"], Remove, "Delete") with { IsEnabled = canAct });
        }
        // WinUI right-aligns commands within a bar. Size the primary bar to content,
        // leaving the secondary bar docked at the opposite edge without restyling buttons.
        return Border(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            (CommandBar(primaryCommands: primary.ToArray()) with { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right })
                .Set(native => { native.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Auto; native.IsDynamicOverflowEnabled = true; })
                .HAlign(HorizontalAlignment.Stretch).AutomationId($"CronCommands:{job.Id}").WithKey($"CronCommands:{job.Id}").Grid(column: 0),
            CommandBar(secondaryCommands: secondary.ToArray())
                .Set(native =>
                {
                    if (native.SecondaryCommands.OfType<AppBarButton>().FirstOrDefault(button => button.Label == t["OpenInWeb"]) is { } web)
                        web.Icon = new FontIcon
                        {
                            Glyph = GlyphOpenInBrowser,
                            FontFamily = FluentIconCatalog.SymbolThemeFontFamily
                        };
                })
                .AutomationName(t.Format("MoreActionsFor", job.Title)).AutomationId($"CronMore:{job.Id}").WithKey($"CronMore:{job.Id}").Grid(column: 1)))
            .Padding(0, 0, 0, 8).BorderBrush(Theme.DividerStroke).BorderThickness(0, 0, 0, 1);
    }
}
