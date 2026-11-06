using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
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
public sealed class DashboardWidgetCellTests(UIThreadFixture ui)
{
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
                AssertFullWidthBackdrop(host, "DashboardsBackdrop", "DashboardsGallery", width);
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
                BackdropMatchesMast(host, "DashboardsBackdrop", "DashboardsGallery"),
            width: width, onViewportChanged: state.SetViewportWidth);

        state.Open(new BoardSessionTarget(target.Key, "main"), target.Session.DisplayName, target);
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                AssertMast(host, "DashboardMastActions");
                AssertFullWidthBackdrop(host, "DashboardBackdrop", "DashboardView", width);
                var grid = ById<Grid>(host, "DashboardTabGrid_tab-0");
                var cells = grid.Children.OfType<FrameworkElement>().ToArray();
                AssertNoOverlap(host, cells);
                Assert.Equal(width < 720 ? 12 : 6, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(cells[0]));
                AssertNoUnmarkedTextClipping(host);
                Assert.Equal(1, client.BoardLoads);
            },
            ready: host => FindDescendants<Grid>(host).Any(grid =>
                AutomationProperties.GetAutomationId(grid) == "DashboardTabGrid_tab-0") &&
                BackdropMatchesMast(host, "DashboardBackdrop", "DashboardView"),
            width: width, onViewportChanged: state.SetViewportWidth);
    }

    private static T ById<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    private static void AssertFullWidthBackdrop(ReactorHostControl host, string backdropId, string columnId, double width)
    {
        var backdrop = ById<Border>(host, backdropId);
        var column = ById<Border>(host, columnId);
        var backdropBounds = Bounds(backdrop, host);
        var columnBounds = Bounds(column, host);
        Assert.InRange(Math.Abs(backdropBounds.Left), 0, 0.5);
        Assert.InRange(Math.Abs(backdrop.ActualWidth - host.ActualWidth), 0, 0.5);
        var expectedPadding = width < 720 ? 20 : 32;
        var expectedContent = Math.Min(1200, width - 2 * expectedPadding);
        Assert.InRange(Math.Abs(column.ActualWidth - column.Padding.Left - column.Padding.Right - expectedContent), 0, 0.5);
        Assert.InRange(Math.Abs((columnBounds.Left + columnBounds.Right) / 2 - host.ActualWidth / 2), 0, 0.5);
        var contentGrid = Assert.IsType<Grid>(column.Child);
        Assert.True(backdrop.ActualHeight > 0);
        Assert.InRange(Math.Abs(backdrop.ActualHeight - contentGrid.RowDefinitions[0].ActualHeight - column.Padding.Top), 0, 0.5);
    }

    private static bool BackdropMatchesMast(ReactorHostControl host, string backdropId, string columnId)
    {
        var backdrop = FindDescendants<Border>(host).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == backdropId);
        var column = FindDescendants<Border>(host).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == columnId);
        return backdrop is not null && column?.Child is Grid grid && backdrop.ActualHeight > 0 &&
            Math.Abs(backdrop.ActualWidth - host.ActualWidth) <= 0.5 &&
            Math.Abs(backdrop.ActualHeight - grid.RowDefinitions[0].ActualHeight - column.Padding.Top) <= 0.5;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageScroll_MovesTheMastButNotTheDecorativeViewportBackdrop(bool board)
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
        Windows.Foundation.Rect backdropBefore = default;
        Windows.Foundation.Rect titleBefore = default;
        await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)),
            host =>
            {
                var backdrop = ById<Border>(host, board ? "DashboardBackdrop" : "DashboardsBackdrop");
                var scroll = ById<ScrollViewer>(host, board ? "DashboardPageScroll" : "DashboardsGalleryScroll");
                Assert.False(backdrop.IsHitTestVisible);
                for (DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(backdrop);
                    parent is not null && parent != host; parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
                    Assert.False(parent is ScrollViewer, "Backdrop must not be inside any page scroller.");
                Assert.True(scroll.ScrollableHeight > 120);
            },
            ready: host => BackdropMatchesMast(host, board ? "DashboardBackdrop" : "DashboardsBackdrop",
                board ? "DashboardView" : "DashboardsGallery"),
            width: 900, onViewportChanged: state.SetViewportWidth,
            afterMounted: async host =>
            {
                for (var turn = 0; turn < 4; turn++)
                    await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(() =>
                {
                    backdropBefore = Bounds(ById<Border>(host, board ? "DashboardBackdrop" : "DashboardsBackdrop"), host);
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
                    var backdropAfter = Bounds(ById<Border>(host, board ? "DashboardBackdrop" : "DashboardsBackdrop"), host);
                    var titleAfter = Bounds(ById<TextBlock>(host, "DashboardsTitle"), host);
                    Assert.Equal(backdropBefore, backdropAfter);
                    Assert.InRange(titleBefore.Top - titleAfter.Top, 119, 121);
                });
            });
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    public async Task BackdropWash_FadesToTransparentBeforeTheBottomForGalleryAndBoard(string theme, bool board)
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
                var backdrop = ById<Border>(host, board ? "DashboardBackdrop" : "DashboardsBackdrop");
                var gradient = Assert.IsType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(backdrop.Background);
                Assert.Equal(new Windows.Foundation.Point(0, 0), gradient.StartPoint);
                Assert.Equal(new Windows.Foundation.Point(0, 1), gradient.EndPoint);
                Assert.Equal(0, gradient.GradientStops[^1].Color.A);
                Assert.Equal((double)0.9f, gradient.GradientStops[^1].Offset);
                AssertFullWidthBackdrop(host, board ? "DashboardBackdrop" : "DashboardsBackdrop",
                    board ? "DashboardView" : "DashboardsGallery", 1400);
            },
            ready: host => BackdropMatchesMast(host, board ? "DashboardBackdrop" : "DashboardsBackdrop",
                board ? "DashboardView" : "DashboardsGallery"),
            theme: theme, width: 1400, onViewportChanged: state.SetViewportWidth);
    }

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
            Assert.InRange(Math.Abs(feature.Left - column.Left - 1), 0, 0.5);
            Assert.InRange(Math.Abs(feature.Right - column.Right + 1), 0, 0.5);
        });

    [Theory]
    [InlineData(1400)]
    [InlineData(1008)]
    [InlineData(761)]
    [InlineData(739)]
    [InlineData(937)]
    public Task MastWebButton_HasNativeBorderAndAlignsWithTheCardColumn(double width) =>
        WithFreshGalleryAsync(width, host =>
        {
            var button = ById<Button>(host, "DashboardsOpenWeb");
            Assert.Contains(FindDescendants<TextBlock>(button), text => text.Text == "Open in web");
            Assert.Contains(FindDescendants<TextBlock>(button), text => text.Text == "\uE8A7");
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
            Assert.InRange(Math.Abs(Bounds(ById<FrameworkElement>(host, "DashboardsMastActions"), host).Right - column.Right), 0, 0.5);
            AssertMast(host, "DashboardsMastActions");
            if (width == 739)
            {
                var title = ById<TextBlock>(host, "DashboardsTitle");
                Assert.Equal("Dashboards for Fixture Main", title.Text);
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
                    Assert.InRange(Math.Abs(side.Right - column.Right + 1), 0, 0.5);
                    var gallery = ById<Border>(host, "DashboardsGallery");
                    Assert.InRange(Math.Abs(column.Left - Bounds(gallery, host).Left - gallery.Padding.Left), 0, 0.5);
                    Assert.InRange(Math.Abs(column.Right - Bounds(gallery, host).Right + gallery.Padding.Right), 0, 0.5);
                    AssertDefaultWebButton(host, ById<Button>(host, "DashboardsOpenWeb"));
                    Assert.InRange(Math.Abs(Bounds(ById<FrameworkElement>(host, "DashboardsMastActions"), host).Right - column.Right), 0, 0.5);
                });
            }
        });

    private Task WithFreshGalleryAsync(double width, Action<ReactorHostControl> assert,
        Func<ReactorHostControl, Task>? afterMounted = null)
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
            UpdatedAtMs = (index < 2 ? now.AddMinutes(-index) : now.AddDays(-2)).ToUnixTimeMilliseconds(),
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
                AutomationProperties.GetAutomationId(button) == "DashboardCardFresh_agent:main:resize:1"),
            width: width, onViewportChanged: state.SetViewportWidth, afterMounted: afterMounted);
    }

    private static void AssertDefaultWebButton(ReactorHostControl host, Button button)
    {
        var fill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(button.Background);
        var stroke = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(button.BorderBrush);
        var expectedFill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(
            Application.Current.Resources["ControlFillColorDefaultBrush"]);
        var expectedStroke = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(
            Application.Current.Resources["ControlStrokeColorDefaultBrush"]);
        Assert.Equal(expectedFill.Color, fill.Color);
        Assert.Equal(expectedStroke.Color, stroke.Color);
        Assert.True(fill.Color.A > 0 && fill.Opacity > 0);
        Assert.True(stroke.Color.A > 0 && stroke.Opacity > 0);
        Assert.Equal(new Thickness(11, 5, 11, 6), button.Padding);
        Assert.True(button.MinHeight >= 32);
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
        Assert.Equal(fill.Color, Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(button.Resources["ButtonBackground"]).Color);
        Assert.Equal(stroke.Color, Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(button.Resources["ButtonBorderBrush"]).Color);
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
                    "A clear board, ready for work", "Your agent can pin widgets here.", "Open chat",
                    DashboardsGlyphsExtra.Report, DashboardsGlyphsExtra.Code,
                    DashboardsGlyphsExtra.Globe, DashboardsGlyphsExtra.Sparkle,
                };
                Assert.Equal("Your agent can pin widgets here.", ById<TextBlock>(empty, "DashboardEmptyDetail").Text);
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
    [InlineData(0, "No dashboards yet")]
    [InlineData(1, "1 dashboard")]
    [InlineData(6, "6 dashboards")]
    public async Task GalleryHeader_NamesTheAgentAndKeepsGatewayRowCountSecondary(int count, string expectedCount)
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
                Assert.Equal("Dashboards for Research team", ById<TextBlock>(host, "DashboardsTitle").Text);
                Assert.Equal(expectedCount, ById<TextBlock>(host, "DashboardsSubtitle").Text);
                var picker = ById<ComboBox>(host, "DashboardsAgentPicker");
                var pickerRow = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(picker);
                Assert.Contains(FindDescendants<TextBlock>(pickerRow), text => text.Text == "Agent");
            }, ready: host => FindDescendants<TextBlock>(host).Any(text =>
                AutomationProperties.GetAutomationId(text) == "DashboardsSubtitle" && text.Text == expectedCount));
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
            await WithMountedAsync(() => Component<ReactorDashboardGallery, DashboardGalleryProps>(
                new DashboardGalleryProps(state, client, "main", 1)), host =>
                {
                    Assert.Contains(FindDescendants<Microsoft.UI.Xaml.FrameworkElement>(host),
                        element => AutomationProperties.GetAutomationId(element) == expected);
                    if (scenario is "error" or "unavailable" or "loading")
                        Assert.NotEqual("No dashboards yet", ById<TextBlock>(host, "DashboardsSubtitle").Text);
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

    [Fact]
    public async Task GalleryHeader_OpensTheWebGalleryAndCardsStayStatic()
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
                new DashboardGalleryProps(state, client, "main", state.ConnectionRevision)),
            host =>
            {
                var button = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardsOpenWeb");
                Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery"), AutomationProperties.GetName(button));
                Assert.Equal(LocalizationHelper.GetString("Dashboards_OpenWebGallery_GalleryHint"), ToolTipText(button));
                Invoke(button);
                var chat = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardCardChat_agent:main:dashboard:ops");
                Assert.Equal(new Microsoft.UI.Xaml.Thickness(0), chat.Padding);
                Assert.Contains(FindDescendants<TextBlock>(chat), text => text.Text == "\uE8BD");
                var card = Assert.Single(FindDescendants<Button>(host),
                    candidate => AutomationProperties.GetAutomationId(candidate) == "DashboardCard_agent:main:dashboard:ops");
                Assert.DoesNotContain(FindDescendants<Button>(card), candidate => candidate == chat);
                Invoke(chat);
            },
            ready: host => FindDescendants<Button>(host).Any(candidate =>
                AutomationProperties.GetAutomationId(candidate) == "DashboardCard_agent:main:dashboard:ops"));
        Assert.Equal(1, opened);
        Assert.Equal(0, client.BoardLoads);
        Assert.Equal(new[] { "agent:main:dashboard:ops" }, chats);
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
        Func<ReactorHostControl, Task>? afterMounted = null)
    {
        ReactorHostControl? host = null;
        Border? captureFrame = null;
        var previousTheme = ElementTheme.Default;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                EnsureDashboardResources();
                previousTheme = ui.Container.RequestedTheme;
                var requested = theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
                ui.Container.RequestedTheme = requested;
                host = new ReactorHostControl { Width = width, Height = capture is null ? 600 : 760, RequestedTheme = requested };
                host.SizeChanged += (_, args) => onViewportChanged?.Invoke(args.NewSize.Width);
                host.Mount(_ => render());
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
                });
            }
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(captureFrame);
                    if (captureFrame is not null)
                        captureFrame.Child = null;
                    host.Dispose();
                }
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => ui.Container.RequestedTheme = previousTheme);
        }
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
        public DashboardSessionPage? Gallery { get; init; }
        public Exception? Failure { get; set; }
        public bool Pending { get; init; }
        public IReadOnlyList<string> ReadScopes { get; init; } = ["operator.read"];
        public ImmutableArray<PluginWidgetKind> Kinds { get; init; } = [];
        public int BoardLoads { get; private set; }
        public bool IsConnectedToGateway => true;
        public IReadOnlyList<string> GrantedOperatorScopes => ReadScopes;
        public ImmutableArray<PluginWidgetKind> PluginWidgetKinds => Kinds;
        public Uri? GatewayHttpOrigin => new("http://127.0.0.1:18789/");
        public IReadOnlyList<string> AdvertisedServerMethods => ["board.get"];
        public event EventHandler<BoardChangedEvent>? BoardChanged { add { } remove { } }
        public event EventHandler<BoardCommandEvent>? BoardCommandReceived { add { } remove { } }
        public Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000) =>
            Failure is not null ? Task.FromException<DashboardSessionPage?>(Failure) :
            Pending ? new TaskCompletionSource<DashboardSessionPage?>().Task :
            Gallery is null ? throw new NotSupportedException("The board view does not list the gallery.") : Task.FromResult<DashboardSessionPage?>(Gallery);
        public Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000)
        {
            BoardLoads++;
            return Task.FromResult(board);
        }
    }
}
