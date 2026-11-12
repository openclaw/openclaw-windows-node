using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed partial class DashboardWidgetCellTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("Quarterly operating review", "QO")]
    [InlineData("  board   notes ", "BN")]
    [InlineData("One", "O")]
    [InlineData("", "")]
    [InlineData("预算 报告", "预报")]
    public void IdentityTile_UsesOnlyTheFirstTwoTitleWords(string title, string expected) =>
        Assert.Equal(expected, DashboardsPresentation.TitleInitials(title));

    [Fact]
    public void LatestBoard_UsesOnlyValidReportedUpdateTimesIndependentOfGalleryOrder()
    {
        var rows = new long?[] { null, long.MaxValue, 3000, 1000, -1, 3000, 0 }
            .Select((timestamp, index) => new DashboardSessionRow
            {
                Session = new SessionInfo { Key = "board-" + index, AgentId = "main" },
                UpdatedAtMs = timestamp,
            }).ToImmutableArray();
        Assert.Equal("board-2", DashboardsPresentation.LatestBoard(rows.Reverse().ToImmutableArray())?.Key);
        Assert.Null(DashboardsPresentation.LatestBoard(rows.Where(row => row.UpdatedAtMs is null or <= 0 or long.MaxValue).ToImmutableArray()));
        Assert.Null(DashboardsPresentation.LatestBoard([]));
        Assert.Null(DashboardsPresentation.LatestBoard(default));
    }

    public static IEnumerable<object[]> HeroStates()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var width in new[] { 1100d, 820d, 600d })
        foreach (var surface in new[] { "gallery", "board", "disconnected", "empty" })
            yield return [theme, width, surface];
    }

    [Theory]
    [MemberData(nameof(HeroStates))]
    public async Task HeaderHero_RecordsResponsiveReaderStates(string theme, double width, string surface)
    {
        const string key = "agent:main:hero";
        var row = new DashboardSessionRow
        {
            Session = new SessionInfo { Key = key, AgentId = "main", DisplayName = "Quarterly operating review" },
            CreatedActor = new DashboardActor("fixture-author", "Fixture Author"),
            UpdatedAtMs = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds(),
            LastMessagePreview = "A saved report with the daily figures.",
            Status = "running"
        };
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [new BoardTab("main", "Report", 0, "right")],
            [Plugin("hero-report", "session:report", """{"blocks":[{"type":"text","text":"Daily figures: the saved report remains readable."}]}""") with { TabId = "main" }]))
        {
            Gallery = new DashboardSessionPage(surface == "empty" ? [] : [row], false, null, surface == "empty" ? 0 : 1),
            Kinds = [new PluginWidgetKind("session", "session:report", "Report")]
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenConnection = () => { },
            OpenWebDashboards = () => { },
            OpenChat = _ => { }
        };
        state.SetAgents(["main", "research"], "main");
        state.AgentNames = new Dictionary<string, string> { ["main"] = "Atlas", ["research"] = "Research" };
        state.SetConnection(client, surface != "disconnected");
        if (surface == "board")
            state.Open(new BoardSessionTarget(key, "main"), row.Session.DisplayName, row);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                AssertNoUnmarkedTextClipping(host);
                Assert.Equal(surface == "board" ? "Quarterly operating review" : "Dashboards",
                    ById<TextBlock>(host, "DashboardsTitle").Text);
                if (surface is "gallery" or "empty")
                {
                    Assert.NotNull(ById<Button>(host, width < 720 ? "DashboardsGalleryMore" : "DashboardsOpenWeb"));
                    Assert.NotNull(ById<Button>(host, "DashboardsRefresh"));
                    Assert.Equal("Boards from", AutomationProperties.GetName(ById<ComboBox>(host, "DashboardsAgentPicker")));
                    Assert.Equal("Boards are read-only here; ask Atlas to change one.",
                        ById<TextBlock>(host, "DashboardsReaderDetail").Text);
                    var picker = Bounds(ById<ComboBox>(host, "DashboardsAgentPicker"), host);
                    var detail = Bounds(ById<TextBlock>(host, "DashboardsReaderDetail"), host);
                    Assert.True(picker.Bottom <= detail.Top, "Agent selection belongs inside the answer line, above the reader guidance.");
                    Assert.Equal(surface == "gallery", FindDescendants<TextBlock>(host)
                        .Any(text => AutomationProperties.GetAutomationId(text) == "DashboardsLatestBoard"));
                    Assert.Equal(0, client.BoardLoads);
                }
                if (surface == "board")
                {
                    foreach (var actionId in new[] { "DashboardOpenChat", "DashboardRefresh", "DashboardMoreActions" })
                        Assert.NotNull(ById<Button>(host, actionId));
                    Assert.Equal(1, client.BoardLoads);
                    Assert.Contains(FindDescendants<TextBlock>(host), text =>
                        text.Text == "Daily figures: the saved report remains readable.");
                }
                if (surface == "disconnected")
                {
                    var info = ById<InfoBar>(host, "DashboardsDisconnectedInfo");
                    Assert.Equal(InfoBarSeverity.Error, info.Severity);
                    Assert.False(info.IsClosable);
                    Assert.Equal("DashboardsOpenConnection",
                        AutomationProperties.GetAutomationId(Assert.IsType<Button>(info.ActionButton)));
                    Assert.False(ById<ComboBox>(host, "DashboardsAgentPicker").IsEnabled);
                    Assert.Equal(0, client.BoardLoads);
                }
            },
            ready: host => FindDescendants<FrameworkElement>(host).Any(element =>
                    element.ActualHeight > 0 &&
                    AutomationProperties.GetAutomationId(element) == (surface switch
                    {
                        "gallery" => "DashboardsCards",
                        "board" => "BoardWidget_hero-report",
                        "empty" => "DashboardsEmpty",
                        _ => "DashboardsDisconnectedInfo"
                    })) &&
                FindDescendants<TextBlock>(host).Any(element => element.ActualHeight > 0 &&
                    AutomationProperties.GetAutomationId(element) == "DashboardsTitle"),
            theme: theme, width: width, onViewportChanged: state.SetViewportWidth,
            capture: $"hero-{width}-{surface}");
    }

    [Fact]
    public async Task DashboardContentHost_IsReachableInTheAutomationPeerTree()
    {
        ReactorHostControl? host = null;
        Expander? root = null;
        OpenClawTray.Pages.DashboardsPage? page = null;
        var oldTheme = ElementTheme.Default;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                EnsureDashboardResources();
                oldTheme = ui.Container.RequestedTheme;
                ui.Container.RequestedTheme = ElementTheme.Light;
                var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
                {
                    Gallery = new DashboardSessionPage([], false, null, 0),
                };
                var state = new DashboardsPageState
                {
                    Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
                    Schedule = (_, _) => new NoopLease(),
                };
                state.SetConnection(client, true);
                host = new ReactorHostControl();
                AutomationProperties.SetAutomationId(host, "DashboardsContentHost");
                AutomationProperties.SetName(host, LocalizationHelper.GetString("Dashboards_Title"));
                host.Mount(_ => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)));
                page = new OpenClawTray.Pages.DashboardsPage();
                Assert.IsType<Border>(page.FindName("ReactorTarget")).Child = host;
                root = new Expander { Header = "Native dashboard peer root", IsExpanded = true, Content = page, Width = 900, Height = 760 };
                ui.Container.Children.Add(root);
            });
            for (var turn = 0; turn < 4; turn++)
                await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(root!);
                Assert.NotNull(peer);
                var peers = WalkPeers(peer!).ToArray();
                var diagnostic = string.Join("\n", peers.Select(item =>
                    $"{item.GetClassName()} | {item.GetAutomationId()} | control={item.IsControlElement()}"));
                var matches = peers.Where(item => item.GetAutomationId() == "DashboardsContentHost").ToArray();
                Assert.True(matches.Length == 1, "Host must be reachable exactly once via GetChildren:\n" + diagnostic);
                var found = matches[0];
                Assert.True(found.IsControlElement(), diagnostic);
                Assert.InRange(Math.Abs(page!.ActualWidth - host!.ActualWidth), 0, 1);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (page is not null)
                    Assert.IsType<Border>(page.FindName("ReactorTarget")).Child = null;
                if (root is not null)
                {
                    root.Content = null;
                    ui.Container.Children.Remove(root);
                }
                host?.Dispose();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => ui.Container.RequestedTheme = oldTheme);
        }
    }

    private static IEnumerable<AutomationPeer> WalkPeers(AutomationPeer root)
    {
        var pending = new Stack<AutomationPeer>();
        var visited = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var peer))
        {
            if (!visited.Add(peer))
                continue;
            yield return peer;
            foreach (var child in peer.GetChildren() ?? [])
                pending.Push(child);
        }
    }

    [Theory]
    [InlineData(4, 2, false)]
    [InlineData(5, 1, false)]
    [InlineData(5, 2, true)]
    [InlineData(6, 3, true)]
    public async Task FreshToday_RequiresFiveBoardsAndTwoUpdatesInTheCurrentLocalDay(int count, int todayCount, bool expected)
    {
        var today = DateTimeOffset.Now;
        var rows = Enumerable.Range(0, count).Select(index => new DashboardSessionRow
        {
            Session = new SessionInfo { Key = "agent:main:fresh:" + index, AgentId = "main", DisplayName = "Board " + index },
            UpdatedAtMs = (index < todayCount ? today : today.AddDays(-1)).ToUnixTimeMilliseconds(),
        }).ToImmutableArray();
        var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
        {
            Gallery = new DashboardSessionPage(rows, false, null, count),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
            new DashboardGalleryProps(state, client, "main", 1)), host =>
        {
            Assert.Equal(expected, FindDescendants<FrameworkElement>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "DashboardsFreshLayout"));
            var card = ById<Button>(host, "DashboardCard_agent:main:fresh:0");
            Assert.StartsWith("Open board: Board 0", AutomationProperties.GetName(card));
            Assert.Equal("No summary available", AutomationProperties.GetHelpText(card));
            Assert.Contains(FindDescendants<TextBlock>(ById<Grid>(host, AutomationProperties.GetAutomationId(card) + "Layout")),
                text => text.Text == "B0");
            Assert.Equal(0, client.BoardLoads);
        }, ready: host => FindDescendants<Grid>(host).Any(element =>
            AutomationProperties.GetAutomationId(element) == "DashboardsCards"), width: 1400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningFromBoard_FocusesExactOpeningCardOrNamedHeadingWhenRemoved(bool removeBoard)
    {
        var rows = Enumerable.Range(0, 30).Select(index => new DashboardSessionRow
        {
            Session = new SessionInfo { Key = "agent:main:return:" + index, AgentId = "main", DisplayName = "Board " + index },
            UpdatedAtMs = DateTimeOffset.Now.AddDays(-1).ToUnixTimeMilliseconds(),
        }).ToImmutableArray();
        var client = new SnapshotBoardClient(new BoardSnapshot(rows[29].Key, 1, [], []))
        {
            Gallery = new DashboardSessionPage(rows, false, null, 30),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            GalleryQuery = "Board",
            GallerySort = DashboardGallerySort.Title,
        };
        state.SetConnection(client, true);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host => Invoke(ById<Button>(host, "DashboardsShowMore")),
            ready: host => FindDescendants<Button>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "DashboardsShowMore"),
            width: 1400, afterMounted: async host =>
            {
                const string cardId = "DashboardCard_agent:main:return:29";
                await WaitForMountedStateAsync(host, root => ById<Grid>(root, "DashboardsCards").Children.Count == 30);
                await ui.RunOnUIAsync(() =>
                {
                    // The shared hero must retain the gallery heading as the fallback focus destination.
                    Assert.True(ById<TextBlock>(host, "DashboardsTitle").IsTabStop);
                    var card = ById<Button>(host, cardId);
                    Assert.True(card.Focus(FocusState.Keyboard));
                    Invoke(card);
                });
                await WaitForMountedStateAsync(host, root => FindDescendants<FrameworkElement>(root).Any(element =>
                    AutomationProperties.GetAutomationId(element) == "DashboardEmpty"));
                await ui.RunOnUIAsync(() =>
                {
                    if (removeBoard)
                        client.Gallery = new DashboardSessionPage(rows.RemoveAt(29), false, null, 29);
                    state.Open(null);
                });
                var focusId = removeBoard ? "DashboardsTitle" : cardId;
                var focusDiagnostics = "Gallery focus has not been observed.";
                await WaitForMountedStateAsync(host, root =>
                {
                    var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
                    var heading = FindDescendants<TextBlock>(root).FirstOrDefault(text =>
                        AutomationProperties.GetAutomationId(text) == "DashboardsTitle");
                    var actualId = focused is null ? "none" : AutomationProperties.GetAutomationId(focused);
                    focusDiagnostics = $"Expected focus {focusId}; actual {actualId}; heading present={heading is not null}, " +
                        $"size={heading?.ActualWidth}x{heading?.ActualHeight}, tabStop={heading?.IsTabStop}; returnPending={state.ReturnFocusPending}.";
                    return actualId == focusId;
                }, () => focusDiagnostics);
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal("Board", ById<TextBox>(host, "DashboardsSearch").Text);
                    Assert.Equal(1, ById<ComboBox>(host, "DashboardsSort").SelectedIndex);
                    Assert.Equal(removeBoard ? 29 : 30, ById<Grid>(host, "DashboardsCards").Children.Count);
                    Assert.False(state.ReturnFocusPending);
                });
            });
    }

    [Fact]
    public async Task OverflowTab_SelectsItsNamedPanelRestoresTabFocusAndOffersSectionChat()
    {
        const string session = "agent:main:tabs";
        var client = new SnapshotBoardClient(new BoardSnapshot(session, 1,
            [.. Enumerable.Range(0, 8).Select(index => new BoardTab("section-" + index, "Section " + index, index, "right"))], []));
        var chats = new List<string>();
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenChat = chats.Add,
        };
        await WithMountedAsync(() => Component<ReactorBoardView, BoardViewProps>(
            new BoardViewProps(state, client, new BoardSessionTarget(session, "main"), 1)),
            _ => { }, ready: host => FindDescendants<DropDownButton>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "DashboardMoreTabs"),
            width: 1400, afterMounted: async host =>
            {
                await ui.RunOnUIAsync(async () =>
                {
                    var button = ById<DropDownButton>(host, "DashboardMoreTabs");
                    var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                    var item = menu.Items.OfType<MenuFlyoutItem>().Single(candidate =>
                        AutomationProperties.GetAutomationId(candidate) == "DashboardOverflowTab_section-7");
                    var opened = false;
                    var closed = false;
                    void Opened(object? sender, object args) => opened = true;
                    void Closed(object? sender, object args) => closed = true;
                    menu.Opened += Opened;
                    menu.Closed += Closed;
                    try
                    {
                        menu.ShowAt(button);
                        // ShowAt schedules native popup realization; a user cannot select its item before Opened.
                        await WaitForRenderedConditionAsync(() => opened && menu.IsOpen && item.IsLoaded && item.ActualWidth > 0,
                            "realized overflow tab menu", () => $"opened={opened}, isOpen={menu.IsOpen}, itemLoaded={item.IsLoaded}, width={item.ActualWidth}");
                        Invoke(item);
                        menu.Hide();
                        await WaitForRenderedConditionAsync(() => closed && !menu.IsOpen,
                            "closed overflow tab menu", () => $"closed={closed}, isOpen={menu.IsOpen}");
                        await WaitForRenderedConditionAsync(() =>
                            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(host.XamlRoot) is DependencyObject focused &&
                            AutomationProperties.GetAutomationId(focused) == "DashboardTab_section-7",
                            "selected overflow tab focus", () =>
                            {
                                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(host.XamlRoot) as DependencyObject;
                                var selected = ById<SelectorBar>(host, "DashboardTabs").SelectedItem;
                                return $"focus={(focused is null ? "none" : AutomationProperties.GetAutomationId(focused))}, " +
                                    $"selected={selected?.Text}, loaded={selected?.IsLoaded}, size={selected?.ActualWidth}x{selected?.ActualHeight}, menuOpen={menu.IsOpen}";
                            });
                    }
                    finally
                    {
                        menu.Opened -= Opened;
                        menu.Closed -= Closed;
                        if (menu.IsOpen) menu.Hide();
                    }
                });
                await ui.RunOnUIAsync(() =>
                {
                    var tab = ById<SelectorBar>(host, "DashboardTabs").SelectedItem;
                    Assert.Equal("Section 7", AutomationProperties.GetName(tab));
                    var panel = Assert.Single(AutomationProperties.GetControlledPeers(tab));
                    Assert.Equal("Section 7", AutomationProperties.GetName(panel));
                    Assert.Equal("DashboardTabGrid_section-7", AutomationProperties.GetAutomationId(panel));
                    var chat = ById<Button>(host, "DashboardEmptyOpenChat");
                    Assert.Equal("Open chat: Section 7", AutomationProperties.GetName(chat));
                    Invoke(chat);
                    Assert.Equal(new[] { session }, chats);
                });
            });
    }

    [Theory]
    [InlineData("Light", 600)]
    [InlineData("Dark", 900)]
    public Task NativeReportCard_IsAnAccessibleGroupWithFullBoundsAndReachableChildren(string theme, double width) =>
        WithMountedAsync(() => Expander("Native report peer root",
            Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(Cell(
            Plugin("weekly-report", "session:report", """
            {"blocks":[{"type":"metrics","items":[{"label":"Tickets closed","value":"128"}]},
                       {"type":"links","items":[{"label":"Runbook","url":"https://example.com/runbook"}]}]}
            """) with { Title = "Weekly report" }, BoardWidgetBodyKind.Report) with { KindLabel = "Report" }))
                .Set(control => control.IsExpanded = true)
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .AutomationId("NativeReportPeerRoot"), _ => { }, theme: theme, width: width,
            afterMounted: host => ui.RunOnUIAsync(async () =>
        {
            // An explicit Reactor width does not enlarge the shared fixture HWND, which
            // normally lives offscreen at 1x1. Full peer bounds need a real, unclipped viewport.
            var window = ui.TestWindow;
            var appWindow = window.AppWindow;
            var originalPosition = appWindow.Position;
            var originalSize = appWindow.Size;
            var viewportChanged = false;
            Assert.Same(ui.Container, window.Content);
            OnboardingNativeProof.AssertProductDpi(window);
            try
            {
                OnboardingNativeProof.AssertOwned(window);
                // Size the client from the mounted scene, including shared-root padding.
                // Mandatory peer geometry does not depend on optional screenshot capture.
                ui.Container.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var viewportScale = host.XamlRoot.RasterizationScale;
                Assert.True(double.IsFinite(viewportScale) && viewportScale > 0);
                var desired = ui.Container.DesiredSize;
                Assert.True(double.IsFinite(desired.Width) && double.IsFinite(desired.Height) &&
                    desired.Width > 0 && desired.Height > 0);
                var clientSize = new Windows.Graphics.SizeInt32(
                    checked((int)Math.Ceiling(desired.Width * viewportScale)),
                    checked((int)Math.Ceiling(desired.Height * viewportScale)));
                viewportChanged = true;
                appWindow.ResizeClient(clientSize);
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                Assert.True(appWindow.Size.Width <= work.Width && appWindow.Size.Height <= work.Height,
                    "The native report proof viewport must fit the owned window's monitor work area.");
                appWindow.Move(new Windows.Graphics.PointInt32(work.X, work.Y));
                var geometry = "The expanded widget has not been observed.";
                await WaitForRenderedConditionAsync(() =>
                {
                    ui.Container.UpdateLayout();
                    var ancestor = FindDescendants<Expander>(host).SingleOrDefault(element =>
                        AutomationProperties.GetAutomationId(element) == "NativeReportPeerRoot");
                    var observedCard = FindDescendants<FrameworkElement>(host).SingleOrDefault(element =>
                        AutomationProperties.GetAutomationId(element) == "BoardWidget_weekly-report");
                    var ancestorPeer = ancestor is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(ancestor);
                    var groupPeer = ancestorPeer is null ? null : WalkPeers(ancestorPeer).SingleOrDefault(peer =>
                        peer.GetAutomationId() == "BoardWidget_weekly-report");
                    var rectangle = groupPeer?.GetBoundingRectangle() ?? default;
                    geometry = $"expanded={ancestor?.IsExpanded}, ancestorLoaded={ancestor?.IsLoaded}, cardLoaded={observedCard?.IsLoaded}, " +
                        $"cardSize={observedCard?.ActualWidth}x{observedCard?.ActualHeight}, groupPresent={groupPeer is not null}, peerBounds={rectangle}, " +
                        $"client={appWindow.ClientSize.Width}x{appWindow.ClientSize.Height}, root={ui.Container.ActualWidth}x{ui.Container.ActualHeight}";
                    return appWindow.ClientSize.Width == clientSize.Width &&
                        appWindow.ClientSize.Height == clientSize.Height &&
                        ui.Container.ActualWidth >= host.ActualWidth + ui.Container.Padding.Left + ui.Container.Padding.Right &&
                        ui.Container.ActualHeight >= host.ActualHeight + ui.Container.Padding.Top + ui.Container.Padding.Bottom &&
                        ancestor is { IsExpanded: true, IsLoaded: true } &&
                        observedCard is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } &&
                        rectangle.Width > 0 && rectangle.Height > 0 &&
                        Math.Abs(rectangle.Width - observedCard.ActualWidth * viewportScale) <= 1 &&
                        Math.Abs(rectangle.Height - observedCard.ActualHeight * viewportScale) <= 1;
                }, "expanded native widget group geometry", () => geometry);
                OnboardingNativeProof.AssertProductDpi(window);
                OnboardingNativeProof.AssertFullyVisible(host, ui.Container);
                Console.WriteLine($"report-peer viewport theme={theme}; requestedWidth={width}; {geometry}");
                // Start at a real ancestor peer: the plain Reactor host has no native peer of its own.
                var root = FrameworkElementAutomationPeer.CreatePeerForElement(ById<Expander>(host, "NativeReportPeerRoot"));
                Assert.NotNull(root);
                var group = Assert.Single(WalkPeers(root!), peer => peer.GetAutomationId() == "BoardWidget_weekly-report");
                Assert.Equal(AutomationControlType.Group, group.GetAutomationControlType());
                Assert.Equal("Dashboard widget: Weekly report, Report", group.GetName());
                Assert.True(group.IsControlElement());
                Assert.True(group.IsContentElement());
                Assert.False(group.IsKeyboardFocusable());
                Assert.Null(group.GetPattern(PatternInterface.Invoke));
                Assert.Null(group.GetPattern(PatternInterface.SelectionItem));
                var card = ById<FrameworkElement>(host, "BoardWidget_weekly-report");
                var bounds = group.GetBoundingRectangle();
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                var scale = card.XamlRoot.RasterizationScale;
                Assert.InRange(Math.Abs(bounds.Width - card.ActualWidth * scale), 0, 1);
                Assert.InRange(Math.Abs(bounds.Height - card.ActualHeight * scale), 0, 1);
                var children = WalkPeers(group).Skip(1).ToArray();
                Assert.Contains(children, peer => peer.GetName() == "Tickets closed");
                Assert.Contains(children, peer => peer.GetName() == "128");
                var link = Assert.Single(children, peer => peer.GetName() == "Runbook"
                    && peer.GetAutomationControlType() == AutomationControlType.Hyperlink);
                Assert.IsAssignableFrom<IInvokeProvider>(link.GetPattern(PatternInterface.Invoke));
            }
            finally
            {
                if (viewportChanged)
                {
                    appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                        originalPosition.X, originalPosition.Y, originalSize.Width, originalSize.Height));
                    Assert.Equal(originalPosition, appWindow.Position);
                    Assert.Equal(originalSize, appWindow.Size);
                    OnboardingNativeProof.AssertProductDpi(window);
                }
            }
        }));

    [Fact]
    public Task ReportTable_UntypedPayloadCellsStayLeftAlignedWithoutGuessingDatesOrCurrency() =>
        WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(Cell(
            Plugin("table", "session:report", """
            {"blocks":[{"type":"table","columns":["Prose","Date","Status","Count","Cost"],"rows":[["Review","2026-11-06","Ready","12","$4.50"]]}]}
            """), BoardWidgetBodyKind.Report)), host =>
        {
            for (var column = 0; column < 5; column++)
                Assert.Equal(TextAlignment.Left, ById<TextBlock>(host, $"BoardReport_table_0_Cell_0_{column}").TextAlignment);
        });

    [Fact]
    public Task ReportChart_ExactValuesAreAboveBarsAndZeroAndNegativeValuesRemainHonest() =>
        WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(Cell(
            Plugin("chart", "session:report", """
            {"blocks":[{"type":"chart","points":[{"label":"A","value":123.456789012345},{"label":"B","value":0},{"label":"C","value":-10}]}]}
            """), BoardWidgetBodyKind.Report)), host =>
        {
            var values = new[] { "123.456789012345", "0", "-10" };
            for (var index = 0; index < values.Length; index++)
            {
                var label = ById<TextBlock>(host, "BoardReport_chart_0_Value_" + index);
                var bar = ById<Border>(host, "BoardReport_chart_0_Bar_" + index);
                Assert.Equal(values[index], label.Text);
                Assert.True(Microsoft.UI.Xaml.Controls.Canvas.GetTop(label) + label.ActualHeight
                    <= Microsoft.UI.Xaml.Controls.Canvas.GetTop(bar));
            }
            Assert.True(ById<Border>(host, "BoardReport_chart_0_Bar_0").Height >= 110);
            Assert.Equal(1, ById<Border>(host, "BoardReport_chart_0_Bar_1").Height);
        });

    [Theory]
    [InlineData(728, "Light")]
    [InlineData(984, "Dark")]
    public async Task GalleryLivePills_FitTheirContentWithoutStretchingOrClipping(double width, string theme)
    {
        var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
        {
            Gallery = new DashboardSessionPage([.. Enumerable.Range(0, 6).Select(index => new DashboardSessionRow
            {
                Session = new SessionInfo { Key = "live-" + index, AgentId = "main", DisplayName = "Live board " + index },
                Status = "running",
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            })], false, null, 6),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
                new DashboardGalleryProps(state, client, "main", 1, width)), host =>
            {
                var pills = FindDescendants<Border>(host).Where(border =>
                    AutomationProperties.GetName(border) == LocalizationHelper.GetString("Dashboards_Live")).ToArray();
                Assert.NotEmpty(pills);
                foreach (var pill in pills)
                {
                    Assert.Equal(HorizontalAlignment.Left, pill.HorizontalAlignment);
                    var label = Assert.Single(FindDescendants<TextBlock>(pill));
                    var labelBounds = Bounds(label, pill);
                    Assert.True(pill.ActualWidth < width / 2, "Live pill stretched across the card.");
                    Assert.InRange(labelBounds.Left, 0, pill.ActualWidth);
                    Assert.True(labelBounds.Right <= pill.ActualWidth);
                    Assert.InRange(labelBounds.Top, 0, pill.ActualHeight);
                    Assert.True(labelBounds.Bottom <= pill.ActualHeight, "Live label is vertically clipped.");
                }
            }, ready: host => FindDescendants<Border>(host).Any(border =>
                AutomationProperties.GetName(border) == LocalizationHelper.GetString("Dashboards_Live")),
            width: width, theme: theme);
    }

    [Theory]
    [InlineData(728, "Light")]
    [InlineData(984, "Dark")]
    public async Task NativeReports_GrowSharedRowsAndKeepEveryTableCellInsideWithoutNestedScroll(double width, string theme)
    {
        var rows = Enumerable.Range(0, 20).Select(index => new[] { "Queue " + index, "A long unbroken value: abcdefghijklmnopqrstuvwxyz0123456789" }).ToArray();
        var tall = Plugin("tall", "session:report", JsonSerializer.Serialize(new
        {
            blocks = new[] { new { type = "table", title = "Top queues", columns = new[] { "Queue", "Description" }, rows } },
        })) with { SizeH = 4, Position = 0 };
        var shortReport = Plugin("short", "session:report",
            """{"blocks":[{"type":"text","title":"Service health","text":"All services operational."}]}""")
            with { Title = "Service health", SizeH = 4, Position = 1 };
        var key = "agent:main:report-sizing";
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [new BoardTab("main", "Overview", 0, "right")], [tall, shortReport]))
        {
            Kinds = [new PluginWidgetKind("session", "session:report", "Report")],
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetConnection(client, true);
        state.Open(new BoardSessionTarget(key, "main"), "Report sizing");
        await WithMountedAsync(() => Component<ReactorBoardView, BoardViewProps>(
                new BoardViewProps(state, client, new BoardSessionTarget(key, "main"), 1, width)), host =>
            {
                var tallCard = ById<FrameworkElement>(host, "BoardWidget_tall");
                var shortCard = ById<FrameworkElement>(host, "BoardWidget_short");
                Assert.InRange(Math.Abs(tallCard.ActualHeight - shortCard.ActualHeight), 0, 1);
                Assert.True(tallCard.ActualHeight > 4 * 56 + 3 * 12, "Report remains capped at its saved grid height.");
                Assert.Empty(FindDescendants<ScrollViewer>(tallCard));
                Assert.Empty(FindDescendants<ScrollViewer>(shortCard));
                var cells = FindDescendants<TextBlock>(tallCard).Where(text =>
                    AutomationProperties.GetAutomationId(text).StartsWith("BoardReport_tall_0_Cell_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(40, cells.Length);
                foreach (var cell in cells)
                {
                    var bounds = Bounds(cell, tallCard);
                    Assert.True(bounds.Left >= 0 && bounds.Right <= tallCard.ActualWidth + 0.5);
                    Assert.True(bounds.Top >= 0 && bounds.Bottom <= tallCard.ActualHeight + 0.5);
                }
                Assert.Single(FindDescendants<TextBlock>(shortCard), text => text.Text == "Service health");
                Assert.Single(FindDescendants<TextBlock>(tallCard), text => text.Text == "Top queues");
                var pageScroll = ById<ScrollViewer>(host, "DashboardPageScroll");
                Assert.True(pageScroll.ScrollableHeight > 0, "Overflow must remain accessible through page scroll.");
            }, ready: host => FindDescendants<TextBlock>(host).Any(text =>
                AutomationProperties.GetAutomationId(text) == "BoardReport_tall_0_Cell_19_1"),
            width: width, theme: theme);
    }

    [Fact]
    public async Task NativeReportRowGrowth_AlignsCardSurfacesWithoutEnlargingNeighborContent()
    {
        var report = Plugin("tall", "session:report", JsonSerializer.Serialize(new
        {
            blocks = new[] { new { type = "text", text = string.Join("\n", Enumerable.Repeat("Saved report line.", 40)) } },
        }));
        var website = Plugin("website", "session:website", """{"url":"https://status.example.com/"}""")
            with { Position = 1 };
        var key = "agent:main:mixed-sizing";
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [new BoardTab("main", "Overview", 0, "right")], [report, website]))
        {
            Kinds = [new PluginWidgetKind("session", "session:report", "Report"), new PluginWidgetKind("session", "session:website", "Website")],
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        await WithMountedAsync(() => Component<ReactorBoardView, BoardViewProps>(
                new BoardViewProps(state, client, new BoardSessionTarget(key, "main"), 1, 984)), host =>
            {
                var tallCard = ById<FrameworkElement>(host, "BoardWidget_tall");
                var boundedCard = ById<FrameworkElement>(host, "BoardWidget_website");
                Assert.InRange(Math.Abs(tallCard.ActualHeight - boundedCard.ActualHeight), 0, 1);
                var chrome = Assert.IsType<Border>(Assert.Single(Assert.IsAssignableFrom<Panel>(boundedCard).Children));
                var boundedContent = Assert.IsType<Grid>(chrome.Child);
                Assert.InRange(Math.Abs(boundedContent.ActualHeight - (4 * 56 + 3 * 12 - 2)), 0, 1);
                Assert.Equal(VerticalAlignment.Top, boundedContent.VerticalAlignment);
                Assert.InRange(Math.Abs(Bounds(tallCard, host).Top - Bounds(boundedCard, host).Top), 0, 1);
                Assert.Contains(FindDescendants<HyperlinkButton>(boundedCard), button =>
                    AutomationProperties.GetAutomationId(button) == "BoardWebsiteOpen_website");
            }, ready: host => FindDescendants<FrameworkElement>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "BoardWidget_website"), width: 984,
            capture: "mixed-report-website");
    }

    private async Task WaitForMountedStateAsync(ReactorHostControl host, Func<ReactorHostControl, bool> ready,
        Func<string>? failureMessage = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await ui.RunOnUIAsync(() => Task.FromResult(ready(host))))
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage?.Invoke() ?? "Mounted dashboard did not reach the expected state.");
            await ui.YieldToRenderAsync();
        }
    }

    [Theory]
    [InlineData(1400, 4)]
    [InlineData(1000, 3)]
    [InlineData(760, 2)]
    [InlineData(520, 1)]
    public async Task ResponsivePage_UsesMeasuredHostWidthWithoutOverlappingOrUnmarkedClipping(double width, int columns)
    {
        var rows = Enumerable.Range(0, 8).Select(index => new DashboardSessionRow
        {
            Session = new SessionInfo
            {
                Key = "agent:main:board:" + index,
                AgentId = "main",
                DisplayName = "Quarterly operating review with a deliberately long descriptive board title " + index,
            },
            CreatedActor = new DashboardActor("author", "Contributor with a deliberately long display name"),
            LastMessagePreview = "A useful summary with enough whole words to exercise the two-line preview at every supported width.",
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }).ToImmutableArray();
        var target = rows[0];
        using var report = JsonDocument.Parse("""{"blocks":[{"type":"text","text":"A readable report with whole words, not broken fragments, at every supported page width."}]}""");
        var board = new BoardSnapshot(target.Key, 1,
            [.. Enumerable.Range(0, 8).Select(index => new BoardTab("tab-" + index, "A long dashboard tab title " + index, index, "right"))],
            [.. Enumerable.Range(0, 2).Select(index => Plugin("report-" + index, "session:report", report.RootElement.GetRawText()) with
            {
                TabId = "tab-0", SizeW = 6, SizeH = 6, Position = index,
            })]);
        var client = new SnapshotBoardClient(board)
        {
            Gallery = new DashboardSessionPage(rows, false, null, rows.Length),
            Kinds = [new PluginWidgetKind("session", "session:report", "Report")],
        };
        var webOpens = 0;
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenChat = _ => { },
            OpenWebDashboards = () => webOpens++,
            CopySessionKey = _ => { },
        };
        state.SetConnection(client, true);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var cards = ById<Grid>(host, "DashboardsCards");
                Assert.InRange(Math.Abs(state.ViewportWidth - width), 0, 1);
                Assert.Equal(columns, cards.ColumnDefinitions.Count);
                AssertHeroColumn(host, "DashboardsHero", "DashboardsGallery", width);
                var tiles = cards.Children.OfType<FrameworkElement>().ToArray();
                Assert.Equal(8, tiles.Length);
                AssertNoOverlap(host, tiles);
                Assert.All(tiles, tile => Assert.True(tile.ActualWidth >= 240, $"Tile shrank to {tile.ActualWidth} DIP."));
                Assert.InRange(Math.Abs(tiles[0].ActualWidth - tiles[^1].ActualWidth), 0, 2);
                AssertMast(host, "DashboardsMastActions");
                if (width < 720)
                {
                    var menu = Assert.IsType<MenuFlyout>(ById<Button>(host, "DashboardsGalleryMore").Flyout);
                    Invoke(Assert.Single(menu.Items.OfType<MenuFlyoutItem>()));
                    Assert.Equal(1, webOpens);
                }
                var search = Bounds(ById<TextBox>(host, "DashboardsSearch"), host);
                var sort = Bounds(ById<ComboBox>(host, "DashboardsSort"), host);
                if (width >= 1000)
                    Assert.InRange(Math.Abs(search.Top - sort.Top), 0, 2);
                else
                    Assert.True(search.Top >= sort.Bottom + 10);
                var fresh = ById<Grid>(host, "DashboardsFreshLayout");
                Assert.Equal(width >= 1000 ? 2 : 1, fresh.ColumnDefinitions.Count);
                var sides = ById<Grid>(host, "DashboardsFreshSides");
                Assert.Equal(width is >= 720 and < 1000 ? 2 : 1, sides.ColumnDefinitions.Count);
                AssertNoUnmarkedTextClipping(host);
                Assert.Equal(0, client.BoardLoads);
            },
            ready: host => FindDescendants<Grid>(host).Any(grid =>
                AutomationProperties.GetAutomationId(grid) == "DashboardsCards" && grid.ColumnDefinitions.Count == columns) &&
                HeroMatchesColumn(host, "DashboardsHero", "DashboardsGallery"),
            width: width, onViewportChanged: state.SetViewportWidth);

        state.Open(new BoardSessionTarget(target.Key, "main"), target.Session.DisplayName, target);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                AssertMast(host, "DashboardMastActions");
                AssertHeroColumn(host, "DashboardHero", "DashboardView", width);
                var grid = ById<Grid>(host, "DashboardTabGrid_tab-0");
                var cells = grid.Children.OfType<FrameworkElement>().ToArray();
                AssertNoOverlap(host, cells);
                Assert.Equal(width < 720 ? 12 : 6, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(cells[0]));
                AssertNoUnmarkedTextClipping(host);
                Assert.Equal(1, client.BoardLoads);
            },
            ready: host => FindDescendants<Grid>(host).Any(grid =>
                AutomationProperties.GetAutomationId(grid) == "DashboardTabGrid_tab-0") &&
                HeroMatchesColumn(host, "DashboardHero", "DashboardView"),
            width: width, onViewportChanged: state.SetViewportWidth);
    }

    private static T ById<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentInset_MovesRetainedViewportAndHeroWithoutRemounting(bool board)
    {
        var key = "agent:main:inset";
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [], []))
        {
            Gallery = new DashboardSessionPage([], false, null, 0),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetConnection(client, true);
        if (board)
            state.Open(new BoardSessionTarget(key, "main"), "Saved report");
        var heroId = board ? "DashboardHero" : "DashboardsHero";
        var columnId = board ? "DashboardView" : "DashboardsGallery";
        var scrollId = board ? "DashboardPageScroll" : "DashboardsGalleryScroll";
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            _ => { },
            ready: host => HeroMatchesColumn(host, heroId, columnId),
            width: 900, onViewportChanged: state.SetViewportWidth,
            afterMounted: async host =>
            {
                Border? column = null;
                Border? hero = null;
                ScrollViewer? scroll = null;
                double originalHeight = 0;
                double headerHeight = 0;
                await ui.RunOnUIAsync(() =>
                {
                    column = ById<Border>(host, columnId);
                    hero = ById<Border>(host, heroId);
                    scroll = ById<ScrollViewer>(host, scrollId);
                    originalHeight = hero.ActualHeight;
                    headerHeight = Assert.IsType<Grid>(column.Child).RowDefinitions[0].ActualHeight;
                    state.SetTopInset(56);
                });
                foreach (var inset in new[] { 56d, 0d })
                {
                    await ui.RunOnUIAsync(() => state.SetTopInset(inset));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!await ui.RunOnUIAsync(() => Task.FromResult(
                        Math.Abs(ById<Border>(host, heroId).ActualHeight - originalHeight) <= 0.5 &&
                        ById<ScrollViewer>(host, scrollId).Margin.Top == inset &&
                        Math.Abs(Bounds(ById<ScrollViewer>(host, scrollId), host).Top - inset) <= 0.5 &&
                        Math.Abs(Bounds(ById<Border>(host, heroId), host).Top - inset - ById<Border>(host, columnId).Padding.Top) <= 0.5)))
                    {
                        Assert.True(DateTime.UtcNow < deadline, "Inset-only layout did not settle.");
                        await ui.YieldToRenderAsync();
                    }
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.Same(column, ById<Border>(host, columnId));
                        Assert.Same(hero, ById<Border>(host, heroId));
                        Assert.Same(scroll, ById<ScrollViewer>(host, scrollId));
                        Assert.InRange(Math.Abs(Bounds(scroll!, host).Top - inset), 0, 0.5);
                        Assert.InRange(Math.Abs(Bounds(hero!, host).Top - inset - column!.Padding.Top), 0, 0.5);
                        Assert.InRange(Math.Abs(Assert.IsType<Grid>(column!.Child).RowDefinitions[0].ActualHeight - headerHeight), 0, 0.5);
                    });
                }
            });
    }

    [Fact]
    public async Task InitialMount_ReconcilesImmediateInsetAtConstantWidth()
    {
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetViewportWidth(900);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                Assert.Equal(900, state.ViewportWidth);
                Assert.Equal(56, ById<Border>(host, "DashboardsDisconnectedViewport").Margin.Top);
                Assert.Equal(0, host.Margin.Top);
            },
            ready: host => FindDescendants<Border>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "DashboardsDisconnectedViewport" && element.Margin.Top == 56),
            afterMount: () => state.SetTopInset(56));
    }

    [Fact]
    public async Task DisconnectedContent_RetainsInsetWithoutAConnectedBoardOrBackdrop()
    {
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetTopInset(56);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var viewport = ById<Border>(host, "DashboardsDisconnectedViewport");
                Assert.Equal(56, viewport.Margin.Top);
                Assert.InRange(Math.Abs(Bounds(viewport, host).Top - 56), 0, 0.5);
                Assert.Equal(0, host.Margin.Top);
            });
    }

    private static void AssertHeroColumn(ReactorHostControl host, string heroId, string columnId, double width)
    {
        var hero = ById<Border>(host, heroId);
        var column = ById<Border>(host, columnId);
        var heroBounds = Bounds(hero, host);
        var columnBounds = Bounds(column, host);
        Assert.DoesNotContain(FindDescendants<FrameworkElement>(host), element =>
            AutomationProperties.GetAutomationId(element) is "DashboardBackdrop" or "DashboardsBackdrop");
        var expectedPadding = width < 720 ? 20 : 32;
        var expectedContent = Math.Min(1200, width - 2 * expectedPadding);
        Assert.InRange(Math.Abs(column.ActualWidth - column.Padding.Left - column.Padding.Right - expectedContent), 0, 0.5);
        Assert.InRange(Math.Abs((columnBounds.Left + columnBounds.Right) / 2 - host.ActualWidth / 2), 0, 0.5);
        Assert.True(hero.ActualHeight > 0);
        Assert.InRange(Math.Abs(heroBounds.Left - columnBounds.Left - column.Padding.Left), 0, 0.5);
        Assert.InRange(Math.Abs(heroBounds.Right - columnBounds.Right + column.Padding.Right), 0, 0.5);
        Assert.Equal(new Thickness(1), hero.BorderThickness);
    }

    private static bool HeroMatchesColumn(ReactorHostControl host, string heroId, string columnId)
    {
        var hero = FindDescendants<Border>(host).FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element) == heroId);
        var column = FindDescendants<Border>(host).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == columnId);
        return hero is not null && column is not null && hero.ActualHeight > 0 &&
            Math.Abs(hero.ActualWidth - column.ActualWidth + column.Padding.Left + column.Padding.Right) <= 0.5;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageScroll_MovesHeroAndMastTogetherWithoutDecorativeViewportBackdrop(bool board)
    {
        var key = "agent:main:viewport";
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [new BoardTab("main", "Report", 0, "right")],
            [Plugin("long-report", "session:report", """{"markdown":"Saved report"}""") with { SizeH = 24 }]))
        {
            Gallery = new DashboardSessionPage(Enumerable.Range(0, 30)
                .Select(index => new DashboardSessionRow
                {
                    Session = new SessionInfo { Key = $"agent:main:viewport:{index}", AgentId = "main", DisplayName = $"Board {index}" },
                    LastMessagePreview = "Saved board",
                }).ToImmutableArray(), false, null, 30),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetConnection(client, true);
        if (board)
            state.Open(new BoardSessionTarget(key, "main"), "Saved report");
        Windows.Foundation.Rect heroBefore = default;
        Windows.Foundation.Rect titleBefore = default;
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var hero = ById<Border>(host, board ? "DashboardHero" : "DashboardsHero");
                var scroll = ById<ScrollViewer>(host, board ? "DashboardPageScroll" : "DashboardsGalleryScroll");
                Assert.True(hero.IsHitTestVisible);
                Assert.DoesNotContain(FindDescendants<FrameworkElement>(host), element =>
                    AutomationProperties.GetAutomationId(element) == (board ? "DashboardBackdrop" : "DashboardsBackdrop"));
                Assert.True(scroll.ScrollableHeight > 120);
            },
            ready: host => HeroMatchesColumn(host, board ? "DashboardHero" : "DashboardsHero",
                board ? "DashboardView" : "DashboardsGallery"),
            width: 900, onViewportChanged: state.SetViewportWidth,
            afterMounted: async host =>
            {
                for (var turn = 0; turn < 4; turn++)
                    await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(() =>
                {
                    heroBefore = Bounds(ById<Border>(host, board ? "DashboardHero" : "DashboardsHero"), host);
                    titleBefore = Bounds(ById<TextBlock>(host, "DashboardsTitle"), host);
                    Assert.True(ById<ScrollViewer>(host, board ? "DashboardPageScroll" : "DashboardsGalleryScroll")
                        .ChangeView(null, 120, null, disableAnimation: true));
                });
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!await ui.RunOnUIAsync(() => Task.FromResult(
                    ById<ScrollViewer>(host, board ? "DashboardPageScroll" : "DashboardsGalleryScroll").VerticalOffset > 119)))
                {
                    Assert.True(DateTime.UtcNow < deadline, "Page scroll did not reach the requested offset.");
                    await ui.YieldToRenderAsync();
                }
                await ui.RunOnUIAsync(() =>
                {
                    var scroll = ById<ScrollViewer>(host, board ? "DashboardPageScroll" : "DashboardsGalleryScroll");
                    Assert.InRange(scroll.VerticalOffset, 119, 121);
                    var heroAfter = Bounds(ById<Border>(host, board ? "DashboardHero" : "DashboardsHero"), host);
                    var titleAfter = Bounds(ById<TextBlock>(host, "DashboardsTitle"), host);
                    Assert.InRange(heroBefore.Top - heroAfter.Top, 119, 121);
                    Assert.InRange(titleBefore.Top - titleAfter.Top, 119, 121);
                });
            });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public async Task HeaderHero_ReplacesFullPageBackdropForGalleryAndBoard(string theme, bool board)
    {
        var key = "agent:main:fade";
        var client = new SnapshotBoardClient(new BoardSnapshot(key, 1, [], []))
        {
            Gallery = new DashboardSessionPage([], false, null, 0),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetConnection(client, true);
        if (board)
            state.Open(new BoardSessionTarget(key, "main"), "Saved report");
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                AssertHeroColumn(host, board ? "DashboardHero" : "DashboardsHero",
                    board ? "DashboardView" : "DashboardsGallery", 1400);
            },
            ready: host => HeroMatchesColumn(host, board ? "DashboardHero" : "DashboardsHero",
                board ? "DashboardView" : "DashboardsGallery"),
            theme: theme, width: 1400, onViewportChanged: state.SetViewportWidth);
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(1008)]
    [InlineData(761)]
    [InlineData(739)]
    public Task FreshToday_CompactCardInitialsFitWithinTheirBanner(double width) =>
        WithFreshGalleryAsync(width, host =>
        {
            var card = ById<Button>(host, "DashboardCardFresh_agent:main:resize:1");
            var layout = ById<Grid>(host, AutomationProperties.GetAutomationId(card) + "Layout");
            var initials = Assert.Single(FindDescendants<TextBlock>(layout), text => text.Text == "RT");
            var badge = Assert.IsType<Border>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(initials));
            var banner = Assert.IsType<Border>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(badge));
            var badgeBounds = Bounds(badge, banner);
            Assert.InRange(badgeBounds.Width, 47.5, 48.5);
            Assert.InRange(badgeBounds.Height, 47.5, 48.5);
            Assert.InRange(badgeBounds.Top, 7.5, 8.5);
            Assert.InRange(banner.ActualHeight - badgeBounds.Bottom, 7.5, 8.5);
        });

    [Theory]
    [InlineData(1400)]
    [InlineData(1008)]
    public Task FreshToday_WideCardsHaveAlignedTopsAndEqualRowHeights(double width) =>
        WithFreshGalleryAsync(width, host =>
        {
            var feature = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:0"), host);
            var side = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:1"), host);
            Assert.InRange(Math.Abs(feature.Top - side.Top), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Bottom - side.Bottom), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Height - side.Height), 0, 0.5);
        });

    [Theory]
    [InlineData(761)]
    [InlineData(739)]
    [InlineData(937)]
    public Task FreshToday_MediumCardsSpanTheSameContentColumn(double width) =>
        WithFreshGalleryAsync(width, host =>
        {
            var feature = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:0"), host);
            var side = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:1"), host);
            var column = Bounds(ById<Grid>(host, "DashboardsFreshLayout"), host);
            Assert.InRange(Math.Abs(feature.Left - side.Left), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Right - side.Right), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Left - column.Left), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Right - column.Right), 0, 0.5);
        });

    [Theory]
    [InlineData(1400)]
    [InlineData(1008)]
    [InlineData(761)]
    [InlineData(739)]
    [InlineData(937)]
    public Task HeroWebButton_HasNativeBorderAndAlignsWithTheHeroContent(double width) =>
        WithFreshGalleryAsync(width, host =>
        {
            var button = ById<Button>(host, "DashboardsOpenWeb");
            Assert.Contains(FindDescendants<TextBlock>(button), text => text.Text == "Open in web");
            Assert.Contains(FindDescendants<FontIcon>(button), icon => icon.Glyph == "\uE8A7");
            Assert.True(button.BorderThickness.Right > 0, "The web command must retain its native button border.");
            AssertDefaultWebButton(host, button);
            var scroller = ById<ScrollViewer>(host, "DashboardsGalleryScroll");
            var scrollPeer = FrameworkElementAutomationPeer.CreatePeerForElement(scroller);
            var scrollProvider = Assert.IsAssignableFrom<IScrollProvider>(scrollPeer!.GetPattern(PatternInterface.Scroll));
            Assert.True(scrollProvider.VerticallyScrollable);
            Assert.False(scrollProvider.HorizontallyScrollable);
            var column = Bounds(ById<Grid>(host, "DashboardsFreshLayout"), host);
            var gallery = ById<Border>(host, "DashboardsGallery");
            var contentLeft = Bounds(gallery, host).Left + gallery.Padding.Left;
            var contentRight = Bounds(gallery, host).Right - gallery.Padding.Right;
            var ancestry = new List<string>();
            for (DependencyObject? parent = ById<Grid>(host, "DashboardsFreshLayout"); parent is FrameworkElement element;
                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
                ancestry.Add($"{element.GetType().Name}: bounds={Bounds(element, host)}, margin={element.Margin}, border={(element is Control control ? control.BorderThickness : element is Border border ? border.BorderThickness : element is Grid grid ? grid.BorderThickness : default)}, padding={(element is Control padded ? padded.Padding : element is Border paddedBorder ? paddedBorder.Padding : element is Grid paddedGrid ? paddedGrid.Padding : default)}");
            Assert.True(Math.Abs(column.Left - contentLeft) <= 0.5,
                $"Content left {contentLeft}, column left {column.Left}, gallery padding {gallery.Padding}:\n{string.Join("\n", ancestry)}");
            Assert.InRange(Math.Abs(column.Right - contentRight), 0, 0.5);
            AssertHeroActionsRightEdge(host);
            AssertMast(host, "DashboardsMastActions");
            if (width == 739)
            {
                var title = ById<TextBlock>(host, "DashboardsTitle");
                Assert.Equal("Dashboards", title.Text);
                Assert.False(title.IsTextTrimmed);
                Assert.InRange(title.ActualHeight, 0, title.FontSize * 1.8);
            }
        });

    [Fact]
    public Task FreshToday_ResizingFromWideToMediumClearsWideOnlySpacing() =>
        WithFreshGalleryAsync(1400, _ => { }, async host =>
        {
            foreach (var width in new[] { 1008d, 761d, 739d, 937d })
            {
                await ui.RunOnUIAsync(() => host.Width = width);
                for (var turn = 0; turn < 4; turn++)
                    await ui.YieldToRenderAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!await ui.RunOnUIAsync(() => Task.FromResult(
                    Math.Abs(host.ActualWidth - width) < 0.5 &&
                    ById<Grid>(host, "DashboardsFreshLayout").ColumnDefinitions.Count == (width >= 1000 ? 2 : 1))))
                {
                    Assert.True(DateTime.UtcNow < deadline, "The resized Fresh today grid did not reach the expected tier.");
                    await ui.YieldToRenderAsync();
                }
                await ui.RunOnUIAsync(() =>
                {
                    ui.Container.UpdateLayout();
                    var column = Bounds(ById<Grid>(host, "DashboardsFreshLayout"), host);
                    var feature = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:0"), host);
                    var side = Bounds(ById<Button>(host, "DashboardCardFresh_agent:main:resize:1"), host);
                    if (width < 1000)
                    {
                        Assert.InRange(Math.Abs(feature.Left - side.Left), 0, 0.5);
                        Assert.InRange(Math.Abs(feature.Right - side.Right), 0, 0.5);
                    }
                    Assert.InRange(Math.Abs(side.Right - column.Right), 0, 0.5);
                    var gallery = ById<Border>(host, "DashboardsGallery");
                    Assert.InRange(Math.Abs(column.Left - Bounds(gallery, host).Left - gallery.Padding.Left), 0, 0.5);
                    Assert.InRange(Math.Abs(column.Right - Bounds(gallery, host).Right + gallery.Padding.Right), 0, 0.5);
                    AssertDefaultWebButton(host, ById<Button>(host, "DashboardsOpenWeb"));
                    AssertHeroActionsRightEdge(host);
                });
            }
        });

    [Fact]
    public Task HeroResizing_RetainsGalleryFocusScrollAndNativeControls() =>
        WithFreshGalleryAsync(1100, _ => { }, async host =>
        {
            ComboBox? sort = null;
            ScrollViewer? scroll = null;
            Border? hero = null;
            await ui.RunOnUIAsync(() =>
            {
                sort = ById<ComboBox>(host, "DashboardsSort");
                scroll = ById<ScrollViewer>(host, "DashboardsGalleryScroll");
                hero = ById<Border>(host, "DashboardsHero");
                Assert.True(sort.Focus(FocusState.Keyboard));
                host.UpdateLayout();
                Assert.True(scroll.ScrollableHeight >= 120,
                    $"Resize fixture needs 120 DIP of scroll range; extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, range={scroll.ScrollableHeight}.");
                Assert.True(scroll.ChangeView(null, 120, null, disableAnimation: true));
            });
            await WaitForMountedStateAsync(host, mounted =>
                ById<ScrollViewer>(mounted, "DashboardsGalleryScroll").VerticalOffset is >= 119 and <= 121 &&
                ReferenceEquals(sort, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(mounted.XamlRoot)));
            await ui.RunOnUIAsync(() =>
            {
                var focusedBounds = Bounds(sort!, host);
                Assert.InRange(focusedBounds.Top, 0, scroll!.ViewportHeight);
                Assert.InRange(focusedBounds.Bottom, 0, scroll.ViewportHeight);
            });
            foreach (var width in new[] { 820d, 600d, 1100d })
            {
                await ui.RunOnUIAsync(() => host.Width = width);
                await WaitForMountedStateAsync(host, mounted =>
                    Math.Abs(mounted.ActualWidth - width) < 0.5 &&
                    ById<Grid>(mounted, "DashboardsScopeLine").ColumnDefinitions.Count == (width < 1000 ? 1 : 2));
                await ui.RunOnUIAsync(() =>
                {
                    host.UpdateLayout();
                    Assert.Same(sort, ById<ComboBox>(host, "DashboardsSort"));
                    Assert.Same(scroll, ById<ScrollViewer>(host, "DashboardsGalleryScroll"));
                    Assert.Same(hero, ById<Border>(host, "DashboardsHero"));
                    Assert.Same(sort, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(host.XamlRoot));
                    Assert.InRange(scroll!.VerticalOffset, 119, 121);
                    AssertHeroColumn(host, "DashboardsHero", "DashboardsGallery", width);
                    AssertMast(host, "DashboardsMastActions");
                    AssertNoUnmarkedTextClipping(host);
                });
            }
        }, viewportHeight: 360, includeFresh: false);

    private Task WithFreshGalleryAsync(double width, Action<ReactorHostControl> assert,
        Func<ReactorHostControl, Task>? afterMounted = null, double? viewportHeight = null, bool includeFresh = true)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 6).Select(index => new DashboardSessionRow
        {
            Session = new SessionInfo
            {
                Key = "agent:main:resize:" + index, AgentId = "main",
                DisplayName = index == 0 ? "Ops overview" : "Release tracker " + index,
            },
            CreatedActor = new DashboardActor("author", "Fixture Owner"),
            LastMessagePreview = index == 0 ? "Deploy finished; health is green." : "Store submission pending review.",
            UpdatedAtMs = (includeFresh && index < 2 ? now.AddMinutes(-index) : now.AddDays(-2)).ToUnixTimeMilliseconds(),
        }).ToImmutableArray();
        var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
        {
            Gallery = new DashboardSessionPage(rows, false, null, rows.Length),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            AgentNames = new Dictionary<string, string> { ["main"] = "Fixture Main" },
            OpenChat = _ => { },
            OpenWebDashboards = () => { },
        };
        state.SetConnection(client, true);
        return WithMountedAsync(
            () => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            assert,
            ready: host => FindDescendants<Button>(host).Any(button =>
                AutomationProperties.GetAutomationId(button) == (includeFresh
                    ? "DashboardCardFresh_agent:main:resize:1" : "DashboardCard_agent:main:resize:1")),
            width: width, onViewportChanged: state.SetViewportWidth, afterMounted: afterMounted, viewportHeight: viewportHeight);
    }

    private static void AssertDefaultWebButton(ReactorHostControl host, Button button)
    {
        var fill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(button.Background);
        Assert.True(fill.Color.A > 0 && fill.Opacity > 0);
        Assert.NotNull(button.BorderBrush);
        Assert.Null(button.Style);
        Assert.True(double.IsNaN(button.Height));
        Assert.True(double.IsNaN(button.Width));
        var localKeys = button.Resources.Select(entry => entry.Key).ToArray();
        Assert.DoesNotContain(localKeys, key => key is "ButtonBackground" or "ButtonBorderBrush");
        var parent = Assert.IsAssignableFrom<Panel>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(button));
        var pristine = new Button { RequestedTheme = button.ActualTheme };
        parent.Children.Add(pristine);
        try
        {
            var pristineKeys = pristine.Resources.Select(entry => entry.Key).ToArray();
            Assert.DoesNotContain(pristineKeys, key => key is "ButtonBackground" or "ButtonBorderBrush");
            Assert.True(pristine.Resources.ContainsKey("ButtonBackground"));
            pristine.Resources["ButtonBackground"] = pristine.Resources["ButtonBackground"];
            var injectedKeys = pristine.Resources.Select(entry => entry.Key).ToArray();
            Assert.Contains("ButtonBackground", injectedKeys);
        }
        finally
        {
            parent.Children.Remove(pristine);
        }
        Assert.True(button.Padding.Left > 0 && button.Padding.Right > 0);
        var buttonBounds = Bounds(button, host);
        foreach (var text in FindDescendants<TextBlock>(button))
        {
            var textBounds = Bounds(text, host);
            Assert.True(textBounds.Left - buttonBounds.Left >= 8,
                $"Button content starts too close to its left border: {text.Text}.");
            Assert.True(buttonBounds.Right - textBounds.Right >= 8,
                $"Button content ends too close to its right border: {text.Text}.");
        }
        var refresh = ById<Button>(host, "DashboardsRefresh");
        Assert.True(Bounds(button, host).Right < Bounds(refresh, host).Left,
            "Open in web must precede Refresh without overlapping.");
    }

    private static Windows.Foundation.Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void AssertNoOverlap(FrameworkElement root, IReadOnlyList<FrameworkElement> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        for (var j = i + 1; j < elements.Count; j++)
        {
            var a = Bounds(elements[i], root);
            var b = Bounds(elements[j], root);
            Assert.False(Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 1 &&
                Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 1,
                $"Overlapping siblings {i}/{j}: {elements[i].GetType().Name} row={Microsoft.UI.Xaml.Controls.Grid.GetRow(elements[i])} col={Microsoft.UI.Xaml.Controls.Grid.GetColumn(elements[i])} [{a}] and " +
                $"{elements[j].GetType().Name} row={Microsoft.UI.Xaml.Controls.Grid.GetRow(elements[j])} col={Microsoft.UI.Xaml.Controls.Grid.GetColumn(elements[j])} [{b}].");
        }
    }

    private static void AssertMast(ReactorHostControl host, string actionId)
    {
        var actions = ById<FrameworkElement>(host, actionId);
        var buttons = FindDescendants<Button>(actions).Cast<FrameworkElement>().ToArray();
        Assert.NotEmpty(buttons);
        AssertNoOverlap(host, buttons);
        var first = Bounds(buttons[0], host);
        Assert.All(buttons, button => Assert.InRange(Math.Abs(Bounds(button, host).Top - first.Top), 0, 2));
        AssertNoOverlap(host, [ById<TextBlock>(host, "DashboardsTitle"), actions]);
        Assert.All(buttons.SelectMany(FindDescendants<TextBlock>), text =>
            Assert.True(text.ActualHeight <= text.FontSize * 1.8, "An action label wrapped to another line."));
    }

    private static void AssertHeroActionsRightEdge(ReactorHostControl host)
    {
        var hero = ById<Border>(host, "DashboardsHero");
        var content = ById<Grid>(host, "DashboardsHeroGrid");
        var bounds = Bounds(hero, host);
        var actions = Bounds(ById<FrameworkElement>(host, "DashboardsMastActions"), host);
        Assert.InRange(Math.Abs(actions.Right - bounds.Right + content.Padding.Right + hero.BorderThickness.Right), 0, 0.5);
    }

    private static void AssertNoUnmarkedTextClipping(ReactorHostControl host)
    {
        foreach (var text in FindDescendants<TextBlock>(host))
        {
            if (text.ActualWidth <= 1 || text.ActualHeight <= 1 || string.IsNullOrEmpty(text.Text) ||
                text.Text.All(character => character is >= '\uE000' and <= '\uF8FF'))
                continue;
            if (text.IsTextTrimmed)
                Assert.True(text.TextTrimming != TextTrimming.None, $"Trimmed text has no ellipsis: {text.Text}");
            if (text.TextWrapping != TextWrapping.NoWrap)
                continue;
            var natural = new TextBlock
            {
                Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily,
                FontWeight = text.FontWeight, FontStyle = text.FontStyle, CharacterSpacing = text.CharacterSpacing,
            };
            natural.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (natural.DesiredSize.Width > text.ActualWidth + 2)
                Assert.True(text.TextTrimming != TextTrimming.None,
                    $"Unmarked clipped text: {text.Text} ({natural.DesiredSize.Width:0.##} > {text.ActualWidth:0.##} DIP)");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task EmptyBoard_ShowsOnlyNeutralGuidanceAndOpensItsExistingChat(string theme)
    {
        const string sessionKey = "agent:main:board:empty";
        string? openedSession = null;
        var client = new SnapshotBoardClient(new BoardSnapshot(sessionKey, 1, [], []));
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenChat = session => openedSession = session,
        };
        state.SetConnection(client, true);
        state.SetTheme(theme == "Dark");
        state.Open(new BoardSessionTarget(sessionKey, "main"), "Saved board");
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var empty = ById<FrameworkElement>(host, "DashboardEmpty");
                var allowedCopy = new[]
                {
                    "A clear board, ready for work", "No content was supplied for this section. Ask your agent in chat.", "Open chat",
                    DashboardsGlyphsExtra.Report, DashboardsGlyphsExtra.Code,
                    DashboardsGlyphsExtra.Globe, DashboardsGlyphsExtra.Sparkle,
                };
                Assert.Equal("No content was supplied for this section. Ask your agent in chat.", ById<TextBlock>(empty, "DashboardEmptyDetail").Text);
                Assert.All(FindDescendants<TextBlock>(empty), text => Assert.Contains(text.Text, allowedCopy));
                var chat = Assert.Single(FindDescendants<Button>(empty));
                Assert.Equal("DashboardEmptyOpenChat", AutomationProperties.GetAutomationId(chat));
                Invoke(chat);
                Assert.Equal(sessionKey, openedSession);
            }, ready: host => FindDescendants<Button>(host).Any(button =>
                AutomationProperties.GetAutomationId(button) == "DashboardEmptyOpenChat"),
            theme: theme, capture: "empty-board-neutral");
    }

    [Theory]
    [InlineData(true, "Light")]
    [InlineData(false, "Light")]
    [InlineData(true, "Dark")]
    [InlineData(false, "Dark")]
    public async Task ClipboardResult_UsesNativeToastWithoutReloadingTheOpenBoard(bool copied, string theme)
    {
        DashboardsPageState? state = null;
        state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            CopySessionKey = _ => state!.ReportCopyResult(copied),
            OpenChat = _ => { },
        };
        var client = new SnapshotBoardClient(new BoardSnapshot("agent:main:board:proof", 1, [], []));
        state.SetConnection(client, true);
        state.SetTheme(theme == "Dark");
        state.Open(new BoardSessionTarget("agent:main:board:proof", "main"), "Saved board");
        var invoked = false;
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var bar = Assert.Single(FindDescendants<Border>(host), item => AutomationProperties.GetAutomationId(item) == "DashboardCopyNotice");
                Assert.Contains(FindDescendants<TextBlock>(bar), item =>
                    item.Text == LocalizationHelper.GetString(copied ? "Dashboards_CopiedSessionKey" : "Dashboards_CopyFailed"));
                Assert.Equal(theme == "Dark" ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light, bar.RequestedTheme);
                var announcements = Assert.Single(FindDescendants<TextBlock>(host), item =>
                    AutomationProperties.GetAutomationId(item) == "DashboardResultAnnouncements");
                Assert.Equal(copied ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite :
                    Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(announcements));
                Assert.Equal(1, client.BoardLoads);
                Assert.Contains(FindDescendants<Button>(host), button => AutomationProperties.GetAutomationId(button) == "DashboardOpenChat");
                Assert.Contains(FindDescendants<Button>(host), button => AutomationProperties.GetAutomationId(button) == "DashboardEmptyOpenChat");
                Assert.DoesNotContain(FindDescendants<TextBlock>(host), text => text.Text == "agent:main:board:proof");
            },
            ready: host =>
            {
                if (!invoked && FindDescendants<Button>(host).FirstOrDefault(button =>
                    AutomationProperties.GetAutomationId(button) == "DashboardMoreActions") is { } more)
                {
                    invoked = true;
                    var menu = Assert.IsType<MenuFlyout>(more.Flyout);
                    Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(item =>
                        AutomationProperties.GetAutomationId(item) == "DashboardCopySessionKey"));
                }
                return FindDescendants<Border>(host).Any(item => AutomationProperties.GetAutomationId(item) == "DashboardCopyNotice");
            }, theme: theme, capture: copied ? "copy-success-intercepted" : "copy-error-intercepted");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public async Task GalleryHeader_NamesAgentInPickerWithoutInventingLatestUpdate(int count)
    {
        var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
        {
            Gallery = new DashboardSessionPage([.. Enumerable.Range(0, count).Select(index => new DashboardSessionRow
            {
                Session = new SessionInfo { Key = $"agent:main:header:{index}", AgentId = "main", DisplayName = $"Board {index}" },
            })], false, null, count),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            AgentNames = new Dictionary<string, string> { ["main"] = "Research team" },
        };
        state.SetAgents(["main", "research"], "main");
        await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
            new DashboardGalleryProps(state, client, "main", 1)), host =>
            {
                Assert.Equal("Dashboards", ById<TextBlock>(host, "DashboardsTitle").Text);
                Assert.Equal("Boards are read-only here; ask Research team to change one.",
                    ById<TextBlock>(host, "DashboardsReaderDetail").Text);
                Assert.DoesNotContain(FindDescendants<TextBlock>(host), text =>
                    AutomationProperties.GetAutomationId(text) == "DashboardsLatestBoard");
                var picker = ById<ComboBox>(host, "DashboardsAgentPicker");
                var pickerRow = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(picker);
                Assert.Contains(FindDescendants<TextBlock>(pickerRow), text => text.Text == "Boards from");
                Assert.Equal("Research team", picker.SelectedItem);
                Assert.Equal(0, client.BoardLoads);
            }, ready: host => FindDescendants<FrameworkElement>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == (count == 0 ? "DashboardsEmpty" : "DashboardsCards")));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task GalleryStates_KeepFailuresDistinctFromEmptyAndDenied(string theme)
    {
        foreach (var scenario in new[] { "empty", "error", "unavailable", "loading", "stale", "large" })
        {
            var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
            {
                Gallery = new DashboardSessionPage(
                    scenario is "stale" or "large" ? [.. Enumerable.Range(0, scenario == "large" ? 30 : 1).Select(index =>
                        new DashboardSessionRow
                        {
                            Session = new SessionInfo { Key = "agent:main:board:" + index, AgentId = "main", DisplayName = "Saved board " + index },
                            LastMessagePreview = "Synthetic saved summary.",
                        })] : [], false, null, 0),
                Failure = scenario == "error" ? new InvalidOperationException("Synthetic Gateway request failed.") : null,
                ReadScopes = scenario == "unavailable" ? ["operator.pairing"] : ["operator.read"],
                Pending = scenario == "loading",
            };
            var state = new DashboardsPageState
            {
                Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
                Schedule = (_, _) => new NoopLease(),
                OpenAgentChat = _ => { },
            };
            var refreshed = false;
            var expected = scenario switch
            {
                "error" => "DashboardsLoadErrorTitle",
                "unavailable" => "DashboardsUnavailableTitle",
                "loading" => "DashboardsLoading",
                "stale" => "DashboardsStaleError",
                "large" => "DashboardsShowMore",
                _ => "DashboardsEmptyTitle",
            };
            state.SetConnection(client, true);
            await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
                new DashboardGalleryProps(state, client, "main", 1)), host =>
                {
                    Assert.Contains(FindDescendants<Microsoft.UI.Xaml.FrameworkElement>(host),
                        element => AutomationProperties.GetAutomationId(element) == expected);
                    if (scenario is "error" or "unavailable" or "loading")
                        Assert.DoesNotContain(FindDescendants<FrameworkElement>(host), element =>
                            AutomationProperties.GetAutomationId(element) is "DashboardsEmpty" or "DashboardsLatestBoard");
                    if (scenario == "empty")
                    {
                        var empty = ById<FrameworkElement>(host, "DashboardsEmpty");
                        var allowedCopy = new[]
                        {
                            "No dashboards yet",
                            "Dashboards created in your sessions will appear here.",
                            "Open chat",
                            DashboardsGlyphs.Dashboard,
                        };
                        Assert.All(FindDescendants<TextBlock>(empty), text => Assert.Contains(text.Text, allowedCopy));
                        Assert.Single(FindDescendants<Button>(empty), button =>
                            AutomationProperties.GetAutomationId(button) == "DashboardsEmptyOpenChat");
                    }
                    if (scenario == "stale")
                        Assert.Contains(FindDescendants<Button>(host), button =>
                            AutomationProperties.GetAutomationId(button) == "DashboardCard_agent:main:board:0");
                },
                ready: host =>
                {
                    if (scenario == "stale" && !refreshed && FindDescendants<Button>(host).Any(button =>
                        AutomationProperties.GetAutomationId(button) == "DashboardCard_agent:main:board:0"))
                    {
                        refreshed = true;
                        client.Failure = new InvalidOperationException("Synthetic refresh failure.");
                        Invoke(FindDescendants<Button>(host).Single(button => AutomationProperties.GetAutomationId(button) == "DashboardsRefresh"));
                    }
                    return FindDescendants<Microsoft.UI.Xaml.FrameworkElement>(host).Any(element =>
                        AutomationProperties.GetAutomationId(element) == expected);
                }, theme: theme, capture: scenario,
                afterMounted: scenario == "large" ? async host =>
                {
                    await ui.RunOnUIAsync(() => Invoke(ById<Button>(host, "DashboardsShowMore")));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!await ui.RunOnUIAsync(() => Task.FromResult(ById<Grid>(host, "DashboardsCards").Children.Count == 30)))
                    {
                        Assert.True(DateTime.UtcNow < deadline, "Show more did not render all thirty boards.");
                        await ui.YieldToRenderAsync();
                    }
                    await ui.RunOnUIAsync(() => Assert.DoesNotContain(FindDescendants<Button>(host),
                        button => AutomationProperties.GetAutomationId(button) == "DashboardsShowMore"));
                } : null);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TicketFailure_ShowsTheCorrectRecoveryMessage(bool remoteGateway, bool repeatedRejection)
    {
        var widget = new BoardWidget
        {
            Name = "ticket-proof",
            TabId = "main",
            ContentKind = BoardWidgetContentKind.Html,
            SizeW = 6,
            SizeH = 4,
            Position = 0,
            Revision = 1,
            SandboxUrl = "/mcp-app-sandbox",
            SandboxPort = 18790,
        };
        var props = Cell(widget, BoardWidgetBodyKind.Frame,
            new Uri(remoteGateway ? "https://gateway.example.com/" : "http://127.0.0.1:18789/"),
            repeatedRejection ? BoardWidgetTicketFailure.RepeatedRejection : BoardWidgetTicketFailure.RefreshUnavailable);
        await WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(props), host =>
        {
            var expectedKey = !repeatedRejection ? "Dashboards_Widget_ContentUnavailable"
                : remoteGateway ? "Dashboards_Widget_SandboxOriginRequired"
                : "Dashboards_Widget_AuthorizationFailed";
            var text = FindDescendants<TextBlock>(host).Select(block => block.Text).ToArray();
            Assert.Contains(LocalizationHelper.GetString(expectedKey), text);
            if (!repeatedRejection)
            {
                Assert.DoesNotContain(LocalizationHelper.GetString("Dashboards_Widget_AuthorizationFailed"), text);
                Assert.DoesNotContain(LocalizationHelper.GetString("Dashboards_Widget_SandboxOriginRequired"), text);
            }
            Assert.Contains(FindDescendants<Button>(host), button =>
                button.Content is string label && label == LocalizationHelper.GetString("Dashboards_Retry"));
        });
    }

    [Fact]
    public async Task OutOfRangeUpdateTimesRenderAsUnknownInsteadOfThrowing()
    {
        await ui.RunOnUIAsync(() =>
        {
            var unknown = LocalizationHelper.GetString("Dashboards_UpdatedUnknown");
            foreach (var time in new long?[] { null, -1, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1, long.MaxValue })
            {
                var row = new DashboardSessionRow { Session = new SessionInfo { Key = "synthetic" }, UpdatedAtMs = time };
                Assert.Equal(unknown, DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow));
            }
            var recent = new DashboardSessionRow
            {
                Session = new SessionInfo { Key = "synthetic" },
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Assert.NotEqual(unknown, DashboardsPresentation.UpdatedText(recent, DateTimeOffset.UtcNow));
        });
    }

    [Fact]
    public async Task WebsiteWidget_IsAValidatedBrowserLinkWithoutAnEmbeddedBrowser()
    {
        var widget = Plugin("status", "session:website", """{ "url": "https://status.example.com/board?view=all" }""");
        await WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(Cell(widget, BoardWidgetBodyKind.Website)), host =>
        {
            var link = Assert.Single(FindDescendants<HyperlinkButton>(host));
            Assert.Equal(new Uri("https://status.example.com/board?view=all"), link.NavigateUri);
            Assert.Equal("BoardWebsiteOpen_status", AutomationProperties.GetAutomationId(link));
            Assert.Contains(FindDescendants<TextBlock>(host), block => block.Text == "status.example.com");
            Assert.Empty(FindDescendants<WebView2>(host));
        });
    }

    [Theory]
    [InlineData("https://192.168.1.1/admin")]
    [InlineData("http://status.example.com/")]
    [InlineData("javascript:alert(1)")]
    public async Task WebsiteWidget_RefusesAddressesThePolicyRejects(string url)
    {
        var widget = Plugin("status", "session:website", JsonSerializer.Serialize(new { url }));
        await WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(Cell(widget, BoardWidgetBodyKind.Website)), host =>
        {
            Assert.Empty(FindDescendants<HyperlinkButton>(host));
            Assert.Contains(FindDescendants<TextBlock>(host),
                block => block.Text == LocalizationHelper.GetString("Dashboards_Widget_WebsiteInvalid"));
        });
    }

    [Theory]
    [InlineData(BoardWidgetBodyKind.NeedsApproval)]
    [InlineData(BoardWidgetBodyKind.Rejected)]
    [InlineData(BoardWidgetBodyKind.McpApp)]
    [InlineData(BoardWidgetBodyKind.UnsupportedCorePlugin)]
    public async Task DisplayOnlyWidgets_HandOffToTheWebGallery(BoardWidgetBodyKind body)
    {
        var opened = 0;
        var widget = Plugin("pending", "session:progress", "{}") with { GrantState = BoardWidgetGrantState.Pending };
        await WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(
            Cell(widget, body) with { OpenWebDashboards = () => opened++ }), host =>
        {
            var button = Assert.Single(FindDescendants<Button>(host),
                candidate => AutomationProperties.GetAutomationId(candidate) == "BoardWidgetOpenWeb_pending");
            Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery"), AutomationProperties.GetName(button));
            Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery_BoardHint"), ToolTipText(button));
            Invoke(button);
        });
        Assert.Equal(1, opened);
    }

    [Theory]
    [InlineData(BoardWidgetBodyKind.DisabledPlugin, true)]
    [InlineData(BoardWidgetBodyKind.McpApp, false)]
    public async Task WebGalleryHandoff_IsOfferedOnlyWhereTheWebCanHelpAndWired(BoardWidgetBodyKind body, bool wired)
    {
        var widget = Plugin("other", "acme:timeline", "{}");
        await WithMountedAsync(() => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(
            Cell(widget, body) with { OpenWebDashboards = wired ? () => { } : null }), host =>
            Assert.DoesNotContain(FindDescendants<Button>(host),
                candidate => AutomationProperties.GetAutomationId(candidate) == "BoardWidgetOpenWeb_other"));
    }

    [Theory]
    [InlineData("Light", "Normal", 739)]
    [InlineData("Light", "Normal", 1400)]
    [InlineData("Light", "PointerOver", 1400)]
    [InlineData("Light", "Pressed", 1400)]
    [InlineData("Light", "Focused", 1400)]
    [InlineData("Dark", "PointerOver", 739)]
    public async Task GalleryCard_UsesOneNativeSurfaceAndKeepsChatIndependent(string theme, string visualState, double width)
    {
        var opened = 0;
        var chats = new List<string>();
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenWebDashboards = () => opened++,
            OpenChat = chats.Add,
        };
        var row = new DashboardSessionRow
        {
            Session = new SessionInfo { Key = "agent:main:dashboard:ops", AgentId = "main", DisplayName = "Ops overview" },
            LastMessagePreview = "Deploy finished.",
        };
        var client = new SnapshotBoardClient(new BoardSnapshot(row.Key, 1, [], []))
        {
            Gallery = new DashboardSessionPage([row], false, null, 1),
        };
        state.SetConnection(client, connected: true);
        await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
                new DashboardGalleryProps(state, client, "main", state.ConnectionRevision, width)),
            host =>
            {
                var button = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardsOpenWeb");
                Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery"), AutomationProperties.GetName(button));
                Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery_GalleryHint"), ToolTipText(button));
                Invoke(button);
                var chat = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardCardChat_agent:main:dashboard:ops");
                Assert.Equal(new Microsoft.UI.Xaml.Thickness(1), chat.BorderThickness);
                Assert.Contains(FindDescendants<TextBlock>(chat), text => text.Text == "Open chat");
                Assert.Contains(FindDescendants<FontIcon>(chat), icon => icon.Glyph == "\uE8BD");
                var card = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardCard_agent:main:dashboard:ops");
                var layout = ById<Grid>(host, AutomationProperties.GetAutomationId(card) + "Layout");
                Assert.Null(layout.Background);
                Assert.Equal(new Thickness(0), layout.BorderThickness);
                Assert.Null(card.Style);
                Assert.True(double.IsNaN(card.Height));
                Assert.True(double.IsNaN(chat.Height));
                var tolerance = 1 / host.XamlRoot.RasterizationScale;
                var bounds = Bounds(card, layout);
                Assert.InRange(Math.Abs(bounds.Left), 0, tolerance);
                Assert.InRange(Math.Abs(bounds.Top), 0, tolerance);
                Assert.InRange(Math.Abs(bounds.Right - layout.ActualWidth), 0, tolerance);
                Assert.InRange(Math.Abs(bounds.Bottom - layout.ActualHeight), 0, tolerance);
                var initials = Assert.Single(FindDescendants<TextBlock>(layout), text => text.Text == "OO");
                var badge = Assert.IsType<Border>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(initials));
                var cover = Assert.IsType<Border>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(badge));
                Assert.NotNull(cover.Background);
                Assert.DoesNotContain(FindDescendants<Button>(card), candidate => candidate == chat);
                var chatBounds = Bounds(chat, layout);
                var summary = Assert.Single(FindDescendants<TextBlock>(layout), text => text.Text == "Deploy finished.");
                Assert.True(Bounds(summary, layout).Bottom <= chatBounds.Top, "The sibling chat footer must not cover card text.");
                Assert.True(chat.Focus(FocusState.Keyboard));
                Invoke(chat);
                Assert.Null(state.OpenTarget);
                Assert.True(card.Focus(FocusState.Keyboard));
                Assert.Equal(FocusState.Keyboard, card.FocusState);
                if (visualState != "Focused")
                {
                    Assert.True(card.Focus(FocusState.Programmatic));
                    Assert.True(VisualStateManager.GoToState(card, "Normal", false));
                }
                var presenter = Assert.Single(FindDescendants<ContentPresenter>(card));
                var painted = Bounds(presenter, layout);
                Assert.InRange(Math.Abs(painted.Left - bounds.Left), 0, tolerance);
                Assert.InRange(Math.Abs(painted.Top - bounds.Top), 0, tolerance);
                Assert.InRange(Math.Abs(painted.Right - bounds.Right), 0, tolerance);
                Assert.InRange(Math.Abs(painted.Bottom - bounds.Bottom), 0, tolerance);
                Assert.NotNull(presenter.Background);
            },
            ready: host => FindDescendants<Button>(host).Any(candidate =>
                AutomationProperties.GetAutomationId(candidate) == "DashboardCard_agent:main:dashboard:ops"),
            theme: theme, width: width, capture: $"stock-card-{visualState}-{width}",
            afterMounted: async host =>
            {
                await ui.YieldToRenderAsync();
                if (visualState is not ("PointerOver" or "Pressed"))
                    return;
                var card = await ui.RunOnUIAsync(() => Task.FromResult(
                    ById<Button>(host, "DashboardCard_agent:main:dashboard:ops")));
                var normalPixel = await ui.RunOnUIAsync(() => CardCenterPixelAsync(card));
                var probe = await ui.RunOnUIAsync(() =>
                {
                    var reference = (Border)XamlReader.Load(
                        $"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"0\" Height=\"0\" Background=\"{{ThemeResource ButtonBackground{visualState}}}\" />");
                    ui.Container.Children.Add(reference);
                    return Task.FromResult(reference);
                });
                try
                {
                    await ui.RunOnUIAsync(() => Assert.True(VisualStateManager.GoToState(card, visualState, false)));
                    var observation = "The themed reference has not loaded.";
                    await WaitForMountedStateAsync(host, _ =>
                    {
                        if (!probe.IsLoaded)
                            return false;
                        Assert.Equal(theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light, probe.ActualTheme);
                        Assert.Equal(host.ActualTheme, probe.ActualTheme);
                        Assert.Equal(card.ActualTheme, probe.ActualTheme);
                        var presenter = Assert.Single(FindDescendants<ContentPresenter>(card));
                        var expected = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(probe.Background);
                        var actual = presenter.Background as Microsoft.UI.Xaml.Media.SolidColorBrush;
                        observation = $"{visualState}: actual ARGB={actual?.Color}, expected ARGB={expected.Color}; " +
                            $"root={host.ActualTheme}, card={card.ActualTheme}, probe={probe.ActualTheme}.";
                        return actual is not null && actual.Color == expected.Color && actual.Opacity == expected.Opacity;
                    }, () => observation);
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (await ui.RunOnUIAsync(() => CardCenterPixelAsync(card)) == normalPixel)
                    {
                        Assert.True(DateTime.UtcNow < deadline,
                            $"The native surface did not render differently from Normal. {observation}");
                        await ui.YieldToRenderAsync();
                    }
                }
                finally { await ui.RunOnUIAsync(() => ui.Container.Children.Remove(probe)); }
            });
        Assert.Equal(1, opened);
        Assert.Equal(0, client.BoardLoads);
        Assert.Equal(new[] { "agent:main:dashboard:ops" }, chats);
    }

    private static async Task<uint> CardCenterPixelAsync(Button card)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(card);
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var center = ((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4;
        return BitConverter.ToUInt32(pixels, center);
    }

    [Fact]
    public async Task BoardMenu_OpensTheWebGalleryAndCopiesTheStableSessionKey()
    {
        const string sessionKey = "agent:ops:dashboard:status";
        var opened = 0;
        var copied = new List<string>();
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
            OpenWebDashboards = () => opened++,
            CopySessionKey = copied.Add,
        };
        var client = new SnapshotBoardClient(new BoardSnapshot(sessionKey, 1, [], []));
        state.SetConnection(client, connected: true);
        await WithMountedAsync(() => Component<ReactorBoardView, BoardViewProps>(
            new BoardViewProps(state, client, new BoardSessionTarget(sessionKey, "ops"), state.ConnectionRevision)), host =>
        {
            var more = Assert.Single(FindDescendants<Button>(host),
                candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardMoreActions");
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            var items = menu.Items.OfType<MenuFlyoutItem>().ToDictionary(AutomationProperties.GetAutomationId);
            Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery"), items["DashboardOpenWeb"].Text);
            Assert.Equal(sessionKey, ToolTipText(items["DashboardCopySessionKey"]));
            Invoke(items["DashboardOpenWeb"]);
            Invoke(items["DashboardCopySessionKey"]);
        });
        Assert.Equal(1, opened);
        Assert.Equal(new[] { sessionKey }, copied);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task ActiveSearch_RemainsClearableAfterAgentSwitchOrInventoryShrinks(bool switchAgent, int remaining)
    {
        var client = new SnapshotBoardClient(new BoardSnapshot("unused", 1, [], []))
        {
            Gallery = new DashboardSessionPage(
                [.. Enumerable.Range(0, 6).Select(index => new DashboardSessionRow
                {
                    Session = new SessionInfo { Key = "board-" + index, AgentId = "main", DisplayName = "Keep " + index },
                })], false, null, 6),
        };
        var state = new DashboardsPageState
        {
            Post = action => ui.Container.DispatcherQueue.TryEnqueue(() => action()),
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetAgents(["main", "research"], "main");
        state.SetConnection(client, true);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            _ => { }, ready: host => FindDescendants<TextBox>(host).Any(element =>
                AutomationProperties.GetAutomationId(element) == "DashboardsSearch"),
            width: 1400, afterMounted: async host =>
            {
                await ui.RunOnUIAsync(() => ById<TextBox>(host, "DashboardsSearch").Text = "Keep");
                await WaitForMountedStateAsync(host, _ => state.GalleryQuery == "Keep");
                await ui.RunOnUIAsync(() =>
                {
                    var agent = switchAgent ? "research" : "main";
                    client.Gallery = new DashboardSessionPage(remaining == 0 ? [] :
                        [new DashboardSessionRow
                        {
                            Session = new SessionInfo { Key = "remaining", AgentId = agent, DisplayName = "Other board" },
                        }], false, null, remaining);
                    if (switchAgent)
                        ById<ComboBox>(host, "DashboardsAgentPicker").SelectedIndex = 1;
                    else
                        Invoke(ById<Button>(host, "DashboardsRefresh"));
                });
                await WaitForMountedStateAsync(host, root => FindDescendants<FrameworkElement>(root).Any(element =>
                    AutomationProperties.GetAutomationId(element) == "DashboardsNoResults"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal(switchAgent ? "research" : "main", state.AgentId);
                    var search = ById<TextBox>(host, "DashboardsSearch");
                    Assert.Equal("Keep", search.Text);
                    Assert.True(search.IsEnabled);
                    Assert.True(search.ActualWidth > 0 && search.ActualHeight > 0);
                    search.Text = "";
                });
                await WaitForMountedStateAsync(host, root =>
                    state.GalleryQuery.Length == 0 &&
                    FindDescendants<FrameworkElement>(root).Any(element =>
                        AutomationProperties.GetAutomationId(element) == (remaining == 0 ? "DashboardsEmpty" : "DashboardCard_remaining")));
            });
    }

    private static BoardWidgetCellProps Cell(
        BoardWidget widget,
        BoardWidgetBodyKind body,
        Uri? origin = null,
        BoardWidgetTicketFailure? failure = null) =>
        new(widget, body, "Widget", origin ?? new Uri("http://127.0.0.1:18789/"), failure,
            _ => { }, _ => { }, _ => { }, (_, _) => { }, ReducedMotion: true);

    private static BoardWidget Plugin(string name, string kind, string propsJson)
    {
        using var document = JsonDocument.Parse(propsJson);
        return new BoardWidget
        {
            Name = name,
            TabId = "main",
            ContentKind = BoardWidgetContentKind.Plugin,
            PluginKind = kind,
            Props = document.RootElement.Clone(),
            SizeW = 6,
            SizeH = 4,
            Position = 0,
            Revision = 1,
        };
    }

    private static string? ToolTipText(Microsoft.UI.Xaml.DependencyObject element) => ToolTipService.GetToolTip(element) switch
    {
        string text => text,
        ToolTip { Content: string text } => text,
        _ => null,
    };

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static void Invoke(MenuFlyoutItem item) =>
        Assert.IsAssignableFrom<IInvokeProvider>(new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();

    private async Task WithMountedAsync(Func<Element> render, Action<ReactorHostControl> assert, Func<ReactorHostControl, bool>? ready = null,
        string theme = "Light", string? capture = null, double width = 900, Action<double>? onViewportChanged = null,
        Func<ReactorHostControl, Task>? afterMounted = null, Action? afterMount = null, double? viewportHeight = null)
    {
        ReactorHostControl? host = null;
        Border? captureFrame = null;
        Action? restoreCaptureWindow = null;
        Exception? mountedFailure = null;
        var previousTheme = ElementTheme.Default;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                EnsureDashboardResources();
                previousTheme = ui.Container.RequestedTheme;
                var requested = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
                ui.Container.RequestedTheme = requested;
                host = new ReactorHostControl { Width = width, Height = viewportHeight ?? (capture is null ? 600 : 760), RequestedTheme = requested };
                host.SizeChanged += (_, args) => onViewportChanged?.Invoke(args.NewSize.Width);
                host.Mount(_ => render());
                afterMount?.Invoke();
                captureFrame = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" />");
                captureFrame.RequestedTheme = requested;
                captureFrame.Child = host;
                ui.Container.Children.Add(captureFrame);
            });
            // Board views load through the posted dispatcher; let those turns and layout settle.
            for (var turn = 0; turn < 3; turn++)
                await ui.YieldToRenderAsync();
            if (ready is not null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!await ui.RunOnUIAsync(() => Task.FromResult(ready(host!))))
                {
                    Assert.True(DateTime.UtcNow < deadline, "Mounted dashboard content did not become ready.");
                    await ui.YieldToRenderAsync();
                }
            }
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                assert(host!);
            });
            if (afterMounted is not null)
                await afterMounted(host!);
            if (capture is not null && Environment.GetEnvironmentVariable("OPENCLAW_DASHBOARD_STATE_ARTIFACTS") is { Length: > 0 } directory)
            {
                if (theme == "Light" && (capture is "hero-600-gallery" or "hero-1100-board"))
                    await ui.RunOnUIAsync(async () =>
                    {
                        restoreCaptureWindow = await MountedControlCapture.ArrangeSceneAsync(
                            ui.TestWindow, captureFrame!, $"{theme}-{capture}-owned-window");
                    });
                if (capture.StartsWith("hero-", StringComparison.Ordinal))
                    await WaitForMountedStateAsync(host!, HeroCaptureIsSettled);
                await ui.RunOnUIAsync(async () =>
                {
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(captureFrame);
                    var pixels = await bitmap.GetPixelsAsync();
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, $"{theme}-{capture}.png");
                    File.WriteAllBytes(path, []);
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
                    await encoder.FlushAsync();
                    if (theme == "Light" && (capture is "hero-600-gallery" or "hero-1100-board"))
                    {
                        await MountedControlCapture.OptionalAsync(ui.TestWindow, $"{theme}-{capture}-owned-window");
                    }
                });
            }
        }
        catch (Exception failure)
        {
            mountedFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                await ui.RunOnUIAsync(() => restoreCaptureWindow?.Invoke());
            }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    ui.Container.Children.Remove(captureFrame);
                    if (captureFrame is not null)
                        captureFrame.Child = null;
                    host?.Dispose();
                });
            }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { await ui.RunOnUIAsync(() => ui.Container.RequestedTheme = previousTheme); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            if (cleanupFailures.Count > 0)
            {
                if (mountedFailure is not null)
                    cleanupFailures.Insert(0, mountedFailure);
                throw new AggregateException("Mounted fixture cleanup failed.", cleanupFailures);
            }
        }
    }

    private static bool HeroCaptureIsSettled(ReactorHostControl host)
    {
        var hero = FindDescendants<Border>(host).FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element) is "DashboardsHero" or "DashboardHero");
        if (hero is null || !hero.IsLoaded || hero.ActualHeight <= 0)
            return false;
        var descendants = FindDescendants<FrameworkElement>(hero).ToArray();
        var glyph = descendants.FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element).EndsWith(":Glyph", StringComparison.Ordinal));
        var title = descendants.FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element) == "DashboardsTitle");
        if (glyph is null || title is null)
            return false;
        var disconnected = FindDescendants<FrameworkElement>(host).Any(element =>
            AutomationProperties.GetAutomationId(element) == "DashboardsDisconnectedInfo");
        var requiredActionIds = disconnected ? Array.Empty<string>()
            : AutomationProperties.GetAutomationId(hero) == "DashboardHero"
                ? new[] { "DashboardOpenChat", "DashboardRefresh", "DashboardMoreActions" }
                : new[] { host.ActualWidth < 720 ? "DashboardsGalleryMore" : "DashboardsOpenWeb", "DashboardsRefresh" };
        var actions = requiredActionIds.Select(id => descendants.FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element) == id)).ToArray();
        if (actions.Any(element => element is null))
            return false;
        return new[] { hero, glyph, title }.Concat(actions.OfType<FrameworkElement>())
            .All(element => element.IsLoaded && element.Visibility == Visibility.Visible &&
                element.ActualWidth > 0 && element.ActualHeight > 0 && element.Opacity >= 0.99 &&
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element).Opacity >= 0.99) &&
            descendants.Where(element => (element is Panel or Microsoft.UI.Xaml.Controls.Border) &&
                    element.Visibility == Visibility.Visible && element.ActualHeight > 0 &&
                    HasNativeTransition(element) &&
                    !HasDisabledControlAncestor(element, hero))
                .All(element => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element).Opacity >= 0.99);
    }

    private static bool HasNativeTransition(FrameworkElement element) =>
        element.Transitions is { Count: > 0 } ||
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) is Panel { ChildrenTransitions.Count: > 0 };

    private static bool HasDisabledControlAncestor(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? current = element; current is not null && current != root;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (current is Control { IsEnabled: false })
                return true;
        return false;
    }

    private static ResourceDictionary? s_installedResources;

    private static void EnsureDashboardResources()
    {
        var target = Application.Current.Resources;
        if (ReferenceEquals(s_installedResources, target))
            return;
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for native dashboard resource proof.");
        var directory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        target.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(
            File.ReadAllText(Path.Combine(directory, "Themes", "DashboardResources.xaml"))));
        var appResources = XElement.Load(Path.Combine(directory, "App.xaml")).Elements().Single().Elements().Single();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var theme in appResources.Elements().Where(element =>
            element.Name.LocalName == "ResourceDictionary.ThemeDictionaries").SelectMany(element => element.Elements()))
        {
            var key = (string)theme.Attribute(x + "Key")!;
            if (!target.ThemeDictionaries.TryGetValue(key, out var existing) || existing is not ResourceDictionary)
                target.ThemeDictionaries[key] = new ResourceDictionary();
            var dictionary = (ResourceDictionary)target.ThemeDictionaries[key];
            foreach (var resource in theme.Elements())
            {
                var name = (string?)resource.Attribute(x + "Key");
                if (name is null)
                    continue;
                var markup = new XElement(resource);
                markup.Attribute(x + "Key")!.Remove();
                dictionary[name] = XamlReader.Load(markup.ToString());
            }
        }
        s_installedResources = target;
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class SnapshotBoardClient(BoardSnapshot board) : IBoardGatewayClient
    {
        public DashboardSessionPage? Gallery { get; set; }
        public Exception? Failure { get; set; }
        public bool Pending { get; init; }
        public IReadOnlyList<string> ReadScopes { get; init; } = ["operator.read"];
        public ImmutableArray<PluginWidgetKind> Kinds { get; init; } = [];
        public int BoardLoads { get; private set; }
        public Uri? Origin { get; init; }
        public Task<BoardSnapshot>? BoardResponse { get; init; }
        public bool IsConnectedToGateway => true;
        public IReadOnlyList<string> GrantedOperatorScopes => ReadScopes;
        public ImmutableArray<PluginWidgetKind> PluginWidgetKinds => Kinds;
        public Uri? GatewayHttpOrigin => Origin ?? new("http://127.0.0.1:18789/");
        public IReadOnlyList<string> AdvertisedServerMethods => ["board.get"];
        public event EventHandler<BoardChangedEvent>? BoardChanged { add { } remove { } }
        public event EventHandler<BoardCommandEvent>? BoardCommandReceived { add { } remove { } }
        public event EventHandler<SessionInfo[]>? SessionsUpdated { add { } remove { } }
        public Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000) =>
            Failure is not null ? Task.FromException<DashboardSessionPage?>(Failure) :
            Pending ? new TaskCompletionSource<DashboardSessionPage?>().Task :
            Gallery is null ? throw new NotSupportedException("The board view does not list the gallery.") : Task.FromResult<DashboardSessionPage?>(Gallery);
        public Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000)
        {
            BoardLoads++;
            return BoardResponse ?? Task.FromResult(board);
        }
    }
}
