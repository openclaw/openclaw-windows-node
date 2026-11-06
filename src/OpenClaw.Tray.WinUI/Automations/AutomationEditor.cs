using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationEditorProps(
    AutomationsHost Host,
    EditorSession Session,
    string AgentId,
    bool CanManage,
    Action<Func<EditorSession, EditorSession>> UpdateSession,
    Action<string?> OnClose)
{
    public bool Narrow { get; init; }
}

/// <summary>
/// Create, edit and clone editor for everyday automations: what it is, when it runs,
/// what it does and where results go. Settings it does not show are loaded from the
/// saved job and written back unchanged; those, and web-managed schedules or actions,
/// are changed in the Gateway web dashboard.
/// </summary>
internal sealed class AutomationEditor : Component<AutomationEditorProps>
{
    private static readonly (string Key, CronScheduleKind Kind)[] ScheduleKinds =
    [
        ("Kind_Every", CronScheduleKind.Every),
        ("Kind_At", CronScheduleKind.At),
        ("Kind_Cron", CronScheduleKind.Cron),
    ];
    private static readonly (string Key, CronCalendarPattern Pattern)[] CalendarPatterns =
    [
        ("Preset_Daily", CronCalendarPattern.Daily),
        ("Preset_Weekdays", CronCalendarPattern.Weekdays),
        ("Preset_Weekly", CronCalendarPattern.Weekly),
    ];
    private static readonly CronDurationUnit[] EveryUnits = [CronDurationUnit.Seconds, CronDurationUnit.Minutes, CronDurationUnit.Hours, CronDurationUnit.Days];
    private static readonly Lazy<string[]> TimeZones = new(LoadTimeZones);
    private static readonly Lazy<string?> LocalTimeZone = new(LoadLocalTimeZone);

    /// <summary>Fields this editor renders. Errors on any other (preserved) field are shown with the web notice.</summary>
    private static readonly HashSet<CronFormField> VisibleFields =
    [
        CronFormField.Name, CronFormField.ScheduleAt, CronFormField.EveryAmount, CronFormField.CronExpr,
        CronFormField.CronTz, CronFormField.PayloadText, CronFormField.DeliveryMode, CronFormField.DeliveryTo,
    ];

    public override Element Render()
    {
        var (host, session, agentId, canManage, updateSession, onClose) = Props;
        var t = host.Text;
        var form = session.Form;
        var errors = session.Errors;
        var saving = session.Saving;
        var banner = session.Banner;
        // After a revision conflict the editor rebases onto the latest definition.
        var source = session.Source;
        var editing = session.Mode == EditorMode.Edit ? source : null;
        var cloneSource = session.Mode == EditorMode.Clone ? source : null;

        void Update(Func<CronJobForm, CronJobForm> change, bool sessionTargetChanged = false)
        {
            updateSession(current =>
            {
                var next = current.Form.WithStaleThreadCleared(change(current.Form).Normalize(sessionTargetChanged));
                return current with { Form = next, Errors = current.Submitted ? next.Validate() : current.Errors };
            });
        }

        async Task SaveAsync(bool runAfter)
        {
            var validation = form.Normalize().Validate();
            updateSession(current => current with { Submitted = true, Errors = validation });
            if (validation.Count > 0)
            {
                updateSession(current => current with
                {
                    Banner = (t["Editor_CantSave"], validation.Count == 1 ? t["Editor_FixOne"] : t.Format("Editor_FixMany", validation.Count), InfoBarSeverity.Error),
                });
                return;
            }
            updateSession(current => current with { Saving = true, Banner = null });
            var result = await host.Store.SaveAsync(form, editing, cloneSource, runAfter);
            updateSession(current => current with { Saving = false });
            switch (result)
            {
                case AutomationsSaveResult.Saved saved:
                    onClose(saved.Job.Id);
                    break;
                case AutomationsSaveResult.Invalid invalid:
                    updateSession(current => current with { Errors = invalid.Errors });
                    break;
                case AutomationsSaveResult.Conflict conflict:
                    if (conflict.Latest is { } latest)
                    {
                        updateSession(current => current with
                        {
                            Source = latest,
                            Form = CronJobForm.FromJob(latest),
                            Banner = (t["Notice_ConflictTitle"], t["Notice_Conflict"], InfoBarSeverity.Warning),
                        });
                    }
                    else
                    {
                        updateSession(current => current with
                        {
                            Banner = (t["Notice_ConflictTitle"], t["Notice_ConflictUnloadable"], InfoBarSeverity.Error),
                        });
                    }
                    break;
                case AutomationsSaveResult.Failed failed:
                    updateSession(current => current with { Banner = (t["Editor_SaveFailed"], failed.Message, InfoBarSeverity.Error) });
                    break;
                case AutomationsSaveResult.MissingRevision:
                    updateSession(current => current with { Banner = (t["Editor_SaveFailed"], t["Notice_MissingRevision"], InfoBarSeverity.Error) });
                    break;
            }
        }

        string? Err(CronFormField field) => errors.TryGetValue(field, out var e) ? t.FormError(e) : null;

        var title = session.Mode switch
        {
            EditorMode.Edit => t.Format("Editor_EditTitle", session.Source?.Title ?? form.Name),
            EditorMode.Clone => t.Format("Review_CopyAsNewTitle", source?.Title ?? form.Name),
            _ => t["Editor_NewTitle"],
        };

        var sections = new List<Element?>
        {
            banner is { } b
                ? (InfoBar(b.Title, b.Message) with { Severity = b.Severity, OnClosed = () => updateSession(current => current with { Banner = null }) }).AutomationId("AutomationEditorBanner")
                : null,
            canManage ? null : (InfoBar(null, host.Store.State.Connected ? t["ReadOnly"] : t["Disconnected_BannerBody"]) with
                { Severity = InfoBarSeverity.Warning, IsClosable = false }).AutomationId("AutomationEditorAccess"),
            source is null ? null : RenderPreserved(host, source, errors),
            RenderBasics(host, form, Update, Err, editing?.Owner ?? agentId, editing is not null),
            RenderAction(host, form, Update, Err, source),
            RenderSchedule(host, form, Update, Err, source),
            RenderDelivery(t, form, Update, Err, SavedTargets(host.Store.State.Jobs)),
        };

        var footer = Border(Grid(Props.Narrow ? [GridSize.Star()] : [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                Props.Narrow ? [GridSize.Auto, GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                Button(saving ? t["Editor_Saving"] : session.Mode == EditorMode.Edit ? t["Editor_Save"] : t["Editor_Create"], () => _ = SaveAsync(false))
                    .AutomationName(session.Mode == EditorMode.Edit ? t["Editor_Save"] : t["Editor_Create"])
                    .IsEnabled(!saving && canManage).ApplyStyle("AccentButtonStyle").HAlign(HorizontalAlignment.Stretch)
                    .AutomationId("AutomationEditorSave").WithKey("AutomationEditorSave").Grid(column: 0),
                (session.Mode != EditorMode.Edit
                    ? (Element)Button(t["Editor_CreateAndRun"], () => _ = SaveAsync(true)).AutomationName(t["Editor_CreateAndRun"])
                        .IsEnabled(!saving && canManage).HAlign(HorizontalAlignment.Stretch)
                        .AutomationId("AutomationEditorCreateAndRun").WithKey("AutomationEditorCreateAndRun").Margin(Props.Narrow ? 0 : Gap, Props.Narrow ? Gap : 0, 0, 0)
                    : Empty()).Grid(column: Props.Narrow ? 0 : 1, row: Props.Narrow ? 1 : 0),
                Props.Narrow ? Empty() : Empty().Grid(column: 2),
                Button(t["Cancel"], () => onClose(null)).AutomationName(t["Cancel"]).AutomationId("AutomationEditorCancel")
                    .WithKey("AutomationEditorCancel")
                    .HAlign(HorizontalAlignment.Stretch).Margin(Props.Narrow ? 0 : Gap, Props.Narrow ? Gap : 0, 0, 0)
                    .Grid(column: Props.Narrow ? 0 : 3, row: Props.Narrow ? 2 : 0)))
            .Padding(PagePadding, 12)
            .Background(Theme.LayerFill)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                VStack(2,
                    DialogHeader(session.Mode == EditorMode.Create ? GlyphAdd : session.Mode == EditorMode.Clone ? GlyphCopy : GlyphEdit, title, false, "AutomationEditorTitle"),
                    Secondary(t["Editor_Subtitle"], 13)).Padding(PagePadding, 0, PagePadding, 12).Grid(row: 0),
                ScrollView(VStack(SectionGap, sections.ToArray())
                        .Padding(PagePadding, 0, PagePadding, PagePadding)
                        .MaxWidth(ReadingWidth + 2 * PagePadding)
                        .HAlign(HorizontalAlignment.Stretch))
                    .IsEnabled(!saving)
                    .AutomationId("AutomationEditorForm")
                    .Grid(row: 1),
                footer.Grid(row: 2))
            .AutomationId("AutomationEditor");
    }

    private static Element? RenderPreserved(AutomationsHost host, CronJob source, IReadOnlyDictionary<CronFormField, CronFormError> errors)
    {
        var t = host.Text;
        var settings = t.PreservedSettings(source);
        var hiddenErrors = errors.Where(e => !VisibleFields.Contains(e.Key)).Select(e => t.FormError(e.Value)).Distinct().ToList();
        if (settings.Count == 0 && hiddenErrors.Count == 0)
            return null;
        var parts = new List<string>();
        if (settings.Count > 0)
            parts.Add(t.Format("Editor_PreservedBody", string.Join(CultureInfo.CurrentCulture.TextInfo.ListSeparator + " ", settings)));
        parts.AddRange(hiddenErrors);
        return (InfoBar(t["Editor_PreservedTitle"], string.Join(" ", parts)) with
        {
            Severity = hiddenErrors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            IsClosable = false,
            ActionButtonContent = t["OpenInWeb"],
            OnActionButtonClick = () => host.OpenWeb(source.Id),
        }).AutomationId("AutomationEditorPreserved");
    }

    /// <summary>In-place explanation for a schedule or action that only the web can change.</summary>
    private static Element WebManagedNote(AutomationsHost host, string message, string? jobId, string automationId) =>
        (InfoBar(null, message) with
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            ActionButtonContent = jobId is null ? null : host.Text["OpenInWeb"],
            OnActionButtonClick = jobId is null ? null : () => host.OpenWeb(jobId),
        }).AutomationId(automationId);

    // ─── Field helpers ───

    private static Element Section(string title, params Element?[] children) =>
        VStack(12, TextBlock(title).FontSize(16).FontWeight(FontWeights.SemiBold), VStack(8, children));

    private static Element Field(Element control, string? error, string? help = null) =>
        VStack(4, control, help is null ? Empty() : Secondary(help), ErrorText(error, ""));

    private static Element Text(string header, string value, Action<string> changed, string automationId, string? placeholder = null, bool multiline = false, bool showHeader = true)
    {
        var box = TextBox(value, changed, placeholder, showHeader ? header : null).AutomationId(automationId).AutomationName(header);
        if (automationId == "AutomationEditorName")
            box = box.Set(native => native.TabIndex = 0);
        return multiline ? box.AcceptsReturn(true).TextWrapping(TextWrapping.Wrap).MinHeight(96) : box;
    }

    private static Element Suggest(string header, string value, IReadOnlyList<string> suggestions, Action<string> changed, string automationId, string? placeholder = null) =>
        (AutoSuggestBox(value, changed, changed) with
        {
            OnSuggestionChosen = changed,
            Header = header,
            PlaceholderText = placeholder,
            Suggestions = suggestions
                .Where(s => string.IsNullOrWhiteSpace(value) || s.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(50)
                .ToArray(),
        }).AutomationId(automationId).AutomationName(header);

    private static Element Choice(string header, string[] labels, int selected, Action<int> changed, string automationId, bool enabled = true) =>
        ComboBox(labels, selected, index => { if (index >= 0) changed(index); })
            .Header(header)
            .IsEnabled(enabled)
            .MinWidth(200)
            .AutomationId(automationId)
            .AutomationName(header);

    private static Element Check(string label, bool value, Action<bool> changed, string automationId, string? help = null) =>
        VStack(0,
            CheckBox(value, changed, label).AutomationId(automationId),
            help is null ? Empty() : Secondary(help).Margin(28, 0, 0, 0));

    private static Element Row(params Element[] children) =>
        Component<AutomationEditorRow, AutomationEditorRowProps>(new(children));

    // ─── Sections ───

    /// <param name="agentHint">Placeholder owner: the edited job's owner, else the agent new automations use.</param>
    private static Element RenderBasics(AutomationsHost host, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update, Func<CronFormField, string?> err, string agentHint, bool editing)
    {
        var t = host.Text;
        return Section(t["Section_Basics"],
            Field(Text(t["Field_Name"], form.Name, v => update(f => f with { Name = v }, false), "AutomationEditorName"), err(CronFormField.Name)),
            Row(
                // Blank keeps an edited job's owner (the patch omits agentId); it never moves the job.
                Field(Suggest(t["Field_Agent"], form.AgentId, host.Store.State.FilterAgents(host.KnownAgents()), v => update(f => f with { AgentId = v }, false), "AutomationEditorAgent", agentHint), null,
                    editing ? t["Help_AgentEdit"] : t["Help_Agent"]),
                VStack(4,
                    ToggleSwitch(form.Enabled, v => update(f => f with { Enabled = v }, false), t["On"], t["Off"], t["Field_Enabled"])
                        .AutomationId("AutomationEditorEnabled"))));
    }

    internal sealed record AutomationEditorRowProps(Element[] Children);

    internal sealed class AutomationEditorRow : Component<AutomationEditorRowProps>
    {
        public override Element Render()
        {
            var (stacked, setStacked) = UseState(false);
            return Grid(stacked ? [GridSize.Star()] : Enumerable.Repeat(GridSize.Star(), Props.Children.Length).ToArray(),
                stacked ? Enumerable.Repeat(GridSize.Auto, Props.Children.Length).ToArray() : [GridSize.Auto],
                Props.Children.Select((child, index) => child
                    .Margin(stacked || index == 0 ? 0 : 6, stacked && index > 0 ? 12 : 0,
                        stacked || index == Props.Children.Length - 1 ? 0 : 6, 0)
                    .Grid(column: stacked ? 0 : index, row: stacked ? index : 0).WithKey($"c{index}")).ToArray())
                .OnSizeChanged((_, args) => setStacked(args.NewSize.Width < AutomationsVisuals.DialogFactsStackWidth));
        }
    }

    private static Element RenderSchedule(
        AutomationsHost host,
        CronJobForm form,
        Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err,
        CronJob? source)
    {
        var t = host.Text;
        var kindIndex = Array.FindIndex(ScheduleKinds, k => k.Kind == form.ScheduleKind);
        if (kindIndex < 0)
        {
            // Event, process and unknown schedules are written back exactly as saved.
            return Section(t["Section_Schedule"],
                source is null ? null : TextBlock(t.Schedule(source.Schedule)).FontSize(14).FontWeight(FontWeights.SemiBold)
                    .TextWrapping(TextWrapping.WrapWholeWords).AutomationId("AutomationEditorScheduleSummary"),
                WebManagedNote(host, t["Schedule_WebManaged"], source?.Id, "AutomationEditorWebSchedule"));
        }

        var children = new List<Element?>();
        var parsed = CronCalendar.TryParse(form.CronExpr);
        var calendarIndex = Array.FindIndex(CalendarPatterns, p => p.Pattern == parsed?.Pattern);
        var choiceIndex = form.ScheduleKind == CronScheduleKind.At ? 0 : form.ScheduleKind == CronScheduleKind.Every ? 1
            : calendarIndex >= 0 ? calendarIndex + 2 : -1;
        var choices = new[] { t["Kind_At"], t["Kind_Every"] }.Concat(CalendarPatterns.Select(p => t[p.Key])).ToArray();
        children.Add(Choice(t["Review_Repeats"], choices, choiceIndex, index =>
        {
            if (index < 2)
                update(f => f.WithScheduleKind(index == 0 ? CronScheduleKind.At : CronScheduleKind.Every), false);
            else
                update(f => f.WithScheduleKind(CronScheduleKind.Cron) with
                { CronExpr = CronCalendar.Build(CalendarPatterns[index - 2].Pattern, parsed?.Time ?? CronCalendar.DefaultTime, CronCalendar.WeeklyDay(f.CronExpr)) }, false);
        }, "AutomationEditorScheduleKind"));

        switch (form.ScheduleKind)
        {
            case CronScheduleKind.Every:
                children.Add(Row(
                    Field(Text(t["Field_Every"], form.EveryAmount, v => update(f => f with { EveryAmount = v }, false), "AutomationEditorEveryAmount"), err(CronFormField.EveryAmount)),
                    Choice(t["Field_Unit"], EveryUnits.Select(u => t["Unit_" + u]).ToArray(), Array.IndexOf(EveryUnits, form.EveryUnit),
                        i => update(f => f with { EveryUnit = EveryUnits[i] }, false), "AutomationEditorEveryUnit")));
                break;
            case CronScheduleKind.At:
                var at = form.ScheduleAt ?? DateTimeOffset.Now;
                children.Add(Row(
                    VStack(4, Secondary(t["Field_RunDate"], 13),
                        CalendarDatePicker(form.ScheduleAt, date => update(f => f with { ScheduleAt = Combine(date, f.ScheduleAt?.TimeOfDay ?? at.TimeOfDay) }, false))
                            .AutomationId("AutomationEditorRunDate").AutomationName(t["Field_RunDate"])),
                    VStack(4, Secondary(t["Field_RunTime"], 13),
                        TimePicker(at.TimeOfDay, time => update(f => f with { ScheduleAt = Combine(f.ScheduleAt ?? at, time) }, false))
                            .AutomationId("AutomationEditorRunTime").AutomationName(t["Field_RunTime"]))));
                children.Add(ErrorText(err(CronFormField.ScheduleAt), "AutomationEditorRunAtError"));
                // One-time runs are entered in this PC's zone and saved as an absolute instant.
                children.Add(Secondary(t.Format("Help_RunAtLocal", TimeZoneInfo.Local.DisplayName)).AutomationId("AutomationEditorRunAtZone"));
                children.Add(Check(t["Field_DeleteAfterRun"], form.DeleteAfterRun, v => update(f => f with { DeleteAfterRun = v }, false),
                    "AutomationEditorDeleteAfterRun", t["Help_DeleteAfterRun"]));
                break;
            case CronScheduleKind.Cron:
                // Calendar rules are authored as everyday patterns. Any other expression stays
                // readable and is written back unchanged unless a pattern is chosen explicitly.
                var calendar = CronCalendar.TryParse(form.CronExpr);
                if (calendar is null)
                {
                    children.Add(Border(TextBlock(form.CronExpr).FontFamily(MonoFont).FontSize(13).IsTextSelectionEnabled(true))
                        .Padding(12, 8).CornerRadius(6).Background(Theme.SubtleFill)
                        .AutomationName(t["Field_Expression"]).AutomationId("AutomationEditorCustomCron"));
                    children.Add(WebManagedNote(host, t["Schedule_CustomCron"], source?.Id, "AutomationEditorWebCron"));
                }
                children.Add(ErrorText(err(CronFormField.CronExpr), "AutomationEditorCronError"));
                children.Add(Row(
                    calendar is { Pattern: not CronCalendarPattern.Hourly } chosen
                        ? VStack(4, Secondary(t["Field_RunTime"], 13),
                            TimePicker(chosen.Time, picked => update(f => f with { CronExpr = CronCalendar.Build(chosen.Pattern, picked, CronCalendar.WeeklyDay(f.CronExpr)) }, false))
                                .AutomationId("AutomationEditorCalendarTime").AutomationName(t["Field_RunTime"]))
                        : Empty(),
                    Field(Suggest(t["Field_Timezone"], form.CronTz, TimeZones.Value, v => update(f => f with { CronTz = v }, false), "AutomationEditorTimezone", t["Placeholder_Timezone"]),
                        err(CronFormField.CronTz), t["Help_Timezone"])));
                if (LocalTimeZone.Value is { } local && !string.Equals(form.CronTz.Trim(), local, StringComparison.Ordinal))
                {
                    children.Add(HyperlinkButton(t.Format("UseLocalTimezone", local), null, () => update(f => f with { CronTz = local }, false))
                        .AutomationId("AutomationEditorUseLocalTimezone"));
                }
                if (calendar is { Pattern: CronCalendarPattern.Weekly } weekly)
                    children.Add(Choice(t["Redesign_Weekday"], CultureInfo.CurrentCulture.DateTimeFormat.DayNames,
                        (int)CronCalendar.WeeklyDay(form.CronExpr),
                        i => update(f => f with { CronExpr = CronCalendar.Build(weekly.Pattern, weekly.Time, (DayOfWeek)i) }, false),
                        "AutomationEditorWeekday"));
                break;
        }

        var summary = t.FormSchedule(form);
        var summaryTone = Kind(form.ScheduleKind);
        children.Add(Border(HStack(Gap, Glyph(summaryTone.Glyph, 14, summaryTone.Brush),
                TextBlock(summary ?? t["Schedule_Incomplete"]).FontSize(13).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(summary is null ? Theme.SecondaryText : Theme.PrimaryText)))
            .Padding(12, 8).CornerRadius(8).Background(summaryTone.Fill)
            .BorderBrush(Theme.Ref("AutomationsTileStrokeBrush")).BorderThickness(1)
            .AutomationId("AutomationEditorScheduleSummary")
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite));
        children.Add(Secondary(t["Redesign_ExactNextOnSave"]));

        return Section(t["Section_Schedule"], children.ToArray());
    }

    private static Element RenderAction(AutomationsHost host, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update, Func<CronFormField, string?> err, CronJob? source)
    {
        var t = host.Text;
        var label = t.PayloadField(form.PayloadKind);
        var children = new List<Element?>();
        if (form.PayloadLocked)
        {
            children.Add(Border(TextBlock(form.PayloadText).FontFamily(MonoFont).FontSize(13).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true))
                .Padding(12).CornerRadius(6).Background(Theme.SubtleFill).AutomationName(label).AutomationId("AutomationEditorLockedPayload"));
            children.Add(WebManagedNote(host, t["Payload_WebManaged"], source?.Id, "AutomationEditorWebPayload"));
        }
        else
        {
            // The section heading already names the field, so the box carries it only as its accessible name.
            children.Add(Field(Text(label, form.PayloadText,
                    v => update(f => f with { PayloadText = v }, false), "AutomationEditorPrompt", t["Placeholder_Prompt"], multiline: true, showHeader: false),
                err(CronFormField.PayloadText)));
        }

        string[] targets = ["isolated", "main"];
        var targetLabels = targets.Select(t.SessionTarget).ToList();
        var targetIndex = Array.IndexOf(targets, form.SessionTarget);
        if (targetIndex < 0)
        {
            targetLabels.Add(t.SessionTarget(form.SessionTarget));
            targetIndex = targetLabels.Count - 1;
        }
        children.Add(
            Choice(t["Field_Session"], targetLabels.ToArray(), targetIndex,
                i => { if (i < targets.Length) update(f => f with { SessionTarget = targets[i] }, true); }, "AutomationEditorSession"));
        children.Add(Secondary(t["Help_Session"]));
        return Section(label, children.ToArray());
    }

    /// <summary>Recipients other automations already deliver to; plain strings with no account attached.</summary>
    private static string[] SavedTargets(IEnumerable<CronJob> jobs) =>
        jobs.Select(j => j.Delivery?.To?.Trim()).OfType<string>().Where(to => to.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Element RenderDelivery(AutomationsText t, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err, string[] savedTargets)
    {
        var modes = new List<string> { "none", "announce", "webhook" };
        var labels = modes.Select(t.DeliveryMode).ToList();
        var modeIndex = modes.IndexOf(form.DeliveryMode);
        if (modeIndex < 0)
        {
            // A legacy mode is either kept as saved (edits) or must be chosen explicitly (copies).
            labels.Insert(0, form.KeepSavedDelivery ? t["DeliveryMode_KeepSaved"] : t["DeliveryMode_Choose"]);
            modes.Insert(0, "");
            modeIndex = 0;
        }
        var children = new List<Element?>
        {
            Field(Choice(t["Field_DeliveryMode"], labels.ToArray(), modeIndex, i =>
            {
                if (modes[i].Length > 0) update(f => f with { DeliveryMode = modes[i] }, false);
            }, "AutomationEditorDeliveryMode"), err(CronFormField.DeliveryMode),
                form.SupportsAnnounce ? t["Help_Delivery"] : t["Help_DeliveryNoAnnounce"]),
        };

        if (form.DeliveryMode == "announce")
        {
            children.Add(RadioButtons([t["Destination_Channel"], t["Destination_Owner"]], form.DeliveryTarget == "owner" ? 1 : 0,
                    i => update(f => f with { DeliveryTarget = i == 1 ? "owner" : "" }, false))
                .AutomationId("AutomationEditorDestination"));
            if (form.DeliveryTarget == "owner")
                children.Add(Secondary(t["Help_Owner"]));
            children.Add(Row(
                Field(Text(t["Field_Channel"], form.DeliveryChannel == CronProtocol.ChannelLast ? "" : form.DeliveryChannel,
                    v => update(f => f with { DeliveryChannel = string.IsNullOrWhiteSpace(v) ? CronProtocol.ChannelLast : v }, false),
                    "AutomationEditorChannel", t["Placeholder_ChannelLast"]), null, t["Help_Channel"]),
                form.DeliveryTarget == "owner"
                    ? Empty()
                    : Field(Suggest(t["Field_To"], form.DeliveryTo, savedTargets, v => update(f => f with { DeliveryTo = v }, false),
                        "AutomationEditorTo", t["Placeholder_To"]), err(CronFormField.DeliveryTo), t["Help_ToSaved"])));
        }
        else if (form.DeliveryMode == "webhook")
        {
            children.Add(Field(Text(t["Field_WebhookUrl"], form.DeliveryTo, v => update(f => f with { DeliveryTo = v }, false), "AutomationEditorWebhook"),
                err(CronFormField.DeliveryTo), t["Help_Webhook"]));
        }

        return Section(t["Section_Delivery"], children.ToArray());
    }

    private static DateTimeOffset? Combine(DateTimeOffset? date, TimeSpan time)
    {
        if (date is not { } d) return null;
        var local = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Unspecified) + time;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>IANA zone ids for suggestions; the Gateway expects IANA names, not Windows ids.</summary>
    private static string[] LoadTimeZones()
    {
        var zones = new SortedSet<string>(StringComparer.Ordinal) { "UTC" };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrEmpty(iana))
                zones.Add(iana);
            else if (zone.Id.Contains('/'))
                zones.Add(zone.Id);
        }
        return zones.ToArray();
    }

    /// <summary>This PC's zone as an IANA id, offered as a one-click calendar time zone.</summary>
    private static string? LoadLocalTimeZone()
    {
        var id = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && !string.IsNullOrEmpty(iana))
            return iana;
        return id.Contains('/') ? id : null;
    }
}
