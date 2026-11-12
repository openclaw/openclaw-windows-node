// Renders the native Systems page. Usage: mount Component<SystemsPageView, SystemsPageProps>(props) in its page-owned host.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Systems.SystemsText;

namespace OpenClawTray.Systems;

/// <summary>
/// Host-provided operations. They never prompt; the view confirms destructive or
/// privileged actions with its own dialogs first. Operations return an error message or null.
/// </summary>
internal sealed class SystemsPageActions
{
    public required Action<string> OpenSession { get; init; }
    public required Action<string> Navigate { get; init; }
    public required Action<string> CopyText { get; init; }
    /// <summary>Opens a Gateway web interface path (null for its root) through the app's dashboard launcher.</summary>
    public required Action<string?> OpenWeb { get; init; }
    public required Action<Action> PostUi { get; init; }
    public required Func<GatewayNodeInfo, string, Task<string?>> RenameNode { get; init; }
    public required Func<GatewayNodeInfo, Task<string?>> ForgetNode { get; init; }
}

/// <summary>
/// Confirmation dialogs owned by the Reactor tree. Busy dialogs close while the operation runs.
/// Every show gets a new <see cref="Show"/> key, so each open (including a reopen that reports an
/// error) mounts a fresh dialog instead of relying on in-place updates of an open one.
/// </summary>
internal abstract record SystemsDialog(string? Error = null, bool Busy = false)
{
    private static int s_shows;
    public int Show { get; init; }
    /// <summary>The connection the user reviewed (see <see cref="SystemsViewState.ConnectionEpoch"/>).</summary>
    public int Epoch { get; init; }
    public static int NextShow() => Interlocked.Increment(ref s_shows);
}
/// <summary>The typed name lives in a shared box so the close handler reads the latest text without re-rendering.</summary>
internal sealed record RenameNodeDialog(GatewayNodeInfo Node, StrongBox<string> Text) : SystemsDialog;
internal sealed record ForgetNodeDialog(GatewayNodeInfo Node, string GatewayName) : SystemsDialog;
internal sealed record AddSystemDialog() : SystemsDialog;

internal sealed record SystemsPageProps(SystemsController Controller, SystemsPageActions Actions)
{
    public SystemsViewport Viewport { get; init; } = new();
}
internal sealed record SystemsRowIssue(
    string Key,
    string Label,
    string Message,
    string Action,
    Action Invoke,
    SystemsHue Hue);

/// <summary>A themed foreground brush and its translucent tint, from Themes/SystemsResources.xaml.</summary>
internal readonly record struct SystemsHue(string Brush, string Tint)
{
    public static readonly SystemsHue Accent = new("SystemsLoadBrush", "SystemsLoadTintBrush");
    public static readonly SystemsHue Violet = new("SystemsMemoryBrush", "SystemsMemoryTintBrush");
    public static readonly SystemsHue Teal = new("SystemsDiskBrush", "SystemsDiskTintBrush");
    public static readonly SystemsHue Neutral = new("SystemsNeutralBrush", "SystemsNeutralTintBrush");
    public static readonly SystemsHue Success = new("SystemFillColorSuccessBrush", "SystemsSuccessTintBrush");
    public static readonly SystemsHue Caution = new("SystemFillColorCautionBrush", "SystemsCautionTintBrush");
    public static readonly SystemsHue Critical = new("SystemFillColorCriticalBrush", "SystemsCriticalTintBrush");

    /// <summary>Each kind of machine keeps one hue across the list, the hero and the summary.</summary>
    public static SystemsHue ForKind(SystemKind kind) => kind switch
    {
        SystemKind.Host => Accent,
        SystemKind.Node => Violet,
        SystemKind.Worker => Teal,
        _ => Neutral,
    };
}

/// <summary>
/// Native Systems inventory: one list of the Gateway host, paired nodes, workers and
/// connected apps, with details, current statistics, related sessions and desktop
/// availability for the selected machine. Pairing decisions belong to the Connection page;
/// desktop viewing and deeper machine work open the web interface.
/// </summary>
internal sealed class SystemsPageView : Component<SystemsPageProps>
{
    public override Element Render()
    {
        var controller = Props.Controller;
        var actions = Props.Actions;
        var viewport = Props.Viewport;
        var (topInset, setTopInset) = UseState(viewport.TopInset, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => setTopInset(viewport.TopInset);
            viewport.Changed += changed;
            setTopInset(viewport.TopInset);
            return () => viewport.Changed -= changed;
        }), viewport);
        var (state, setState) = UseState(controller.State, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => setState(controller.State);
            controller.Changed += changed;
            setState(controller.State);
            return () => controller.Changed -= changed;
        }), controller);

        var (dialog, setDialogState) = UseState<SystemsDialog?>(null, threadSafe: true);
        // The latest dialog state, read when an operation finishes after later renders.
        var currentDialog = UseRef<SystemsDialog?>(null);
        // New dialogs record the connection they were opened on; reopened ones keep it.
        Action<SystemsDialog?> setDialog = next =>
        {
            var stamped = next is null ? null : next with
            {
                Show = SystemsDialog.NextShow(),
                Epoch = next.Epoch == 0 ? controller.State.ConnectionEpoch : next.Epoch,
            };
            currentDialog.Current = stamped;
            setDialogState(stamped);
        };
        // A dialog opened before the connection changed must not act on the new one.
        if (dialog is not null && dialog.Epoch != state.ConnectionEpoch)
            dialog = null;
        var (layout, setLayout) = UseState(SystemsLayout.Default, threadSafe: true);
        var initialExpandedRows = UseMemo(() =>
            new HashSet<string>(state.SelectedId is { } selected ? [selected] : Array.Empty<string>(), StringComparer.Ordinal), []);
        var (expandedRows, setExpandedRows) = UseState(initialExpandedRows, threadSafe: true);
        var currentExpandedRows = UseRef(initialExpandedRows);
        void PublishExpandedRows(HashSet<string> next)
        {
            currentExpandedRows.Current = next;
            setExpandedRows(next);
        }
        var rowControls = UseRef(new Dictionary<string, Control>(StringComparer.Ordinal));
        // A row Expander created already expanded keeps its details unrealized. Expansion is therefore
        // shown only on the row's current native control once that control has loaded, the same path a
        // user expansion takes. A newly mounted control (deep link, preselection, or a row remounted into
        // another group) is held collapsed until its own Loaded; that native hold is not a user collapse.
        var readyRows = UseRef(new HashSet<Control>(ReferenceEqualityComparer.Instance));
        var rowLoadedHandlers = UseRef(new Dictionary<Control, RoutedEventHandler>(ReferenceEqualityComparer.Instance));
        var heldRows = UseRef(new HashSet<string>(StringComparer.Ordinal));
        var readyVersion = UseRef(0);
        var (_, setReadyVersion) = UseState(0, threadSafe: true);
        void ForgetRowControl(Control control)
        {
            if (rowLoadedHandlers.Current.Remove(control, out var handler))
                control.Loaded -= handler;
            readyRows.Current.Remove(control);
        }
        void MarkReady(Control control)
        {
            if (!rowLoadedHandlers.Current.ContainsKey(control) || !control.IsLoaded || !readyRows.Current.Add(control))
                return;
            setReadyVersion(++readyVersion.Current);
        }
        void TrackRow(string id, Control control)
        {
            rowControls.Current.TryGetValue(id, out var previous);
            rowControls.Current[id] = control;
            if (control is not Expander expander || rowLoadedHandlers.Current.ContainsKey(control))
                return;
            if (previous is not null && !ReferenceEquals(previous, control))
                ForgetRowControl(previous);
            RoutedEventHandler handler = (_, _) => MarkReady(control);
            rowLoadedHandlers.Current[control] = handler;
            control.Loaded += handler;
            if (expander.IsExpanded && !control.IsLoaded)
            {
                heldRows.Current.Add(id);
                try { expander.IsExpanded = false; }
                finally { heldRows.Current.Remove(id); }
            }
            if (control.IsLoaded)
                actions.PostUi(() => MarkReady(control));
        }
        bool ShowsExpanded(string id) =>
            rowControls.Current.TryGetValue(id, out var control) && readyRows.Current.Contains(control);
        UseEffect((Func<Action>)(() => () =>
        {
            foreach (var control in rowLoadedHandlers.Current.Keys.ToArray())
                ForgetRowControl(control);
        }), rowLoadedHandlers);
        var highContrast = UseHighContrast();
        var uiSettings = UseMemo(() => new global::Windows.UI.ViewManagement.UISettings(), []);
        var (reducedMotion, setReducedMotion) = UseState(!uiSettings.AnimationsEnabled, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            void Changed(global::Windows.UI.ViewManagement.UISettings sender, object args) =>
                setReducedMotion(!sender.AnimationsEnabled);
            uiSettings.AnimationsEnabledChanged += Changed;
            setReducedMotion(!uiSettings.AnimationsEnabled);
            return () => uiSettings.AnimationsEnabledChanged -= Changed;
        }), uiSettings);
        var (feedback, setFeedback) = UseState<string?>(null, threadSafe: true);
        var announce = this.UseAnnounce();
        UseEffect((Func<Action>)(() =>
        {
            var cancelled = false;
            if (feedback is not null)
                actions.PostUi(() => { if (!cancelled) announce.Announce(feedback); });
            return () => cancelled = true;
        }), feedback);
        UseEffect((Func<Action>)(() =>
        {
            var cancellation = new CancellationTokenSource();
            async Task DismissAsync()
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), cancellation.Token);
                    setFeedback(null);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            if (feedback is not null)
                _ = DismissAsync();
            return () => { cancellation.Cancel(); cancellation.Dispose(); };
        }), feedback);
        var expansionEpoch = UseRef(state.ConnectionEpoch);
        // Initial inventory can choose a default without an explicit Select request.
        var selectionKey = $"{state.ConnectionEpoch}:{state.SelectionRequest}:{state.SelectedId}";
        UseEffect((Func<Action>)(() =>
        {
            if (expansionEpoch.Current != state.ConnectionEpoch)
            {
                expansionEpoch.Current = state.ConnectionEpoch;
                PublishExpandedRows(new HashSet<string>(StringComparer.Ordinal));
            }
            if (state.SelectedId is { } selectedId)
                PublishExpandedRows(new HashSet<string>(currentExpandedRows.Current, StringComparer.Ordinal) { selectedId });
            return () => { };
        }), selectionKey);
        var now = DateTimeOffset.UtcNow;
        var currentRows = state.Rows.Select(row => row.Id).Concat(PairingRows(state).Select(request => request.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in rowControls.Current.Keys.Where(key => !currentRows.Contains(key)).ToArray())
        {
            ForgetRowControl(rowControls.Current[key]);
            rowControls.Current.Remove(key);
        }
        var shownExpandedRows = expandedRows.Where(ShowsExpanded).ToHashSet(StringComparer.Ordinal);
        void JumpToIssue()
        {
            var first = PairingRows(state).FirstOrDefault()?.Key ??
                SystemsProjection.OrderForInventory(state.Rows, DisplayName, state.Connected)
                    .FirstOrDefault(row => RowIssues(row, state, actions, now).Count > 0)?.Id;
            if (first is not null && rowControls.Current.TryGetValue(first, out var target) && target.IsLoaded)
            {
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = !reducedMotion });
                target.Focus(FocusState.Programmatic);
            }
        }
        return Grid([GridSize.Star()], [GridSize.Star()],
                (ScrollViewer(Grid([GridSize.Star()], [GridSize.Auto], VStack(24,
                    Header(state, actions, layout, setDialog, highContrast, reducedMotion, JumpToIssue),
                    Banners(state, controller, actions, layout),
                    Body(state, controller, actions, setDialog, now, layout, shownExpandedRows,
                        (id, expanded) =>
                        {
                            if (heldRows.Current.Contains(id))
                                return;
                            var next = new HashSet<string>(currentExpandedRows.Current, StringComparer.Ordinal);
                            if (expanded)
                            {
                                next.Add(id);
                                controller.Select(id);
                            }
                            else
                            {
                                next.Remove(id);
                                if (controller.State.SelectedId == id)
                                    controller.ToggleSelection(id);
                            }
                            PublishExpandedRows(next);
                        }, rowControls.Current, TrackRow))
                    .MaxWidth(PageResponsiveLayout.ContentMaxWidth)
                    .HAlign(HorizontalAlignment.Stretch).AutomationId("SystemsContentColumn")
                    .Margin(layout.PagePadding, 20, layout.PagePadding, 32)))
                    with { HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled })
                    .Margin(0, topInset, 0, 0)
                    .AutomationId("SystemsPageScroll").WithKey("scroll"),
                feedback is null ? Empty() : ResultToast(feedback).WithKey("result"),
                announce.Region.LiveRegion(AutomationLiveSetting.Polite).AutomationId("SystemsResultAnnouncements").WithKey("announcements"),
                Dialog(dialog, setDialog, actions, () => controller.State.ConnectionEpoch, () => currentDialog.Current, setFeedback))
            .OnSizeChanged((_, args) =>
            {
                setLayout(SystemsLayout.For(args.NewSize.Width, args.NewSize.Height));
            })
            .AutomationId("SystemsPageRoot");
    }

    private static Element ResultToast(string message) =>
        Border(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Glyph("\uE73E", 18).Foreground(Theme.Ref("SystemFillColorSuccessBrush"))
                .Margin(0, 0, 12, 0).VAlign(VerticalAlignment.Center).Grid(column: 0),
            TextBlock(message).TextWrapping(TextWrapping.WrapWholeWords).Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                .AutomationId("SystemsResultMessage").VAlign(VerticalAlignment.Center).Grid(column: 1)))
            .Padding(16, 12).Background(Theme.Ref("FlyoutPresenterBackground"))
            .BorderBrush(Theme.Ref("FlyoutBorderThemeBrush")).BorderThickness(1).CornerRadius(8)
            .Set(control => control.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow()).Translation(0, 0, 16)
            .MaxWidth(480).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom).Margin(20)
            .AutomationId("SystemsResult");

    // ── Header and banners ─────────────────────────────────────────────

    private static Element RefreshButton(SystemsViewState state, SystemsController controller)
    {
        var refreshing = state.Loading;
        return Button(
                    refreshing
                        ? ProgressRing().IsActive(true).Size(16, 16).AccessibilityView(AccessibilityView.Raw)
                        : Glyph("\uE72C", 16),
                () => _ = controller.RefreshAsync())
            .SubtleButton()
            .VAlign(VerticalAlignment.Center)
            .IsEnabled(state.Connected && !refreshing && state.CanRead)
            .AutomationName(S("Refresh_Name"))
            .ToolTip(S("Refresh_Name"))
            .AutomationId("SystemsRefreshButton");
    }

    private static Element Header(SystemsViewState state, SystemsPageActions actions, SystemsLayout layout,
        Action<SystemsDialog?> open, bool highContrast, bool reducedMotion, Action jumpToIssue)
    {
        var machines = state.Rows.Where(row => row.Kind is SystemKind.Host or SystemKind.Node).ToArray();
        var now = DateTimeOffset.UtcNow;
        var online = machines.Count(row => row.Status == SystemStatus.Online);
        var issues = PairingRows(state).Count + state.Rows.Count(row => RowIssues(row, state, actions, now).Count > 0);
        var gatewayName = state.GatewayName ?? (state.Rows.FirstOrDefault(row => row.Id == SystemsProjection.GatewayId) is { } host
            ? DisplayName(host) : S("Kind_Host"));
        var ownNodeOnline = machines.Any(row => row.IsThisDevice && row.Node is
            { IsOnline: true, ApprovalState: not (GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval) });
        var lineKey = !state.Connected ? state.LastRefreshedAt.HasValue ? "Hero_Disconnected" : "Hero_DisconnectedNoReport"
            : state.OwnNodePending ? "Hero_NodePending" : ownNodeOnline ? "Hero_NodeConnected" : "Hero_NodeNotReported";
        var line = F(lineKey, gatewayName, state.LastRefreshedAt is { } refreshed ? FormatDateTime(refreshed) : null);
        var answer = TextBlock(line).FontSize(14).TextWrapping(TextWrapping.WrapWholeWords)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).AutomationId("SystemsMastSubtitle")
            .Set(native =>
            {
                var nameAt = line.IndexOf(gatewayName, StringComparison.Ordinal);
                native.Inlines.Clear();
                if (nameAt < 0)
                    native.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line });
                else
                {
                    native.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[..nameAt] });
                    native.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = gatewayName, FontWeight = FontWeights.SemiBold,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            OpenClawTray.FunctionalUI.Core.Theme.ResolveColor("TextFillColorPrimary", native.ActualTheme)),
                    });
                    native.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[(nameAt + gatewayName.Length)..] });
                }
            });
        var buttons = WrapRow(8, OpenWebButton(state, actions, "SystemsMastOpenWeb"),
            Button(HStack(8, Glyph("\uE710", 14).VAlign(VerticalAlignment.Center),
                    TextBlock(S("AddSystem")).TextWrapping(TextWrapping.NoWrap).VAlign(VerticalAlignment.Center)),
                () => open(new AddSystemDialog()))
                .AccentButton().VAlign(VerticalAlignment.Center)
                .AutomationName(S("AddSystem")).AutomationId("SystemsAddSystem"))
            .AutomationId("SystemsMastActions");
        var tiles = PageHero.SummaryTiles("SystemsSummary", S("Title"),
            [
                new PageHeroTile(S(state.Connected ? "Status_Online" : "Hero_LastReportedOnline"),
                    state.HasLoaded && state.CanRead ? F("Hero_OnlineCount", Count(online), Count(machines.Length)) : S("Status_Unknown"),
                    "\uE73E", "SystemsOnlineTile",
                    !state.Connected || !state.HasLoaded || machines.Length == 0 ? PageHeroTone.Neutral
                        : online == machines.Length ? PageHeroTone.Success : PageHeroTone.Caution, StatusDot: true),
                new PageHeroTile(S("Issues"), state.CanRead && (state.HasLoaded || issues > 0) ? Count(issues) : S("Status_Unknown"),
                    "\uE7BA", "SystemsIssuesTile",
                    state.CanRead && issues > 0 ? PageHeroTone.Critical : PageHeroTone.Neutral,
                    OnInvoked: jumpToIssue, AutomationName: F("Hero_ShowIssues", Count(issues)),
                    IsEnabled: state.CanRead && issues > 0),
            ], layout.HostWidth, reducedMotion, highContrast)
            .AutomationName(S("Hero_Summary"));
        return VStack(12, PageHero.Header("SystemsHero", "\uE977", S("Title"), "SystemsPageMarker",
            answer, null, buttons, layout.CompactHeader, reducedMotion, highContrast, pageTag: "instances"), tiles)
            .AutomationId("SystemsMastGrid");
    }

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private static Element Banners(SystemsViewState state, SystemsController controller,
        SystemsPageActions actions, SystemsLayout layout)
    {
        var banners = new List<Element>();
        if (!state.Connected)
        {
            banners.Add(InfoBar(S("Disconnected_Title"), S("Disconnected_Message")).Error().IsClosable(false)
                .Content(Button(S("OpenConnection"), () => actions.Navigate("connection"))
                    .AutomationName(S("OpenConnection")).AutomationId("SystemsDisconnectedConnection").Margin(0, 0, 0, 8))
                .AutomationId("SystemsDisconnectedBanner").WithKey("disconnected"));
        }
        else if (!state.CanRead)
        {
            banners.Add(InfoBar(S("ReadAccess_Title"), S("ReadAccess_Message")).Warning().IsClosable(false)
                .AutomationId("SystemsReadAccessBanner").WithKey("read-access"));
        }

        if (state.InventoryError is { } error)
        {
            var failure = InfoBar(S("LoadFailed"),
                state.InventoryTimedOut
                    ? F("RequestTimeout", Count((int)SystemsController.InventoryTimeout.TotalSeconds))
                    : error);
            Element recoveryActions = layout.Tier == PageWidthTier.Narrow
                ? VStack(8,
                    Button(S("Retry"), () => _ = controller.RefreshAsync()).AutomationName(S("Retry"))
                        .AutomationId("SystemsRetry").IsEnabled(state.Connected && !state.Loading),
                    Button(S("Connection"), () => actions.Navigate("connection")).AutomationName(S("Connection"))
                        .AutomationId("SystemsInventoryConnection"))
                : HStack(8,
                    Button(S("Retry"), () => _ = controller.RefreshAsync()).AutomationName(S("Retry"))
                        .AutomationId("SystemsRetry").IsEnabled(state.Connected && !state.Loading),
                    Button(S("Connection"), () => actions.Navigate("connection")).AutomationName(S("Connection"))
                        .AutomationId("SystemsInventoryConnection"));
            banners.Add((state.HasLoaded ? failure.Warning() : failure.Error()).IsClosable(false)
                .Content(recoveryActions)
                .AutomationId("SystemsInventoryErrorBanner").WithKey("inventory-error"));
        }

        if (state.PartialErrors.Any)
        {
            banners.Add(InfoBar(S("PartialErrors_Title"), string.Join("\n", state.PartialErrors.Messages)).Informational().IsClosable(false)
                .AutomationId("SystemsPartialErrorsBanner").WithKey("partial-errors"));
        }

        return banners.Count == 0 ? Empty() : VStack(8, banners.ToArray()).Margin(0, 0, 0, 12);
    }

    private static IReadOnlyList<PendingApproval> PairingRows(SystemsViewState state)
    {
        var requests = state.PendingApprovals.DistinctBy(approval => approval.Key).ToList();
        if (state.OwnNodePending && !requests.Any(approval => approval.Kind == PairingApprovalKind.NodePair &&
                string.Equals(approval.DeviceId, state.LocalNodeId, StringComparison.OrdinalIgnoreCase)))
        {
            requests.Insert(0, new PendingApproval
            {
                Kind = PairingApprovalKind.NodePair, DeviceId = state.LocalNodeId ?? "",
                DisplayName = S("ThisDevice"), Role = "node",
            });
        }
        return requests;
    }

    private static Element IssueBadge(string automationId, string label, string message, SystemsHue hue) =>
        PageHero.AttentionBadge(automationId, label, message,
            hue == SystemsHue.Accent ? "\uE916" : hue == SystemsHue.Critical ? "\uE783" : "\uE7BA",
            hue == SystemsHue.Accent ? PageHeroTone.Accent
                : hue == SystemsHue.Critical ? PageHeroTone.Critical : PageHeroTone.Caution,
            !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled);

    private static IReadOnlyList<SystemsRowIssue> RowIssues(
        SystemRow row, SystemsViewState state, SystemsPageActions actions, DateTimeOffset now)
    {
        var items = new List<SystemsRowIssue>();
        if (row.Kind == SystemKind.Client)
            return items;
        var approval = row.Node?.ApprovalState;
        if (row.IsThisDevice && state.OwnNodePending ||
            approval is GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval)
        {
            items.Add(new SystemsRowIssue("approval", S(approval == GatewayNodeApprovalState.PendingReapproval
                    ? "Badge_Reapproval" : "Badge_Approval"), S("AwaitingApproval"),
                S("Pairing_Review"), () => actions.Navigate("connection"), SystemsHue.Accent));
        }
        if (row.Status == SystemStatus.Offline)
        {
            var nowForGateway = now - state.GatewayClockOffset;
            var disconnectedAt = row.Environment?.LastDisconnectedAt;
            items.Add(new SystemsRowIssue("offline", !state.Connected ? S("LastKnownOffline")
                    : disconnectedAt is { } time && time <= nowForGateway
                    ? F("Badge_OfflineAge", CompactAge(time, nowForGateway)) : S("Status_Offline"),
                IssueLine(row, state, nowForGateway) ?? S("Offline_Title"), S("HowToReconnect"),
                () => actions.Navigate("connection"), SystemsHue.Critical));
        }
        if (row.Environment?.Issues.Any(value => value.Code == "update-required") == true)
        {
            items.Add(new SystemsRowIssue("update", S("Badge_Update"), S("Issue_UpdateRequired"),
                S("CopyUpdate"), () => actions.CopyText("openclaw update"), SystemsHue.Caution));
        }
        if (row.Node?.Permissions is { } permissions && permissions.Any(permission => !permission.Value))
        {
            items.Add(new SystemsRowIssue("permissions", S(row.Platform is "win32" or "windows"
                    ? "Badge_Permission" : "Capability_TurnedOff"),
                string.Join(", ", permissions.Where(permission => !permission.Value)
                    .Select(permission => F("Capability_Disabled", CapabilityLabel(permission.Key)))),
                row.IsThisDevice ? S("ManagePermissions") : S("OpenWeb"),
                row.IsThisDevice ? () => actions.Navigate("permissions")
                    : () => actions.OpenWeb(WebDestination(state)), SystemsHue.Caution));
        }
        if (row.Environment?.Worker is { Error.Length: > 0 } || row.Status == SystemStatus.Error)
        {
            items.Add(new SystemsRowIssue("error", S("Badge_WorkerError"),
                row.Environment?.Worker?.Error ?? IssueLine(row, state, now - state.GatewayClockOffset) ?? S("Status_Error"),
                S("OpenWeb"), () => actions.OpenWeb(WebDestination(state)), SystemsHue.Critical));
        }
        return items;
    }

    // ── Body ───────────────────────────────────────────────────────────

    private static Element PairingRow(PendingApproval request, SystemsViewState state,
        SystemsPageActions actions, SystemsLayout layout, Dictionary<string, Control> rowControls)
    {
        var name = string.IsNullOrWhiteSpace(request.DisplayName) ? S("Pairing_TitleOne") : request.DisplayName;
        var reason = !state.Connected ? S("ConnectionRequired")
            : !state.CanManageNodes ? S("Manage_PairingRequired")
            : !request.IsActionable ? S("Pairing_Unavailable") : "";
        var review = TextBlock(S("Pairing_Review")).VAlign(VerticalAlignment.Center);
        var title = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            TextBlock(name).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(name).Grid(column: 0),
            IssueBadge("SystemsPairingBadge_" + request.Key, S("Badge_Join"), S("Pairing_Message"), SystemsHue.Accent)
                .Margin(8, 0, 0, 0).Grid(column: 1));
        var content = VStack(4, title,
            Note(Join(request.Platform, S("Pairing_Message"), reason) ?? S("Pairing_Message")));
        Element header = layout.CompactHeader
            ? VStack(8, content, review.HAlign(HorizontalAlignment.Left))
            : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                content.Grid(column: 0), review.Margin(16, 0, 0, 0).Grid(column: 1));
        // Review only opens Connection. It does not approve or require a decision id.
        return Button(header, () => actions.Navigate("connection"))
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationName(name + ", " + S("Badge_Join") + ", " + S("Pairing_Review"))
            .HelpText(reason)
            .AutomationId("SystemsPairing_" + request.Key)
            .Set(control => rowControls[request.Key] = control).WithKey("pairing:" + request.Key);
    }

    private static Element Body(SystemsViewState state, SystemsController controller, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now, SystemsLayout layout,
        IReadOnlySet<string> expandedRows, Action<string, bool> toggleExpanded,
        Dictionary<string, Control> rowControls, Action<string, Control> trackRow)
    {
        if (!state.CanRead)
            return Card(VStack(8, TextBlock(S("ReadAccess_Title")).FontSize(20), Note(S("ReadAccess_Message"))))
                .Padding(24).AutomationId("SystemsReadAccessPanel");
        return VStack(16,
            state.SelectionMissing
                ? Placeholder("\uE7BA", SystemsHue.Caution, S("Missing_Title"), S("Missing_Hint"), "SystemsSelectionMissing")
                : Empty(),
            Inventory(state, controller, actions, open, now, layout, expandedRows, toggleExpanded, rowControls, trackRow));
    }

    private static Element Inventory(SystemsViewState state, SystemsController controller, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now, SystemsLayout layout,
        IReadOnlySet<string> expandedRows, Action<string, bool> toggleExpanded, Dictionary<string, Control> rowControls,
        Action<string, Control> trackRow)
    {
        var visible = SystemsProjection.OrderForInventory(state.Rows, DisplayName, state.Connected);
        var requests = PairingRows(state);

        Element list;
        if (!state.HasLoaded && state.Loading)
        {
            list = VStack(12, TextBlock(S("Loading")),
                Card(Border(null).Height(64).Background(Theme.Ref("SubtleFillColorSecondaryBrush"))),
                Card(Border(null).Height(64).Background(Theme.Ref("SubtleFillColorSecondaryBrush"))),
                Card(Border(null).Height(64).Background(Theme.Ref("SubtleFillColorSecondaryBrush"))))
                .AutomationName(S("Loading")).AutomationId("SystemsLoading");
        }
        else if (!state.HasLoaded && state.InventoryError is not null)
            list = Empty();
        else if (visible.Count == 0 && requests.Count == 0)
        {
            list = VStack(8,
                    Glyph("\uE977", 32).HAlign(HorizontalAlignment.Center)
                        .Foreground(Theme.Ref("TextFillColorTertiaryBrush")),
                    TextBlock(S("Empty")).TextWrapping(TextWrapping.WrapWholeWords)
                        .TextAlignment(TextAlignment.Center).Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                .Margin(16, 32, 16, 0)
                .AutomationId("SystemsEmpty");
        }
        else
        {
            var groups = new List<Element>();
            if (requests.Count > 0)
                groups.Add(PageHero.WithMotion(VStack(PageResponsiveLayout.GapInner, requests.Select(request =>
                    PairingRow(request, state, actions, layout, rowControls).WithKey("pairing:" + request.Key)).ToArray())
                    .AutomationId("SystemsPairingRows"),
                    !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled).WithKey("pairing-group"));
            var devices = visible.Where(row => row.Kind is SystemKind.Host or SystemKind.Node).ToArray();
            if (devices.Length > 0)
                groups.Add(InventoryGroup(S("DevicesAndGateway"), SystemKind.Host, devices,
                    state, actions, open, now, layout, expandedRows, toggleExpanded, trackRow, "SystemsDevicesGroup"));
            var workers = visible.Where(row => row.Kind == SystemKind.Worker).ToArray();
            var clients = visible.Where(row => row.Kind == SystemKind.Client).ToArray();
            if (workers.Length > 0)
                groups.Add(InventoryGroup(S("Group_Workers"), SystemKind.Worker, workers,
                    state, actions, open, now, layout, expandedRows, toggleExpanded, trackRow, "SystemsWorkersGroup", true));
            if (clients.Length > 0)
                groups.Add(InventoryGroup(S("ConnectedApps"), SystemKind.Client, clients,
                    state, actions, open, now, layout, expandedRows, toggleExpanded, trackRow, "SystemsConnectedAppsGroup", true));
            list = VStack(12, groups.ToArray());
        }

        return VStack(12, Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(F("DevicesAndGatewayCount",
                        Count(visible.Count(row => row.Kind is SystemKind.Host or SystemKind.Node))))
                    .FontSize(18).FontWeight(FontWeights.SemiBold)
                    .HeadingLevel(AutomationHeadingLevel.Level2)
                    .AutomationId("SystemsDevicesAndGatewayHeading").Grid(column: 0),
                RefreshButton(state, controller).Grid(column: 1)),
                list)
            .AutomationName(S("Inventory"))
            .AutomationId("SystemsInventory");
    }

    private static Element InventoryGroup(string title, SystemKind kind, IReadOnlyList<SystemRow> rows,
        SystemsViewState state, SystemsPageActions actions, Action<SystemsDialog?> open,
        DateTimeOffset now, SystemsLayout layout, IReadOnlySet<string> expandedRows,
        Action<string, bool> toggleExpanded, Action<string, Control> trackRow, string automationId, bool showHeading = false)
    {
        var heading = $"{title} ({Count(rows.Count)})";
        var content = PageHero.WithMotion(VStack(PageResponsiveLayout.GapInner, rows.Select(row =>
                Expander("", Details(row, state, actions, open, now, layout),
                        expandedRows.Contains(row.Id), expanded => toggleExpanded(row.Id, expanded))
                    .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
                    .HeaderTemplate(InventoryItem(row, state, actions, layout))
                    .AutomationName(Join(DisplayName(row), StatusLabel(row, state),
                        string.Join(", ", RowIssues(row, state, actions, now).Select(issue => issue.Label))) ?? DisplayName(row))
                    .Set(control => trackRow(row.Id, control))
                    .HelpText(S(expandedRows.Contains(row.Id) ? "CollapseSystem" : "ExpandSystem"))
                    .AutomationId("SystemsRow_" + row.Id).WithKey(row.Id)).ToArray()),
                !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            .AutomationName(heading)
            .AutomationId(automationId);
        if (!showHeading)
            return content.WithKey(automationId);
        return VStack(4,
            HStack(8,
                Border(null).Size(8, 8).CornerRadius(4).Background(Theme.Ref(SystemsHue.ForKind(kind).Brush))
                    .VAlign(VerticalAlignment.Center).AccessibilityView(AccessibilityView.Raw),
                TextBlock(heading).FontSize(12).FontWeight(FontWeights.SemiBold)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .HeadingLevel(AutomationHeadingLevel.Level2))
                .Margin(4, 0, 0, 0),
            content).WithKey(automationId);
    }

    private static Element InventoryItem(SystemRow row, SystemsViewState state, SystemsPageActions actions, SystemsLayout layout)
    {
        var name = DisplayName(row);
        var status = EffectiveStatus(row, state.Connected);
        var secondary = Join(Kind(row), row.Platform);
        var badges = new List<Element>();
        var issues = RowIssues(row, state, actions, DateTimeOffset.UtcNow);
        badges.AddRange(issues.Select((issue, index) =>
            IssueBadge("SystemsBadge_" + row.Id + "_" + index, issue.Label, issue.Message, issue.Hue)
                .WithKey(row.Id + ":" + issue.Key)));
        if (row.IsThisDevice && !layout.CompactHeader)
            badges.Add(Tag(S("ThisDevice")));
        var statusLabel = StatusLabel(row, state);
        var helpText = row.Platform is { Length: > 0 } platform ? $"{statusLabel}, {platform}" : statusLabel;
        var issue = IssueLine(row, state, DateTimeOffset.UtcNow - state.GatewayClockOffset);
        var summary = issues.FirstOrDefault(value => value.Key == "update")?.Label ?? issue;
        var meta = VStack(4, StatusBadge(status, statusLabel),
                summary is not null ? TextBlock(summary).FontSize(12).TextWrapping(TextWrapping.Wrap)
                    .AutomationId("SystemsIssueSummary_" + row.Id).ToolTip(issue ?? summary)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")) : Empty())
            .MaxWidth(300)
            .HAlign(layout.CompactHeader ? HorizontalAlignment.Left : HorizontalAlignment.Stretch)
            .AutomationId("SystemsMeta_" + row.Id);
        return Grid([GridSize.Auto, GridSize.Star(), layout.CompactHeader ? GridSize.Auto : GridSize.Px(SystemsLayout.HeaderMetadataWidth)],
                layout.CompactHeader ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                MachineAvatar(row, status, 36, 18).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                VStack(2,
                    TextBlock(name).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(name)
                        .AutomationId(row.Id == state.SelectedId ? "SystemsSelectedName" : "SystemsName_" + row.Id)
                        .Opacity(status == SystemStatus.Offline ? 0.75 : 1),
                    TextBlock(secondary ?? "").FontSize(12).TextWrapping(TextWrapping.NoWrap)
                        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(secondary ?? "")
                        .AutomationId("SystemsSubtitle_" + row.Id)
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                    (FlexRow(badges.ToArray()) with
                    {
                        Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap,
                        ColumnGap = 6,
                        RowGap = 4,
                        AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Center,
                    })
                        .Margin(0, badges.Count > 0 ? 4 : 0, 0, 0)
                        .WithKey("SystemsRowBadges-" + row.Id)).VAlign(VerticalAlignment.Center).Grid(column: 1),
                meta.VAlign(VerticalAlignment.Center).Margin(layout.CompactHeader ? 0 : 12, layout.CompactHeader ? 8 : 0, 0, 0)
                    .Grid(row: layout.CompactHeader ? 1 : 0, column: layout.CompactHeader ? 1 : 2))
            .Padding(0, PageResponsiveLayout.GapInner)
            .AutomationName(name)
            .HelpText(helpText)
            .ToolTip(name)
            .AutomationId("SystemsItem_" + row.Id);
    }

    private static string StatusLabel(SystemRow row, SystemsViewState state)
    {
        if (!state.Connected)
            return row.Status switch
            {
                SystemStatus.Online => S("LastKnownOnline"),
                SystemStatus.Offline => S("LastKnownOffline"),
                _ => Status(EffectiveStatus(row, false)),
            };
        return Status(EffectiveStatus(row, true));
    }

    private static string? IssueLine(SystemRow row, SystemsViewState state, DateTimeOffset now)
    {
        if (row.Environment?.Issues.Any(issue => issue.Code == "update-required") == true)
            return S("Issue_UpdateRequired");
        if (row.IsThisDevice && state.OwnNodePending)
            return S("Pairing_OwnNodeMessage");
        if (row.Node?.ApprovalState is GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval)
            return S("AwaitingApproval");
        if (!state.Connected)
            return row.Environment?.LastSeenAt is { } lastSeen ? F("NotSeen", Ago(lastSeen, now)) : null;
        if (row.Status == SystemStatus.Offline && row.Environment?.LastSeenAt is { } seen)
            return F("NotSeen", Ago(seen, now));
        if (row.Environment?.Worker is { } worker)
            return Join(worker.Error, worker.State, worker.TunnelStatus);
        if (row.Node?.Permissions.FirstOrDefault(pair => !pair.Value) is { Key: { Length: > 0 } key })
            return F("Capability_Disabled", CapabilityLabel(key));
        return row.Status == SystemStatus.Offline ? S("Offline_Title") : null;
    }

    // ── Details ────────────────────────────────────────────────────────

    private static Element Details(SystemRow row, SystemsViewState state, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now, SystemsLayout layout)
    {
        var gatewayNow = now - state.GatewayClockOffset;
        if (row.Kind == SystemKind.Client)
            return MachineDetails(row, state, actions, gatewayNow).AutomationId("SystemsPresenceDetails");
        var sections = new List<Element> { DetailsHeader(row, state, actions, open).WithKey("header") };
        var issues = RowIssues(row, state, actions, now);
        if (issues.Count > 0)
            sections.Add(VStack(8, issues.Select((issue, index) =>
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    Note(issue.Message).Grid(column: 0),
                    HyperlinkButton(issue.Action, onClick: issue.Invoke).AutomationName(issue.Action)
                        .AutomationId("SystemsIssueAction_" + row.Id + "_" + index)
                        .Margin(12, 0, 0, 0).Grid(column: 1)).WithKey(issue.Key)).ToArray()).WithKey("issues"));
        var right = new List<Element> { Statistics(row, state, now, layout).WithKey("stats") };
        if (DesktopPresentation.Resolve(row, state.Connected) is { } desktop)
            right.Add(Desktop(row, desktop).WithKey("desktop"));
        right.Add(Sessions(row, actions, gatewayNow).WithKey("sessions"));
        var left = Capabilities(row, actions);
        // Capabilities keep a fixed readable width; the readings and sessions take the remaining space.
        sections.Add(Grid(layout.Stacked
                    ? [GridSize.Star()]
                    : [GridSize.Px(SystemsLayout.CapabilitiesWidth), GridSize.Px(SystemsLayout.DetailsColumnGap), GridSize.Star()],
                layout.Stacked ? [GridSize.Auto, GridSize.Px(PageResponsiveLayout.GapStack), GridSize.Auto] : [GridSize.Auto],
                left.VAlign(VerticalAlignment.Top).Grid(column: 0),
                VStack(PageResponsiveLayout.GapStack, right.ToArray())
                    .Grid(row: layout.Stacked ? 2 : 0, column: layout.Stacked ? 0 : 2))
            .AutomationId("SystemsDetailsGrid").WithKey("details-grid"));
        sections.Add(Expander(S("Diagnostics"), VStack(12,
                MachineDetails(row, state, actions, gatewayNow),
                DiagnosticDeclarations(row),
                Button(S("CopyDebug"), () => actions.CopyText(DebugSummary(row)))
                    .AutomationName(S("CopyDebug")).AutomationId("SystemsCopyDebug").HAlign(HorizontalAlignment.Left)), false)
            .HAlign(HorizontalAlignment.Stretch).AutomationId("SystemsDiagnostics").WithKey("diagnostics"));
        return VStack(16, sections.ToArray()).WithKey("expanded:" + row.Id)
            .AutomationId("SystemsDetails");
    }

    private static Element Placeholder(string glyph, SystemsHue hue, string title, string hint, string automationId) =>
        VStack(12,
                GlyphChip(glyph, hue, 72, 32).HAlign(HorizontalAlignment.Center),
                TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold).TextAlignment(TextAlignment.Center).TextWrapping(TextWrapping.WrapWholeWords),
                TextBlock(hint).TextWrapping(TextWrapping.WrapWholeWords).TextAlignment(TextAlignment.Center).MaxWidth(420)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
            .AutomationId(automationId);

    private static Element DetailsHeader(SystemRow row, SystemsViewState state, SystemsPageActions actions,
        Action<SystemsDialog?> open)
    {
        var primary = new List<AppBarButtonData>();
        var secondary = new List<AppBarButtonData>
        {
            AppBarButton(S("CopyIdentifier"), () => actions.CopyText(row.Node?.NodeId ?? row.Id), "\uE8C8"),
        };
        if (row.IsThisDevice)
            primary.Add(AppBarButton(S("OpenConnection"), () => actions.Navigate("connection"), "\uE713"));
        Element manageNote = Empty();
        if (row.CanManageNode && row.Node is { } node)
        {
            var canManage = state.Connected && state.CanManageNodes;
            primary.Add(AppBarButton(LocalizationHelper.GetString("NodesPage_Action_Rename"),
                    () => open(new RenameNodeDialog(node, new StrongBox<string>(node.HasExplicitDisplayName ? node.DisplayName : ""))))
                with { Icon = "\uE8AC", IsEnabled = canManage });
            secondary.Add(AppBarButton(LocalizationHelper.GetString("NodesPage_Action_Forget"),
                    () => open(new ForgetNodeDialog(node,
                        state.Rows.FirstOrDefault(value => value.Id == SystemsProjection.GatewayId) is { } gateway
                            ? DisplayName(gateway) : S("Kind_Host"))), "\uE74D") with { IsEnabled = canManage });
            if (!canManage)
            {
                manageNote = TextBlock(S(state.Connected ? "Manage_PairingRequired" : "ConnectionRequired"))
                    .FontSize(12).TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).Margin(0, 8, 0, 0)
                    .AutomationId("SystemsManageRequiresPairing");
            }
        }

        return VStack(8,
            CommandBar(primary.ToArray(), secondary.ToArray())
                .Set(bar =>
                {
                    bar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
                    bar.IsDynamicOverflowEnabled = true;
                    var priority = 1;
                    foreach (var button in bar.PrimaryCommands.OfType<Microsoft.UI.Xaml.Controls.AppBarButton>().Reverse())
                        button.DynamicOverflowOrder = priority++;
                    NameCommands(bar);
                })
                .AutomationId("SystemsCommandBar"),
            manageNote);
    }

    private static void NameCommands(Microsoft.UI.Xaml.Controls.CommandBar bar)
    {
        foreach (var button in bar.PrimaryCommands.Concat(bar.SecondaryCommands).OfType<Microsoft.UI.Xaml.Controls.AppBarButton>())
        {
            var id = button.Label == LocalizationHelper.GetString("NodesPage_Action_Rename") ? "SystemsRenameNode"
                : button.Label == LocalizationHelper.GetString("NodesPage_Action_Forget") ? "SystemsForgetNode"
                : button.Label == S("CopyIdentifier") ? "SystemsCopyIdentifier" : "SystemsConnection";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, button.Label);
        }
    }

    // ── Statistics ─────────────────────────────────────────────────────

    /// <summary>
    /// The latest reported resource readings with their age. Only reported values are shown:
    /// Windows nodes report processor count, memory and system-drive space, not processor usage.
    /// </summary>
    private static Element Statistics(SystemRow row, SystemsViewState state, DateTimeOffset now, SystemsLayout layout)
    {
        if (row.Measurements is not { } stats)
        {
            return Section(S("Stats_Title"),
                TextBlock(S("Stats_None")).TextWrapping(TextWrapping.WrapWholeWords).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .AutomationId("SystemsNoStatistics"),
                null, "SystemsStatistics");
        }

        var refreshFailed = row.Id == SystemsProjection.GatewayId
            ? state.PartialErrors.SystemInfo is not null
            : state.PartialErrors.Nodes is not null;
        var lastKnown = SystemsFreshness.IsLastKnown(row, state.Connected, refreshFailed, now);

        var tiles = new List<Element>
        {
            StatTile("cpu", S("Stats_Cpu"), SystemsHue.Accent,
                stats.CpuCount > 0 ? stats.CpuCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : S("Capability_NotReported"), S("Stats_CpuCores"),
                null, null, lastKnown),
        };
        if (stats.Load1 is { } load)
        {
            var percent = SystemsMeters.LoadPercent(stats);
            tiles.Add(StatTile("load", S("Stats_Load"), SystemsHue.Accent, Load(load), F("Stats_Cores", stats.CpuCount),
                percent, percent is { } p ? F("Stats_LoadMeter", Math.Round(p)) : null, lastKnown));
        }

        var memory = SystemsMeters.MemoryUsedPercent(stats);
        tiles.Add(StatTile("memory", S("Stats_Memory"), SystemsHue.Violet, Bytes(stats.MemoryUsedBytes),
            F("Stats_OfTotal", Bytes(stats.MemoryTotalBytes)),
            memory, memory is { } m ? F("Stats_UsedMeter", Math.Round(m)) : null, lastKnown));

        if (stats.Disks is { Count: > 0 } disks)
        {
            // Disk paths key the tiles, so a repeated path is shown once.
            foreach (var disk in disks.DistinctBy(disk => disk.Path, StringComparer.Ordinal))
            {
                var used = SystemsMeters.DiskUsedPercent(disk.AvailableBytes, disk.TotalBytes);
                tiles.Add(StatTile("disk:" + disk.Path, F("Stats_DiskPath", disk.Path), SystemsHue.Teal,
                    Bytes(disk.AvailableBytes), F("Stats_OfTotal", Bytes(disk.TotalBytes)),
                    used, used is { } u ? F("Stats_UsedMeter", Math.Round(u)) : null, lastKnown));
            }
        }
        else if (stats.DiskAvailableBytes is { } available)
        {
            var used = SystemsMeters.DiskUsedPercent(available, stats.DiskTotalBytes);
            tiles.Add(StatTile("disk", S("Stats_Disk"), SystemsHue.Teal, Bytes(available),
                stats.DiskTotalBytes is { } total ? F("Stats_OfTotal", Bytes(total)) : "",
                used, used is { } u ? F("Stats_UsedMeter", Math.Round(u)) : null, lastKnown));
        }

        Element sampled = Empty();
        if (row.MeasuredAt is { } measuredAt)
        {
            sampled = TextBlock(F("Stats_Sampled", CompactAge(measuredAt, now))).TextWrapping(TextWrapping.WrapWholeWords)
                        .FontSize(12).Foreground(Theme.Ref("TextFillColorTertiaryBrush"))
                .AutomationId("SystemsStatsSampled");
        }

        var freshnessBrush = lastKnown ? "TextFillColorSecondaryBrush" : "SystemFillColorSuccessBrush";
        var freshness = HStack(4, Glyph(lastKnown ? "\uE823" : "\uE91F", 10).Foreground(Theme.Ref(freshnessBrush)),
            TextBlock(S(lastKnown ? "Stats_LastKnownBadge" : "Stats_LiveBadge")).FontSize(12)
                .Foreground(Theme.Ref(freshnessBrush)));
        return Section(S("Stats_Title"),
            Component<SystemsStatRows, SystemsStatRowsProps>(new(tiles)), sampled, "SystemsStatistics", freshness);
    }

    private sealed record SystemsStatRowsProps(IReadOnlyList<Element> Tiles);

    // Expander template padding reduces the actual column below the host-width estimate.
    private sealed class SystemsStatRows : Component<SystemsStatRowsProps>
    {
        public override Element Render()
        {
            var (width, setWidth) = UseState(0d, threadSafe: true);
            var columns = PageResponsiveLayout.TileColumns(width, SystemsLayout.StatTileMinWidth);
            return Rows(Props.Tiles, columns, stretchLastSingle: true)
                .OnSizeChanged((_, args) => setWidth(args.NewSize.Width))
                .AutomationId("SystemsStatRows");
        }
    }

    private static Element StatTile(string metric, string label, SystemsHue hue, string value, string sub,
        double? meter, string? meterLabel, bool lastKnown)
    {
        var summary = $"{label}, {value} {sub}";
        if (meterLabel is not null)
            summary += ", " + meterLabel;

        var children = new List<Element>
        {
            TextBlock(value).FontSize(20).FontWeight(FontWeights.SemiBold)
                .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(value).WithKey("value"),
            TextBlock(label).FontSize(12).TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(label)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("head"),
            TextBlock(sub).FontSize(12).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(sub)
                .Foreground(Theme.Ref("TextFillColorTertiaryBrush")).WithKey("sub"),
        };
        if (lastKnown)
            children.Add(TextBlock(S("Stats_LastKnownBadge")).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("freshness")
                .AutomationId("SystemsStatLastKnown"));
        if (meter is { } percent)
        {
            children.Add(Progress(percent)
                .Foreground(Theme.Ref(lastKnown ? "TextFillColorTertiaryBrush" : hue.Brush))
                .Background(Theme.Ref("SystemsMeterTrackBrush"))
                .MinHeight(4).CornerRadius(2)
                .AutomationName(meterLabel ?? label)
                .WithKey("meter"));
        }
        return Border(VStack(4, children.ToArray()))
            .Padding(12)
            .CornerRadius(8)
            .BorderThickness(1)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .Opacity(lastKnown ? 0.8 : 1)
            .AutomationName(summary)
            .ToolTip(summary)
            .AutomationId("SystemsStat_" + metric)
            .WithKey(metric);
    }

    private static Element MachineDetails(SystemRow row, SystemsViewState state, SystemsPageActions actions, DateTimeOffset now)
    {
        var rows = new List<(string Label, Element Value)>
        {
            (S("Field_Identifier"), row.Kind == SystemKind.Client
                ? Value(row.Instance?.IdentityCaption ?? row.Id)
                : Identifier(row.Node?.NodeId ?? row.Id, actions)),
            (S("Field_Status"), StatusBadge(EffectiveStatus(row, state.Connected), StatusLabel(row, state))),
            (S("Field_Platform"), Value(row.Platform ?? S("Status_Unknown"))),
        };

        if (row.Instance is { } instance && instance.Status != PresenceStatus.Gateway)
        {
            var parts = new List<string> { Presence(instance.Status) };
            if (instance.Timestamp is { } ts)
                parts.Add(Ago(new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)), now));
            if (Reason(instance.Reason) is { Length: > 0 } reason)
                parts.Add(reason);
            var tooltip = string.IsNullOrWhiteSpace(instance.Reason)
                ? LocalizationHelper.GetString("InstancesPage_UpdateReason_Tooltip_NoReason")
                : string.Format(LocalizationHelper.GetString("InstancesPage_UpdateReason_Tooltip_Format"), instance.Reason!.Trim());
            rows.Add((S("Field_Activity"), Value(string.Join(" · ", parts)).ToolTip(tooltip)));
            if (instance.LastInputSeconds is { } input)
                rows.Add((S("Field_LastInput"), Value(AgoSeconds(input))));
            if (RolePills(instance) is { } roles)
                rows.Add((S("Field_Roles"), roles));
        }

        if (row.SystemInfo is { } info)
        {
            AddIf(rows, S("Field_Hostname"), info.Hostname);
            AddIf(rows, S("Field_OperatingSystem"), Join(info.OsLabel, info.Release, info.Arch));
            AddIf(rows, S("Field_Processor"), info.CpuModel);
            AddIf(rows, S("Field_Address"), info.LanAddress is { } lan ? (info.Port is { } port ? $"{lan}:{port}" : lan) : null);
            AddIf(rows, S("Field_Runtime"), string.IsNullOrWhiteSpace(info.NodeVersion) ? null : "Node.js " + info.NodeVersion);
            if (info.UptimeMs > 0)
                rows.Add((S("Field_Uptime"), Value(Duration(info.UptimeMs))));
        }

        var version = Join(row.Node?.Version ?? row.Instance?.Version,
            row.Node?.CoreVersion is { } core ? $"core {core}" : null,
            row.Node?.UiVersion is { } ui ? $"ui {ui}" : null);
        AddIf(rows, LocalizationHelper.GetString("NodesPage_Label_Version").TrimEnd(':', ' '), version);
        AddIf(rows, LocalizationHelper.GetString("NodesPage_Label_Network").TrimEnd(':', ' '),
            Join(row.Node?.RemoteIp ?? row.Instance?.Ip, row.Node?.ClientId, row.Node?.ClientMode));
        AddIf(rows, S("Field_Hardware"), Join(row.Node?.ModelIdentifier ?? row.Instance?.ModelIdentifier,
            row.Node?.DeviceFamily ?? row.Instance?.DeviceFamily));

        if (row.Environment is { } environment)
        {
            AddTime(rows, S("Field_LastConnected"), environment.LastConnectedAt, now);
            AddTime(rows, S("Field_LastDisconnected"), environment.LastDisconnectedAt, now);
            if (environment.LastSeenAt is { } seen)
            {
                var text = Ago(seen, now);
                if (Reason(environment.LastSeenReason) is { Length: > 0 } reason)
                    text += " · " + reason;
                rows.Add((S("Field_LastSeen"), Value(text).ToolTip(FormatDateTime(seen))));
            }
            if (environment.Worker is { } worker)
            {
                rows.Add((S("Field_WorkerState"), Value(Join(worker.State, worker.TunnelStatus) ?? worker.State)));
                AddIf(rows, S("Field_WorkerError"), worker.Error);
            }
            foreach (var issue in environment.Issues)
            {
                rows.Add((S("Field_Issue"), Value(issue.Code == "update-required"
                    ? S("Issue_UpdateRequired")
                    : issue.Message ?? issue.Code).Foreground(Theme.Ref("SystemFillColorCautionBrush"))));
            }
        }

        if (row.Node?.ApprovedAt is { } approved)
            AddTime(rows, S("Field_Approved"), new DateTimeOffset(DateTime.SpecifyKind(approved, DateTimeKind.Utc)), now);

        var grid = Grid([GridSize.Px(150), GridSize.Star()], rows.Select(_ => GridSize.Auto).ToArray(),
            rows.SelectMany((entry, index) => new[]
            {
                TextBlock(entry.Label).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).TextWrapping(TextWrapping.WrapWholeWords)
                    .Margin(0, 4, 12, 4).Grid(row: index, column: 0),
                entry.Value.Margin(0, 4, 0, 4).VAlign(VerticalAlignment.Center).Grid(row: index, column: 1),
            }).ToArray());
        return Section(S("Details_Title"), grid, null, "SystemsMachineDetails");
    }

    private static Element? RolePills(MergedInstance instance)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roles = instance.Roles.Select(role => role?.Trim() ?? "").Where(role => role.Length > 0 && seen.Add(role)).ToList();
        if (instance.Mode?.Trim() is { Length: > 0 } mode && !mode.Equals("gateway", StringComparison.OrdinalIgnoreCase) && seen.Add(mode))
            roles.Add(mode);
        return roles.Count == 0
            ? null
            : HStack(4, roles.Select(role => Tag(role)
                .AutomationName(string.Format(LocalizationHelper.GetString("InstancesPage_Role_AccessibilityFormat"), role))
                .WithKey(role)).ToArray());
    }

    private static Element Identifier(string id, SystemsPageActions actions) =>
        Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            TextBlock(id).FontFamily("Consolas").IsTextSelectionEnabled(true)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(id)
                .VAlign(VerticalAlignment.Center).Grid(column: 0),
            Button(Glyph("\uE8C8", 14), () => actions.CopyText(id))
                .SubtleButton()
                .AutomationName(S("CopyIdentifier"))
                .ToolTip(S("CopyIdentifier"))
                .Margin(8, 0, 0, 0)
                .AutomationId("SystemsCopyIdentifier")
                .Grid(column: 1));

    // ── Desktop ────────────────────────────────────────────────────────

    /// <summary>
    /// Desktop availability only. Viewing, control and Gateway host desktop setup belong to the web
    /// interface, reached through the page-level Open in web button.
    /// </summary>
    private static Element Desktop(SystemRow row, DesktopSectionKind kind)
    {
        var (glyph, title) = kind switch
        {
            DesktopSectionKind.Available => ("\uE7F4", S("Desktop_Available")),
            DesktopSectionKind.Offline => ("\uE7F4", S("Desktop_Title_Offline")),
            DesktopSectionKind.NotReady => ("\uE7BA", S("Desktop_Title_NotReady")),
            _ => ("\uE7F4", S("Desktop_Title_NoDesktop")),
        };
        if (kind == DesktopSectionKind.Available && row.Environment?.DesktopAvailability is { } availability)
        {
            title += " · " + (availability switch
            {
                DesktopLockState.Locked => S("Desktop_Locked"),
                DesktopLockState.Unlocked => S("Desktop_Unlocked"),
                _ => S("Status_Unknown"),
            });
        }
        return Section(S("Desktop"),
            WrapRow(8, Glyph(glyph, 16), TextBlock(title).TextWrapping(TextWrapping.WrapWholeWords).AutomationId("SystemsDesktopTitle")),
            null, "SystemsDesktop");
    }

    // ── Sessions ───────────────────────────────────────────────────────

    private static Element Sessions(SystemRow row, SystemsPageActions actions, DateTimeOffset now)
    {
        var items = new List<Element>();
        if (row.Sessions.Count == 0)
        {
            items.Add(TextBlock(S("Sessions_None")).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .AutomationId("SystemsNoSessions").WithKey("none"));
        }
        else
        {
            items.AddRange(row.Sessions.Select((relation, index) =>
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        Glyph("\uE8A5", 14).VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).Grid(column: 0),
                        VStack(4, TextBlock(relation.Session.Title).TextWrapping(TextWrapping.WrapWholeWords),
                            TextBlock(Join(Relation(relation.Kind), relation.Session.AgentId,
                                relation.Session.UpdatedAt is { } updated ? Ago(updated, now) : null) ?? "")
                                .FontSize(12).TextWrapping(TextWrapping.WrapWholeWords)
                                .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                            .VAlign(VerticalAlignment.Center).Grid(column: 1),
                        Button(Glyph("\uE8BD", 16), () => actions.OpenSession(relation.Session.Key))
                            .SubtleButton().ToolTip(S("OpenChat"))
                            .AutomationName(F("OpenChat_Name", relation.Session.Title))
                            .AutomationId("SystemsSession_" + relation.Session.Key)
                            .VAlign(VerticalAlignment.Center).Grid(column: 2))
                    .WithKey(relation.Kind + ":" + relation.Session.Key + ":" + index)));
        }

        // Related sessions come from the loaded list, so the full list stays one click away.
        var viewAll = HyperlinkButton(S("Sessions_ViewAll"), onClick: () => actions.Navigate("sessions"))
            .AutomationName(S("Sessions_ViewAll"))
            .AutomationId("SystemsViewAllSessions");
        return Section(S("Sessions_Title"), VStack(6, items.ToArray()), viewAll, "SystemsSessions");
    }

    // ── Capabilities ───────────────────────────────────────────────────

    private static Element Capabilities(SystemRow row, SystemsPageActions actions)
    {
        var capabilities = (row.Environment?.Capabilities ?? Array.Empty<string>())
            .Concat(row.Node?.Capabilities ?? new List<string>())
            .Concat(row.Node?.Permissions.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Permission identity is exact, even if a transport-created dictionary uses a looser comparer.
        var permissions = row.Node?.Permissions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        var content = new List<Element>();
        foreach (var capability in capabilities)
        {
            var known = permissions.TryGetValue(capability, out var allowed);
            var status = S(!known ? "Capability_NotReported" : allowed ? "Capability_Allowed" : "Capability_TurnedOff");
            content.Add(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    Glyph(CapabilityGlyph(capability), 16).Margin(0, 0, 12, 0).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    TextBlock(CapabilityLabel(capability)).TextWrapping(TextWrapping.WrapWholeWords).ToolTip(capability)
                        .VAlign(VerticalAlignment.Center).Grid(column: 1),
                    TextBlock(status).FontSize(12).TextWrapping(TextWrapping.WrapWholeWords).MaxWidth(120)
                        .Foreground(Theme.Ref(known && allowed ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 2))
                .Padding(0, 10).AutomationName(CapabilityLabel(capability) + ": " + status).WithKey(capability));
        }
        if (row.Node is { ApprovalState: GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval } pendingNode &&
            (pendingNode.PendingDeclaredCapabilities.Count > 0 || pendingNode.PendingDeclaredCommands.Count > 0))
        {
            content.Add(TextBlock(pendingNode.PendingDeclaredCapabilities.Count > 0
                    ? F("Capability_PendingDeclared", string.Join(", ", pendingNode.PendingDeclaredCapabilities.Select(CapabilityLabel)))
                    : S("AwaitingApproval"))
                .FontSize(12).TextWrapping(TextWrapping.WrapWholeWords).Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                .AutomationId("SystemsPendingDeclarations").WithKey("pending"));
        }
        if (content.Count == 0)
            content.Add(Note(S("Capability_NotReported")));
        var manage = row.IsThisDevice ? HyperlinkButton(S("ManagePermissions"), onClick: () => actions.Navigate("permissions"))
            .AutomationName(S("ManagePermissions")).AutomationId("SystemsManagePermissions") : null;
        return Section(S("Capabilities_Title"), VStack(0, content.ToArray()), manage, "SystemsCapabilities");
    }

    private static string CapabilityLabel(string capability) => capability switch
    {
        "system" => S("Capability_System"),
        "exec" or "shell" => S("Capability_Shell"),
        "screen" => S("Capability_Screen"),
        "camera" => S("Capability_Camera"),
        "browser" => S("Capability_Browser"),
        "files" => S("Capability_Files"),
        "canvas" => S("Capability_Canvas"),
        "clipboard" => S("Capability_Clipboard"),
        "notifications" => S("Capability_Notifications"),
        "audio" or "microphone" => S("Capability_Audio"),
        _ => HumanizeCapability(capability),
    };

    private static string HumanizeCapability(string capability)
    {
        var label = string.Join(" ", capability.Replace('-', ' ').Replace('_', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return label.Length == 0 ? label
            : char.ToUpper(label[0], System.Globalization.CultureInfo.CurrentCulture) + label[1..];
    }

    private static Element DiagnosticDeclarations(SystemRow row)
    {
        var content = new List<Element>();
        var commands = (row.Node?.Commands ?? row.Environment?.InvocableCommands.ToList() ?? new List<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        if (row.Node?.ApprovalState is GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval)
        {
            pending.UnionWith(row.Node.PendingDeclaredCommands.Except(commands, StringComparer.Ordinal));
            commands.AddRange(pending);
        }
        if (commands.Count > 0)
        {
            var disabled = new HashSet<string>(row.Node?.DisabledCommands ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            content.Add(Expander(
                    string.Format(LocalizationHelper.GetString("NodesPage_Commands_Header"), commands.Count),
                    VStack(2, commands.Select(command => TextBlock(pending.Contains(command)
                                ? command + " · " + S("AwaitingApproval")
                                : disabled.Contains(command)
                                ? command + LocalizationHelper.GetString("NodesPage_Command_DisabledSuffix")
                                : command)
                            .FontFamily("Consolas").FontSize(12).IsTextSelectionEnabled(true)
                            .Foreground(Theme.Ref(disabled.Contains(command) ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush"))
                            .WithKey(command))
                        .ToArray()),
                    false)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationId("SystemsCommands").WithKey("commands"));
        }

        if (!string.IsNullOrWhiteSpace(row.Node?.PathEnv))
        {
            // PATH can reveal user names and share paths, so it stays collapsed.
            content.Add(Expander(LocalizationHelper.GetString("NodesPage_Label_PathEnv").TrimEnd(':', ' '),
                    TextBlock(row.Node!.PathEnv!).FontFamily("Consolas").FontSize(12).TextWrapping().IsTextSelectionEnabled(true),
                    false)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationId("SystemsPathEnv").WithKey("path"));
        }
        return VStack(10, content.ToArray());
    }

    private static string CapabilityGlyph(string capability) => capability.Trim().ToLowerInvariant() switch
    {
        "system" or "exec" or "shell" => "\uE756",
        "browser" or "web" => "\uE774",
        "canvas" or "a2ui" => "\uE790",
        "screen" or "desktop" => "\uE7F4",
        "camera" => "\uE722",
        "files" or "file" or "fs" => "\uE8B7",
        "location" => "\uE81D",
        "notify" or "notifications" => "\uEA8F",
        "voice" or "audio" or "talk" or "microphone" => "\uE720",
        "clipboard" => "\uE77F",
        _ => "\uE945",
    };

    // ── Dialogs ────────────────────────────────────────────────────────

    private static Element Dialog(SystemsDialog? dialog, Action<SystemsDialog?> setDialog, SystemsPageActions actions,
        Func<int> currentEpoch, Func<SystemsDialog?> currentDialog, Action<string?> feedback)
    {
        if (dialog is null)
            return Empty();

        Element ErrorText() => dialog.Error is { } error
            ? TextBlock(error).FontSize(12).TextWrapping(TextWrapping.WrapWholeWords).Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                .LiveRegion(AutomationLiveSetting.Assertive)
                .AutomationId("SystemsDialogError")
            : Empty();

        // Busy dialogs close while the operation runs and reopen (as a new show) only to show an error.
        async Task RunAsync(Func<Task<string?>> operation)
        {
            setDialog(dialog with { Busy = true, Error = null });
            var running = currentDialog();
            string? error;
            try { error = await operation(); }
            catch (Exception ex) { error = ex.Message; }
            // The user may have opened another dialog meanwhile; leave it alone.
            if (!ReferenceEquals(currentDialog(), running))
                return;
            if (error is null && dialog.Epoch == currentEpoch())
                feedback(dialog is ForgetNodeDialog forgotten ? F("Forgotten", NodeName(forgotten.Node)) : S("RenameSucceeded"));
            setDialog(error is null ? null : dialog with { Busy = false, Error = error });
        }

        Action<ContentDialogResult> OnPrimary(Action confirm) => result =>
        {
            // Re-check at click time: the connection may have changed since the last render.
            if (result == ContentDialogResult.Primary && dialog.Epoch == currentEpoch())
                confirm();
            else if (!dialog.Busy)
                setDialog(null);
        };

        ContentDialogElement element;
        switch (dialog)
        {
            case AddSystemDialog:
                element = ContentDialog(S("AddSystem"),
                    VStack(16,
                        TextBlock(S("AddSystemIntro")).TextWrapping(TextWrapping.WrapWholeWords),
                        TextBlock(S("AddSystemSteps")).TextWrapping(TextWrapping.WrapWholeWords)),
                    S("OpenConnection")) with
                {
                    DefaultButton = ContentDialogButton.Primary,
                    OnClosed = OnPrimary(() => { setDialog(null); actions.Navigate("connection"); }),
                };
                element = element.Set(native => ConfigureDialog(native, "SystemsAddDialogTitleTemplate", primaryFocus: true));
                break;
            case RenameNodeDialog rename:
                element = ContentDialog(LocalizationHelper.GetString("NodesPage_Rename_Title"),
                    VStack(8,
                        TextBlock(string.Format(LocalizationHelper.GetString("NodesPage_Rename_Body"), NodeName(rename.Node))).TextWrapping(TextWrapping.WrapWholeWords),
                        TextBox(rename.Text.Value ?? "", text => rename.Text.Value = text,
                                placeholderText: LocalizationHelper.GetString("NodesPage_Rename_Placeholder"))
                            .MaxLength(64)
                            .AutomationName(LocalizationHelper.GetString("NodesPage_Rename_Title"))
                            .AutomationId("SystemsRenameInput"),
                        ErrorText()),
                    LocalizationHelper.GetString("NodesPage_Rename_Primary")) with
                {
                    DefaultButton = ContentDialogButton.Primary,
                    OnClosed = OnPrimary(() =>
                    {
                        var name = (rename.Text.Value ?? "").Trim();
                        if (name.Length == 0)
                            setDialog(rename with { Error = LocalizationHelper.GetString("NodesPage_Rename_Error_Empty") });
                        else
                            _ = RunAsync(() => actions.RenameNode(rename.Node, name));
                    }),
                };
                element = element.Set(ConfigureDialogSizing);
                break;
            case ForgetNodeDialog forget:
                var now = DateTimeOffset.UtcNow;
                element = ContentDialog(F("ForgetTitle", NodeName(forget.Node)),
                    VStack(16,
                        TextBlock(F("ForgetConsequence", forget.GatewayName)).TextWrapping(TextWrapping.WrapWholeWords),
                        Border(Grid([GridSize.Px(120), GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                            Note(S("Field_Platform")).Grid(row: 0, column: 0),
                            Value(PlatformName(forget.Node.Platform)).Grid(row: 0, column: 1),
                            Note(S("Field_LastSeen")).Margin(0, 8, 0, 0).Grid(row: 1, column: 0),
                            Value(forget.Node.LastSeen is { } seen
                                ? DialogAge(new DateTimeOffset(DateTime.SpecifyKind(seen, DateTimeKind.Utc)), now)
                                : S("Status_Unknown")).Margin(0, 8, 0, 0).Grid(row: 1, column: 1),
                            Note(S("Field_Paired")).Margin(0, 8, 0, 0).Grid(row: 2, column: 0),
                            Value(forget.Node.ApprovedAt is { } approved
                                ? approved.ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture)
                                : S("Status_Unknown")).Margin(0, 8, 0, 0).Grid(row: 2, column: 1)).AutomationId("SystemsForgetFacts")
                            ).Padding(16).CornerRadius(8).Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                            .AutomationId("SystemsForgetFactsSurface"),
                        ErrorText()),
                    S("ForgetAction")) with
                {
                    // Enter must never implicitly choose a destructive action.
                    DefaultButton = ContentDialogButton.None,
                    OnClosed = OnPrimary(() => _ = RunAsync(() => actions.ForgetNode(forget.Node))),
                };
                element = element.Set(native => ConfigureDialog(native, "SystemsForgetDialogTitleTemplate", primaryFocus: false));
                break;
            default:
                return Empty();
        }
        if (dialog.Busy)
            return Empty();
        return (element with
            {
                IsOpen = true,
                CloseButtonText = dialog is AddSystemDialog ? S("Close") : LocalizationHelper.GetString("NodesPage_Common_Cancel"),
            })
            .AutomationName(dialog is ForgetNodeDialog f ? F("ForgetTitle", NodeName(f.Node)) : dialog is AddSystemDialog ? S("AddSystem") : LocalizationHelper.GetString("NodesPage_Rename_Title"))
            .AutomationId("SystemsDialog")
            .WithKey("dialog-" + dialog.Show.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void ConfigureDialog(Microsoft.UI.Xaml.Controls.ContentDialog dialog, string titleTemplate, bool primaryFocus)
    {
        dialog.TitleTemplate = (DataTemplate)Application.Current.Resources[titleTemplate];
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        ConfigureDialogSizing(dialog);
        // ContentDialog is side-mounted by Reactor. Its own Opened event, not the
        // collapsed Reactor placeholder's Loaded event, owns initial button focus.
        dialog.Opened -= FocusPrimary;
        dialog.Opened -= FocusCancel;
        dialog.Opened += primaryFocus ? FocusPrimary : FocusCancel;
        dialog.GettingFocus -= RedirectInitialFocus;
        if (!primaryFocus)
            dialog.GettingFocus += RedirectInitialFocus;
        // A side-mounted dialog can have opened before Reactor applies this modifier.
        // Only initialize once, so subsequent reconciles never steal keyboard focus.
        if (dialog.IsLoaded && dialog.Tag is null)
            QueueDialogFocus(dialog, primaryFocus ? "PrimaryButton" : "CloseButton");
    }

    private static void ConfigureDialogSizing(Microsoft.UI.Xaml.Controls.ContentDialog dialog)
    {
        dialog.Resources["ContentDialogMaxWidth"] = 560d;
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Opened -= SizeDialog;
        dialog.Opened += SizeDialog;
        dialog.SizeChanged -= SizeDialogOnResize;
        dialog.SizeChanged += SizeDialogOnResize;
    }

    private static void SizeDialog(Microsoft.UI.Xaml.Controls.ContentDialog dialog, ContentDialogOpenedEventArgs args)
    {
        var root = dialog.XamlRoot;
        void Resized(XamlRoot sender, XamlRootChangedEventArgs changed) => ApplyDialogLayout(dialog);
        void Closed(Microsoft.UI.Xaml.Controls.ContentDialog sender, ContentDialogClosedEventArgs closed)
        {
            root.Changed -= Resized;
            dialog.Closed -= Closed;
        }
        root.Changed += Resized;
        dialog.Closed += Closed;
        ApplyDialogLayout(dialog);
    }

    private static void SizeDialogOnResize(object sender, SizeChangedEventArgs args) =>
        ApplyDialogLayout((Microsoft.UI.Xaml.Controls.ContentDialog)sender);

    private static void ApplyDialogLayout(Microsoft.UI.Xaml.Controls.ContentDialog dialog)
    {
        var width = Math.Max(0, Math.Min(560, dialog.XamlRoot.Size.Width - 48));
        // WinUI sizes the popup-hosted control to the window and centers this card inside it.
        // Constraining the control instead would pin the dialog and its smoke layer to the left edge.
        if (FindDialogPart<FrameworkElement>(dialog, "BackgroundElement") is { } card)
            card.MaxWidth = width;
        var primary = FindDialogButton(dialog, "PrimaryButton");
        var close = FindDialogButton(dialog, "CloseButton");
        var narrow = PageResponsiveLayout.TierFor(dialog.XamlRoot.Size.Width) == PageWidthTier.Narrow;
        // Adapt only this dialog's native button grid, leaving the global ContentDialog style intact.
        if (primary is not null && close is not null &&
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(primary) is Microsoft.UI.Xaml.Controls.Grid footer &&
            (footer.Tag is not bool previous || previous != narrow))
        {
            footer.Tag = narrow;
            footer.ColumnDefinitions.Clear();
            footer.RowDefinitions.Clear();
            if (narrow)
            {
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            else
            {
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(primary, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(primary, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(close, narrow ? 0 : 1);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(close, narrow ? 1 : 0);
            primary.HorizontalAlignment = close.HorizontalAlignment = HorizontalAlignment.Stretch;
            primary.Margin = narrow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 8, 0);
            close.Margin = new Thickness(0);
        }
        var facts = FindDialogElement(dialog, "SystemsForgetFacts") as Microsoft.UI.Xaml.Controls.Grid;
        if (facts is not null)
        {
            var stacked = width < 400;
            facts.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(120);
            facts.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            while (facts.RowDefinitions.Count < 6)
                facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < facts.Children.Count; index++)
            {
                var child = (FrameworkElement)facts.Children[index];
                Microsoft.UI.Xaml.Controls.Grid.SetRow(child, stacked ? index : index / 2);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(child, stacked ? 0 : index % 2);
            }
        }
    }

    private static FrameworkElement? FindDialogElement(DependencyObject parent, string id)
    {
        if (parent is FrameworkElement element && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id)
            return element;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindDialogElement(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index), id) is { } found)
                return found;
        return null;
    }

    private static void FocusPrimary(Microsoft.UI.Xaml.Controls.ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        QueueDialogFocus(sender, "PrimaryButton");

    private static void FocusCancel(Microsoft.UI.Xaml.Controls.ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        QueueDialogFocus(sender, "CloseButton");

    private static void RedirectInitialFocus(object sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs args)
    {
        var dialog = (Microsoft.UI.Xaml.Controls.ContentDialog)sender;
        if (dialog.Tag is null && FindDialogButton(dialog, "CloseButton") is { } cancel &&
            args.TrySetNewFocusedElement(cancel))
            dialog.Tag = "CloseButton";
    }

    private static void QueueDialogFocus(Microsoft.UI.Xaml.Controls.ContentDialog dialog, string name) =>
        // Finish WinUI's initial focus transaction before choosing the button.
        dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (dialog.IsLoaded && FindDialogButton(dialog, name) is { } button && dialog.Tag is null)
            {
                if (button.Focus(FocusState.Programmatic))
                    dialog.Tag = name;
            }
        });

    private static Microsoft.UI.Xaml.Controls.Button? FindDialogButton(DependencyObject parent, string name) =>
        FindDialogPart<Microsoft.UI.Xaml.Controls.Button>(parent, name);

    private static T? FindDialogPart<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T part && part.Name == name)
            return part;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindDialogPart<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index), name) is { } found)
                return found;
        return null;
    }

    private static string NodeName(GatewayNodeInfo node) =>
        string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;

    private static string DialogAge(DateTimeOffset at, DateTimeOffset now)
    {
        var seconds = (int)Math.Clamp((now - at).TotalSeconds, 0, int.MaxValue);
        return seconds < 60 ? F("SecondsAgo", seconds) : Ago(at, now);
    }

    // ── Shared pieces ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the Gateway's web Systems page. The web page keeps its own selection, so this cannot
    /// open a specific machine; the identifier stays copyable here. Gateways without
    /// <c>environments.list</c> have no Systems page, so they open the web interface root.
    /// </summary>
    private static string? WebDestination(SystemsViewState state) => state.HasWebSystemsPage ? "systems" : null;

    private static Element OpenWebButton(SystemsViewState state, SystemsPageActions actions, string automationId)
    {
        var name = state.HasWebSystemsPage ? S("OpenWeb_Name") : S("OpenWeb_RootName");
        var help = state.HasWebSystemsPage ? S("OpenWeb_Help") : S("OpenWeb_RootHelp");
        return Button(HStack(6, Glyph(FluentIconCatalog.OpenInBrowser, 14).VAlign(VerticalAlignment.Center),
                    TextBlock(S("OpenWeb")).TextWrapping(TextWrapping.NoWrap).VAlign(VerticalAlignment.Center)),
                () => actions.OpenWeb(WebDestination(state)))
            .VAlign(VerticalAlignment.Center)
            .AutomationName(name)
            .HelpText(help)
            .ToolTip(help)
            .AutomationId(automationId);
    }

    private static Element Section(string title, Element content, Element? action, string automationId, Element? badge = null) =>
        VStack(10,
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    WrapRow(8, TextBlock(title).FontSize(13).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.WrapWholeWords)
                        .HeadingLevel(AutomationHeadingLevel.Level3).AutomationId(automationId + "Heading"), badge ?? Empty())
                        .VAlign(VerticalAlignment.Center).Grid(column: 0),
                    (action ?? Empty()).Margin(12, 0, 0, 0).VAlign(VerticalAlignment.Center).Grid(column: 1)),
                content)
            .AutomationId(automationId);

    /// <summary>A row that wraps onto more lines instead of clipping when the pane is narrow.</summary>
    private static Element WrapRow(double gap, params Element[] children) =>
        FlexRow(children) with
        {
            Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap,
            ColumnGap = gap,
            RowGap = gap,
            AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Center,
        };

    /// <summary>A glyph on a rounded tint of its hue. Decorative: callers name the meaning in text.</summary>
    private static BorderElement GlyphChip(string glyph, SystemsHue hue, double size, double glyphSize) =>
        Border(Glyph(glyph, glyphSize).Foreground(Theme.Ref(hue.Brush))
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.3))
            .Background(Theme.Ref(hue.Tint))
            .AccessibilityView(AccessibilityView.Raw);

    /// <summary>
    /// The machine's kind glyph on its kind hue, with a presence dot. The dot repeats the status
    /// shown in text beside it, so it stays out of the accessibility tree.
    /// </summary>
    private static Element MachineAvatar(SystemRow row, SystemStatus status, double size, double glyphSize)
    {
        var dot = Math.Round(size * 0.3);
        return Grid([GridSize.Px(size)], [GridSize.Px(size)],
                GlyphChip(DeviceGlyph(row), SystemsHue.ForKind(row.Kind), size, glyphSize)
                    .Opacity(status == SystemStatus.Offline ? 0.7 : 1),
                Border(null).Size(dot, dot).CornerRadius(dot / 2)
                    .BorderThickness(2)
                    .BorderBrush(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
                    .Background(Theme.Ref(StatusBrush(status)))
                    .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom)
                    .Margin(0, 0, -3, -3))
            .AccessibilityView(AccessibilityView.Raw);
    }

    private static BorderElement Card(Element child) =>
        Border(child)
            .CornerRadius(8)
            .BorderThickness(1)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"));

    /// <summary>Lays children out in rows of <paramref name="columns"/> equal columns.</summary>
    private static Element Rows(IReadOnlyList<Element> children, int columns, double spacing = 12,
        bool stretchLastSingle = false)
    {
        var rows = new List<Element>();
        for (var start = 0; start < children.Count; start += columns)
        {
            var slice = children.Skip(start).Take(columns).ToArray();
            var cells = new List<Element>();
            for (var i = 0; i < slice.Length; i++)
                cells.Add(slice[i].Grid(column: i * 2,
                    columnSpan: stretchLastSingle && slice.Length == 1 ? columns * 2 - 1 : 1));
            var definition = Enumerable.Range(0, columns * 2 - 1)
                .Select(index => index % 2 == 0 ? GridSize.Star() : GridSize.Px(spacing)).ToArray();
            rows.Add(Grid(definition, [GridSize.Auto], cells.ToArray()).WithKey("row" + start));
        }
        return VStack(spacing, rows.ToArray());
    }

    private static Element StatusBadge(SystemStatus status, string? label = null) =>
        HStack(4,
                Glyph(StatusGlyph(status), 12).Foreground(Theme.Ref(StatusBrush(status))).VAlign(VerticalAlignment.Center),
                TextBlock(label ?? Status(status)).FontSize(12))
            .HAlign(HorizontalAlignment.Left)
            .AutomationName(F("Status_Name", label ?? Status(status)))
            .AutomationId("SystemsStatusBadge");

    private static Element Tag(string text) =>
        Border(TextBlock(text).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
            .Padding(8, 2)
            .CornerRadius(10)
            .BorderThickness(1)
            .Background(Theme.Ref("ControlAltFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("ControlStrokeColorDefaultBrush"))
            .AutomationName(text);

    private static TextBlockElement Value(string text) => TextBlock(text).TextWrapping(TextWrapping.WrapWholeWords)
        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(text).IsTextSelectionEnabled(true);

    private static Element Note(string text) =>
        TextBlock(text).TextWrapping(TextWrapping.WrapWholeWords).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(text)
            .MaxWidth(800).Foreground(Theme.Ref("TextFillColorSecondaryBrush"));

    private static TextBlockElement Glyph(string glyph, double size) =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .AccessibilityView(AccessibilityView.Raw);

    private static void AddIf(List<(string, Element)> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add((label, Value(value)));
    }

    private static void AddTime(List<(string, Element)> rows, string label, DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is { } at)
            rows.Add((label, Value(Ago(at, now)).ToolTip(FormatDateTime(at))));
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    internal static string StatusGlyph(SystemStatus status) => status switch
    {
        SystemStatus.Online => "\uF13E",   // StatusCircleCheckmark
        SystemStatus.Offline => "\uF140",  // StatusCircleBlock
        SystemStatus.Starting or SystemStatus.Stopping => "\uE895", // Sync
        SystemStatus.Error => "\uEA39",    // ErrorBadge
        _ => "\uF142",                     // StatusCircleQuestionMark
    };

    private static string StatusBrush(SystemStatus status) => status switch
    {
        SystemStatus.Online => "SystemFillColorSuccessBrush",
        SystemStatus.Starting or SystemStatus.Stopping => "SystemFillColorAttentionBrush",
        SystemStatus.Error => "SystemFillColorCriticalBrush",
        SystemStatus.Offline => "TextFillColorTertiaryBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    internal static string DeviceGlyph(SystemRow row)
    {
        if (row.Kind == SystemKind.Host) return "\uE968";   // ServerEnvironment
        if (row.Kind == SystemKind.Worker) return "\uE753"; // Cloud
        var family = (row.Node?.DeviceFamily ?? row.Instance?.DeviceFamily ?? "").Trim().ToLowerInvariant();
        var model = (row.Node?.ModelIdentifier ?? row.Instance?.ModelIdentifier ?? "").Trim().ToLowerInvariant();
        var platform = (row.Platform ?? "").Trim().ToLowerInvariant();
        if (family == "iphone" || family == "android" || platform.StartsWith("ios") || platform.StartsWith("android")) return "\uE8EA";
        if (family == "ipad" || platform.StartsWith("ipados")) return "\uE70A";
        if (family == "mac" || platform.StartsWith("macos")) return model.Contains("macbook") ? "\uE7F8" : "\uE977";
        if (family == "windows" || platform.StartsWith("windows")) return "\uE7F8";
        if (row.Kind == SystemKind.Client) return "\uE774";
        return "\uE950";
    }
}
