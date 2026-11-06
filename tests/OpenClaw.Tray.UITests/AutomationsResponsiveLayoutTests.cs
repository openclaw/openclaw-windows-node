using System.Xml.Linq;
using System.Reflection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Automations;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class AutomationsResponsiveLayoutTests(UIThreadFixture ui)
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ToastPersistenceBoundary = TimeSpan.FromSeconds(5);
    private const int SteadyScrollFrames = 3;

    [Fact]
    public async Task Page_RemountsAfterToastAndResponsiveLifecycleWithSteadyResources()
    {
        RequireProductionResources();
        await ui.RunOnUIAsync(() =>
        {
            var failure = Assert.Throws<InvalidOperationException>(() => new ChatThemeProofScope("Light"));
            Assert.Contains("Standard test resource profile", failure.Message);
        });
        await OutcomeToast_UsesNeutralInformationAndKeepsErrorsUntilClosed();
        await Page_UsesHostWidthWithoutOverlappingActionsOrRows(1400, 2, 1, 2);
        await Page_UsesHostWidthWithoutOverlappingActionsOrRows(1000, 2, 1, 2);
    }

    [Fact]
    public async Task OutcomeToast_UsesNeutralInformationAndKeepsErrorsUntilClosed()
    {
        RequireProductionResources();
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
        var catalog = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        var text = new AutomationsText(key => catalog[key]);
        var dismissed = new List<long>();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        Window? window = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl();
                renderer.Mount(_ => Component<AutomationToast, AutomationToastProps>(new(
                    new(AutomationsNoticeKind.RunNotStarted, "Inbox sweep", "already-running") { Id = 1 },
                    text, true, id => dismissed.Add(id))).WithKey("info"));
                surface = new Border { Width = 480, Height = 200, Child = renderer };
                window = new Window { Content = surface };
                window.AppWindow.Move(new global::Windows.Graphics.PointInt32(-32000, -32000));
                window.Activate();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(surface!).OfType<TextBlock>().Any(label => label.Text == "\uE946"), "neutral information glyph"));
            await ui.RunOnUIAsync(() =>
            {
                var glyph = Assert.Single(Controls(surface!).OfType<TextBlock>(), label => label.Text == "\uE946");
                Assert.Equal(((SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(glyph.Foreground).Color);
                renderer!.Mount(_ => Component<AutomationToast, AutomationToastProps>(new(
                    new(AutomationsNoticeKind.RunNotStarted, "Dentist reminder", "FORBIDDEN", "operator.admin") { Id = 2 },
                    text, true, id => dismissed.Add(id))).WithKey("error"));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationToastClose"), "persistent error close action"));
            await Task.Delay(ToastPersistenceBoundary);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(dismissed);
                var close = Assert.IsType<Button>(Find(surface!, "AutomationToastClose"));
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close);
                var invoke = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider>(
                    peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke));
                invoke.Invoke();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => Assert.Equal(2, Assert.Single(dismissed)));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) surface.Child = null;
                renderer?.Dispose();
                if (window is not null) { window.Content = null; window.Close(); }
            });
        }
    }

    [Theory]
    [InlineData(1400, 2, 1, 2)]
    [InlineData(1000, 2, 1, 2)]
    [InlineData(760, 2, 2, 1)]
    [InlineData(937, 2, 2, 1)]
    [InlineData(520, 1, 3, 1)]
    public async Task Page_UsesHostWidthWithoutOverlappingActionsOrRows(double width, int mastColumns, int filterRows, int detailColumns)
    {
        RequireProductionResources();
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT.");
        var capture = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1";
        var runId = Guid.NewGuid().ToString("N");
        var artifactRoot = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS")
            ?? Path.Combine(root, "artifacts", "automations-mounted");
        var artifacts = Path.Combine(artifactRoot, $"responsive-{width}-Light-{runId}");
        var identity = Path.Combine(artifacts, "identity");
        Directory.CreateDirectory(identity);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: identity, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => connected.TrySetResult();
        await client.ConnectAsync();
        await connected.Task.WaitAsync(ReadyTimeout);
        Assert.True(await client.RunCronJobAsync("inbox-sweep"));
        var access = DispatchProxy.Create<IOperatorGatewayClient, AdmissionProxy>();
        var admission = (AdmissionProxy)(object)access;
        admission.Client = client;
        admission.CanManage = width != 1400;
        var catalog = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        AutomationsStore? store = null;
        ReactorHostControl? renderer = null;
        Border? surface = null;
        Window? proofWindow = null;
        IntPtr proofHandle = IntPtr.Zero;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                store = new AutomationsStore("main", () => access, action => { ui.Dispatcher.TryEnqueue(() => action()); });
                store.Refresh();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => store!.State.OverviewLoaded && store.State.ListLoaded, "complete mounted page data"));
            await ui.RunOnUIAsync(() =>
            {
                store!.Select("morning-brief");
                var host = new AutomationsHost(store, new AutomationsText(key => catalog[key]), () => access,
                    () => { }, _ => { }, _ => { }, _ => true, () => ["main", "ops"]);
                renderer = new ReactorHostControl { RequestedTheme = ElementTheme.Light };
                renderer.Mount(_ => Component<AutomationsRoot, AutomationsRootProps>(new(host)));
                surface = new Border
                {
                    Width = width,
                    Height = 1100,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = renderer,
                };
                proofWindow = new Window { Title = $"Automations responsive Light {width}", Content = surface };
                var position = capture ? 40 : -32000;
                proofWindow.AppWindow.Move(new global::Windows.Graphics.PointInt32(position, position));
                proofWindow.Activate();
                proofHandle = WinRT.Interop.WindowNative.GetWindowHandle(proofWindow);
            });
            // Activate schedules XamlRoot attachment; it is not available in the
            // same dispatcher callback that constructs the new window.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                surface!.IsLoaded && surface.XamlRoot is not null && renderer!.IsLoaded,
                "mounted surface attached to its XamlRoot"));
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                proofWindow!.AppWindow.ResizeClient(new global::Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(width * scale), (int)Math.Ceiling(1100 * scale)));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                PageResponsiveLayout.Snap(surface!.XamlRoot.Size.Width) >= width
                && PageResponsiveLayout.Snap(surface.XamlRoot.Size.Height) >= 1100,
                "owned client contains the required page-host viewport"));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationDetailColumns")
                && Math.Abs(Find(surface!, "AutomationsRoot").ActualWidth - width) < 1
                && Find(surface!, "AutomationsFilters") is Grid filters && filters.RowDefinitions.Count == filterRows
                && Math.Abs(Find(surface!, "AutomationsMast").ActualWidth - PageResponsiveLayout.ContentWidth(width)) < 1
                && Bounds(Find(surface!, "AutomationsMast"), surface!).Left >= -1
                && Bounds(Find(surface!, "AutomationsMast"), surface!).Right <= width + 1,
                "measured page host and completed content arrangement",
                () => $"host={Find(surface!, "AutomationsRoot").ActualWidth}; mast={Bounds(Find(surface!, "AutomationsMast"), surface!)}; requested={width}"));
            if (width == 1400)
            {
                await ui.RunOnUIAsync(() =>
                {
                    Assert.DoesNotContain(Controls(surface!), element => AutomationProperties.GetAutomationId(element) == "AutomationsNew");
                    admission.CanManage = true;
                    store!.Refresh();
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationsNew"),
                    "operator admin admission inserts the populated New action"));
            }
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(Find(surface!, "AutomationDetail")).Any(element => element is Expander
                    && AutomationProperties.GetAutomationId(element).StartsWith("AutomationRunExpand:", StringComparison.Ordinal)),
                "inline run summary disclosure"));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                surface!.UpdateLayout();
                AssertGatewayFieldLabels(Find(surface, "AutomationDetail"));
                AssertNewButton(surface, artifacts, width);
                var mast = Assert.IsType<Grid>(Find(surface, "AutomationsMastGrid"));
                Assert.Equal(mastColumns, mast.ColumnDefinitions.Count);
                var backdrop = Bounds(Find(surface, "AutomationsBackdrop"), surface);
                Assert.InRange(Math.Abs(backdrop.Left), 0, 1);
                Assert.InRange(Math.Abs(backdrop.Width - width), 0, 1);
                Assert.InRange(Math.Abs(Find(surface, "AutomationsMast").ActualWidth
                    - PageResponsiveLayout.ContentWidth(width)), 0, 1);
                Assert.InRange(Math.Abs(Bounds(Find(surface, "AutomationsAgentScope"), surface).Left
                    - Bounds(Find(surface, "AutomationsHeadline"), surface).Left), 0, 1);
                AssertTextAlignment(surface);
                AssertSeparatedInventoryCards(surface, artifacts, width);
                var filters = Assert.IsType<Grid>(Find(surface, "AutomationsFilters"));
                Assert.Equal(filterRows, filters.RowDefinitions.Count);
                var detail = Find(surface, "AutomationDetailColumns");
                Assert.Equal(detailColumns, detail is Grid columns ? columns.ColumnDefinitions.Count : 1);
                var agenda = Find(surface, "AutomationsAgendaBody");
                Assert.Equal(detailColumns, agenda is Grid agendaColumns ? agendaColumns.ColumnDefinitions.Count : 1);
                var agendaListBounds = Bounds(Find(surface, "AutomationsAgendaList"), surface);
                var agendaHeaders = Controls(surface).Where(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("AutomationAgendaGroup:", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(agendaHeaders);
                foreach (var header in agendaHeaders)
                    Assert.InRange(Math.Abs(Bounds(header, surface).Left - agendaListBounds.Left), 0, 1);
                if (detailColumns == 1)
                    Assert.InRange(Math.Abs(Bounds(Find(surface, "AutomationEarlierHeading"), surface).Left - agendaListBounds.Left), 0, 1);
                var attention = Find(surface, "AutomationsAttentionLayout");
                Assert.Equal(detailColumns, attention is Grid attentionColumns ? attentionColumns.ColumnDefinitions.Count : 1);
                if (detailColumns == 1)
                {
                    var section = Assert.IsType<StackPanel>(Find(surface, "AutomationsAttention"));
                    var heading = Assert.IsAssignableFrom<FrameworkElement>(section.Children[0]);
                    var headingBounds = Bounds(heading, surface);
                    var contentWidth = Find(surface, "AutomationsMast").ActualWidth;
                    var cards = Controls(attention).Where(element =>
                        AutomationProperties.GetAutomationId(element).StartsWith("AutomationAttention:", StringComparison.Ordinal)).ToArray();
                    Assert.NotEmpty(cards);
                    foreach (var card in cards)
                    {
                        var cardBounds = Bounds(card, surface);
                        Assert.InRange(Math.Abs(cardBounds.Left - headingBounds.Left), 0, 1);
                        Assert.InRange(Math.Abs(cardBounds.Width - contentWidth), 0, 1);
                    }
                }
                var actions = Assert.IsType<StackPanel>(Find(surface, "AutomationsHeaderActions"));
                Assert.Equal(Orientation.Horizontal, actions.Orientation);
                var actionBounds = actions.Children.OfType<FrameworkElement>()
                    .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0)
                    .Select(element => Bounds(element, surface)).ToArray();
                for (var index = 1; index < actionBounds.Length; index++)
                {
                    Assert.True(actionBounds[index].Left >= actionBounds[index - 1].Right - 1);
                    Assert.InRange(Math.Abs(actionBounds[index].Top + actionBounds[index].Height / 2
                        - actionBounds[0].Top - actionBounds[0].Height / 2), 0, 1);
                }
                foreach (var id in new[] { "AutomationsMast", "AutomationsFilters", "AutomationRow:morning-brief", "AutomationsHeaderActions" })
                {
                    var bounds = Bounds(Find(surface, id), surface);
                    Assert.True(bounds.Left >= -1 && bounds.Right <= width + 1, $"{id}: {bounds} outside host {width}.");
                }
                foreach (var title in Controls(surface).OfType<TextBlock>().Where(text =>
                    AutomationProperties.GetAutomationId(text).StartsWith("CronJobTitle:", StringComparison.Ordinal)))
                {
                    Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
                    Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                    Assert.NotNull(ToolTipService.GetToolTip(title));
                }
                foreach (var text in Controls(surface).OfType<TextBlock>().Where(text =>
                    text.IsLoaded && text.ActualWidth > 0 && text.ActualHeight > 0 && text.Visibility == Visibility.Visible))
                {
                    if (text.IsTextTrimmed) Assert.NotEqual(TextTrimming.None, text.TextTrimming);
                }
                Assert.True(Assert.IsType<CommandBar>(Find(surface, "CronCommands:morning-brief")).IsDynamicOverflowEnabled);
                var webCommand = Assert.Single(Assert.IsType<CommandBar>(Find(surface, "CronMore:morning-brief"))
                    .SecondaryCommands.OfType<AppBarButton>(), command => command.Label == "Open in web");
                Assert.Equal("\uE8A7", Assert.IsType<FontIcon>(webCommand.Icon).Glyph);
                var meta = Find(surface, "AutomationRowMeta:morning-brief");
                Assert.Equal(width < 720 ? 1 : 0, Microsoft.UI.Xaml.Controls.Grid.GetRow(meta));
                Assert.True(Find(surface, "AutomationsHeadline") is TextBlock { MaxLines: 2, TextTrimming: TextTrimming.CharacterEllipsis });
                Assert.Equal(width < 1000, Controls(surface).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationsHeaderMore"));
                if (capture)
                {
                    Assert.True(PageResponsiveLayout.Snap(surface.XamlRoot.Size.Width) >= width, "The native client crops the required page-host width.");
                    Assert.True(PageResponsiveLayout.Snap(surface.XamlRoot.Size.Height) >= PageResponsiveLayout.Snap(surface.ActualHeight),
                        "The native client crops the required page-host height.");
                }
            });
            if (capture)
            {
                OwnedWindowCapture.SaveCurrentProcessWindow(proofHandle, runId, artifacts, $"automations-responsive-{width}-Light-details.png");
                await ui.RunOnUIAsync(() => Assert.IsType<ScrollView>(Find(surface!, "AutomationsList"))
                    .ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)));
                await ui.YieldToRenderAsync();
                OwnedWindowCapture.SaveCurrentProcessWindow(proofHandle, runId, artifacts, $"automations-responsive-{width}-Light.png");
            }
            await AssertViewportBackdropAsync(surface!, width, capture
                ? name => OwnedWindowCapture.SaveCurrentProcessWindow(proofHandle, runId, artifacts, name)
                : null);
            if (width == 1400)
            {
                Button? retiredMore = null;
                foreach (var reusedWidth in new double[] { 937, 1008, 754, 737, 1386, 937 })
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        surface!.Width = reusedWidth;
                        var scale = surface.XamlRoot.RasterizationScale;
                        proofWindow!.AppWindow.ResizeClient(new global::Windows.Graphics.SizeInt32(
                            (int)Math.Ceiling(reusedWidth * scale), (int)Math.Ceiling(1100 * scale)));
                    });
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        Math.Abs(Find(surface!, "AutomationsRoot").ActualWidth - reusedWidth) < 1
                        && Math.Abs(Find(surface!, "AutomationsMast").ActualWidth - PageResponsiveLayout.ContentWidth(reusedWidth)) < 1,
                        "same mounted page resized without replacing its element pool"));
                    await ui.YieldToRenderAsync();
                    await ui.RunOnUIAsync(() =>
                    {
                        AssertTextAlignment(surface!);
                        AssertNewButton(surface!, artifacts, reusedWidth);
                        var more = Controls(surface!).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == "AutomationsHeaderMore") as Button;
                        if (more is not null) retiredMore = more;
                        else if (retiredMore is not null) Assert.Null(retiredMore.Flyout);
                    });
                }
                await ui.RunOnUIAsync(() => store!.SetAgentFilter("research"));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationsEmptyNew"),
                    "same populated page enters the empty state"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Empty(store!.State.Jobs);
                    var empty = Find(surface!, "AutomationsEmpty");
                    Assert.Equal(2, Controls(empty).OfType<Button>().Count());
                    Assert.Contains(Controls(empty).OfType<TextBlock>(), label =>
                        label.Text == "Automations run an agent on a schedule. Create one here, or describe it in chat.");
                    Assert.DoesNotContain(Controls(surface!), element =>
                        AutomationProperties.GetAutomationId(element).Contains("Suggestion", StringComparison.Ordinal)
                        || AutomationProperties.GetAutomationId(element).StartsWith("AutomationCard:", StringComparison.Ordinal));
                    AssertNewButton(surface!, artifacts, 937, "AutomationsEmptyNew");
                    AssertNewButton(surface!, artifacts, 937);
                    store!.SetAgentFilter("main");
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationRow:morning-brief"),
                    "same page returns from empty to populated"));
                await ui.RunOnUIAsync(() => AssertNewButton(surface!, artifacts, 937));
            }
        }
        catch (Exception failure)
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, "mounted-failure.txt"), failure.ToString());
            throw;
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) { ui.Container.Children.Remove(surface); surface.Child = null; }
                if (proofWindow is not null) { proofWindow.Content = null; proofWindow.Close(); }
                renderer?.Dispose();
                store?.Dispose();
            });
            await client.DisconnectAsync();
            Directory.Delete(identity, recursive: true);
            if (!capture && !Directory.EnumerateFileSystemEntries(artifacts).Any()) Directory.Delete(artifacts);
        }
    }

    private void RequireProductionResources() =>
        Assert.True(ui.UsesProductionResources,
            $"Run Category=ProductionResources in its own test process with {TestAppResourceLoader.ProfileVariable}=Production.");

    /// <summary>The accent wash is a root layer behind the ScrollView: content scrolls, the wash stays put.</summary>
    private async Task AssertViewportBackdropAsync(Border surface, double width, Action<string>? save)
    {
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            Math.Abs(Find(surface, "AutomationsBackdrop").ActualHeight - HeaderBand(surface).ActualHeight) < 1,
            "viewport backdrop matches the header band",
            () => $"backdrop={Find(surface, "AutomationsBackdrop").ActualHeight}; band={HeaderBand(surface).ActualHeight}"));
        await ui.RunOnUIAsync(() =>
        {
            var root = Assert.IsAssignableFrom<Panel>(Find(surface, "AutomationsRoot"));
            var backdrop = Find(surface, "AutomationsBackdrop");
            var scroll = Assert.IsType<ScrollView>(Find(surface, "AutomationsList"));
            Assert.Same(root, VisualTreeHelper.GetParent(backdrop));
            Assert.InRange(root.Children.IndexOf(backdrop), 0, root.Children.IndexOf(scroll) - 1);
            Assert.DoesNotContain(backdrop, Controls(scroll));
            Assert.False(backdrop.IsHitTestVisible);
            Assert.Equal(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(backdrop));
        });
        var atTop = await CaptureViewportFrameAsync(surface, 0, save, $"automations-viewport-{width}-Light-top.png");
        Assert.InRange(Math.Abs(atTop.Offset), 0, 0.5);
        Assert.InRange(Math.Abs(atTop.Backdrop.Top), 0, 1);
        Assert.InRange(Math.Abs(atTop.Backdrop.Left), 0, 1);
        Assert.InRange(Math.Abs(atTop.Backdrop.Width - width), 0, 1);
        Assert.InRange(Math.Abs(atTop.Backdrop.Height - atTop.BandHeight), 0, 1);
        Assert.True(atTop.ScrollableHeight > 100, $"The mounted page must scroll; {atTop}.");
        var offset = Math.Min(240, atTop.ScrollableHeight);
        var scrolled = await CaptureViewportFrameAsync(surface, offset, save, $"automations-viewport-{width}-Light-scrolled.png");
        Assert.InRange(Math.Abs(scrolled.Offset - offset), 0, 0.5);
        Assert.InRange(Math.Abs(scrolled.Backdrop.Top - atTop.Backdrop.Top), 0, 0.5);
        Assert.InRange(Math.Abs(scrolled.Backdrop.Left - atTop.Backdrop.Left), 0, 0.5);
        Assert.InRange(Math.Abs(scrolled.Backdrop.Width - atTop.Backdrop.Width), 0, 0.5);
        Assert.InRange(Math.Abs(scrolled.Backdrop.Height - atTop.Backdrop.Height), 0, 0.5);
        Assert.True(Math.Abs(atTop.Mast.Top - offset - scrolled.Mast.Top) <= 1,
            $"Header must scroll normally: {atTop} -> {scrolled}.");
        await ScrollPageAsync(surface, 0);
    }

    private readonly record struct ViewportFrame(double Offset, double ScrollableHeight,
        global::Windows.Foundation.Rect Backdrop, global::Windows.Foundation.Rect Mast, double BandHeight, int ScrollRequests);

    /// <summary>
    /// Measures and captures one rendered viewport frame. A screenshot is kept only when the page is
    /// unchanged after the capture, so a late page scroll cannot pair an image with another frame.
    /// </summary>
    private async Task<ViewportFrame> CaptureViewportFrameAsync(Border surface, double offset, Action<string>? save, string name)
    {
        const int attempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            var frame = await ScrollPageAsync(surface, offset);
            if (save is null) return frame;
            save(name);
            var after = await ui.RunOnUIAsync(() => Task.FromResult(ReadViewportFrame(surface, frame.ScrollRequests)));
            if (Math.Abs(after.Offset - frame.Offset) < 0.5 && Math.Abs(after.Mast.Top - frame.Mast.Top) < 0.5) return frame;
            if (attempt == attempts)
                Assert.Fail($"The page moved while capturing {name}: {frame} -> {after}.");
        }
    }

    /// <summary>
    /// Scrolls the page and returns the frame measured in the same render callback that proves the
    /// requested offset and a steady header. The page's once-per-selection reveal can land after this
    /// request, so a moved offset re-requests the target instead of measuring a stale frame.
    /// </summary>
    private async Task<ViewportFrame> ScrollPageAsync(Border surface, double offset)
    {
        const int framesBeforeRetry = 5;
        var options = new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
        var requests = 0;
        var framesSinceRequest = 0;
        var reached = false;
        double? lastTop = null;
        var steadyFrames = 0;
        ViewportFrame? measured = null;
        await ui.RunOnUIAsync(() =>
        {
            var scroll = Assert.IsType<ScrollView>(Find(surface, "AutomationsList"));
            void Request()
            {
                scroll.ScrollTo(0, offset, options);
                requests++;
                framesSinceRequest = 0;
            }
            Request();
            return WaitForRenderedConditionAsync(() =>
            {
                if (measured is not null) return true;
                if (Math.Abs(scroll.VerticalOffset - offset) >= 0.5)
                {
                    if (reached || ++framesSinceRequest >= framesBeforeRetry) Request();
                    reached = false;
                    lastTop = null;
                    steadyFrames = 0;
                    return false;
                }
                reached = true;
                var top = Bounds(Find(surface, "AutomationsMast"), surface).Top;
                steadyFrames = lastTop is { } previous && Math.Abs(previous - top) < 0.01 ? steadyFrames + 1 : 0;
                lastTop = top;
                if (steadyFrames < SteadyScrollFrames) return false;
                measured = ReadViewportFrame(surface, requests);
                return true;
            }, $"page rendered steadily at offset {offset}",
            () => $"offset={scroll.VerticalOffset}; mastTop={lastTop}; steadyFrames={steadyFrames}; requests={requests}");
        });
        return measured!.Value;
    }

    private static ViewportFrame ReadViewportFrame(Border surface, int scrollRequests)
    {
        var scroll = Assert.IsType<ScrollView>(Find(surface, "AutomationsList"));
        return new(scroll.VerticalOffset, scroll.ScrollableHeight, Bounds(Find(surface, "AutomationsBackdrop"), surface),
            Bounds(Find(surface, "AutomationsMast"), surface), HeaderBand(surface).ActualHeight, scrollRequests);
    }

    private static FrameworkElement HeaderBand(Border surface) =>
        Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(Find(surface, "AutomationsMast")));

    private static FrameworkElement Find(DependencyObject root, string id) =>
        Assert.Single(Controls(root), element => AutomationProperties.GetAutomationId(element) == id);

    // Rendered labels are the Gateway field names: schedule, payload message, delivery, delivery.mode, sessionTarget and run summary.
    private static void AssertGatewayFieldLabels(FrameworkElement detail)
    {
        Assert.Equal("Schedule", SectionTitle(Find(detail, "AutomationScheduleSummary")));
        Assert.Equal("Prompt", SectionTitle(Find(detail, "AutomationPrompt")));
        Assert.Equal("Delivery", SectionTitle(Find(detail, "AutomationFactDeliveryMode")));
        Assert.Equal("Session target", FactLabel(Find(detail, "AutomationFactSession")));
        Assert.Equal("Mode", FactLabel(Find(detail, "AutomationFactDeliveryMode")));
        var summary = Assert.IsType<Expander>(Controls(detail).First(element =>
            AutomationProperties.GetAutomationId(element).StartsWith("AutomationRunExpand:", StringComparison.Ordinal)));
        Assert.Equal("Summary", summary.Header);
        Assert.Equal("Summary", AutomationProperties.GetName(summary));
    }

    private static string SectionTitle(FrameworkElement member)
    {
        DependencyObject child = member;
        for (var node = VisualTreeHelper.GetParent(member); node is not null; child = node, node = VisualTreeHelper.GetParent(node))
            if (node is StackPanel { Children.Count: > 1 } section && section.Children[0] is TextBlock { FontSize: 16 } title
                && !ReferenceEquals(title, child))
                return title.Text;
        throw new Xunit.Sdk.XunitException($"{AutomationProperties.GetAutomationId(member)} has no section heading.");
    }

    private static string FactLabel(FrameworkElement value) =>
        Assert.Single(Assert.IsType<Grid>(VisualTreeHelper.GetParent(value)).Children.OfType<TextBlock>(),
            label => !ReferenceEquals(label, value)).Text;

    private static void AssertTextAlignment(Border surface)
    {
        Assert.InRange(Math.Abs(Bounds(Find(surface, "AutomationsAgentScope"), surface).Left
            - Bounds(Find(surface, "AutomationsHeadline"), surface).Left), 0, 1);
        foreach (var title in Controls(surface).Where(element =>
            AutomationProperties.GetAutomationId(element).StartsWith("AutomationAttentionTitle:", StringComparison.Ordinal)))
        {
            var id = AutomationProperties.GetAutomationId(title)["AutomationAttentionTitle:".Length..];
            Assert.InRange(Math.Abs(Bounds(Find(surface, $"AutomationAttentionBody:{id}"), surface).Left
                - Bounds(title, surface).Left), 0, 1);
        }

    }

    // Measures arranged card edges, so a margin added on top of the panel spacing fails as a doubled gap.
    private static void AssertSeparatedInventoryCards(Border surface, string artifacts, double width)
    {
        var reference = Assert.IsType<Border>(Controls(surface).First(element =>
            AutomationProperties.GetAutomationId(element).StartsWith("AutomationAttention:", StringComparison.Ordinal)));
        var cardBackground = Assert.IsType<SolidColorBrush>(reference.Background).Color;
        var cardStroke = Assert.IsType<SolidColorBrush>(reference.BorderBrush).Color;
        var measured = new List<(string List, string Above, string Below, double AboveHeight, double Gap)>();
        foreach (var listId in new[] { "AutomationsScheduledList", "AutomationsPausedList" })
        {
            var list = Find(surface, listId);
            var listBounds = Bounds(list, surface);
            var cards = Controls(list)
                .Where(element => AutomationProperties.GetAutomationId(element).StartsWith("AutomationCard:", StringComparison.Ordinal))
                .Select(element => (Card: Assert.IsType<Border>(element), Bounds: Bounds(element, surface)))
                .OrderBy(card => card.Bounds.Top).ToArray();
            Assert.True(cards.Length >= 2, $"{listId} needs adjacent cards to prove separation; found {cards.Length}.");
            foreach (var (card, bounds) in cards)
            {
                var id = AutomationProperties.GetAutomationId(card)["AutomationCard:".Length..];
                Assert.Equal(new CornerRadius(AutomationsVisuals.CardRadius), card.CornerRadius);
                Assert.Equal(new Thickness(1), card.BorderThickness);
                Assert.Equal(cardBackground, Assert.IsType<SolidColorBrush>(card.Background).Color);
                Assert.Equal(cardStroke, Assert.IsType<SolidColorBrush>(card.BorderBrush).Color);
                Assert.InRange(Math.Abs(bounds.Left - listBounds.Left), 0, 1);
                Assert.InRange(Math.Abs(bounds.Width - listBounds.Width), 0, 1);
                Assert.Contains(Controls(card), element => AutomationProperties.GetAutomationId(element) == $"AutomationRow:{id}");
                Assert.Equal(id == "morning-brief",
                    Controls(card).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationDetailColumns"));
            }
            for (var index = 1; index < cards.Length; index++)
                measured.Add((listId, AutomationProperties.GetAutomationId(cards[index - 1].Card),
                    AutomationProperties.GetAutomationId(cards[index].Card), cards[index - 1].Bounds.Height,
                    cards[index].Bounds.Top - cards[index - 1].Bounds.Bottom));
        }
        File.WriteAllText(Path.Combine(artifacts, $"automations-card-gaps-{width}.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                width,
                expected = PageResponsiveLayout.GapInner,
                pairs = measured.Select(pair => new { list = pair.List, above = pair.Above, below = pair.Below, aboveHeight = pair.AboveHeight, gap = pair.Gap }),
            }));
        foreach (var pair in measured)
            Assert.True(Math.Abs(pair.Gap - PageResponsiveLayout.GapInner) <= 0.5, $"{pair.Above} -> {pair.Below}: gap {pair.Gap}.");
        Assert.Contains(measured, pair => pair.Above == "AutomationCard:morning-brief" || pair.Below == "AutomationCard:morning-brief");
    }

    private static void AssertNewButton(Border surface, string artifacts, double width, string id = "AutomationsNew")
    {
        var button = Assert.IsType<Button>(Find(surface, id));
        var accent = Assert.IsType<SolidColorBrush>(Application.Current.Resources["AccentFillColorDefaultBrush"]);
        var background = Assert.IsType<SolidColorBrush>(button.Background);
        var glyph = Assert.Single(Controls(button).OfType<TextBlock>(), text => text.Text == "\uE710");
        File.WriteAllText(Path.Combine(artifacts, $"new-button-style-{id}-{width}.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            width,
            styleMatchesAccent = ReferenceEquals(button.Style, Application.Current.Resources["AccentButtonStyle"]),
            background = background.Color.ToString(),
            expectedAccent = accent.Color.ToString(),
            localBackground = button.ReadLocalValue(Control.BackgroundProperty)?.ToString(),
            localBackgroundUnset = ReferenceEquals(button.ReadLocalValue(Control.BackgroundProperty), DependencyProperty.UnsetValue),
            glyphForeground = Assert.IsType<SolidColorBrush>(glyph.Foreground).Color.ToString(),
        }));
        Assert.Same(Application.Current.Resources["AccentButtonStyle"], button.Style);
        Assert.Equal(accent.Color, background.Color);
        Assert.NotEqual(background.Color, Assert.IsType<SolidColorBrush>(glyph.Foreground).Color);
        foreach (var secondaryId in new[] { "AutomationsRefresh", "AutomationsHeaderMore" })
            if (Controls(surface).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == secondaryId) is Button secondary)
            {
                Assert.NotSame(Application.Current.Resources["AccentButtonStyle"], secondary.Style);
                if (secondaryId == "AutomationsHeaderMore")
                {
                    var menu = Assert.IsType<MenuFlyout>(secondary.Flyout);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(menu)));
                    Assert.NotNull(menu.MenuFlyoutPresenterStyle);
                }
            }
    }
    private static IEnumerable<FrameworkElement> Controls(DependencyObject root)
    {
        if (root is FrameworkElement element) yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Controls(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static global::Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new global::Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    public class AdmissionProxy : DispatchProxy
    {
        public IOperatorGatewayClient Client { get; set; } = null!;
        public bool CanManage { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "get_GrantedOperatorScopes"
                ? CanManage ? new[] { "operator.read", "operator.admin" } : new[] { "operator.read" }
                : targetMethod.Invoke(Client, args);
    }
}
