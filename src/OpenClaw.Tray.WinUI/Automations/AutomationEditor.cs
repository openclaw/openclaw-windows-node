using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    Action<string?> OnClose);

internal sealed record EditorRemote(
    CronEditorHints Hints,
    string? ConversationsKey,
    IReadOnlyList<ConversationTarget> Conversations,
    string? ConversationsError,
    string? EventsServer,
    IReadOnlyList<McpEventDefinition> Events,
    bool EventsLoading,
    string? EventsError)
{
    public static EditorRemote Empty { get; } = new(CronEditorHints.Empty, null, Array.Empty<ConversationTarget>(), null, null, Array.Empty<McpEventDefinition>(), false, null);
}

/// <summary>
/// Create, edit and clone editor. Sections follow the questions a user asks:
/// what is it, when does it run, what does it do, where do results go.
/// </summary>
internal sealed class AutomationEditor : Component<AutomationEditorProps>
{
    private static readonly string[] ThinkingSuggestions = ["off", "minimal", "low", "medium", "high", "xhigh"];
    private static readonly (string Key, CronScheduleKind Kind)[] ScheduleKinds =
    [
        ("Kind_Every", CronScheduleKind.Every),
        ("Kind_At", CronScheduleKind.At),
        ("Kind_Cron", CronScheduleKind.Cron),
        ("Kind_Event", CronScheduleKind.Event),
    ];
    private static readonly (string Key, string Expr)[] CronPresets =
    [
        ("Preset_Hourly", "0 * * * *"),
        ("Preset_Daily", "0 9 * * *"),
        ("Preset_Weekdays", "0 9 * * 1-5"),
        ("Preset_Weekly", "0 9 * * 1"),
        ("Preset_Monthly", "0 9 1 * *"),
    ];
    private static readonly CronDurationUnit[] EveryUnits = [CronDurationUnit.Seconds, CronDurationUnit.Minutes, CronDurationUnit.Hours, CronDurationUnit.Days];
    private static readonly CronDurationUnit[] StaggerUnits = [CronDurationUnit.Seconds, CronDurationUnit.Minutes];
    private static readonly Lazy<string[]> TimeZones = new(LoadTimeZones);
    public override Element Render()
    {
        var (host, session, agentId, canManage, updateSession, onClose) = Props;
        var t = host.Text;
        var form = session.Form;
        var errors = session.Errors;
        var saving = session.Saving;
        var banner = session.Banner;
        var (remote, setRemote) = UseState(EditorRemote.Empty, threadSafe: true);
        var (advancedOpen, setAdvancedOpen) = UseState(session.Mode != EditorMode.Create && HasAdvancedValues(session.Form));
        var remoteRef = UseRef(EditorRemote.Empty);
        // After a revision conflict the editor rebases onto the latest definition.
        var source = session.Source;
        var ui = SynchronizationContext.Current;

        // Remote results arrive concurrently; apply each on the UI thread against the latest value.
        void UpdateRemote(Func<EditorRemote, EditorRemote> change)
        {
            void Apply()
            {
                remoteRef.Current = change(remoteRef.Current);
                setRemote(remoteRef.Current);
            }
            if (ui is null || SynchronizationContext.Current == ui) Apply();
            else ui.Post(_ => Apply(), null);
        }

        var editing = session.Mode == EditorMode.Edit ? source : null;
        var cloneSource = session.Mode == EditorMode.Clone ? session.Source : null;
        var effectiveAgent = form.ClearAgent ? agentId : string.IsNullOrWhiteSpace(form.AgentId) ? agentId : form.AgentId.Trim();

        void Update(Func<CronJobForm, CronJobForm> change, bool sessionTargetChanged = false)
        {
            updateSession(current =>
            {
                var next = current.Form.WithStaleThreadCleared(change(current.Form).Normalize(sessionTargetChanged));
                return current with { Form = next, Errors = current.Submitted ? next.Validate() : current.Errors };
            });
        }

        // Editor hints: enabled MCP servers and configured models (config.get).
        UseEffect((Func<Action>)(() =>
        {
            var client = host.Client();
            if (client is null) return static () => { };
            var cancelled = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    var hints = await CronGatewayRequests.EditorHintsAsync(client);
                    if (!cancelled) UpdateRemote(r => r with { Hints = hints });
                }
                catch (Exception)
                {
                    // Hints only seed suggestions; every field still accepts free text.
                }
            });
            return () => cancelled = true;
        }), session.Nonce);

        // Delivery target suggestions for the current agent and channel.
        var routeKey = $"{form.DeliveryMode}|{form.DeliveryTarget}|{form.DeliveryChannel.Trim()}|{effectiveAgent}";
        var currentRoute = UseRef(routeKey);
        currentRoute.Current = routeKey;
        UseEffect((Func<Action>)(() =>
        {
            var client = host.Client();
            UpdateRemote(r => r with
            {
                ConversationsKey = routeKey,
                Conversations = Array.Empty<ConversationTarget>(),
                ConversationsError = null,
            });
            // Suggestions need an explicit channel: "last" could mix several channels, and a
            // target is only meaningful on the channel it came from (as on the web page).
            var channel = form.DeliveryChannel.Trim();
            if (client is null || !canManage || form.DeliveryMode != "announce" || form.DeliveryTarget == "owner"
                || channel.Length == 0 || channel == CronProtocol.ChannelLast)
            {
                return static () => { };
            }
            var cancelled = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    var conversations = await client.ListConversationsAsync(effectiveAgent, channel);
                    UpdateRemote(r => !cancelled && currentRoute.Current == routeKey
                        ? r with { ConversationsKey = routeKey, Conversations = conversations, ConversationsError = null }
                        : r);
                }
                catch (Exception ex)
                {
                    UpdateRemote(r => !cancelled && currentRoute.Current == routeKey
                        ? r with { ConversationsKey = routeKey, Conversations = Array.Empty<ConversationTarget>(), ConversationsError = ex.Message }
                        : r);
                }
            });
            return () => cancelled = true;
        }), routeKey, canManage);

        // MCP event catalog for the chosen server.
        var eventsKey = form.ScheduleKind == CronScheduleKind.Event ? $"{effectiveAgent}|{form.EventServer.Trim()}" : "";
        var (eventsNonce, setEventsNonce) = UseState(0);
        UseEffect((Func<Action>)(() =>
        {
            var client = host.Client();
            var server = form.EventServer.Trim();
            if (client is null || eventsKey.Length == 0 || server.Length == 0)
            {
                UpdateRemote(r => r with { EventsServer = null, Events = Array.Empty<McpEventDefinition>(), EventsError = null, EventsLoading = false });
                return static () => { };
            }
            var cancelled = false;
            UpdateRemote(r => r with { EventsServer = server, EventsLoading = true, EventsError = null });
            _ = Task.Run(async () =>
            {
                try
                {
                    var events = await client.ListMcpEventsAsync(effectiveAgent, server);
                    if (!cancelled) UpdateRemote(r => r with { Events = events, EventsLoading = false });
                }
                catch (Exception ex)
                {
                    if (!cancelled) UpdateRemote(r => r with { Events = Array.Empty<McpEventDefinition>(), EventsLoading = false, EventsError = ex.Message });
                }
            });
            return () => cancelled = true;
        }), eventsKey, eventsNonce);

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
            EditorMode.Clone => t["Editor_CloneTitle"],
            _ => t["Editor_NewTitle"],
        };

        var sections = new List<Element?>
        {
            banner is { } b
                ? (InfoBar(b.Title, b.Message) with { Severity = b.Severity, OnClosed = () => updateSession(current => current with { Banner = null }) }).AutomationId("AutomationEditorBanner")
                : null,
            RenderBasics(t, form, Update, Err, agentId),
            RenderSchedule(t, form, Update, Err, remote, () => setEventsNonce(eventsNonce + 1)),
            RenderAction(t, form, Update, Err, remote),
            RenderDelivery(t, form, Update, Err, remote, routeKey, () => currentRoute.Current == routeKey, SavedTargets(host.Store.State.Jobs)),
            RenderFailureAlerts(t, form, Update, Err),
            RenderAdvanced(t, form, Update, Err, advancedOpen, setAdvancedOpen),
        };

        var footer = Border(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                Secondary(t.FormSchedule(form) ?? "", 13).VAlign(VerticalAlignment.Center).Grid(column: 0),
                HStack(Gap,
                    Button(t["Cancel"], () => onClose(null)).AutomationName(t["Cancel"]).IsEnabled(!saving).AutomationId("AutomationEditorCancel"),
                    session.Mode != EditorMode.Edit
                        ? Button(t["Editor_CreateAndRun"], () => _ = SaveAsync(true)).AutomationName(t["Editor_CreateAndRun"]).IsEnabled(!saving && canManage).AutomationId("AutomationEditorCreateAndRun")
                        : Empty(),
                    Button(saving ? t["Editor_Saving"] : session.Mode == EditorMode.Edit ? t["Editor_Save"] : t["Editor_Create"], () => _ = SaveAsync(false)).AutomationName(session.Mode == EditorMode.Edit ? t["Editor_Save"] : t["Editor_Create"])
                        .IsEnabled(!saving && canManage)
                        .ApplyStyle("AccentButtonStyle")
                        .AutomationId("AutomationEditorSave")).Grid(column: 1)))
            .Padding(PagePadding, 12)
            .Background(Theme.LayerFill)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                VStack(2,
                    TextBlock(title).FontSize(22).FontWeight(FontWeights.SemiBold).AutomationId("AutomationEditorTitle"),
                    Secondary(t["Editor_Subtitle"], 13)).Padding(PagePadding, 0, PagePadding, 12).Grid(row: 0),
                ScrollView(VStack(SectionGap, sections.ToArray())
                        .Padding(PagePadding, 0, PagePadding, PagePadding)
                        .MaxWidth(ReadingWidth + 2 * PagePadding)
                        .HAlign(HorizontalAlignment.Left))
                    .IsEnabled(!saving)
                    .Grid(row: 1),
                footer.Grid(row: 2))
            .AutomationId("AutomationEditor");
    }

    private static bool HasAdvancedValues(CronJobForm form) =>
        form.TriggerEnabled || form.PayloadLightContext || form.PayloadIncludeReasoning || form.PayloadSkipIfScratchEmpty
        || form.IdleOnly || form.SessionKey.Length > 0 || form.ActiveHoursEnabled;

    // ─── Field helpers ───

    private static Element Field(Element control, string? error, string? help = null) =>
        VStack(4, control, help is null ? Empty() : Secondary(help), ErrorText(error, ""));

    private static Element Text(string header, string value, Action<string> changed, string automationId, string? placeholder = null, bool multiline = false, bool mono = false)
    {
        var box = TextBox(value, changed, placeholder, header).AutomationId(automationId).AutomationName(header);
        if (multiline)
            box = box.AcceptsReturn(true).TextWrapping(TextWrapping.Wrap).MinHeight(96);
        if (mono)
            box = box.FontFamily(MonoFont);
        return box;
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
        Grid(Enumerable.Repeat(GridSize.Star(), children.Length).ToArray(), [GridSize.Auto],
            children.Select((c, i) => c.Margin(i == 0 ? 0 : 6, 0, i == children.Length - 1 ? 0 : 6, 0).Grid(column: i).WithKey($"c{i}")).ToArray());

    // ─── Sections ───

    private static Element RenderBasics(AutomationsText t, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update, Func<CronFormField, string?> err, string agentId) =>
        Section(t["Section_Basics"], GlyphNote,
            Field(Text(t["Field_Name"], form.Name, v => update(f => f with { Name = v }, false), "AutomationEditorName", t["Placeholder_Name"]), err(CronFormField.Name)),
            Text(t["Field_Description"], form.Description, v => update(f => f with { Description = v }, false), "AutomationEditorDescription", t["Placeholder_Description"]),
            Row(
                Field(Text(t["Field_Agent"], form.AgentId, v => update(f => f with { AgentId = v, ClearAgent = false }, false), "AutomationEditorAgent", agentId), null, t["Help_Agent"]),
                VStack(4,
                    ToggleSwitch(form.Enabled, v => update(f => f with { Enabled = v }, false), t["On"], t["Off"], t["Field_StartEnabled"])
                        .AutomationId("AutomationEditorEnabled"))));

    private static Element RenderSchedule(
        AutomationsText t,
        CronJobForm form,
        Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err,
        EditorRemote remote,
        Action refreshEvents)
    {
        var kindIndex = Array.FindIndex(ScheduleKinds, k => k.Kind == form.ScheduleKind);
        var children = new List<Element?>();

        if (kindIndex < 0)
        {
            children.Add((InfoBar(null, t["Schedule_ReadOnlyKind"]) with { Severity = InfoBarSeverity.Informational, IsClosable = false })
                .AutomationId("AutomationEditorReadOnlySchedule"));
        }
        else
        {
            var cards = ScheduleKinds.Select((k, i) =>
            {
                var tone = Kind(k.Kind);
                var selected = i == kindIndex;
                return (Element)Button(VStack(8,
                            KindTile(k.Kind, 36),
                            TextBlock(t[k.Key]).FontSize(14).FontWeight(FontWeights.SemiBold),
                            Secondary(t["KindHint_" + k.Kind]).MaxLines(2)),
                        () => update(f => f.WithScheduleKind(k.Kind), false))
                    .HAlign(HorizontalAlignment.Stretch)
                    .VAlign(VerticalAlignment.Stretch)
                    .HorizontalContentAlignment(HorizontalAlignment.Left)
                    .VerticalContentAlignment(VerticalAlignment.Top)
                    .Padding(12)
                    .CornerRadius(10)
                    .BorderThickness(selected ? 2 : 1)
                    .BorderBrush(selected ? tone.Brush : Theme.CardStroke)
                    .Background(selected ? tone.Fill : Theme.CardBackground)
                    .Resources(r => r
                        .Set("ButtonBorderBrushPointerOver", tone.Brush)
                        .Set("ButtonBorderBrushPressed", tone.Brush))
                    .AutomationName(t[k.Key])
                    .ItemStatus(selected ? t["Selected"] : "")
                    .AutomationId($"AutomationEditorKind:{k.Kind}")
                    .Margin(i == 0 ? 0 : 4, 0, i == ScheduleKinds.Length - 1 ? 0 : 4, 0)
                    .Grid(column: i)
                    .WithKey(k.Key);
            }).ToArray();
            children.Add(Grid([GridSize.Star(), GridSize.Star(), GridSize.Star(), GridSize.Star()], [GridSize.Auto], cards)
                .AutomationId("AutomationEditorScheduleKind"));
        }

        switch (form.ScheduleKind)
        {
            case CronScheduleKind.Every:
                children.Add(Row(
                    Field(Text(t["Field_Every"], form.EveryAmount, v => update(f => f with { EveryAmount = v }, false), "AutomationEditorEveryAmount", "30"), err(CronFormField.EveryAmount)),
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
                children.Add(Check(t["Field_DeleteAfterRun"], form.DeleteAfterRun, v => update(f => f with { DeleteAfterRun = v }, false),
                    "AutomationEditorDeleteAfterRun", t["Help_DeleteAfterRun"]));
                break;
            case CronScheduleKind.Cron:
                children.Add(HStack(6, CronPresets.Select(p =>
                    (Element)Button(t[p.Key], () => update(f => f with { CronExpr = p.Expr }, false)).AutomationName(t[p.Key])
                        .Padding(10, 4)
                        .Resources(r => r.Set("ButtonBackground", form.CronExpr.Trim() == p.Expr ? Theme.SystemAttentionBackground : Theme.ControlFill))
                        .AutomationId($"AutomationEditorPreset:{p.Key}")).ToArray()));
                children.Add(Row(
                    Field(Text(t["Field_Expression"], form.CronExpr, v => update(f => f with { CronExpr = v }, false), "AutomationEditorCronExpr", "0 7 * * *"),
                        err(CronFormField.CronExpr), t["Help_Expression"]),
                    Field(Suggest(t["Field_Timezone"], form.CronTz, TimeZones.Value, v => update(f => f with { CronTz = v }, false), "AutomationEditorTimezone", t["Placeholder_Timezone"]),
                        err(CronFormField.CronTz), t["Help_Timezone"])));
                children.Add(Check(t["Field_ExactTiming"], form.ScheduleExact, v => update(f => f with { ScheduleExact = v }, false),
                    "AutomationEditorExact", t["Help_ExactTiming"]));
                if (!form.ScheduleExact)
                {
                    children.Add(Row(
                        Field(Text(t["Field_Stagger"], form.StaggerAmount, v => update(f => f with { StaggerAmount = v }, false), "AutomationEditorStagger",
                            CronDurations.DefaultStaggerMs(form.CronExpr) is { } d ? (d / 60_000).ToString() : "30"), err(CronFormField.StaggerAmount), t["Help_Stagger"]),
                        Choice(t["Field_Unit"], StaggerUnits.Select(u => t["Unit_" + u]).ToArray(), Array.IndexOf(StaggerUnits, form.StaggerUnit),
                            i => update(f => f with { StaggerUnit = StaggerUnits[i] }, false), "AutomationEditorStaggerUnit")));
                }
                break;
            case CronScheduleKind.Event when form.EventSource != CronProtocol.McpEventsSource:
                // Plugin-managed event sources are preserved on save, so their fields are not editable.
                children.Add((InfoBar(null, t["Schedule_ReadOnlyKind"]) with { Severity = InfoBarSeverity.Informational, IsClosable = false })
                    .AutomationId("AutomationEditorReadOnlyEventSource"));
                break;
            case CronScheduleKind.Event:
                children.AddRange(RenderEventSource(t, form, update, err, remote, refreshEvents));
                break;
        }

        var summary = t.FormSchedule(form);
        var summaryTone = Kind(form.ScheduleKind);
        children.Add(Border(HStack(Gap, Glyph(summaryTone.Glyph, 14, summaryTone.Brush),
                TextBlock(summary ?? t["Schedule_Incomplete"]).FontSize(13).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap)
                    .Foreground(summary is null ? Theme.SecondaryText : Theme.PrimaryText)))
            .Padding(12, 8).CornerRadius(8).Background(summaryTone.Fill)
            .BorderBrush(Theme.Ref("AutomationsTileStrokeBrush")).BorderThickness(1)
            .AutomationId("AutomationEditorScheduleSummary")
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite));

        children.Add(ToggleSwitch(form.ActiveHoursEnabled, v => update(f => f with { ActiveHoursEnabled = v }, false), t["On"], t["Off"], t["Field_ActiveHours"])
            .AutomationId("AutomationEditorActiveHours"));
        if (form.ActiveHoursEnabled)
        {
            children.Add(Secondary(t["Help_ActiveHours"]));
            children.Add(Row(
                Field(Text(t["Field_WindowStart"], form.ActiveHoursStart, v => update(f => f with { ActiveHoursStart = v }, false), "AutomationEditorActiveStart", "09:00"), err(CronFormField.ActiveHoursStart)),
                Field(Text(t["Field_WindowEnd"], form.ActiveHoursEnd, v => update(f => f with { ActiveHoursEnd = v }, false), "AutomationEditorActiveEnd", "17:00"), err(CronFormField.ActiveHoursEnd)),
                Field(Suggest(t["Field_Timezone"], form.ActiveHoursTimezone, ["local", "user", .. TimeZones.Value], v => update(f => f with { ActiveHoursTimezone = v }, false),
                    "AutomationEditorActiveTimezone", t["Placeholder_Timezone"]), err(CronFormField.ActiveHoursTimezone))));
        }

        return Section(t["Section_When"], GlyphCalendar, children.ToArray());
    }

    private static IEnumerable<Element?> RenderEventSource(
        AutomationsText t,
        CronJobForm form,
        Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err,
        EditorRemote remote,
        Action refreshEvents)
    {
        var servers = remote.Hints.McpServers.ToList();
        if (form.EventServer.Trim().Length > 0 && !servers.Contains(form.EventServer.Trim()))
            servers.Add(form.EventServer.Trim());
        var events = remote.Events.Select(e => e.Name).ToList();
        if (form.EventName.Trim().Length > 0 && !events.Contains(form.EventName.Trim()))
            events.Add(form.EventName.Trim());
        var definition = remote.Events.FirstOrDefault(e => e.Name == form.EventName.Trim());

        yield return Secondary(t["Event_Help"]);
        if (servers.Count == 0)
            yield return Secondary(t["Event_NoServers"]).AutomationId("AutomationEditorNoServers");
        yield return Row(
            Field(Suggest(t["Field_EventServer"], form.EventServer, servers,
                v => update(f => f with { EventServer = v, EventName = v.Trim() == f.EventServer.Trim() ? f.EventName : "", EventArguments = v.Trim() == f.EventServer.Trim() ? f.EventArguments : "{}" }, false),
                "AutomationEditorEventServer", t["Event_ChooseServer"]), err(CronFormField.EventServer)),
            Field(Suggest(t["Field_EventName"], form.EventName, events, v => update(f => f with { EventName = v }, false),
                "AutomationEditorEventName", t["Event_ChooseEvent"]), err(CronFormField.EventName), definition?.Description));
        yield return HStack(Gap,
            Button(t["Event_Refresh"], refreshEvents).AutomationName(t["Event_Refresh"]).IsEnabled(!remote.EventsLoading && form.EventServer.Trim().Length > 0).AutomationId("AutomationEditorEventRefresh"),
            remote.EventsLoading ? HStack(6, ProgressRing().Size(14, 14), Secondary(t["Event_Loading"])) : Empty());
        if (remote.EventsError is { } eventsError)
            yield return ErrorText(t.Format("Event_DiscoveryFailed", eventsError), "AutomationEditorEventError");
        else if (!remote.EventsLoading && remote.EventsServer is not null && remote.Events.Count == 0)
            yield return Secondary(t["Event_NoEvents"]);
        yield return Field(Text(t["Field_EventArguments"], form.EventArguments, v => update(f => f with { EventArguments = v }, false),
            "AutomationEditorEventArguments", "{}", multiline: true, mono: true), err(CronFormField.EventArguments), t["Help_EventArguments"]);
        if (definition?.InputSchemaJson is { } schema)
            yield return Expander(t["Event_ViewSchema"],
                Border(TextBlock(schema).FontFamily(MonoFont).FontSize(12).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)).Padding(8))
                .HAlign(HorizontalAlignment.Stretch);
    }

    private static Element RenderAction(AutomationsText t, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update, Func<CronFormField, string?> err, EditorRemote remote)
    {
        var children = new List<Element?>();
        if (form.PayloadLocked)
        {
            children.Add(Pill(t.PayloadKind(form.PayloadKind), Theme.AccentText, Theme.SystemAttentionBackground));
            children.Add(Border(TextBlock(form.PayloadText).FontFamily(MonoFont).FontSize(13).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true))
                .Padding(12).CornerRadius(6).Background(Theme.SubtleFill).AutomationId("AutomationEditorLockedPayload"));
            children.Add(Secondary(t["Payload_ReadOnlyHelp"]));
        }
        else
        {
            var isAgent = form.PayloadKind == CronPayloadKind.AgentTurn;
            children.Add(RadioButtons([t["Payload_AgentTurn"], t["Payload_SystemEvent"]], isAgent ? 0 : 1,
                    i => update(f => i == 0
                        ? f with { PayloadKind = CronPayloadKind.AgentTurn, SessionTarget = f.SessionTarget == "main" ? "isolated" : f.SessionTarget }
                        : f with { PayloadKind = CronPayloadKind.SystemEvent, SessionTarget = "main" }, false))
                .IsEnabled(form.ScheduleKind != CronScheduleKind.Event)
                .AutomationId("AutomationEditorPayloadKind"));
            children.Add(Secondary(isAgent ? t["Help_AgentTurn"] : t["Help_SystemEvent"]));
            children.Add(Field(Text(isAgent ? t["Field_Prompt"] : t["Field_TimelineMessage"], form.PayloadText,
                    v => update(f => f with { PayloadText = v }, false), "AutomationEditorPrompt", t["Placeholder_Prompt"], multiline: true),
                err(CronFormField.PayloadText)));
            if (isAgent)
            {
                children.Add(Row(
                    Field(Suggest(t["Field_Model"], form.PayloadModel, remote.Hints.Models, v => update(f => f with { PayloadModel = v }, false),
                        "AutomationEditorModel", t["Default"]), null, t["Help_Model"]),
                    Field(Suggest(t["Field_Thinking"], form.PayloadThinking, ThinkingSuggestions, v => update(f => f with { PayloadThinking = v }, false),
                        "AutomationEditorThinking", t["Default"]), null, t["Help_Thinking"])));
                children.Add(Field(Text(t["Field_TimeoutSeconds"], form.TimeoutSeconds, v => update(f => f with { TimeoutSeconds = v }, false),
                    "AutomationEditorTimeout", t["Placeholder_Timeout"]), err(CronFormField.TimeoutSeconds), t["Help_Timeout"]));
            }
        }

        string[] targets = ["isolated", "main", "current"];
        var targetLabels = targets.Select(t.SessionTarget).ToList();
        var targetIndex = Array.IndexOf(targets, form.SessionTarget);
        if (targetIndex < 0)
        {
            targetLabels.Add(t.SessionTarget(form.SessionTarget));
            targetIndex = targetLabels.Count - 1;
        }
        string[] wakes = ["now", "next-heartbeat"];
        children.Add(Row(
            Choice(t["Field_Session"], targetLabels.ToArray(), targetIndex,
                i => { if (i < targets.Length) update(f => f with { SessionTarget = targets[i] }, true); }, "AutomationEditorSession"),
            Choice(t["Field_Wake"], wakes.Select(t.WakeMode).ToArray(), Math.Max(0, Array.IndexOf(wakes, form.WakeMode)),
                i => update(f => f with { WakeMode = wakes[i] }, false), "AutomationEditorWake")));
        children.Add(Secondary(t["Help_Session"]));
        return Section(t["Section_What"], GlyphSparkle, children.ToArray());
    }

    /// <summary>Recipients other automations already deliver to; plain strings with no account attached.</summary>
    private static string[] SavedTargets(IEnumerable<CronJob> jobs) =>
        jobs.Select(j => j.Delivery?.To?.Trim()).OfType<string>().Where(to => to.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Element RenderDelivery(AutomationsText t, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err, EditorRemote remote, string routeKey, Func<bool> isCurrentRoute, string[] savedTargets)
    {
        var modes = new List<string> { "none", "announce", "webhook" };
        var labels = modes.Select(t.DeliveryMode).ToList();
        var modeIndex = modes.IndexOf(form.DeliveryMode);
        if (modeIndex < 0)
        {
            labels.Insert(0, t["DeliveryMode_Choose"]);
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
            // Directory targets belong to one account; offer them only once the operator names it (as on the web page).
            var account = form.DeliveryAccountId.Trim();
            var routeable = account.Length == 0 || remote.ConversationsKey != routeKey
                ? Array.Empty<ConversationTarget>()
                : remote.Conversations.Where(c => string.Equals(c.AccountId.Trim(), account, StringComparison.Ordinal)).ToArray();
            var suggestions = savedTargets.Concat(routeable.Select(c => c.DisplayLabel)).Distinct(StringComparer.Ordinal).ToArray();
            children.Add(Row(
                Field(Text(t["Field_Channel"], form.DeliveryChannel == CronProtocol.ChannelLast ? "" : form.DeliveryChannel,
                    v => update(f => f with { DeliveryChannel = string.IsNullOrWhiteSpace(v) ? CronProtocol.ChannelLast : v }, false),
                    "AutomationEditorChannel", t["Placeholder_ChannelLast"]), null, t["Help_Channel"]),
                form.DeliveryTarget == "owner"
                    ? Empty()
                    : Field(Suggest(t["Field_To"], form.DeliveryTo, suggestions, v =>
                        {
                            if (!isCurrentRoute()) return;
                            // A suggestion fills the recipient only; the account stays operator-authored.
                            var match = routeable.FirstOrDefault(c => c.DisplayLabel == v);
                            update(f => f.DeliveryAccountId.Trim() == account
                                ? f with { DeliveryTo = match?.Target ?? v }
                                : f, false);
                        }, "AutomationEditorTo", t["Placeholder_To"]),
                        null, remote.ConversationsKey == routeKey && remote.ConversationsError is { } convError
                            ? t.Format("Conversations_Failed", convError) : t["Help_To"])));
            children.Add(Row(
                Text(t["Field_Account"], form.DeliveryAccountId, v => update(f => f with { DeliveryAccountId = v }, false), "AutomationEditorAccount", t["Placeholder_Optional"]),
                Choice(t["Field_DirectPolicy"], [t["DirectPolicy_Default"], t["DirectPolicy_Allow"], t["DirectPolicy_Block"]],
                    form.DeliveryDirectPolicy switch { "allow" => 1, "block" => 2, _ => 0 },
                    i => update(f => f with { DeliveryDirectPolicy = i switch { 1 => "allow", 2 => "block", _ => "" } }, false), "AutomationEditorDirectPolicy")));
            if (form.DeliveryThreadId is { } thread)
                children.Add(Secondary(t.Format("Thread", thread)));
        }
        else if (form.DeliveryMode == "webhook")
        {
            children.Add(Field(Text(t["Field_WebhookUrl"], form.DeliveryTo, v => update(f => f with { DeliveryTo = v }, false), "AutomationEditorWebhook", "https://example.com/cron"),
                err(CronFormField.DeliveryTo), t["Help_Webhook"]));
        }
        if (form.DeliveryMode is "announce" or "webhook")
            children.Add(Check(t["Field_BestEffort"], form.DeliveryBestEffort, v => update(f => f with { DeliveryBestEffort = v }, false),
                "AutomationEditorBestEffort", t["Help_BestEffort"]));

        return Section(t["Section_Delivery"], GlyphSend, children.ToArray());
    }

    private static Element RenderFailureAlerts(AutomationsText t, CronJobForm form, Action<Func<CronJobForm, CronJobForm>, bool> update, Func<CronFormField, string?> err)
    {
        var modes = new[] { CronFailureAlertMode.Inherit, CronFailureAlertMode.Disabled, CronFailureAlertMode.Custom };
        var children = new List<Element?>
        {
            Choice(t["Field_FailureAlerts"], [t["FailureAlert_Inherit"], t["FailureAlert_Disabled"], t["FailureAlert_Custom"]],
                Array.IndexOf(modes, form.FailureAlertMode), i => update(f => f with { FailureAlertMode = modes[i] }, false), "AutomationEditorFailureMode"),
            Secondary(t["Help_FailureAlerts"]),
        };
        if (form.FailureAlertMode == CronFailureAlertMode.Custom)
        {
            string[] alertModes = ["", "announce", "webhook"];
            children.Add(Row(
                Field(Text(t["Field_AlertAfter"], form.FailureAlertAfter, v => update(f => f with { FailureAlertAfter = v }, false), "AutomationEditorAlertAfter", "3"),
                    err(CronFormField.FailureAlertAfter), t["Help_AlertAfter"]),
                Field(Text(t["Field_AlertCooldown"], form.FailureAlertCooldownSeconds, v => update(f => f with { FailureAlertCooldownSeconds = v }, false), "AutomationEditorAlertCooldown", "3600"),
                    err(CronFormField.FailureAlertCooldown), t["Help_AlertCooldown"])));
            children.Add(Row(
                Text(t["Field_AlertChannel"], form.FailureAlertChannel == CronProtocol.ChannelLast ? "" : form.FailureAlertChannel,
                    v => update(f => f with { FailureAlertChannel = string.IsNullOrWhiteSpace(v) ? CronProtocol.ChannelLast : v }, false), "AutomationEditorAlertChannel", t["Placeholder_ChannelLast"]),
                Text(t["Field_AlertTo"], form.FailureAlertTo, v => update(f => f with { FailureAlertTo = v }, false), "AutomationEditorAlertTo", t["Placeholder_To"])));
            children.Add(Row(
                Choice(t["Field_AlertMode"], [t["Default"], t["FailureAlert_Announce"], t["FailureAlert_Webhook"]],
                    Math.Max(0, Array.IndexOf(alertModes, form.FailureAlertDeliveryMode)), i => update(f => f with { FailureAlertDeliveryMode = alertModes[i] }, false), "AutomationEditorAlertMode"),
                Text(t["Field_AlertAccount"], form.FailureAlertAccountId, v => update(f => f with { FailureAlertAccountId = v }, false), "AutomationEditorAlertAccount", t["Placeholder_Optional"])));
        }
        return Section(t["Section_FailureAlerts"], GlyphAlert, children.ToArray());
    }

    private static Element RenderAdvanced(
        AutomationsText t,
        CronJobForm form,
        Action<Func<CronJobForm, CronJobForm>, bool> update,
        Func<CronFormField, string?> err,
        bool open,
        Action<bool> setOpen)
    {
        var isAgent = form.PayloadKind == CronPayloadKind.AgentTurn && !form.PayloadLocked;
        var triggerAllowed = form.ScheduleKind is CronScheduleKind.Every or CronScheduleKind.Cron or CronScheduleKind.Stream;
        var content = VStack(12,
            isAgent ? Check(t["Field_LightContext"], form.PayloadLightContext, v => update(f => f with { PayloadLightContext = v }, false), "AutomationEditorLightContext", t["Help_LightContext"]) : Empty(),
            isAgent ? Check(t["Field_IncludeReasoning"], form.PayloadIncludeReasoning, v => update(f => f with { PayloadIncludeReasoning = v }, false), "AutomationEditorIncludeReasoning", t["Help_IncludeReasoning"]) : Empty(),
            isAgent ? Check(t["Field_SkipEmptyScratch"], form.PayloadSkipIfScratchEmpty, v => update(f => f with { PayloadSkipIfScratchEmpty = v }, false), "AutomationEditorSkipScratch", t["Help_SkipEmptyScratch"]) : Empty(),
            Check(t["Field_IdleOnly"], form.IdleOnly, v => update(f => f with { IdleOnly = v }, false), "AutomationEditorIdleOnly", t["Help_IdleOnly"]),
            ToggleSwitch(form.TriggerEnabled, v => update(f => f with { TriggerEnabled = v }, false), t["On"], t["Off"], t["Field_Condition"])
                .IsEnabled(triggerAllowed || form.TriggerEnabled)
                .AutomationId("AutomationEditorTrigger"),
            Secondary(triggerAllowed ? t["Help_Condition"] : t["Error_TriggerScheduleUnsupported"]),
            form.TriggerEnabled
                ? VStack(8,
                    Field(Text(t["Field_TriggerScript"], form.TriggerScript, v => update(f => f with { TriggerScript = v }, false), "AutomationEditorTriggerScript",
                        "return json({ fire: true })", multiline: true, mono: true), err(CronFormField.TriggerScript), t["Help_TriggerScript"]),
                    Check(t["Field_TriggerOnce"], form.TriggerOnce, v => update(f => f with { TriggerOnce = v }, false), "AutomationEditorTriggerOnce", t["Help_TriggerOnce"]))
                : Empty(),
            Field(Text(t["Field_SessionKey"], form.SessionKey, v => update(f => f with { SessionKey = v }, false), "AutomationEditorSessionKey", t["Placeholder_Optional"]), null, t["Help_SessionKey"]),
            Check(t["Field_ClearAgent"], form.ClearAgent, v => update(f => f with { ClearAgent = v }, false), "AutomationEditorClearAgent", t["Help_ClearAgent"]));

        return Surface(Expander(t["Section_Advanced"], content, open, setOpen)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationId("AutomationEditorAdvanced"),
            padding: 4);
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
}
