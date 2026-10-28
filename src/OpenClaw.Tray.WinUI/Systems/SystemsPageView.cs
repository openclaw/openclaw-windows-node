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
    public required Func<GatewayNodeInfo, string, Task<string?>> RenameNode { get; init; }
    public required Func<GatewayNodeInfo, Task<string?>> ForgetNode { get; init; }
    public required Func<PendingApproval, bool, Task<string?>> DecidePairing { get; init; }
    public required Func<Task> EnableHostDesktop { get; init; }
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
internal sealed record ForgetNodeDialog(GatewayNodeInfo Node) : SystemsDialog;
internal sealed record PairingDialog(PendingApproval Approval, bool Approve) : SystemsDialog;
internal sealed record EnableDesktopDialog() : SystemsDialog;

internal sealed record SystemsPageProps(SystemsController Controller, SystemsPageActions Actions);

[Microsoft.UI.Reactor.Wrappers.GenerateReactorWrapper(typeof(SystemsSparkline), AutoDiscover = false, RegisterAssembly = false)]
internal partial record SystemsSparklineElement;

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
/// connected apps, with details, statistics, related sessions, desktop setup and backups
/// for the selected machine. Viewing or controlling a remote desktop is deferred.
/// </summary>
internal sealed class SystemsPageView : Component<SystemsPageProps>
{
    private const double SparkHeight = 44;

    public override Element Render()
    {
        var controller = Props.Controller;
        var actions = Props.Actions;
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
        var reducedMotion = UseReducedMotion();
        var now = DateTimeOffset.UtcNow;
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
                Header(state, controller, now, layout.ShowOverview).Grid(row: 0),
                Summary(state, controller, layout).Grid(row: 1),
                Banners(state, controller, actions, setDialog, layout).Grid(row: 2),
                Body(state, controller, actions, setDialog, now, reducedMotion, layout).Grid(row: 3),
                Dialog(dialog, setDialog, actions, () => controller.State.ConnectionEpoch, () => currentDialog.Current))
            .Padding(SystemsLayout.PagePadding, 20, SystemsLayout.PagePadding, 16)
            .OnSizeChanged((_, args) => setLayout(SystemsLayout.For(args.NewSize.Width, args.NewSize.Height)))
            .AutomationId("SystemsPageRoot");
    }

    // ── Header and banners ─────────────────────────────────────────────

    private static Element Header(SystemsViewState state, SystemsController controller, DateTimeOffset now, bool showSubtitle)
    {
        var refreshing = state.Loading;
        var refresh = Button(
                HStack(8,
                    refreshing
                        ? ProgressRing().IsActive(true).Size(16, 16).AccessibilityView(AccessibilityView.Raw)
                        : Glyph("\uE72C", 14),
                    TextBlock(S("Refresh"))),
                () => _ = controller.RefreshAsync(manual: true))
            .IsEnabled(state.Connected && !refreshing && state.CanRead)
            .AutomationName(S("Refresh_Name"))
            .ToolTip(S("Refresh_Name"))
            .AutomationId("SystemsRefreshButton");

        var updated = state.LastRefreshedAt is { } at
            ? TextBlock(F("UpdatedAgo", Ago(at, now))).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorTertiaryBrush")).VAlign(VerticalAlignment.Center)
            : Empty();

        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                VStack(4,
                    TextBlock(S("Title")).FontSize(28).FontWeight(FontWeights.SemiBold)
                        .HeadingLevel(AutomationHeadingLevel.Level1)
                        .AutomationId("SystemsPageMarker"),
                    showSubtitle
                        ? TextBlock(S("Subtitle")).FontSize(12).TextWrapping()
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        : Empty()).Grid(column: 0),
                HStack(12, updated, refresh).VAlign(VerticalAlignment.Top).Grid(column: 1))
            .Margin(0, 0, 0, 16);
    }

    /// <summary>
    /// Fleet at a glance. The machine tiles double as status filters; the backups tile jumps to
    /// the Gateway host, where backups live. Every tile pairs its hue with a glyph and a label.
    /// </summary>
    private static Element Summary(SystemsViewState state, SystemsController controller, SystemsLayout layout)
    {
        if (!state.HasLoaded || state.Rows.Count == 0 || !layout.ShowOverview)
            return Empty();

        // Count what the rows show: while disconnected every machine reads as offline.
        var online = state.Rows.Count(row => EffectiveStatus(row, state.Connected) == SystemStatus.Online);
        var offline = state.Rows.Count(row => EffectiveStatus(row, state.Connected) == SystemStatus.Offline);
        var tiles = new List<Element>
        {
            SummaryTile("all", "\uE977", SystemsHue.Accent, Count(state.Rows.Count), S("Summary_All"),
                state.Filter == SystemsStatusFilter.All, () => controller.SetFilter(SystemsStatusFilter.All)),
            SummaryTile("online", "\uF13E", SystemsHue.Success, Count(online), S("Status_Online"),
                state.Filter == SystemsStatusFilter.Online, () => controller.SetFilter(SystemsStatusFilter.Online)),
            SummaryTile("offline", "\uF140", SystemsHue.Neutral, Count(offline), S("Status_Offline"),
                state.Filter == SystemsStatusFilter.Offline, () => controller.SetFilter(SystemsStatusFilter.Offline)),
        };
        if (state.PendingPairingCount > 0)
        {
            tiles.Add(SummaryTile("pairing", "\uE8FA", SystemsHue.Caution, Count(state.PendingPairingCount),
                S("Summary_Pairing"), false, null));
        }
        if (state.Backups is { } backups && state.Rows.Any(row => row.Id == SystemsProjection.GatewayId))
        {
            var view = BackupsPresentation.Project(backups);
            var issues = view.Targets.Count(target => target.Health != OpenClaw.Shared.Systems.BackupHealth.Ok);
            var (value, label, hue) = view.IsEmpty
                ? ("0", S("Summary_BackupsNone"), SystemsHue.Neutral)
                : issues == 0
                    ? (Count(view.Targets.Count), S("Summary_BackupsOk"), SystemsHue.Success)
                    : (Count(issues), S("Summary_BackupsIssues"),
                        view.Targets.Any(target => target.Health == OpenClaw.Shared.Systems.BackupHealth.Failed)
                            ? SystemsHue.Critical : SystemsHue.Caution);
            tiles.Add(SummaryTile("backups", "\uE777", hue, value, label, false,
                () => controller.Select(SystemsProjection.GatewayId), S("Summary_ShowBackups")));
        }

        return Rows(tiles.Select((tile, index) => tile.WithKey("tile" + index)).ToList(),
                Math.Min(layout.SummaryColumns, tiles.Count))
            .Margin(0, 0, 0, 16)
            .AutomationName(S("Summary_Name"))
            .AutomationId("SystemsSummary");
    }

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private static Element SummaryTile(string id, string glyph, SystemsHue hue, string value, string label,
        bool active, Action? onClick, string? help = null)
    {
        var content = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            GlyphChip(glyph, hue, 36, 18).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
            VStack(0,
                TextBlock(value).FontSize(20).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                TextBlock(label).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))).VAlign(VerticalAlignment.Center).Grid(column: 1));
        var name = $"{label}: {value}";
        if (onClick is null)
        {
            return Card(content).Padding(14, 10).AutomationName(name).AutomationId("SystemsSummary_" + id);
        }
        var tile = Button(content, onClick)
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(14, 10).CornerRadius(8)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderThickness(active ? 2 : 1)
            .BorderBrush(Theme.Ref(active ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"))
            .AutomationName(name)
            .HelpText(help ?? S("Filter_Name"))
            .AutomationId("SystemsSummary_" + id);
        return active ? tile.ItemStatus(S("Summary_Active")) : tile;
    }

    private static Element Banners(SystemsViewState state, SystemsController controller, SystemsPageActions actions,
        Action<SystemsDialog?> open, SystemsLayout layout)
    {
        var banners = new List<Element>();
        if (!state.Connected)
        {
            banners.Add(InfoBar(S("Disconnected_Title"), S("Disconnected_Message")).Warning().IsClosable(false)
                .Content(Button(S("OpenConnection"), () => actions.Navigate("connection")).AutomationName(S("OpenConnection")).Margin(0, 0, 0, 8))
                .AutomationId("SystemsDisconnectedBanner").WithKey("disconnected"));
        }
        else if (!state.CanRead)
        {
            banners.Add(InfoBar(S("ReadAccess_Title"), S("ReadAccess_Message")).Warning().IsClosable(false)
                .AutomationId("SystemsReadAccessBanner").WithKey("read-access"));
        }

        if (state.InventoryError is { } error)
        {
            banners.Add(InfoBar(S("InventoryError_Title"), error).Error().IsClosable(false)
                .Content(Button(S("Retry"), () => _ = controller.RefreshAsync(manual: true)).AutomationName(S("Retry"))
                    .IsEnabled(state.Connected && !state.Loading).Margin(0, 0, 0, 8))
                .AutomationId("SystemsInventoryErrorBanner").WithKey("inventory-error"));
        }

        if (state.PartialErrors.Any)
        {
            banners.Add(InfoBar(S("PartialErrors_Title"), string.Join("\n", state.PartialErrors.Messages)).Informational().IsClosable(false)
                .AutomationId("SystemsPartialErrorsBanner").WithKey("partial-errors"));
        }

        if (state.PendingPairingCount > 0)
            banners.Add(PendingApprovals(state, actions, open, layout.ApprovalsMaxHeight).WithKey("pairing"));

        return banners.Count == 0 ? Empty() : VStack(8, banners.ToArray()).Margin(0, 0, 0, 12);
    }

    private static Element PendingApprovals(SystemsViewState state, SystemsPageActions actions, Action<SystemsDialog?> open,
        double maxHeight)
    {
        var count = state.PendingPairingCount;
        var title = count == 1 ? S("Pairing_TitleOne") : F("Pairing_TitleMany", count);
        var message = state.OwnNodePending ? S("Pairing_OwnNodeMessage") : S("Pairing_Message");
        var rows = state.PendingApprovals.Select<PendingApproval, Element>(approval =>
        {
            var name = string.IsNullOrWhiteSpace(approval.DisplayName) ? approval.DeviceId : approval.DisplayName!;
            var kind = approval.Kind == PairingApprovalKind.NodePair ? S("Pairing_KindNode") : S("Pairing_KindDevice");
            var details = new[] { kind, PlatformLabels.Pretty(approval.Platform), approval.RemoteIp,
                    approval.IsRepair ? S("Pairing_Repair") : null }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            var scopes = approval.Scopes.Count > 0 ? F("Pairing_Scopes", string.Join(", ", approval.Scopes)) : null;
            Element buttons = state.CanPair && approval.IsActionable
                ? HStack(8,
                    Button(S("Pairing_Approve"), () => open(new PairingDialog(approval, true))).AccentButton()
                        .AutomationName(F("Pairing_ApproveName", name)).AutomationId("SystemsPairApprove_" + approval.DeviceId),
                    Button(S("Pairing_Reject"), () => open(new PairingDialog(approval, false)))
                        .AutomationName(F("Pairing_RejectName", name)).AutomationId("SystemsPairReject_" + approval.DeviceId))
                : Button(S("Pairing_Review"), () => actions.Navigate("connection")).AutomationName(S("Pairing_Review"));
            return Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    GlyphChip(approval.Kind == PairingApprovalKind.NodePair ? "\uE7F8" : "\uE8EA", SystemsHue.Caution, 36, 18)
                        .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                    VStack(2,
                        TextBlock(name).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis),
                        TextBlock(string.Join(" · ", details)).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                        scopes is null ? Empty() : TextBlock(scopes).FontSize(12).TextWrapping()
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))).Grid(column: 1),
                    buttons.VAlign(VerticalAlignment.Center).Grid(column: 2))
                .WithKey(approval.Key);
        }).ToList();
        if (state.OwnNodePending)
            rows.Insert(0, Button(S("Pairing_Review"), () => actions.Navigate("connection"))
                .AutomationName(S("Pairing_Review"))
                .AutomationId("SystemsOwnNodePairingReview").WithKey("own-node"));
        return InfoBar(title, message).Warning().IsClosable(false)
            // Many requests scroll inside the banner instead of pushing the machines off the page.
            .Content((ScrollViewer(VStack(10, rows.ToArray())) with { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled })
                .MaxHeight(maxHeight).Margin(0, 0, 0, 12))
            .AutomationId("SystemsPendingPairingBanner");
    }

    // ── Body ───────────────────────────────────────────────────────────

    private static Element Body(SystemsViewState state, SystemsController controller, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now, bool reducedMotion, SystemsLayout layout)
    {
        var inventory = Inventory(state, controller, actions, compact: !layout.Stacked && layout.InventoryWidth < 300);
        var details = Details(state, controller, actions, open, now, reducedMotion, layout);
        // Narrow windows stack the list above the details, with the list capped so details stay visible.
        return layout.Stacked
            ? Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                inventory.MaxHeight(240).Grid(row: 0),
                details.Margin(0, SystemsLayout.PaneGap, 0, 0).Grid(row: 1))
            : Grid([GridSize.Px(layout.InventoryWidth), GridSize.Star()], [GridSize.Star()],
                inventory.Grid(column: 0),
                details.Margin(SystemsLayout.PaneGap, 0, 0, 0).Grid(column: 1));
    }

    private static Element Inventory(SystemsViewState state, SystemsController controller, SystemsPageActions actions, bool compact)
    {
        var visible = SystemsProjection.Query(state.Rows, state.Query, state.Filter, state.Sort, DisplayName, state.Connected);
        var search = TextBox(state.Query, controller.SetQuery, placeholderText: S("Search"))
            .SearchInput()
            .AutomationName(S("Search"))
            .AutomationId("SystemsSearchBox");

        var filters = new[] { SystemsStatusFilter.All, SystemsStatusFilter.Online, SystemsStatusFilter.Offline };
        var filter = SelectorBar(
                [SelectorBarItem(S("Filter_All")), SelectorBarItem(S("Status_Online")), SelectorBarItem(S("Status_Offline"))],
                Array.IndexOf(filters, state.Filter),
                index => { if (index >= 0) controller.SetFilter(filters[index]); })
            .AutomationName(S("Filter_Name"))
            .AutomationId("SystemsStatusFilter");

        var sortLabel = SortLabel(state.Sort);
        var sort = DropDownButton(sortLabel, MenuItems(
                RadioMenuItem(S("Sort_OnlineFirst"), "systems-sort", state.Sort == SystemsSortMode.OnlineFirst,
                    () => controller.SetSort(SystemsSortMode.OnlineFirst)),
                RadioMenuItem(S("Sort_OfflineFirst"), "systems-sort", state.Sort == SystemsSortMode.OfflineFirst,
                    () => controller.SetSort(SystemsSortMode.OfflineFirst)),
                RadioMenuItem(S("Sort_Alphabetical"), "systems-sort", state.Sort == SystemsSortMode.Alphabetical,
                    () => controller.SetSort(SystemsSortMode.Alphabetical))))
            .AutomationName(F("Sort_Name", sortLabel))
            .ToolTip(S("Sort_By"))
            .AutomationId("SystemsSortButton");

        Element list;
        if (!state.HasLoaded && state.Loading)
        {
            list = VStack(8, ProgressRing().IsActive(true).Size(24, 24), TextBlock(S("Loading")).HAlign(HorizontalAlignment.Center))
                .Margin(0, 32, 0, 0).HAlign(HorizontalAlignment.Center).AutomationId("SystemsLoading");
        }
        else if (visible.Count == 0)
        {
            var filtered = state.Rows.Count > 0;
            list = VStack(8,
                    Glyph(filtered ? "\uE71E" : "\uE977", 32).HAlign(HorizontalAlignment.Center)
                        .Foreground(Theme.Ref("TextFillColorTertiaryBrush")),
                    TextBlock(filtered ? S("NoMatches") : S("Empty")).TextWrapping()
                        .TextAlignment(TextAlignment.Center).Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                .Margin(16, 32, 16, 0)
                .AutomationId(filtered ? "SystemsNoMatches" : "SystemsEmpty");
        }
        else
        {
            var groups = new (SystemKind Kind, string Title)[]
            {
                (SystemKind.Host, S("Group_Host")),
                (SystemKind.Node, S("Group_Nodes")),
                (SystemKind.Worker, S("Group_Workers")),
                (SystemKind.Client, S("Group_Clients")),
            };
            list = VStack(12, groups
                .Select(group => (group.Title, group.Kind, Rows: visible.Where(row => row.Kind == group.Kind).ToArray()))
                .Where(group => group.Rows.Length > 0)
                .Select(group => InventoryGroup(group.Title, group.Kind, group.Rows, state, controller, actions)
                    .WithKey(group.Kind.ToString()))
                .ToArray());
        }

        // The compact list has no room for the filter tabs and the sort button on one line.
        Element controls = compact
            ? VStack(4, filter, sort.HAlign(HorizontalAlignment.Right))
            : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto], filter.Grid(column: 0), sort.VAlign(VerticalAlignment.Center).Grid(column: 1));
        return Card((ScrollViewer(VStack(8, search, controls, list)) with
                { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled })
                .AutomationId("SystemsInventoryScroll"))
            .Padding(12)
            .AutomationName(S("Inventory"))
            .AutomationId("SystemsInventory");
    }

    private static string SortLabel(SystemsSortMode sort) => sort switch
    {
        SystemsSortMode.Alphabetical => S("Sort_Alphabetical"),
        SystemsSortMode.OfflineFirst => S("Sort_OfflineFirst"),
        _ => S("Sort_OnlineFirst"),
    };

    private static Element InventoryGroup(string title, SystemKind kind, IReadOnlyList<SystemRow> rows,
        SystemsViewState state, SystemsController controller, SystemsPageActions actions)
    {
        var selectedIndex = rows.ToList().FindIndex(row => row.Id == state.SelectedId);
        var heading = kind == SystemKind.Host ? title : $"{title} ({rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)})";
        return VStack(4,
            HStack(8,
                Border(null).Size(8, 8).CornerRadius(4).Background(Theme.Ref(SystemsHue.ForKind(kind).Brush))
                    .VAlign(VerticalAlignment.Center).AccessibilityView(AccessibilityView.Raw),
                TextBlock(heading).FontSize(12).FontWeight(FontWeights.SemiBold)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .HeadingLevel(AutomationHeadingLevel.Level2))
                .Margin(4, 0, 0, 0),
            ListView((int?)selectedIndex, index =>
                    {
                        if (index >= 0 && index < rows.Count)
                            controller.Select(rows[index].Id);
                    },
                    rows.Select(row => InventoryItem(row, state, actions).WithKey(row.Id)).ToArray())
                .SelectionMode(ListViewSelectionMode.Single)
                .AutomationName(heading)
                .AutomationId("SystemsGroup_" + kind));
    }

    private static Element InventoryItem(SystemRow row, SystemsViewState state, SystemsPageActions actions)
    {
        var name = DisplayName(row);
        var status = EffectiveStatus(row, state.Connected);
        var secondary = status != SystemStatus.Online
            ? Status(status)
            : row.Kind == SystemKind.Worker ? SystemsProjection.ShortId(row.Id) : row.Platform ?? Status(status);
        var badges = new List<Element>();
        if (row.IsThisDevice)
            badges.Add(Tag(S("ThisDevice")));
        if (row.Environment?.Desktop == true)
            badges.Add(Glyph("\uE7F4", 14).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).ToolTip(S("Desktop")));
        var helpText = row.Platform is { Length: > 0 } platform ? $"{Status(status)}, {platform}" : Status(status);
        return Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                MachineAvatar(row, status, 36, 18).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                VStack(2,
                    TextBlock(name).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis)
                        .Opacity(status == SystemStatus.Offline ? 0.75 : 1),
                    HStack(6,
                        Glyph(StatusGlyph(status), 12).Foreground(Theme.Ref(StatusBrush(status))).VAlign(VerticalAlignment.Center),
                        TextBlock(secondary).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis)
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))).VAlign(VerticalAlignment.Center).Grid(column: 1),
                HStack(6, badges.ToArray()).VAlign(VerticalAlignment.Center).Margin(8, 0, 0, 0).Grid(column: 2))
            .Padding(0, 6)
            .AutomationName(name)
            .HelpText(helpText)
            .ToolTip(name)
            .WithContextFlyout(MenuItems(MenuItem(S("CopyDebug"),
                () => actions.CopyText(DebugSummary(row)))))
            .AutomationId("SystemsItem_" + row.Id);
    }

    // ── Details ────────────────────────────────────────────────────────

    private static Element Details(SystemsViewState state, SystemsController controller, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now, bool reducedMotion, SystemsLayout layout)
    {
        if (state.SelectionMissing)
            return Placeholder("\uE7BA", SystemsHue.Caution, S("Missing_Title"), S("Missing_Hint"), "SystemsSelectionMissing");
        if (state.Selected is not { } row)
        {
            if (state.HasLoaded && state.Rows.Count == 0)
                return EmptyState(state, controller, actions);
            return Placeholder("\uE977", SystemsHue.Accent, S("Select"), S("Select_Hint"), "SystemsSelectPrompt");
        }

        // Gateway timestamps are aged against the Gateway's clock so skew never shows as old or future data.
        var gatewayNow = now - state.GatewayClockOffset;
        var sections = new List<Element> { DetailsHeader(row, state, actions, open, gatewayNow).WithKey("header") };
        var status = EffectiveStatus(row, state.Connected);
        if (status == SystemStatus.Offline && row.Kind != SystemKind.Client)
        {
            sections.Add(InfoBar(S("Offline_Title"), S("Offline_Hint")).Informational().IsClosable(false)
                .AutomationId("SystemsOfflineNote").WithKey("offline"));
        }
        if (row.Kind != SystemKind.Client)
            sections.Add(Statistics(row, state, controller, now, layout).WithKey("stats"));
        sections.Add(MachineDetails(row, state, actions, gatewayNow).WithKey("details"));
        if (row.Kind != SystemKind.Client)
        {
            sections.Add(Desktop(row, state, controller, open).WithKey("desktop"));
            sections.Add(Sessions(row, actions).WithKey("sessions"));
        }
        if (Capabilities(row) is { } capabilities)
            sections.Add(capabilities.WithKey("capabilities"));
        if (row.Id == SystemsProjection.GatewayId)
            sections.Add(Backups(state, controller, gatewayNow).WithKey("backups"));

        // Keyed by machine: switching machines mounts a fresh pane, which plays the entrance.
        Element pane = VStack(16, sections.ToArray()).Padding(0, 0, 12, 24);
        if (!reducedMotion)
        {
            pane = pane.Transition(Microsoft.UI.Reactor.Animation.Transition.Enter(
                Microsoft.UI.Reactor.Animation.Transition.Fade +
                Microsoft.UI.Reactor.Animation.Transition.Slide(Microsoft.UI.Reactor.Animation.Edge.Bottom)));
        }
        return (ScrollViewer(pane.WithKey("pane:" + row.Id))
                with { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled })
            .AutomationId("SystemsDetails");
    }

    private static Element Placeholder(string glyph, SystemsHue hue, string title, string hint, string automationId) =>
        VStack(12,
                GlyphChip(glyph, hue, 72, 32).HAlign(HorizontalAlignment.Center),
                TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold).TextAlignment(TextAlignment.Center).TextWrapping(),
                TextBlock(hint).TextWrapping().TextAlignment(TextAlignment.Center).MaxWidth(420)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
            .AutomationId(automationId);

    /// <summary>
    /// No machines: a small constellation of the three machine kinds around the Gateway, with
    /// the ways forward. Decorative shapes stay out of the accessibility tree.
    /// </summary>
    private static Element EmptyState(SystemsViewState state, SystemsController controller, SystemsPageActions actions)
    {
        static Element Satellite(string glyph, SystemsHue hue, HorizontalAlignment h, VerticalAlignment v) =>
            Border(GlyphChip(glyph, hue, 44, 20))
                .Padding(3).CornerRadius(25)
                .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
                .HAlign(h).VAlign(v);

        var art = Grid([GridSize.Px(200)], [GridSize.Px(140)],
                Border(null).Size(120, 120).CornerRadius(60).Background(Theme.Ref("SystemsHeroBrush"))
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                GlyphChip("\uE968", SystemsHue.Accent, 76, 36).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                Satellite("\uE7F8", SystemsHue.Violet, HorizontalAlignment.Left, VerticalAlignment.Top),
                Satellite("\uE753", SystemsHue.Teal, HorizontalAlignment.Right, VerticalAlignment.Bottom),
                Satellite("\uE8EA", SystemsHue.Neutral, HorizontalAlignment.Right, VerticalAlignment.Top))
            .HAlign(HorizontalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);

        return VStack(12,
                art.Margin(0, 0, 0, 8),
                TextBlock(S("Empty_Title")).FontSize(24).FontWeight(FontWeights.SemiBold)
                    .TextAlignment(TextAlignment.Center).TextWrapping()
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                TextBlock(state.Connected ? S("Empty_Hint") : S("Disconnected_Message")).TextWrapping()
                    .TextAlignment(TextAlignment.Center).MaxWidth(440)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                HStack(8,
                        Button(S("Empty_Pair"), () => actions.Navigate("connection")).AccentButton()
                            .AutomationName(S("Empty_Pair")).AutomationId("SystemsEmptyPair"),
                        Button(S("Refresh"), () => _ = controller.RefreshAsync(manual: true))
                            .IsEnabled(state.Connected && !state.Loading)
                            .AutomationName(S("Refresh_Name")).AutomationId("SystemsEmptyRefresh"))
                    .HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0))
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
            .AutomationId("SystemsDetailsEmpty");
    }

    private static Element DetailsHeader(SystemRow row, SystemsViewState state, SystemsPageActions actions,
        Action<SystemsDialog?> open, DateTimeOffset now)
    {
        var name = DisplayName(row);
        var status = EffectiveStatus(row, state.Connected);
        var subtitle = new[] { Kind(row), row.Platform }.Where(part => !string.IsNullOrWhiteSpace(part));
        var buttons = new List<Element>
        {
            Button(HStack(6, Glyph("\uE8C8", 14), TextBlock(S("CopyDebug"))), () => actions.CopyText(DebugSummary(row))).AutomationName(S("CopyDebug"))
                .AutomationId("SystemsCopyDebug").WithKey("copy"),
        };
        if (row.CanManageNode && row.Node is { } node)
        {
            buttons.Add(Button(HStack(6, Glyph("\uE8AC", 14), TextBlock(LocalizationHelper.GetString("NodesPage_Action_Rename"))),
                    () => open(new RenameNodeDialog(node, new StrongBox<string>(node.HasExplicitDisplayName ? node.DisplayName : ""))))
                .AutomationName(LocalizationHelper.GetString("NodesPage_Action_Rename"))
                .IsEnabled(state.Connected).AutomationId("SystemsRenameNode").WithKey("rename"));
            buttons.Add(Button(HStack(6, Glyph("\uE74D", 14), TextBlock(LocalizationHelper.GetString("NodesPage_Action_Forget"))),
                    () => open(new ForgetNodeDialog(node)))
                .AutomationName(LocalizationHelper.GetString("NodesPage_Action_Forget"))
                .IsEnabled(state.Connected)
                .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                .AutomationId("SystemsForgetNode").WithKey("forget"));
        }

        var badges = new List<Element> { StatusBadge(status).WithKey("status") };
        if (row.IsThisDevice)
            badges.Add(Tag(S("ThisDevice")).WithKey("this"));
        if (row.Node?.ApprovalState is GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval)
            badges.Add(Tag(S("AwaitingApproval")).WithKey("pending"));

        // Quick facts read at a glance; the full list stays in Machine details.
        var facts = new List<Element>();
        if (row.SystemInfo is { UptimeMs: > 0 } info)
            facts.Add(Fact("\uE916", Duration(info.UptimeMs), S("Field_Uptime")).WithKey("uptime"));
        if ((row.Node?.Version ?? row.Instance?.Version) is { Length: > 0 } version)
            facts.Add(Fact("\uE8EC", version, LocalizationHelper.GetString("NodesPage_Label_Version").TrimEnd(':', ' ')).WithKey("version"));
        if (row.Environment?.LastSeenAt is { } seen && status != SystemStatus.Online)
            facts.Add(Fact("\uE823", Ago(seen, now), S("Field_LastSeen")).WithKey("seen"));
        if (row.Sessions.Count > 0)
            facts.Add(Fact("\uE8BD", Count(row.Sessions.Count), S("Sessions_Title")).WithKey("sessions"));

        var content = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            MachineAvatar(row, status, 64, 30).Margin(0, 0, 20, 0).VAlign(VerticalAlignment.Top).Grid(row: 0, column: 0, rowSpan: 2),
            VStack(6,
                WrapRow(12,
                    TextBlock(name).FontSize(24).FontWeight(FontWeights.SemiBold)
                        .TextWrapping(TextWrapping.WrapWholeWords).ToolTip(name)
                        .HeadingLevel(AutomationHeadingLevel.Level2)
                        .AutomationId("SystemsSelectedName"),
                    WrapRow(6, badges.ToArray())),
                TextBlock(string.Join(" · ", subtitle)).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                facts.Count == 0 ? Empty() : WrapRow(8, facts.ToArray()).Margin(0, 4, 0, 0)).Grid(row: 0, column: 1),
            WrapRow(8, buttons.ToArray()).Margin(0, 16, 0, 0).Grid(row: 1, column: 1));

        return Card(Grid([GridSize.Star()], [GridSize.Auto],
                // Decorative aurora wash behind the hero; high contrast maps it to the window color.
                Border(null).Background(Theme.Ref("SystemsHeroBrush")).CornerRadius(8)
                    .AccessibilityView(AccessibilityView.Raw),
                content.Padding(20)))
            .AutomationId("SystemsHero");
    }

    private static Element Fact(string glyph, string text, string label) =>
        Border(HStack(6,
                Glyph(glyph, 12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center),
                TextBlock(text).FontSize(12)))
            .Padding(10, 4)
            .CornerRadius(12)
            .Background(Theme.Ref("SystemsNeutralTintBrush"))
            .ToolTip(label)
            .AutomationName($"{label}: {text}");

    // ── Statistics ─────────────────────────────────────────────────────

    private static Element Statistics(SystemRow row, SystemsViewState state, SystemsController controller, DateTimeOffset now,
        SystemsLayout layout)
    {
        var toggle = Button(state.ShowStatistics ? S("Stats_Hide") : S("Stats_Show"),
                () => controller.SetShowStatistics(!state.ShowStatistics))
            .SubtleButton()
            .AutomationName(state.ShowStatistics ? S("Stats_Hide") : S("Stats_Show"))
            .AutomationId("SystemsStatsToggle");
        if (!state.ShowStatistics)
            return Section(S("Stats_Title"), "\uE9D9", SystemsHue.Accent, Empty(), toggle, "SystemsStatistics");

        if (row.Measurements is not { } stats)
        {
            return Section(S("Stats_Title"), "\uE9D9", SystemsHue.Accent,
                TextBlock(S("Stats_None")).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .AutomationId("SystemsNoStatistics"),
                toggle, "SystemsStatistics");
        }

        var refreshFailed = row.Id == SystemsProjection.GatewayId
            ? state.PartialErrors.SystemInfo is not null
            : state.PartialErrors.Nodes is not null;
        var lastKnown = SystemsTelemetryHistory.IsLastKnown(row, state.Connected, refreshFailed, now);
        var history = controller.Telemetry.For(row.Id);
        if (history.Count == 0)
            history = [new TelemetrySample(row.MeasuredAt ?? now, stats)];
        SparklineData Spark(Func<HostMeasurements, double?> read, double ceiling, bool autorange, Func<double, string> format) =>
            new(SystemsTelemetryHistory.TimedSeries(history, read), ceiling, autorange, !lastKnown, format, at => Ago(at, now));

        var tiles = new List<Element>();
        if (stats.Load1 is { } load)
        {
            var percent = SystemsMeters.LoadPercent(stats);
            tiles.Add(StatTile(row.Id, "load", S("Stats_Load"), "\uE9D9", SystemsHue.Accent, Load(load), F("Stats_Cores", stats.CpuCount),
                null, percent is { } p ? F("Stats_LoadMeter", Math.Round(p)) : null,
                Spark(sample => sample.Load1, Math.Max(1, stats.CpuCount), false, Load), CoreGrid(stats.CpuCount, percent, layout.CoresPerRow), lastKnown));
        }
        else
        {
            tiles.Add(StatTile(row.Id, "cpu", S("Stats_Cpu"), "\uE950", SystemsHue.Accent,
                stats.CpuCount.ToString(System.Globalization.CultureInfo.CurrentCulture), S("Stats_CpuCores"),
                null, null, null, CoreGrid(stats.CpuCount, null, layout.CoresPerRow), lastKnown));
        }

        var memory = SystemsMeters.MemoryUsedPercent(stats);
        tiles.Add(StatTile(row.Id, "memory", S("Stats_Memory"), "\uE964", SystemsHue.Violet, Bytes(stats.MemoryUsedBytes),
            F("Stats_OfTotal", Bytes(stats.MemoryTotalBytes)),
            memory, memory is { } m ? F("Stats_UsedMeter", Math.Round(m)) : null,
            Spark(sample => sample.MemoryUsedBytes, stats.MemoryTotalBytes, false, value => Bytes((long)value)), null, lastKnown));

        if (stats.Disks is { Count: > 0 } disks)
        {
            // Disk paths key the tiles, so a repeated path is shown once.
            foreach (var disk in disks.DistinctBy(disk => disk.Path, StringComparer.Ordinal))
            {
                var used = SystemsMeters.DiskUsedPercent(disk.AvailableBytes, disk.TotalBytes);
                tiles.Add(StatTile(row.Id, "disk:" + disk.Path, F("Stats_DiskPath", disk.Path), "\uEDA2", SystemsHue.Teal,
                    Bytes(disk.AvailableBytes), F("Stats_OfTotal", Bytes(disk.TotalBytes)),
                    used, used is { } u ? F("Stats_UsedMeter", Math.Round(u)) : null,
                    Spark(sample => sample.Disks?.FirstOrDefault(d => d.Path == disk.Path)?.AvailableBytes, disk.TotalBytes, true,
                        value => Bytes((long)value)), null, lastKnown));
            }
        }
        else if (stats.DiskAvailableBytes is { } available)
        {
            var used = SystemsMeters.DiskUsedPercent(available, stats.DiskTotalBytes);
            tiles.Add(StatTile(row.Id, "disk", S("Stats_Disk"), "\uEDA2", SystemsHue.Teal, Bytes(available),
                stats.DiskTotalBytes is { } total ? F("Stats_OfTotal", Bytes(total)) : "",
                used, used is { } u ? F("Stats_UsedMeter", Math.Round(u)) : null,
                Spark(sample => sample.Disks is null ? sample.DiskAvailableBytes : null, stats.DiskTotalBytes ?? available, true,
                    value => Bytes((long)value)), null, lastKnown));
        }

        var captions = new List<Element>();
        if (SystemsTelemetryHistory.IsCollecting(history.Count, lastKnown))
            captions.Add(TextBlock(S("Stats_Collecting")).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("collecting"));
        if (row.MeasuredAt is { } measuredAt)
        {
            captions.Add(HStack(4,
                    Glyph(lastKnown ? "\uE823" : "\uE9D9", 12).Foreground(Theme.Ref("TextFillColorTertiaryBrush")),
                    TextBlock(lastKnown ? F("Stats_LastKnown", Ago(measuredAt, now)) : F("Stats_Sampled", Ago(measuredAt, now)))
                        .FontSize(12).Foreground(Theme.Ref("TextFillColorTertiaryBrush")))
                .AutomationId("SystemsStatsSampled").WithKey("sampled"));
        }

        var freshness = lastKnown
            ? Pill(S("Stats_LastKnownBadge"), "\uE823", SystemsHue.Neutral).WithKey("stale")
            : Pill(S("Stats_LiveBadge"), "\uE91F", SystemsHue.Success).WithKey("live");
        var actions = WrapRow(8, freshness, toggle);
        var body = VStack(10, Rows(tiles, layout.StatColumns), WrapRow(16, captions.ToArray()));
        // A one-column pane has no room for the title, the badge and the button on one line.
        return layout.StatColumns == 1
            ? Section(S("Stats_Title"), "\uE9D9", SystemsHue.Accent, VStack(10, actions, body), null, "SystemsStatistics")
            : Section(S("Stats_Title"), "\uE9D9", SystemsHue.Accent, body, actions, "SystemsStatistics");
    }

    private static Element StatTile(string rowId, string metric, string label, string glyph, SystemsHue hue, string value, string sub,
        double? meter, string? meterLabel, SparklineData? spark, Element? extra, bool lastKnown)
    {
        var summary = spark is { Points.Count: > 1 } trend
            ? F("Stats_TrendName", label, value, trend.Points.Count)
            : $"{label}, {value} {sub}";
        if (meterLabel is not null)
            summary += ", " + meterLabel;

        var children = new List<Element>
        {
            Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                GlyphChip(glyph, hue, 28, 14).VAlign(VerticalAlignment.Center).Margin(0, 0, 10, 0).Grid(column: 0),
                TextBlock(label).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).Grid(column: 1),
                meterLabel is null ? Empty() : TextBlock(meterLabel).FontSize(12).VAlign(VerticalAlignment.Center)
                    .Foreground(Theme.Ref(hue.Brush)).Margin(8, 0, 0, 0).Grid(column: 2)).WithKey("head"),
            HStack(6,
                TextBlock(value).FontSize(24).FontWeight(FontWeights.SemiBold),
                TextBlock(sub).FontSize(12).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, 5)
                    .Foreground(Theme.Ref("TextFillColorTertiaryBrush"))).WithKey("value"),
        };
        if (meter is { } percent)
        {
            children.Add(Progress(percent)
                .Foreground(Theme.Ref(hue.Brush))
                .Background(Theme.Ref("SystemsMeterTrackBrush"))
                .MinHeight(4).CornerRadius(2)
                .AutomationName(meterLabel ?? label)
                .WithKey("meter"));
        }
        if (extra is not null)
            children.Add(extra.WithKey("extra"));
        // A lone last-known sample has no trend to draw; a live one shows the "now" dot while history collects.
        if (spark is { } candidate && candidate.Points.Count < 2 && !candidate.Live)
            spark = null;
        if (spark is not null)
        {
            children.Add(SystemsSparklineElement.SystemsSparkline()
                .Set(chart => chart.Update(spark))
                .Foreground(Theme.Ref(lastKnown ? "TextFillColorTertiaryBrush" : hue.Brush))
                .Height(SparkHeight)
                .Margin(-14, 2, -14, 0)
                .AccessibilityView(AccessibilityView.Raw)
                .WithKey("spark"));
        }
        return Border(VStack(8, children.ToArray()))
            .Padding(14, 12, 14, spark is null ? 14 : 0)
            .CornerRadius(8)
            .BorderThickness(1)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .Opacity(lastKnown ? 0.8 : 1)
            .AutomationName(summary)
            .ToolTip(summary)
            // Keyed per machine so a chart's hover and pulse never carry over to another machine.
            .WithKey(rowId + ":" + metric);
    }

    /// <summary>
    /// One cell per logical core, up to 64. With a load reading, that share of cells lights up,
    /// so a busy machine reads at a glance. Decorative; the tile names the numbers.
    /// </summary>
    private static Element CoreGrid(int cores, double? loadPercent, int perRow)
    {
        if (cores <= 0)
            return Empty();
        var shown = Math.Min(cores, 64);
        var lit = loadPercent is { } percent ? (int)Math.Round(shown * percent / 100) : 0;
        var rows = new List<Element>();
        for (var start = 0; start < shown; start += perRow)
        {
            // Without a load reading the grid only counts cores, so no cell claims to be busy.
            var cells = Enumerable.Range(start, Math.Min(perRow, shown - start))
                .Select(index => (Element)Border(null).Size(9, 9).CornerRadius(2)
                    .Background(Theme.Ref(index < lit ? SystemsHue.Accent.Brush : SystemsHue.Accent.Tint))
                    .BorderThickness(loadPercent is null ? 1 : 0)
                    .BorderBrush(Theme.Ref(SystemsHue.Accent.Brush))
                    .Opacity(loadPercent is null ? 0.7 : 1)
                    .WithKey("c" + index))
                .ToArray();
            rows.Add(HStack(3, cells).WithKey("r" + start));
        }
        return VStack(3, rows.ToArray()).Margin(0, 2, 0, 0).AccessibilityView(AccessibilityView.Raw);
    }

    // ── Machine details ────────────────────────────────────────────────

    private static Element MachineDetails(SystemRow row, SystemsViewState state, SystemsPageActions actions, DateTimeOffset now)
    {
        var rows = new List<(string Label, Element Value)>
        {
            (S("Field_Identifier"), Identifier(row.Node?.NodeId ?? row.Instance?.IdentityCaption ?? row.Id, actions)),
            (S("Field_Status"), StatusBadge(EffectiveStatus(row, state.Connected))),
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
            if (environment.SessionHost)
            {
                rows.Add((S("Field_SessionHost"), Value(environment.WorkerSlots is { } slots
                    ? F("Field_SlotsValue", slots.Available, slots.Total)
                    : S("Field_SessionHostEnabled"))));
            }
            if (environment.Worker is { } worker)
            {
                rows.Add((S("Field_Provider"), Value(Join(worker.ProviderId, worker.ProfileId) ?? worker.ProviderId)));
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
                TextBlock(entry.Label).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).TextWrapping()
                    .Margin(0, 4, 12, 4).Grid(row: index, column: 0),
                entry.Value.Margin(0, 4, 0, 4).VAlign(VerticalAlignment.Center).Grid(row: index, column: 1),
            }).ToArray());
        return Section(S("Details_Title"), "\uE946", SystemsHue.Neutral, grid, null, "SystemsMachineDetails");
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

    private static Element Desktop(SystemRow row, SystemsViewState state, SystemsController controller, Action<SystemsDialog?> open)
    {
        var section = DesktopPresentation.Resolve(row, state.Connected, state.HostDesktopEnabled, state.CanAdmin);
        var (desktopGlyph, desktopHue) = section.Kind switch
        {
            DesktopSectionKind.Available => ("\uE7F4", SystemsHue.Success),
            DesktopSectionKind.Offline => ("\uE7F4", SystemsHue.Neutral),
            DesktopSectionKind.HostSetup => ("\uE7F4", SystemsHue.Accent),
            _ => ("\uE7BA", SystemsHue.Caution),
        };
        var content = new List<Element>
        {
            Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                GlyphChip(desktopGlyph, desktopHue, 44, 20).VAlign(VerticalAlignment.Top).Margin(0, 0, 14, 0).Grid(column: 0),
                VStack(4,
                    TextBlock(DesktopTitle(section.Title)).FontWeight(FontWeights.SemiBold).TextWrapping()
                        .AutomationId("SystemsDesktopTitle"),
                    TextBlock(DesktopHint(section.Hint)).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                    .VAlign(VerticalAlignment.Center).Grid(column: 1)).WithKey("title"),
        };
        if (section.Detail is { } detail)
            content.Add(TextBlock(detail).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("detail"));
        if (section.Kind == DesktopSectionKind.Available && row.Environment?.DesktopAvailability is { } availability)
        {
            content.Add(Tag(availability switch
            {
                DesktopLockState.Locked => S("Desktop_Locked"),
                DesktopLockState.Unlocked => S("Desktop_Unlocked"),
                _ => S("Status_Unknown"),
            }).HAlign(HorizontalAlignment.Left).WithKey("lock"));
        }
        if (state.DesktopError is { } error && row.Id == SystemsProjection.GatewayId)
        {
            content.Add(TextBlock(F("Desktop_Failed", error)).TextWrapping()
                .Foreground(Theme.Ref("SystemFillColorCriticalBrush")).WithKey("error"));
        }
        if (section.ShowEnable)
        {
            content.Add(section.CanEnable
                ? VStack(4,
                    Button(state.DesktopEnabling ? S("Desktop_Enabling") : S("Desktop_Enable"), () => open(new EnableDesktopDialog()))
                        .AutomationName(S("Desktop_Enable"))
                        .AccentButton().IsEnabled(!state.DesktopEnabling && state.Connected)
                        .AutomationId("SystemsEnableDesktop"),
                    TextBlock(S("Desktop_ApplyHint")).FontSize(12).Foreground(Theme.Ref("TextFillColorTertiaryBrush"))).WithKey("enable")
                : TextBlock(S("Desktop_AdminHint")).FontSize(12).TextWrapping()
                    .Foreground(Theme.Ref("TextFillColorTertiaryBrush")).WithKey("admin"));
        }
        if (section.ShowCheckAgain)
        {
            content.Add(Button(S("Desktop_CheckAgain"), () => _ = controller.RefreshAsync(manual: true)).AutomationName(S("Desktop_CheckAgain"))
                .IsEnabled(state.Connected && !state.Loading).AutomationId("SystemsDesktopCheckAgain").WithKey("check"));
        }
        return Section(S("Desktop"), "\uE7F4", SystemsHue.Teal, VStack(8, content.ToArray()), null, "SystemsDesktop");
    }

    // ── Sessions ───────────────────────────────────────────────────────

    private static Element Sessions(SystemRow row, SystemsPageActions actions)
    {
        var items = new List<Element>
        {
            TextBlock(S("Sessions_Hint")).FontSize(12).TextWrapping().Foreground(Theme.Ref("TextFillColorTertiaryBrush")).WithKey("hint"),
        };
        if (row.Sessions.Count == 0)
        {
            items.Add(TextBlock(S("Sessions_None")).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .AutomationId("SystemsNoSessions").WithKey("none"));
        }
        else
        {
            items.AddRange(row.Sessions.Select((relation, index) =>
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        GlyphChip("\uE8BD", SystemsHue.Violet, 32, 14).VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).Grid(column: 0),
                        HyperlinkButton(relation.Session.Title, onClick: () => actions.OpenSession(relation.Session.Key))
                            .ToolTip(relation.Session.Key)
                            .AutomationName(F("Sessions_OpenName", relation.Session.Title, Relation(relation.Kind)))
                            .AutomationId("SystemsSession_" + relation.Session.Key)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(column: 1),
                        Pill(Relation(relation.Kind), RelationGlyph(relation.Kind), RelationHue(relation.Kind))
                            .VAlign(VerticalAlignment.Center).Grid(column: 2))
                    .WithKey(relation.Kind + ":" + relation.Session.Key + ":" + index)));
        }

        if (row.Environment?.Worker?.AttachedSessionIds is { Count: > 0 } attached)
        {
            items.Add(TextBlock(S("Sessions_Attached")).FontWeight(FontWeights.SemiBold).Margin(0, 8, 0, 0).WithKey("attached"));
            items.Add(TextBlock(S("Sessions_AttachedHint")).FontSize(12).TextWrapping()
                .Foreground(Theme.Ref("TextFillColorTertiaryBrush")).WithKey("attached-hint"));
            items.AddRange(attached.Distinct(StringComparer.Ordinal).Select(id => HStack(8,
                    Glyph("\uE71B", 12).Foreground(Theme.Ref(SystemsHue.Teal.Brush)).VAlign(VerticalAlignment.Center),
                    TextBlock(id).FontFamily("Consolas").IsTextSelectionEnabled(true))
                .WithKey("attached:" + id)));
        }
        return Section(S("Sessions_Title"), "\uE8BD", SystemsHue.Violet, VStack(6, items.ToArray()), null, "SystemsSessions");
    }

    private static string RelationGlyph(SessionRelationKind kind) => kind switch
    {
        SessionRelationKind.Placement => "\uE81D",
        SessionRelationKind.RetainedPlacement => "\uE718",
        SessionRelationKind.Runner => "\uE768",
        SessionRelationKind.ExecBinding => "\uE756",
        _ => "\uE968",
    };

    private static SystemsHue RelationHue(SessionRelationKind kind) => kind switch
    {
        SessionRelationKind.Placement => SystemsHue.Accent,
        SessionRelationKind.Runner => SystemsHue.Violet,
        SessionRelationKind.ExecBinding => SystemsHue.Teal,
        _ => SystemsHue.Neutral,
    };

    // ── Capabilities ───────────────────────────────────────────────────

    private static Element? Capabilities(SystemRow row)
    {
        var capabilities = (row.Environment?.Capabilities ?? Array.Empty<string>())
            .Concat(row.Node?.Capabilities ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Commands key their rows, so a repeated command is listed once.
        var commands = (row.Node?.Commands ?? row.Environment?.InvocableCommands.ToList() ?? new List<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        var hasPendingDeclarations =
            row.Node?.ApprovalState is GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval &&
            (row.Node.PendingDeclaredCapabilities.Count > 0 || row.Node.PendingDeclaredCommands.Count > 0);
        if (capabilities.Length == 0 && commands.Count == 0 && string.IsNullOrWhiteSpace(row.Node?.PathEnv) &&
            !hasPendingDeclarations)
            return null;

        var permissions = row.Node?.Permissions ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var content = new List<Element>();
        if (capabilities.Length > 0)
        {
            content.Add(Rows(capabilities.Select(capability =>
                {
                    // Only claim enabled/disabled when the permissions map names this exact capability.
                    var known = permissions.TryGetValue(capability, out var allowed);
                    var label = !known ? capability : allowed
                        ? F("Capability_Enabled", capability)
                        : F("Capability_Disabled", capability);
                    var parts = new List<Element>
                    {
                        Glyph(CapabilityGlyph(capability), 14).Foreground(Theme.Ref(SystemsHue.Accent.Brush))
                            .VAlign(VerticalAlignment.Center).WithKey("glyph"),
                        TextBlock(capability).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis)
                            .VAlign(VerticalAlignment.Center).WithKey("name"),
                    };
                    if (known)
                    {
                        parts.Add(Glyph(allowed ? "\uE73E" : "\uE711", 10)
                            .Foreground(Theme.Ref(allowed ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"))
                            .VAlign(VerticalAlignment.Center).WithKey("permission"));
                    }
                    return Border(HStack(8, parts.ToArray()))
                        .Padding(10, 6).CornerRadius(8).BorderThickness(1)
                        .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                        .ToolTip(label)
                        .AutomationName(label)
                        .WithKey(capability);
                }).ToList(), 4, spacing: 6).WithKey("chips"));
        }

        if (hasPendingDeclarations && row.Node is { } pendingNode)
        {
            content.Add(TextBlock(F("Capability_PendingDeclared",
                    string.Join(", ", pendingNode.PendingDeclaredCapabilities.Concat(pendingNode.PendingDeclaredCommands))))
                .FontSize(12).TextWrapping().Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                .AutomationId("SystemsPendingDeclarations").WithKey("pending"));
        }

        if (commands.Count > 0)
        {
            var disabled = new HashSet<string>(row.Node?.DisabledCommands ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            content.Add(Expander(
                    string.Format(LocalizationHelper.GetString("NodesPage_Commands_Header"), commands.Count),
                    VStack(2, commands.Select(command => TextBlock(disabled.Contains(command)
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
        return Section(S("Capabilities_Title"), "\uE945", SystemsHue.Caution, VStack(10, content.ToArray()), null, "SystemsCapabilities");
    }

    // ── Backups ────────────────────────────────────────────────────────

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

    private static Element Backups(SystemsViewState state, SystemsController controller, DateTimeOffset now)
    {
        Element content;
        if (!state.CanRead)
        {
            content = Note(S("Backups_ReadAccess"));
        }
        else if (state.BackupsError is { } error)
        {
            content = VStack(8,
                TextBlock(error).TextWrapping().Foreground(Theme.Ref("SystemFillColorCriticalBrush")),
                Button(S("Retry"), () => _ = controller.RefreshBackupsAsync()).AutomationName(S("Retry"))
                    .IsEnabled(state.Connected && !state.BackupsLoading).HAlign(HorizontalAlignment.Left));
        }
        else if (state.Backups is not { } backups)
        {
            content = state.BackupsUnsupported
                ? Note(S("Backups_Unsupported"))
                : HStack(8, ProgressRing().IsActive(state.Connected).Size(16, 16),
                    TextBlock(state.Connected ? S("Backups_Loading") : S("Disconnected_Message")).TextWrapping());
        }
        else
        {
            content = BackupsContent(BackupsPresentation.Project(backups), state, controller, now);
        }
        return Section(S("Backups_Title"), "\uE777", SystemsHue.Success, content, null, "SystemsBackups");
    }

    private static Element BackupsContent(BackupsView view, SystemsViewState state, SystemsController controller, DateTimeOffset now)
    {
        var items = new List<Element>();
        if (view.IsEmpty)
        {
            items.Add(VStack(4,
                    TextBlock(S("Backups_Empty")).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                    TextBlock("openclaw backup enable --to <location>").FontFamily("Consolas").IsTextSelectionEnabled(true))
                .AutomationId("SystemsBackupsEmpty").WithKey("empty"));
        }
        items.AddRange(view.Targets.Select((target, index) => BackupTarget(target, now).WithKey("target:" + index + ":" + target.Name)));
        items.AddRange(view.UnmatchedSchedules.Select(schedule =>
        {
            var parts = new List<string> { F("Backups_Scheduled", schedule.Target) };
            if (schedule.Namespace is { } ns) parts.Add(F("Backups_Namespace", ns));
            if (schedule.NextRunAt is { } next) parts.Add(F("Backups_NextRun", FormatDateTime(next)));
            return (Element)Note(string.Join(" · ", parts)).WithKey("schedule:" + schedule.Id);
        }));

        if (view.Locations.Count > 0)
        {
            items.Add(TextBlock(S("Backups_Locations")).FontWeight(FontWeights.SemiBold).Margin(0, 8, 0, 0)
                .HeadingLevel(AutomationHeadingLevel.Level3).WithKey("locations"));
            items.AddRange(view.Locations.Select(location => StorageLocationRow(location, state, controller).WithKey("location:" + location.Name)));
        }
        return VStack(10, items.ToArray());
    }

    private static Element BackupTarget(BackupTargetView target, DateTimeOffset now)
    {
        var meta = new List<string> { BackupKind(target.Kind) };
        var lastSuccess = target.LastSuccessAt is { } ok ? Ago(ok, now) : S("Backups_Never");
        meta.Add(target.Health == OpenClaw.Shared.Systems.BackupHealth.PushFailed
            ? F("Backups_LastLocalSuccess", lastSuccess)
            : F("Backups_LastSuccess", lastSuccess));
        if (target.SizeBytes is { } size) meta.Add(Bytes(size));
        if (target.NextRunAt is { } next) meta.Add(F("Backups_NextRun", FormatDateTime(next)));

        var (glyph, hue) = target.Health switch
        {
            OpenClaw.Shared.Systems.BackupHealth.Ok => ("\uE73E", SystemsHue.Success),
            OpenClaw.Shared.Systems.BackupHealth.PushFailed => ("\uE7BA", SystemsHue.Caution),
            _ => ("\uEA39", SystemsHue.Critical),
        };
        var lines = new List<Element>
        {
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(target.Name).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center).Grid(column: 0),
                Pill(BackupHealth(target.Health), glyph, hue).Grid(column: 1)).WithKey("head"),
            TextBlock(string.Join(" · ", meta)).FontSize(12).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("meta"),
            TextBlock(target.Destination).FontFamily("Consolas").FontSize(12).IsTextSelectionEnabled(true)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(target.Destination).WithKey("destination"),
        };
        if (target.Namespace is { } ns)
            lines.Add(TextBlock(F("Backups_Namespace", ns)).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).WithKey("ns"));
        if (target.Health != OpenClaw.Shared.Systems.BackupHealth.Ok)
        {
            lines.Add(TextBlock(target.Error ?? (target.Health == OpenClaw.Shared.Systems.BackupHealth.PushFailed
                    ? S("Backups_PushFailureHint") : S("Backups_FailureHint")))
                .FontSize(12).TextWrapping().Foreground(Theme.Ref("SystemFillColorCriticalBrush")).WithKey("error"));
        }
        return Border(Grid([GridSize.Px(4), GridSize.Star()], [GridSize.Auto],
                Border(null).Background(Theme.Ref(hue.Brush)).CornerRadius(2).Margin(0, 2, 0, 2)
                    .AccessibilityView(AccessibilityView.Raw).Grid(column: 0),
                VStack(4, lines.ToArray()).Margin(12, 0, 0, 0).Grid(column: 1)))
            .Padding(12).CornerRadius(8).BorderThickness(1)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .AutomationName($"{target.Name}, {BackupHealth(target.Health)}");
    }

    private static Element StorageLocationRow(StorageLocation location, SystemsViewState state, SystemsController controller)
    {
        state.Probes.TryGetValue(location.Name, out var probe);
        var checking = probe?.Status == ProbeStatus.Checking;
        Element result = Empty();
        if (probe?.Result is { } outcome)
        {
            var parts = new List<string> { Probe(outcome.State) };
            if (!string.IsNullOrWhiteSpace(outcome.Message)) parts.Add(outcome.Message!);
            if (outcome.FreeBytes is { } free)
            {
                parts.Add(outcome.TotalBytes is { } capacity
                    ? F("Backups_FreeOf", Bytes(free), Bytes(capacity))
                    : F("Backups_Free", Bytes(free)));
            }
            var ok = outcome.State == StorageProbeState.Ok;
            var used = outcome.FreeBytes is { } available ? SystemsMeters.DiskUsedPercent(available, outcome.TotalBytes) : null;
            result = VStack(6,
                    HStack(4,
                        Glyph(ok ? "\uE73E" : "\uE7BA", 12)
                            .Foreground(Theme.Ref(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"))
                            .VAlign(VerticalAlignment.Center),
                        TextBlock(string.Join(" · ", parts)).FontSize(12).TextWrapping()),
                    used is { } percent
                        ? Progress(percent).Foreground(Theme.Ref(SystemsHue.Teal.Brush))
                            .Background(Theme.Ref("SystemsMeterTrackBrush")).MinHeight(4).CornerRadius(2)
                            .AutomationName(F("Stats_UsedMeter", Math.Round(percent)))
                        : Empty())
                .AutomationId("SystemsProbeResult_" + location.Name);
        }
        var name = location.Encrypted ? F("Backups_Encrypted", location.Name) : location.Name;
        return Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
            GlyphChip(StorageGlyph(location), SystemsHue.Teal, 36, 16).VAlign(VerticalAlignment.Top).Margin(0, 0, 12, 0)
                .Grid(row: 0, column: 0, rowSpan: 2),
            VStack(2,
                HStack(6,
                    TextBlock(name).FontWeight(FontWeights.SemiBold),
                    location.Encrypted
                        ? Glyph("\uE72E", 12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center)
                        : Empty()),
                TextBlock(location.DisplayTarget ?? location.Provider).FontFamily("Consolas").FontSize(12)
                    .TextTrimming(TextTrimming.CharacterEllipsis).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))).Grid(row: 0, column: 1),
            Button(checking ? S("Backups_Checking") : S("Backups_Check"), () => _ = controller.ProbeStorageAsync(location.Name))
                .IsEnabled(!checking && state.Connected)
                .AutomationName(F("Backups_CheckName", location.Name))
                .AutomationId("SystemsProbe_" + location.Name)
                .VAlign(VerticalAlignment.Center).Grid(row: 0, column: 2),
            result.Margin(0, 6, 0, 0).Grid(row: 1, column: 1, columnSpan: 2));
    }

    private static string StorageGlyph(StorageLocation location)
    {
        var target = (location.DisplayTarget ?? "").Trim();
        var provider = location.Provider.Trim().ToLowerInvariant();
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || provider is "smb" or "nfs" or "network")
            return "\uE968";
        if (provider is "local" or "file" or "path" || (target.Length > 1 && target[1] == ':') || target.StartsWith('/'))
            return "\uEDA2";
        return "\uE753";
    }

    // ── Dialogs ────────────────────────────────────────────────────────

    private static Element Dialog(SystemsDialog? dialog, Action<SystemsDialog?> setDialog, SystemsPageActions actions,
        Func<int> currentEpoch, Func<SystemsDialog?> currentDialog)
    {
        if (dialog is null)
            return Empty();

        Element ErrorText() => dialog.Error is { } error
            ? TextBlock(error).FontSize(12).TextWrapping().Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
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
            case RenameNodeDialog rename:
                element = ContentDialog(LocalizationHelper.GetString("NodesPage_Rename_Title"),
                    VStack(8,
                        TextBlock(string.Format(LocalizationHelper.GetString("NodesPage_Rename_Body"), NodeName(rename.Node))).TextWrapping(),
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
                break;
            case ForgetNodeDialog forget:
                var subtitle = string.Join(" · ", new[] { forget.Node.Platform, forget.Node.ShortId }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                element = ContentDialog(LocalizationHelper.GetString("NodesPage_Forget_Title"),
                    VStack(8,
                        TextBlock(LocalizationHelper.GetString("NodesPage_Forget_Body")).TextWrapping(),
                        VStack(2,
                            TextBlock(NodeName(forget.Node)).FontWeight(FontWeights.SemiBold),
                            TextBlock(subtitle).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))),
                        TextBlock(LocalizationHelper.GetString("NodesPage_Forget_Warning")).FontSize(12).TextWrapping()
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                        ErrorText()),
                    LocalizationHelper.GetString("NodesPage_Forget_Primary")) with
                {
                    // Cancel is the default so Enter never removes a node.
                    DefaultButton = ContentDialogButton.Close,
                    OnClosed = OnPrimary(() => _ = RunAsync(() => actions.ForgetNode(forget.Node))),
                };
                break;
            case PairingDialog pairing:
                var who = string.IsNullOrWhiteSpace(pairing.Approval.DisplayName) ? pairing.Approval.DeviceId : pairing.Approval.DisplayName!;
                element = ContentDialog(
                    pairing.Approve ? F("Pairing_ApproveTitle", who) : F("Pairing_RejectTitle", who),
                    VStack(8,
                        TextBlock(pairing.Approve ? F("Pairing_ApproveBody", who) : F("Pairing_RejectBody", who)).TextWrapping(),
                        pairing.Approval.Scopes.Count > 0
                            ? TextBlock(F("Pairing_Scopes", string.Join(", ", pairing.Approval.Scopes))).TextWrapping()
                            : Empty(),
                        ErrorText()),
                    pairing.Approve ? S("Pairing_Approve") : S("Pairing_Reject")) with
                {
                    DefaultButton = ContentDialogButton.Close,
                    OnClosed = OnPrimary(() => _ = RunAsync(() => actions.DecidePairing(pairing.Approval, pairing.Approve))),
                };
                break;
            default:
                element = ContentDialog(S("Desktop_ConfirmTitle"),
                    TextBlock(S("Desktop_ConfirmBody")).TextWrapping(),
                    S("Desktop_Enable")) with
                {
                    DefaultButton = ContentDialogButton.Close,
                    // The desktop card reports the outcome, so the dialog closes immediately.
                    OnClosed = OnPrimary(() =>
                    {
                        setDialog(null);
                        _ = actions.EnableHostDesktop();
                    }),
                };
                break;
        }
        if (dialog.Busy)
            return Empty();
        return (element with
            {
                IsOpen = true,
                CloseButtonText = LocalizationHelper.GetString("NodesPage_Common_Cancel"),
            })
            .WithKey("dialog-" + dialog.Show.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string NodeName(GatewayNodeInfo node) =>
        string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;

    // ── Shared pieces ──────────────────────────────────────────────────

    private static Element Section(string title, string glyph, SystemsHue hue, Element content, Element? action, string automationId) =>
        Card(VStack(14,
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    GlyphChip(glyph, hue, 32, 16).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                    TextBlock(title).FontSize(16).FontWeight(FontWeights.SemiBold).TextWrapping()
                        .HeadingLevel(AutomationHeadingLevel.Level3).VAlign(VerticalAlignment.Center).Grid(column: 1),
                    (action ?? Empty()).VAlign(VerticalAlignment.Center).Grid(column: 2)),
                content))
            .Padding(18, 16)
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
    private static Element GlyphChip(string glyph, SystemsHue hue, double size, double glyphSize) =>
        Border(Glyph(glyph, glyphSize).Foreground(Theme.Ref(hue.Brush))
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.3))
            .Background(Theme.Ref(hue.Tint))
            .AccessibilityView(AccessibilityView.Raw);

    /// <summary>Compact labelled pill on a hue tint, with a glyph so the state never relies on color.</summary>
    private static Element Pill(string text, string glyph, SystemsHue hue) =>
        Border(HStack(5,
                Glyph(glyph, 11).Foreground(Theme.Ref(hue.Brush)).VAlign(VerticalAlignment.Center),
                TextBlock(text).FontSize(12).VAlign(VerticalAlignment.Center)))
            .Padding(9, 3)
            .CornerRadius(11)
            .Background(Theme.Ref(hue.Tint))
            .AutomationName(text);

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
    private static Element Rows(IReadOnlyList<Element> children, int columns, double spacing = 12)
    {
        var rows = new List<Element>();
        for (var start = 0; start < children.Count; start += columns)
        {
            var slice = children.Skip(start).Take(columns).ToArray();
            var cells = new List<Element>();
            for (var i = 0; i < slice.Length; i++)
                cells.Add(slice[i].Grid(column: i * 2));
            var definition = Enumerable.Range(0, columns * 2 - 1)
                .Select(index => index % 2 == 0 ? GridSize.Star() : GridSize.Px(spacing)).ToArray();
            rows.Add(Grid(definition, [GridSize.Auto], cells.ToArray()).WithKey("row" + start));
        }
        return VStack(spacing, rows.ToArray());
    }

    private static Element StatusBadge(SystemStatus status) =>
        Border(HStack(4,
                Glyph(StatusGlyph(status), 12).Foreground(Theme.Ref(StatusBrush(status))).VAlign(VerticalAlignment.Center),
                TextBlock(Status(status)).FontSize(12)))
            .Padding(8, 2)
            .CornerRadius(10)
            .Background(Theme.Ref(StatusBackground(status)))
            .HAlign(HorizontalAlignment.Left)
            .AutomationName(F("Status_Name", Status(status)))
            .AutomationId("SystemsStatusBadge");

    private static Element Tag(string text) =>
        Border(TextBlock(text).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
            .Padding(8, 2)
            .CornerRadius(10)
            .BorderThickness(1)
            .Background(Theme.Ref("ControlAltFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("ControlStrokeColorDefaultBrush"))
            .AutomationName(text);

    private static TextBlockElement Value(string text) => TextBlock(text).TextWrapping().IsTextSelectionEnabled(true);

    private static Element Note(string text) =>
        TextBlock(text).TextWrapping().Foreground(Theme.Ref("TextFillColorSecondaryBrush"));

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

    private static string StatusBackground(SystemStatus status) => status switch
    {
        SystemStatus.Online => "SystemFillColorSuccessBackgroundBrush",
        SystemStatus.Starting or SystemStatus.Stopping => "SystemFillColorAttentionBackgroundBrush",
        SystemStatus.Error => "SystemFillColorCriticalBackgroundBrush",
        _ => "SubtleFillColorSecondaryBrush",
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
