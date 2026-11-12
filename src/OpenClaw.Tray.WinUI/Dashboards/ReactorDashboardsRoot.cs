using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Presentation.PageResponsiveLayout;

namespace OpenClawTray.Dashboards;

internal sealed record DashboardsRootProps(DashboardsPageState State);

/// <summary>
/// Production Reactor root for the Dashboards hub page: the gallery of
/// session dashboards, or one open dashboard.
/// </summary>
internal sealed class ReactorDashboardsRoot : Component<DashboardsRootProps>
{
    public override Element Render()
    {
        var state = Props.State;
        var (_, setVersion) = UseState(0, threadSafe: true);
        var version = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            var current = true;
            void OnChanged() => state.Post(() =>
            {
                if (current)
                    setVersion(++version.Current);
            });
            state.Changed += OnChanged;
            OnChanged();
            return () =>
            {
                current = false;
                state.Changed -= OnChanged;
            };
        }), state);

        var announce = this.UseAnnounce();
        UseEffect((Func<Action>)(() =>
        {
            var current = true;
            if (state.Connected && state.CopySucceeded is { } copied)
            {
                var message = LocalizationHelper.GetString(copied ? "Dashboards_CopiedSessionKey" : "Dashboards_CopyFailed");
                state.Post(() =>
                {
                    if (current)
                        announce.Announce(message, assertive: !copied);
                });
            }
            return () => current = false;
        }), state.CopyNoticeRevision, state.Connected);

        if (state.Client is not { } client || !state.Connected)
            return Component<ReactorDisconnectedDashboards, DashboardsRootProps>(Props).WithKey("disconnected");

        Element content = state.OpenTarget is { } target
            ? Component<ReactorBoardView, BoardViewProps>(new BoardViewProps(state, client, target, state.ConnectionRevision, state.ViewportWidth, state.TopInset))
                .WithKey("board:" + target.AgentId + "\0" + target.SessionKey)
            : Component<ReactorDashboardGallery, DashboardGalleryProps>(
                    new DashboardGalleryProps(state, client, state.AgentId, state.ConnectionRevision, state.ViewportWidth, state.TopInset))
                .WithKey("gallery");
        Element notice = Empty();
        if (state.CopySucceeded is { } copied)
        {
            notice = Border(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    DashboardsStates.Glyph(copied ? "\uE73E" : "\uE783", 18,
                        copied ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush")
                        .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                    TextBlock(LocalizationHelper.GetString(copied ? "Dashboards_CopiedSessionKey" : "Dashboards_CopyFailed"))
                        .Foreground(Theme.Ref("TextFillColorPrimaryBrush")).TextWrapping(TextWrapping.WrapWholeWords)
                        .AutomationId("DashboardCopyNoticeMessage").Grid(column: 1)))
                .Set(control => control.RequestedTheme = state.DarkTheme ? ElementTheme.Dark : ElementTheme.Light)
                .Padding(16, 12).CornerRadius(8)
                .Background(Theme.Ref("FlyoutPresenterBackground"))
                .BorderBrush(Theme.Ref("FlyoutBorderThemeBrush")).BorderThickness(1)
                .Set(control => control.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow())
                .Translation(0, 0, 16)
                .AutomationId("DashboardCopyNotice")
                .MaxWidth(480).Margin(20)
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom);
        }
        return Grid([GridSize.Star()], [GridSize.Star()],
            content,
            notice.WithKey("copy-notice"),
            announce.Region.LiveRegion(state.CopySucceeded == false ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)
                .AutomationId("DashboardResultAnnouncements").WithKey("announcements"));
    }
}

internal sealed class ReactorDisconnectedDashboards : Component<DashboardsRootProps>
{
    public override Element Render()
    {
        var state = Props.State;
        var (measuredWidth, setWidth) = UseState(MinWidth);
        var width = state.ViewportWidth > 0 ? state.ViewportWidth : measuredWidth;
        var reducedMotion = UseReducedMotion();
        var highContrast = UseHighContrast();
        var notice = InfoBar(LocalizationHelper.GetString("Dashboards_Disconnected_Title"),
                LocalizationHelper.GetString("Dashboards_Disconnected_Detail"))
            .Error().IsClosable(false).AutomationId("DashboardsDisconnectedInfo")
            .Set(info =>
            {
                info.ActionButton = state.OpenConnection is not null
                    ? new Button
                    {
                        Content = LocalizationHelper.GetString("Dashboards_Disconnected_Action")
                    }
                    : null;
                if (info.ActionButton is Button button)
                {
                    button.Click += (_, _) => state.OpenConnection?.Invoke();
                    AutomationProperties.SetAutomationId(button, "DashboardsOpenConnection");
                }
            });
        var content = Border(VStack(12,
                ReactorDashboardGallery.Hero(state, state.AgentId, DashboardGallerySnapshot.Initial, () => { },
                    width, highContrast, reducedMotion),
                notice))
            .Width(ContentWidth(width) + 2 * HorizontalPadding(TierFor(width)))
            .MaxWidth(ContentMaxWidth + 2 * HorizontalPadding(TierFor(width)))
            .Padding(HorizontalPadding(TierFor(width)), 24, HorizontalPadding(TierFor(width)), 0)
            .HAlign(HorizontalAlignment.Center);
        return Border(DashboardsStates.VerticalScroll(content))
            .Margin(0, state.TopInset, 0, 0)
            .AutomationId("DashboardsDisconnectedViewport")
            .OnSizeChanged((_, args) =>
            {
                if (state.ViewportWidth <= 0 && Snap(args.NewSize.Width) != Snap(measuredWidth))
                    setWidth(args.NewSize.Width);
            });
    }
}

internal static class DashboardsGlyphs
{
    public const string Refresh = "\uE72C";
    public const string Search = "\uE721";
    public const string Chat = "\uE8BD";
    public const string Dashboard = "\uECA5";
    public const string Shield = "\uEA18";
    public const string Disconnected = "\uF384";
    public const string Error = "\uEA39";
    public const string More = "\uE712";
    public const string Copy = "\uE8C8";
    public const string OpenInBrowser = "\uE8A7";
}

/// <summary>Shared visual building blocks: status panels, glyphs, icon buttons.</summary>
internal static class DashboardsStates
{
    public static ScrollViewerElement VerticalScroll(Element content) => ScrollViewer(content) with
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public static Element ChatButton(string title, Action open, string id) =>
        IconButton(DashboardsGlyphs.Chat, LocalizationHelper.GetString("Dashboards_OpenChat"), open, id)
            .AutomationName(LocalizationHelper.Format("Dashboards_OpenChatFor", title));

    public static TextBlockElement Glyph(string glyph, double size, string brush = "TextFillColorSecondaryBrush") =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .Foreground(Theme.Ref(brush))
            .AccessibilityView(AccessibilityView.Raw);

    public static Element Status(string id, string glyph, string title, string? detail, Element? action = null, bool alert = false, Element? art = null, int hue = DashboardHues.Blue)
    {
        var children = new List<Element>
        {
            (art ?? (alert
                    ? DashboardsArt.GlyphBadge(glyph, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 72)
                    : DashboardsArt.HueBadge(glyph, hue, 72)))
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 0, 0, 4),
            TextBlock(title).FontSize(20).SemiBold()
                .TextWrapping(TextWrapping.WrapWholeWords).TextAlignment(TextAlignment.Center)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .AutomationId(id + "Title"),
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            children.Add(TextBlock(detail).FontSize(14)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.WrapWholeWords).TextAlignment(TextAlignment.Center)
                .MaxWidth(440)
                .AutomationId(id + "Detail"));
        }
        if (action is not null)
            children.Add(action.HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0));
        return VStack(12, children.ToArray())
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
            .Margin(24, 48)
            .LiveRegion(alert ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)
            .AutomationId(id);
    }

    public static Element Disconnected(DashboardsPageState state) =>
        Status(
            "DashboardsDisconnected",
            DashboardsGlyphs.Disconnected,
            LocalizationHelper.GetString("Dashboards_Disconnected_Title"),
            LocalizationHelper.GetString("Dashboards_Disconnected_Detail"),
            state.OpenConnection is { } open
                ? Button(LocalizationHelper.GetString("Dashboards_Disconnected_Action"), open).AccentButton()
                    .AutomationName(LocalizationHelper.GetString("Dashboards_Disconnected_Action"))
                    .AutomationId("DashboardsOpenConnection")
                : null,
            art: DashboardsArt.GlyphBadge(DashboardsGlyphs.Disconnected, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 72));

    public static Element Unavailable() =>
        Status(
            "DashboardsUnavailable",
            DashboardsGlyphsExtra.Lock,
            LocalizationHelper.GetString("Dashboards_Unavailable_Title"),
            LocalizationHelper.GetString("Dashboards_Unavailable_Detail"),
            hue: DashboardHues.Purple);

    public static ButtonElement IconButton(string glyph, string label, Action onClick, string automationId) =>
        Button(Icon(FontIcon(glyph)), onClick)
            .ToolTip(label)
            .AutomationName(label)
            .AutomationId(automationId);
}

internal sealed record DashboardGalleryProps(
    DashboardsPageState State,
    IBoardGatewayClient Client,
    string AgentId,
    int ConnectionRevision,
    double ViewportWidth = 0,
    double TopInset = 0);

/// <summary>
/// Gallery of every session dashboard for the selected agent. Cards are
/// static gallery-row metadata: opening a card loads its board, but the
/// gallery itself never loads boards, widget tickets, or web content.
/// </summary>
internal sealed class ReactorDashboardGallery : Component<DashboardGalleryProps>
{
    private const double CardHeight = 296;

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(DashboardGallerySnapshot.Initial, threadSafe: true);
        var (query, updateQuery) = UseState(props.State.GalleryQuery);
        var (sort, updateSort) = UseState(props.State.GallerySort);
        var (shown, updateShown) = UseState(props.State.GalleryShown);
        void setQuery(string value) { props.State.GalleryQuery = value; updateQuery(value); }
        void setSort(DashboardGallerySort value) { props.State.GallerySort = value; updateSort(value); }
        void setShown(int value) { props.State.GalleryShown = value; updateShown(value); }
        var (measuredWidth, setWidth) = UseState(MinWidth);
        var width = props.ViewportWidth > 0 ? props.ViewportWidth : measuredWidth;
        var model = UseRef<DashboardGalleryModel?>(null);
        var focusRoot = UseRef<FrameworkElement?>(null);
        var bindReturnFocusRoot = UseRef<Action<FrameworkElement>?>(null);

        UseEffect((Func<Action>)(() =>
        {
            var current = true;
            var queued = false;
            FrameworkElement? root = null;
            void QueueReturnFocus()
            {
                if (!current || queued || !props.State.ReturnFocusPending || root is not { IsLoaded: true })
                    return;
                queued = true;
                props.State.Post(async () =>
                {
                    try
                    {
                        if (!current || !props.State.ReturnFocusPending || root is not { IsLoaded: true })
                            return;
                        var elements = Descendants(root).ToArray();
                        var card = elements.OfType<Button>().FirstOrDefault(button =>
                            AutomationProperties.GetAutomationId(button) == props.State.ReturnCardId);
                        if (card is { ActualWidth: > 0, ActualHeight: > 0 } && card.Focus(FocusState.Programmatic))
                            props.State.CompleteGalleryReturn();
                        else if (card is null && elements.FirstOrDefault(element =>
                            AutomationProperties.GetAutomationId(element) == "DashboardsTitle") is { ActualWidth: > 0, ActualHeight: > 0 } heading &&
                            (await Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(heading, FocusState.Programmatic)).Succeeded)
                            props.State.CompleteGalleryReturn();
                    }
                    finally
                    {
                        queued = false;
                    }
                });
            }
            void OnLoaded(object sender, RoutedEventArgs args) => QueueReturnFocus();
            void OnLayoutUpdated(object? sender, object args) => QueueReturnFocus();
            void BindRoot(FrameworkElement mountedRoot)
            {
                if (!current || ReferenceEquals(root, mountedRoot))
                    return;
                if (root is not null)
                {
                    root.Loaded -= OnLoaded;
                    root.LayoutUpdated -= OnLayoutUpdated;
                }
                root = mountedRoot;
                root.Loaded += OnLoaded;
                root.LayoutUpdated += OnLayoutUpdated;
                QueueReturnFocus();
            }
            if (props.State.ReturnFocusPending && snapshot.Status == DashboardLoadStatus.Ready)
            {
                bindReturnFocusRoot.Current = BindRoot;
                if (focusRoot.Current is { } mountedRoot)
                    BindRoot(mountedRoot);
            }
            return () =>
            {
                current = false;
                bindReturnFocusRoot.Current = null;
                if (root is not null)
                {
                    root.Loaded -= OnLoaded;
                    root.LayoutUpdated -= OnLayoutUpdated;
                }
            };
        }), snapshot, props.State.ReturnFocusPending);

        UseEffect((Func<Action>)(() =>
        {
            var gallery = new DashboardGalleryModel(props.Client, props.State.Post);
            model.Current = gallery;
            var discovering = props.State.NeedsDefaultAgent;
            gallery.Changed += next =>
            {
                if (!discovering)
                    setSnapshot(next);
            };
            async Task Load()
            {
                if (discovering)
                {
                    await gallery.LoadAsync(null);
                    props.State.Post(() =>
                    {
                        if (!ReferenceEquals(model.Current, gallery))
                            return;
                        var result = gallery.Snapshot;
                        discovering = false;
                        if (result.Status != DashboardLoadStatus.Ready)
                        {
                            setSnapshot(result);
                            return;
                        }
                        props.State.ResolveDefaultAgent(result.Rows);
                        if (props.State.AgentId == props.AgentId)
                            _ = gallery.LoadAsync(props.AgentId);
                    });
                    return;
                }
                await gallery.LoadAsync(props.AgentId);
            }
            _ = Load();
            return () =>
            {
                model.Current = null;
                gallery.Dispose();
            };
        }), props.Client, props.AgentId, props.ConnectionRevision);

        var refresh = () => { _ = model.Current?.LoadAsync(props.AgentId); };
        var reducedMotion = UseReducedMotion();
        var highContrast = UseHighContrast();
        var header = Hero(props.State, props.AgentId, snapshot, refresh, width, highContrast, reducedMotion);

        Element body = snapshot.Status switch
        {
            DashboardLoadStatus.Unavailable => DashboardsStates.Unavailable(),
            DashboardLoadStatus.Disconnected => DashboardsStates.Disconnected(props.State),
            DashboardLoadStatus.Loading when !snapshot.HasRows => Skeleton(width),
            DashboardLoadStatus.Error when !snapshot.HasRows => DashboardsStates.Status(
                "DashboardsLoadError",
                DashboardsGlyphs.Error,
                LocalizationHelper.GetString("Dashboards_LoadError_Title"),
                LocalizationHelper.Format("Dashboards_LoadErrorFormat", snapshot.Error ?? ""),
                Button(LocalizationHelper.GetString("Dashboards_Retry"), refresh).AutomationName(LocalizationHelper.GetString("Dashboards_Retry")).AutomationId("DashboardsRetry"),
                alert: true),
            _ when !snapshot.HasRows && string.IsNullOrWhiteSpace(query) => DashboardsStates.Status(
                "DashboardsEmpty",
                DashboardsGlyphs.Dashboard,
                LocalizationHelper.GetString("Dashboards_Empty_Title"),
                LocalizationHelper.GetString("Dashboards_Empty_Detail"),
                action: props.State.OpenAgentChat is { } open
                    ? Button(LocalizationHelper.GetString("Dashboards_OpenChat"), () => open(props.AgentId))
                        .AutomationName(LocalizationHelper.GetString("Dashboards_OpenChat"))
                        .AutomationId("DashboardsEmptyOpenChat")
                    : null,
                art: DashboardsArt.GlyphBadge(DashboardsGlyphs.Dashboard, "AccentTextFillColorPrimaryBrush", "SubtleFillColorSecondaryBrush", 72)),
            _ => Gallery(props, snapshot, query, setQuery, sort, setSort, reducedMotion, shown, setShown, width),
        };

        var children = new List<Element> { header.Grid(row: 0) };
        if (snapshot.Status == DashboardLoadStatus.Error && snapshot.HasRows)
        {
            children.Add(InfoBar(
                    LocalizationHelper.GetString("Dashboards_LoadError_Title"),
                    LocalizationHelper.Format("Dashboards_LoadErrorFormat", snapshot.Error ?? ""))
                .Warning().IsClosable(false)
                .Margin(0, 0, 0, 12)
                .AutomationId("DashboardsStaleError")
                .Grid(row: 1));
        }
        children.Add(body.Grid(row: 2));
        var column = Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto], children.ToArray()))
            .Set(control =>
            {
                focusRoot.Current = control;
                bindReturnFocusRoot.Current?.Invoke(control);
            })
            .Width(ContentWidth(width) + 2 * HorizontalPadding(TierFor(width)))
            .MaxWidth(ContentMaxWidth + 2 * HorizontalPadding(TierFor(width)))
            .Padding(HorizontalPadding(TierFor(width)), 24, HorizontalPadding(TierFor(width)), 0)
            .HAlign(HorizontalAlignment.Center)
            .AutomationId("DashboardsGallery");
        return Grid([GridSize.Star()], [GridSize.Star()],
                DashboardsStates.VerticalScroll(column)
                    .Margin(0, props.TopInset, 0, 0)
                    .AutomationName(LocalizationHelper.GetString("Dashboards_GalleryLabel"))
                    .AutomationId("DashboardsGalleryScroll"))
            .OnSizeChanged((_, args) =>
            {
                if (props.ViewportWidth <= 0 && Snap(args.NewSize.Width) != Snap(measuredWidth))
                    setWidth(args.NewSize.Width);
            });
    }

    internal static Element Hero(DashboardsPageState state, string agentId, DashboardGallerySnapshot snapshot, Action refresh, double width, bool highContrast, bool reducedMotion)
    {
        var narrow = TierFor(width) == PageWidthTier.Narrow;
        var headline = LocalizationHelper.GetString("Dashboards_Title");
        var detail = TextBlock(LocalizationHelper.Format("Dashboards_ReadOnlyAgentFormat", state.AgentName(agentId))).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.WrapWholeWords)
                .AutomationId("DashboardsReaderDetail");

        var more = new MenuFlyout();
        if (state.Connected && state.OpenWebDashboards is { } browser)
        {
            var item = new MenuFlyoutItem { Text = LocalizationHelper.GetString("Dashboards_OpenWebGallery") };
            AutomationProperties.SetAutomationId(item, "DashboardsOpenWebMenu");
            item.Click += (_, _) => browser();
            more.Items.Add(item);
        }
        var actions = HStack(GapInner,
                narrow && more.Items.Count > 0
                    ? DashboardsStates.IconButton(DashboardsGlyphs.More, LocalizationHelper.GetString("Dashboards_MoreActions"),
                            () => { }, "DashboardsGalleryMore")
                        .Set(button => button.Flyout = more)
                    : state.Connected && state.OpenWebDashboards is { } openWeb
                    ? Button(
                            HStack(8,
                                Icon(FontIcon(DashboardsGlyphs.OpenInBrowser)).VAlign(VerticalAlignment.Center),
                                TextBlock(LocalizationHelper.GetString("Dashboards_OpenWebGallery"))),
                            openWeb)
                        .VAlign(VerticalAlignment.Top)
                        .ToolTip(LocalizationHelper.GetString("Dashboards_OpenWebGallery_GalleryHint"))
                        .AutomationName(LocalizationHelper.GetString("Dashboards_OpenWebGallery"))
                        .AutomationId("DashboardsOpenWeb")
                    : Empty(),
                state.Connected
                    ? DashboardsStates.IconButton(DashboardsGlyphs.Refresh, LocalizationHelper.GetString("Dashboards_Refresh"), refresh, "DashboardsRefresh")
                    : Empty())
            .VAlign(VerticalAlignment.Top)
            .HAlign(narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right)
            .AutomationId("DashboardsMastActions");

        return PageHero.Header("DashboardsHero", DashboardsGlyphs.Dashboard, headline, "DashboardsTitle",
                AgentPicker(state, snapshot, TierFor(width) != PageWidthTier.Wide), detail, actions, narrow, reducedMotion, highContrast,
                pageTag: DashboardsRoute.Tag, titleIsTabStop: true)
            .Margin(0, 0, 0, 20);
    }

    private static Element AgentPicker(DashboardsPageState state, DashboardGallerySnapshot snapshot, bool narrow)
    {
        var agentNames = state.AgentIds.IsEmpty
            ? new[] { state.AgentName(state.AgentId) }
            : state.AgentIds.Select(state.AgentName).ToArray();
        var index = Math.Max(0, state.AgentIds.IndexOf(state.AgentId));
        var picker = HStack(8,
            TextBlock(LocalizationHelper.GetString("Dashboards_BoardsFrom"))
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center),
            ComboBox(agentNames, index, selected =>
            {
                if (selected >= 0 && selected < state.AgentIds.Length)
                    state.SelectAgent(state.AgentIds[selected]);
            })
            .MinWidth(140)
            .MaxWidth(240)
            .IsEnabled(state.Connected && state.AgentIds.Length > 1)
            .AutomationName(LocalizationHelper.GetString("Dashboards_BoardsFrom"))
            .AutomationId("DashboardsAgentPicker"));
        Element latest = Empty();
        if (DashboardsPresentation.LatestBoard(snapshot.Rows) is { } row)
        {
            var title = DashboardsPresentation.RowTitle(row);
            var updated = DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow);
            var format = LocalizationHelper.GetString("Dashboards_LatestBoardFormat");
            var titleOffset = format.IndexOf("{0}", StringComparison.Ordinal);
            latest = TextBlock(LocalizationHelper.Format("Dashboards_LatestBoardFormat", title, updated))
                .FontSize(14).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Set(text =>
                {
                    if (titleOffset < 0)
                        return;
                    text.Inlines.Clear();
                    text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = format[..titleOffset].Replace("{1}", updated, StringComparison.Ordinal)
                    });
                    var emphasized = (Microsoft.UI.Xaml.Documents.Run)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                        """<Run xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource TextFillColorPrimaryBrush}" FontWeight="SemiBold"/>""");
                    emphasized.Text = title;
                    text.Inlines.Add(emphasized);
                    text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = format[(titleOffset + 3)..].Replace("{1}", updated, StringComparison.Ordinal)
                    });
                })
                .AutomationId("DashboardsLatestBoard");
        }
        return Grid(narrow ? [GridSize.Star()] : [GridSize.Auto, GridSize.Star()],
            narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
            picker.Grid(row: 0, column: 0),
            latest.VAlign(VerticalAlignment.Center)
                .Margin(narrow ? 0 : GapGroup, narrow ? GapInner : 0, 0, 0)
                .Grid(row: narrow ? 1 : 0, column: narrow ? 0 : 1))
            .AutomationId("DashboardsScopeLine");
    }

    private static Element Gallery(
        DashboardGalleryProps props,
        DashboardGallerySnapshot snapshot,
        string query,
        Action<string> setQuery,
        DashboardGallerySort sort,
        Action<DashboardGallerySort> setSort,
        bool reducedMotion,
        int shown,
        Action<int> setShown,
        double width)
    {
        var fallbackAgentId = props.AgentId;
        var filters = new DashboardGalleryFilters(query, sort);
        var visible = DashboardGalleryPolicy.Apply(snapshot.Rows, filters, fallbackAgentId, DashboardsPresentation.RowTitle);

        var sortLabels = new[]
        {
            LocalizationHelper.GetString("Dashboards_SortUpdated"),
            LocalizationHelper.GetString("Dashboards_SortTitle"),
        };

        var toolbar = Grid(
            TierFor(width) == PageWidthTier.Wide ? [GridSize.Auto, GridSize.Star()] : [GridSize.Star()],
            TierFor(width) == PageWidthTier.Wide ? [GridSize.Auto] : [GridSize.Auto, GridSize.Auto],
            TextBox(query, setQuery, LocalizationHelper.GetString("Dashboards_SearchPlaceholder"))
                .SearchInput()
                .AutomationName(LocalizationHelper.GetString("Dashboards_SearchLabel"))
                .AutomationId("DashboardsSearch")
                .HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Bottom)
                .MinWidth(240).MaxWidth(TierFor(width) == PageWidthTier.Wide ? 360 : double.PositiveInfinity)
                .HAlign(TierFor(width) == PageWidthTier.Wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch)
                .Margin(0, TierFor(width) == PageWidthTier.Wide ? 0 : GapGroup, 0, 0)
                .Grid(row: TierFor(width) == PageWidthTier.Wide ? 0 : 1, column: TierFor(width) == PageWidthTier.Wide ? 1 : 0),
            ComboBox(sortLabels, sort == DashboardGallerySort.Title ? 1 : 0, selected =>
                    setSort(selected == 1 ? DashboardGallerySort.Title : DashboardGallerySort.Updated))
                .AutomationName(LocalizationHelper.GetString("Dashboards_SortLabel"))
                .MinWidth(160)
                .AutomationId("DashboardsSort")
                .Grid(column: 0))
            .VAlign(VerticalAlignment.Bottom);

        Element results = visible.IsEmpty
            ? DashboardsStates.Status(
                "DashboardsNoResults",
                DashboardsGlyphs.Search,
                LocalizationHelper.GetString("Dashboards_NoResults_Title"),
                LocalizationHelper.GetString("Dashboards_NoResults_Detail"),
                hue: DashboardHues.Teal)
            : Cards(props, visible.Take(shown).ToImmutableArray(), fallbackAgentId, reducedMotion, width);

        var sections = new List<Element>();
        var fresh = DashboardGalleryPolicy.FreshToday(snapshot.Rows, DateTimeOffset.Now);
        if (!fresh.IsEmpty && string.IsNullOrEmpty(query))
        {
            sections.Add(TextBlock(LocalizationHelper.GetString("Dashboards_FreshToday")).FontSize(22).SemiBold());
            var now = DateTimeOffset.UtcNow;
            Element feature = Border(Card(props, fresh[0], fallbackAgentId, now, reducedMotion, "Fresh", featured: true))
                .Margin(0).HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Stretch);
            var sides = fresh.Skip(1).Select((row, index) =>
                Border(Card(props, row, fallbackAgentId, now, reducedMotion, "Fresh", compact: true))
                    .Margin(0).HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Stretch)
                    .Grid(row: TierFor(width) == PageWidthTier.Medium ? 0 : index,
                        column: TierFor(width) == PageWidthTier.Medium ? index : 0)
                    .WithKey(row.AgentId + "\0" + row.Key)).ToArray();
            var sideGrid = Grid(
                TierFor(width) == PageWidthTier.Medium ? Enumerable.Repeat(GridSize.Star(), sides.Length).ToArray() : [GridSize.Star()],
                TierFor(width) == PageWidthTier.Medium ? [GridSize.Auto] : Enumerable.Repeat(GridSize.Star(), sides.Length).ToArray(), sides)
                .Set(grid => { grid.ColumnSpacing = GapGroup; grid.RowSpacing = GapStack; })
                .Margin(0).HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Stretch)
                .AutomationId("DashboardsFreshSides");
            sections.Add((fresh.Length == 1
                ? feature
                : TierFor(width) == PageWidthTier.Wide
                    ? Grid([GridSize.Star(3), GridSize.Star(2)], [GridSize.Auto],
                    feature.Grid(column: 0),
                    sideGrid.Grid(column: 1))
                        .Set(grid => { grid.ColumnSpacing = GapStack; grid.RowSpacing = GapStack; })
                    : Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                        feature.Grid(row: 0), sideGrid.Grid(row: 1))
                        .Set(grid => { grid.ColumnSpacing = GapStack; grid.RowSpacing = GapStack; }))
                .AutomationId("DashboardsFreshLayout"));
        }
        sections.Add(HStack(12,
            TextBlock(LocalizationHelper.Format("Dashboards_AgentBoards", props.State.AgentName(props.AgentId))).FontSize(24).SemiBold(),
            Border(TextBlock(visible.Length.ToString(System.Globalization.CultureInfo.CurrentCulture)).FontSize(14).LiveRegion()
                .AutomationName(LocalizationHelper.Format("Dashboards_ResultCountFormat", visible.Length))
                .AutomationId("DashboardsResultCount"))
                .Padding(10, 3).CornerRadius(12).VAlign(VerticalAlignment.Center)
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))));
        if (snapshot.Rows.Length > 5 || !string.IsNullOrWhiteSpace(query))
            sections.Add(toolbar);
        sections.Add(results);
        if (visible.Length > shown)
            sections.Add(Button(LocalizationHelper.GetString("Dashboards_ShowMore"), () => setShown(shown + 24))
                .AutomationName(LocalizationHelper.GetString("Dashboards_ShowMore"))
                .HAlign(HorizontalAlignment.Center).AutomationId("DashboardsShowMore"));
        return VStack(GapStack, sections.ToArray());
    }

    private static Element Cards(DashboardGalleryProps props, ImmutableArray<DashboardSessionRow> rows, string fallbackAgentId, bool reducedMotion, double width)
    {
        var now = DateTimeOffset.UtcNow;
        var columns = TileColumns(ContentWidth(width), DashboardsPresentation.BoardTileMinWidth);
        return Grid(Enumerable.Repeat(GridSize.Star(), columns).ToArray(),
                Enumerable.Repeat(GridSize.Auto, (rows.Length + columns - 1) / columns).ToArray(),
                // The layout cell owns Grid placement; entrance motion stays inside it.
                rows.Select((row, index) => Border(Card(props, row, fallbackAgentId, now, reducedMotion, "", compact: rows.Length > 9))
                    .Grid(row: index / columns, column: index % columns)
                    .WithKey(row.AgentId + "\0" + row.Key)).ToArray())
            .Set(grid => { grid.ColumnSpacing = GapGroup; grid.RowSpacing = GapGroup; })
            .Margin(0, 0, 0, 24)
            .AutomationName(LocalizationHelper.GetString("Dashboards_GalleryLabel"))
            .AutomationId("DashboardsCards");
    }

    private static Element Card(DashboardGalleryProps props, DashboardSessionRow row, string fallbackAgentId, DateTimeOffset now, bool reducedMotion, string prefix,
        bool featured = false, bool compact = false)
    {
        var title = DashboardsPresentation.RowTitle(row);
        var author = DashboardGalleryPolicy.ResolveAuthor(row, fallbackAgentId);
        var updated = DashboardsPresentation.UpdatedText(row, now);
        var hue = DashboardHues.For(row.AgentId ?? fallbackAgentId);

        var heading = TextBlock(title).FontSize(featured ? 24 : 18).SemiBold()
            .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2)
            .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(title);
        var summaryText = DashboardsPresentation.Summary(row);
        Element summary = TextBlock(summaryText).FontSize(13)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.WrapWholeWords)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(featured ? 3 : 2).MaxWidth(440).HAlign(HorizontalAlignment.Left)
                .ToolTip(summaryText)
                .VAlign(VerticalAlignment.Top);

        var cover = Border(Border(TextBlock(DashboardsPresentation.TitleInitials(title)).FontSize(20).SemiBold()
                    .Foreground(Theme.Ref(DashboardHues.Strong(hue))).Center().AccessibilityHidden())
                .Size(48, 48).CornerRadius(8).Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                .HAlign(HorizontalAlignment.Left).Margin(16, compact ? 8 : 12))
            .Height(compact ? 64 : 80)
            .Background(Theme.Ref(DashboardHues.Subtle(hue))).CornerRadius(12, 12, 0, 0);
        var content = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            (featured ? Empty() : cover).Grid(row: 0),
            VStack(8, heading, summary).Margin(16, 16, 16, 12).Grid(row: 1),
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto, GridSize.Auto],
                TextBlock(author.Label + " · " + updated).FontSize(12)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(author.Label + " · " + updated).Grid(row: 0),
                (row.IsLive ? DashboardsArt.LiveBadge(reducedMotion) : Empty()).Margin(0, 4, 0, 0).Grid(row: 1),
                TextBlock(LocalizationHelper.GetString("Dashboards_OpenAction")).FontSize(12).SemiBold()
                    .Foreground(Theme.Ref("AccentTextFillColorPrimaryBrush")).VAlign(VerticalAlignment.Center)
                    .Margin(8, 0, 0, 0).Grid(row: 0, column: 1, rowSpan: 2))
                .Margin(16, 0, 16, 16).Grid(row: 2));
        var label = string.Join(", ", new[]
        {
            LocalizationHelper.Format("Dashboards_OpenBoardFor", title),
            row.IsLive ? LocalizationHelper.GetString("Dashboards_Live") : null,
            LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label),
            updated,
        }.Where(part => !string.IsNullOrEmpty(part)));

        // One stock button paints the whole card. Noninteractive content and the
        // independent chat action occupy auto-sized rows above its surface.
        var cardId = "DashboardCard" + prefix + "_" + row.Key;
        var button = Button(Empty(), () => props.State.Open(new BoardSessionTarget(row.Key, row.AgentId), title, row, cardId))
            .CornerRadius(12)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .AutomationName(label)
            .HelpText(summaryText)
            .ToolTip(title + "\n" + summaryText + "\n" + author.Label + " · " + updated)
            .AutomationId(cardId);

        // The chat action is a sibling, never an interactive control inside the card button.
        Element card = Grid([GridSize.Star()], [GridSize.Star(), GridSize.Auto],
                button.Grid(row: 0, rowSpan: 2),
                content.IsHitTestVisible(false).AccessibilityHidden().Margin(1).Grid(row: 0),
                props.State.OpenChat is { } chat
                    ? Button(HStack(8, Icon(FontIcon(DashboardsGlyphs.Chat)).VAlign(VerticalAlignment.Center),
                            TextBlock(LocalizationHelper.GetString("Dashboards_OpenChat"))), () => chat(row.Key))
                        .AutomationName(LocalizationHelper.Format("Dashboards_OpenChatFor", title))
                        .AutomationId("DashboardCardChat" + prefix + "_" + row.Key)
                        .HAlign(HorizontalAlignment.Right).Margin(16, 0, 16, 12).Grid(row: 1)
                    : Empty())
            .VAlign(featured || compact ? VerticalAlignment.Stretch : VerticalAlignment.Top)
            .AutomationId(cardId + "Layout");
        return DashboardsArt.Enter(card, reducedMotion)
            .Margin(0).HAlign(HorizontalAlignment.Stretch)
            .VAlign(featured || compact ? VerticalAlignment.Stretch : VerticalAlignment.Top);
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element)
                yield return element;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static Element Skeleton(double width)
    {
        var columns = TileColumns(ContentWidth(width), DashboardsPresentation.BoardTileMinWidth);
        var placeholders = Enumerable.Range(0, 6)
            .Select(index =>
            {
                Element tile = Border(Empty())
                    .Height(CardHeight)
                    .CornerRadius(12)
                    .Background(Theme.Ref(DashboardHues.Wash(index)))
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(1);
                return tile.Grid(row: index / columns, column: index % columns).WithKey("skeleton" + index);
            })
            .ToArray();
        return VStack(12,
                TextBlock(LocalizationHelper.GetString("Dashboards_Loading")).FontSize(14)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).LiveRegion(),
                Grid(Enumerable.Repeat(GridSize.Star(), columns).ToArray(),
                    Enumerable.Repeat(GridSize.Auto, (6 + columns - 1) / columns).ToArray(), placeholders)
                    .Set(grid =>
                    {
                        grid.ColumnSpacing = GapGroup;
                        grid.RowSpacing = GapGroup;
                    }))
            .AutomationId("DashboardsLoading");
    }
}
