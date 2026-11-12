using System.Xml.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Automations;
using OpenClawTray.Presentation;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class AutomationsResponsiveLayoutTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ToastPersistenceBoundary = TimeSpan.FromSeconds(5);
    // DIPs, not physical pixels. Native fit also depends on the owned HWND's scale and chrome.
    private const int ProofClientHeight = 900;
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
        await Page_UsesHostWidthWithoutOverlappingActionsOrRows(1400, false, 1, 2, true);
        await Page_UsesHostWidthWithoutOverlappingActionsOrRows(1000, false, 1, 2, true);
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
                Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationsNotice"),
                "native informational InfoBar"));
            await ui.RunOnUIAsync(() =>
            {
                var info = Assert.IsType<InfoBar>(Find(surface!, "AutomationsNotice"));
                Assert.Equal(InfoBarSeverity.Informational, info.Severity);
                Assert.False(info.IsClosable);
                renderer!.Mount(_ => Component<AutomationToast, AutomationToastProps>(new(
                    new(AutomationsNoticeKind.RunNotStarted, "Dentist reminder", "FORBIDDEN", "operator.admin") { Id = 2 },
                    text, true, id => dismissed.Add(id))).WithKey("error"));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationsNotice"),
                "persistent error InfoBar"));
            await Task.Delay(ToastPersistenceBoundary);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(dismissed);
                var info = Assert.IsType<InfoBar>(Find(surface!, "AutomationsNotice"));
                Assert.Equal(InfoBarSeverity.Error, info.Severity);
                Assert.True(info.IsClosable);
                var close = Assert.Single(Controls(info).OfType<Button>());
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
    [InlineData(1400, false, 1, 2, true)]
    [InlineData(1000, false, 1, 2, true)]
    [InlineData(760, false, 2, 1, false)]
    [InlineData(759, false, 2, 1, false)]
    [InlineData(937, false, 2, 1, true)]
    [InlineData(520, true, 3, 1, false)]
    [InlineData(380, true, 3, 1, false)]
    [InlineData(379, true, 3, 1, false)]
    public async Task Page_UsesHostWidthWithoutOverlappingActionsOrRows(double width, bool actionsBelowTitle, int filterRows, int detailColumns, bool agendaSideBySide)
        => await RunMountedPageAsync(width, actionsBelowTitle, filterRows, detailColumns, agendaSideBySide, retainActionFocus: false);

    [Fact]
    public async Task Page_RetainsOriginalAddAndParentsAndFocusAcross760560760()
        => await RunMountedPageAsync(760, false, 2, 1, false, retainActionFocus: true);

    private async Task RunMountedPageAsync(double width, bool actionsBelowTitle, int filterRows, int detailColumns, bool agendaSideBySide, bool retainActionFocus)
    {
        RequireProductionResources();
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT.");
        var capture = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1";
        if (retainActionFocus)
            Assert.True(capture, "Actual-page focus proof requires fresh captures with OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS=1.");
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
        global::Windows.Graphics.SizeInt32 requestedClient = default;
        double requestedScale = 0;
        var viewport = new AutomationsViewportState();
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
                    () => { }, _ => { }, _ => { }, _ => true, () => ["main", "ops"])
                { Viewport = viewport };
                renderer = new ReactorHostControl { RequestedTheme = ElementTheme.Light };
                renderer.Mount(_ => Component<AutomationsRoot, AutomationsRootProps>(new(host)));
                surface = new Border
                {
                    Width = width,
                    Height = ProofClientHeight,
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
            if (capture)
            {
                Assert.Equal((uint)Environment.ProcessId, OwnedWindowCapture.ReadCurrentProcessGeometry(proofHandle).ProcessId);
                await NativeKeyboardProof.ActivateAsync(proofHandle);
            }
            // Activate schedules XamlRoot attachment; it is not available in the
            // same dispatcher callback that constructs the new window.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                surface!.IsLoaded && surface.XamlRoot is not null && renderer!.IsLoaded,
                "mounted surface attached to its XamlRoot"));
            await ui.RunOnUIAsync(() =>
            {
                requestedScale = surface!.XamlRoot.RasterizationScale;
                requestedClient = new global::Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(width * requestedScale), (int)Math.Ceiling(ProofClientHeight * requestedScale));
                proofWindow!.AppWindow.ResizeClient(requestedClient);
            });
            await ui.RunOnUIAsync(async () =>
            {
                await WaitForRenderedConditionAsync(() =>
                    PageResponsiveLayout.Snap(surface!.XamlRoot.Size.Width) >= width
                    && PageResponsiveLayout.Snap(surface.XamlRoot.Size.Height) >= ProofClientHeight,
                    "owned client contains the required page-host viewport",
                    () => RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, "viewport-timeout"));
                RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, "viewport-ready");
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationDetailColumns")
                && Math.Abs(Find(surface!, "AutomationsRoot").ActualWidth - width) < 1
                && Find(surface!, "AutomationsFilters") is Grid filters && filters.RowDefinitions.Count == filterRows
                && Math.Abs(Find(surface!, "AutomationsMast").ActualWidth - PageResponsiveLayout.ContentWidth(width)) < 1
                && Bounds(Find(surface!, "AutomationsMast"), surface!).Left >= -1
                && Bounds(Find(surface!, "AutomationsMast"), surface!).Right <= width + 1,
                "measured page host and completed content arrangement",
                () => $"host={Find(surface!, "AutomationsRoot").ActualWidth}; mast={Bounds(Find(surface!, "AutomationsMast"), surface!)}; requested={width}"));
            if (retainActionFocus)
            {
                Button? originalAdd = null;
                DependencyObject? originalAddParent = null;
                Grid? originalActionHost = null;
                Grid? originalContent = null;
                await ui.RunOnUIAsync(() =>
                {
                    originalAdd = Assert.IsType<Button>(Assert.Single(Controls(surface!), element =>
                        AutomationProperties.GetAutomationId(element) == "AutomationsNew"));
                    originalAddParent = VisualTreeHelper.GetParent(originalAdd);
                    Assert.Same(Find(surface!, "AutomationsHeaderActions"), originalAddParent);
                    originalActionHost = Assert.IsType<Grid>(VisualTreeHelper.GetParent(originalAddParent));
                    originalContent = Assert.IsType<Grid>(Find(surface!, "AutomationsMastGrid"));
                    Assert.Same(originalContent, VisualTreeHelper.GetParent(originalActionHost));
                    Assert.True(originalAdd.Focus(FocusState.Programmatic));
                    Assert.Same(originalAdd, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface!.XamlRoot));
                });
                foreach (var focusWidth in new[] { 760, 560, 760 })
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        surface!.Width = focusWidth;
                        requestedScale = surface.XamlRoot.RasterizationScale;
                        requestedClient = new global::Windows.Graphics.SizeInt32(
                            (int)Math.Ceiling(focusWidth * requestedScale),
                            (int)Math.Ceiling(ProofClientHeight * requestedScale));
                        proofWindow!.AppWindow.ResizeClient(requestedClient);
                    });
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        Math.Abs(Find(surface!, "AutomationsRoot").ActualWidth - focusWidth) < 1
                        && Math.Abs(Find(surface!, "AutomationsMast").ActualWidth -
                            PageResponsiveLayout.ContentWidth(focusWidth)) < 1,
                        "same reactive page focus viewport settled",
                        () => RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, "focus-timeout")));
                    await ui.YieldToRenderAsync();
                    await ui.RunOnUIAsync(() =>
                    {
                        var add = Assert.IsType<Button>(Assert.Single(Controls(surface!), element =>
                            AutomationProperties.GetAutomationId(element) == "AutomationsNew"));
                        Assert.Same(originalAdd, add);
                        Assert.True(add.IsLoaded);
                        Assert.Same(originalAddParent, VisualTreeHelper.GetParent(add));
                        Assert.Same(originalActionHost, VisualTreeHelper.GetParent(originalAddParent));
                        Assert.Same(originalContent, Find(surface!, "AutomationsMastGrid"));
                        Assert.Same(originalContent, VisualTreeHelper.GetParent(originalActionHost));
                        Assert.Same(originalAdd, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface!.XamlRoot));
                        var narrow = focusWidth == 560;
                        Assert.Equal(narrow ? 1 : 0, Microsoft.UI.Xaml.Controls.Grid.GetRow(originalActionHost));
                        Assert.Equal(narrow ? 1 : 2, Microsoft.UI.Xaml.Controls.Grid.GetColumn(originalActionHost));
                        Assert.Equal(narrow ? 2 : 1, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(originalActionHost));
                        var glyph = Assert.IsType<Border>(Find(surface!, "AutomationsMast:Glyph"));
                        var title = Assert.IsType<TextBlock>(Find(surface!, "AutomationsHeadline"));
                        var copy = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(title));
                        Assert.Same(originalContent, VisualTreeHelper.GetParent(copy));
                        var glyphBounds = Bounds(glyph, originalContent!);
                        var copyBounds = Bounds(copy, originalContent!);
                        var actionBounds = Bounds(originalActionHost!, originalContent!);
                        Assert.InRange(copyBounds.Left - glyphBounds.Right, 15.5, 16.5);
                        if (narrow)
                        {
                            Assert.InRange(actionBounds.Left - copyBounds.Left, -0.5, 0.5);
                            Assert.InRange(actionBounds.Top - copyBounds.Bottom, 11.5, 12.5);
                        }
                        RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, $"focus-{focusWidth}");
                        File.AppendAllText(Path.Combine(artifacts, "focus-retention.jsonl"), JsonSerializer.Serialize(new
                        {
                            runId, width = focusWidth, timestampUtc = DateTimeOffset.UtcNow,
                            sameAdd = ReferenceEquals(originalAdd, add),
                            sameAddParent = ReferenceEquals(originalAddParent, VisualTreeHelper.GetParent(add)),
                            sameActionHost = ReferenceEquals(originalActionHost, VisualTreeHelper.GetParent(originalAddParent)),
                            sameContentGrid = ReferenceEquals(originalContent, VisualTreeHelper.GetParent(originalActionHost)),
                            sameFocusedObject = ReferenceEquals(originalAdd, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot)),
                            glyphBounds, copyBounds, actionBounds,
                            captureLimit = "Owned window capture is not compositor acceptance."
                        }) + Environment.NewLine);
                        OwnedWindowCapture.SaveCurrentProcessWindow(proofHandle, runId, artifacts,
                            $"automations-focus-{focusWidth}-{(narrow ? "narrow" : "wide")}-{Guid.NewGuid():N}.png");
                    });
                }
            }
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
                Controls(Find(surface!, "AutomationDetail")).Any(element => element is HyperlinkButton
                    && AutomationProperties.GetAutomationId(element).StartsWith("AutomationRunExpand:", StringComparison.Ordinal))
                && Find(surface!, "AutomationsSummaryTiles") is Grid,
                "inline run summary and shared page tiles"));
            await ui.YieldToRenderAsync();
            ScrollView? retainedScroll = null;
            FrameworkElement? retainedMast = null;
            double originalMastTop = 0;
            double originalSurfaceWidth = 0;
            await ui.RunOnUIAsync(() =>
            {
                retainedScroll = Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker"));
                var markerPeer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(retainedScroll)
                    ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(retainedScroll);
                Assert.NotNull(markerPeer);
                Assert.Equal(catalog["Automations_Title"], markerPeer.GetName());
                Assert.True(markerPeer.IsControlElement());
                // ScrollView is the named container; its loaded ScrollPresenter owns scrolling.
                // External fixture proof exercises that part through the native UIA ScrollPattern.
                var presenter = retainedScroll.ScrollPresenter;
                Assert.NotNull(presenter);
                Assert.True(presenter.IsLoaded && presenter.ActualWidth > 0 && presenter.ActualHeight > 0);
                Assert.Same(retainedScroll.XamlRoot, presenter.XamlRoot);
                Assert.Same(retainedScroll.Content, presenter.Content);
                retainedMast = Find(surface!, "AutomationsMast");
                originalMastTop = Bounds(retainedMast, surface!).Top;
                originalSurfaceWidth = surface!.ActualWidth;
                viewport.ApplyTopInset(56);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker")).Margin.Top == 56
                && Math.Abs(Bounds(Find(surface!, "AutomationsMast"), surface!).Top - originalMastTop - 56) < 1,
                "inset-only change at constant width updates the retained scroll content"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(retainedScroll, Find(surface!, "CronPageMarker"));
                Assert.Same(retainedMast, Find(surface!, "AutomationsMast"));
                AssertWithinOnePhysicalPixel(surface!.ActualWidth, originalSurfaceWidth, surface.XamlRoot.RasterizationScale);
                Assert.True(Bounds(retainedMast!, surface).Top >= 56);
                viewport.ApplyTopInset(0);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker")).Margin.Top == 0,
                "removing inset restores the retained page viewport"));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var header = Bounds(Find(surface!, "AutomationRowHeader:morning-brief"), surface!);
                var chat = Bounds(Find(surface!, "AutomationRowChat:morning-brief"), surface!);
                return Math.Abs(chat.Top + chat.Height / 2 - header.Top - header.Height / 2) <= 1;
            }, "measured chat placement centered on its header"));
            await ui.RunOnUIAsync(() =>
            {
                surface!.UpdateLayout();
                AssertGatewayFieldLabels(Find(surface, "AutomationDetail"));
                AssertNewButton(surface, artifacts, width);
                var mastBounds = Bounds(Find(surface, "AutomationsMast"), surface);
                var headerActions = Bounds(Find(surface, "AutomationsHeaderActions"), surface);
                var headerText = new[] { "AutomationsHeadline", "AutomationsHeroAnswer", "AutomationsAgentScope" }
                    .Select(id => Bounds(Find(surface, id), surface)).ToArray();
                var pixel = 1 / surface.XamlRoot.RasterizationScale;
                foreach (var bounds in headerText.Append(headerActions))
                {
                    Assert.True(bounds.Width > 0 && bounds.Height > 0);
                    Assert.True(bounds.Left >= mastBounds.Left - pixel && bounds.Right <= mastBounds.Right + pixel
                        && bounds.Top >= mastBounds.Top - pixel && bounds.Bottom <= mastBounds.Bottom + pixel,
                        $"Header content {bounds} must fit inside {mastBounds}.");
                }
                for (var index = 1; index < headerText.Length; index++)
                    Assert.True(headerText[index].Top >= headerText[index - 1].Bottom - pixel);
                if (actionsBelowTitle)
                {
                    Assert.True(headerActions.Top >= headerText.Max(bounds => bounds.Bottom) - pixel);
                    AssertWithinOnePhysicalPixel(headerActions.Left, headerText[0].Left, surface.XamlRoot.RasterizationScale);
                }
                else
                    Assert.True(headerActions.Left >= headerText.Max(bounds => bounds.Right) - pixel);
                Assert.DoesNotContain(Controls(surface), element =>
                    AutomationProperties.GetAutomationId(element) is "AutomationsBackdrop" or "AutomationsAttention"
                    || AutomationProperties.GetAutomationId(element).StartsWith("AutomationAttention:", StringComparison.Ordinal));
                Assert.InRange(Math.Abs(Find(surface, "AutomationsMast").ActualWidth
                    - PageResponsiveLayout.ContentWidth(width)), 0, 1);
                Assert.InRange(Math.Abs(Bounds(Find(surface, "AutomationsAgentScope"), surface).Left
                    - Bounds(Find(surface, "AutomationsHeadline"), surface).Left), 0, 1);
                AssertTextAlignment(surface);
                AssertSeparatedInventoryRows(surface, artifacts, width);
                if (actionsBelowTitle)
                    Assert.True(Find(surface, "AutomationRowHeader:morning-brief").ActualHeight > 48,
                        "Narrow coverage must exercise a header taller than the stock collapsed minimum.");
                var filters = Assert.IsType<Grid>(Find(surface, "AutomationsFilters"));
                Assert.Equal(filterRows, filters.RowDefinitions.Count);
                AssertSearchFilterGeometry(surface, filterRows);
                var detail = Find(surface, "AutomationDetailColumns");
                Assert.Equal(detailColumns, detail is Grid columns ? columns.ColumnDefinitions.Count : 1);
                var agendaListBounds = Bounds(Find(surface, "AutomationsAgendaList"), surface);
                var earlierHeading = Bounds(Find(surface, "AutomationEarlierHeading"), surface);
                if (agendaSideBySide)
                {
                    Assert.True(earlierHeading.Left >= agendaListBounds.Right - pixel);
                    Assert.True(earlierHeading.Top < agendaListBounds.Bottom && earlierHeading.Bottom > agendaListBounds.Top);
                }
                else
                {
                    AssertWithinOnePhysicalPixel(earlierHeading.Left, agendaListBounds.Left, surface.XamlRoot.RasterizationScale);
                    Assert.True(earlierHeading.Top >= agendaListBounds.Bottom - pixel);
                }
                var agendaHeaders = Controls(surface).Where(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("AutomationAgendaGroup:", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(agendaHeaders);
                foreach (var header in agendaHeaders)
                    Assert.InRange(Math.Abs(Bounds(header, surface).Left - agendaListBounds.Left), 0, 1);
                var tiles = Assert.IsType<Grid>(Find(surface, "AutomationsSummaryTiles"));
                Assert.Equal(width >= 760 ? 4 : width < 380 ? 1 : 2, tiles.ColumnDefinitions.Count);
                Assert.Equal(width >= 760 ? 1 : width < 380 ? 4 : 2, tiles.RowDefinitions.Count);
                var tileBounds = new[]
                {
                    "AutomationsTileScheduler", "AutomationsTileRunning", "AutomationsTileFailing", "AutomationsTilePaused",
                }.Select(id => Bounds(Find(tiles, id), surface)).ToArray();
                Assert.All(tileBounds, bounds => Assert.True(bounds.Left >= -1 && bounds.Right <= width + 1, $"Tile outside host: {bounds}."));
                var failingBadge = Find(surface, "AutomationAttentionBadge:flaky-report");
                var badgeLabel = Assert.Single(Controls(failingBadge).OfType<TextBlock>(), text => text.Text == "Stopped");
                Assert.Equal(TextWrapping.NoWrap, badgeLabel.TextWrapping);
                Assert.Equal(TextTrimming.None, badgeLabel.TextTrimming);
                Assert.False(badgeLabel.IsTextTrimmed);
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
                foreach (var id in new[] { "AutomationsMast", "AutomationsFilters", "AutomationExpander:morning-brief", "AutomationsHeaderActions" })
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
            await AssertRowAllocationAcrossExpansionAsync(surface!);
            OwnedWindowCapture.WindowGeometry? qualifiedCapture = null;
            if (capture && width < 400)
            {
                // The required client and mounted layout were qualified above. Preserve that
                // exact native frame rather than applying the ordinary window-width heuristic.
                qualifiedCapture = OwnedWindowCapture.ReadCurrentProcessGeometry(proofHandle);
                Assert.Equal(requestedClient.Width, qualifiedCapture.ClientPixels.Width);
                Assert.Equal(requestedClient.Height, qualifiedCapture.ClientPixels.Height);
            }
            void CaptureQualifiedViewport(string name) => OwnedWindowCapture.SaveCurrentProcessWindow(
                proofHandle, runId, artifacts, name, qualifiedCapture);
            if (capture)
            {
                CaptureQualifiedViewport($"automations-responsive-{width}-Light-details.png");
                await ui.RunOnUIAsync(() => Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker"))
                    .ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)));
                await ui.YieldToRenderAsync();
                CaptureQualifiedViewport($"automations-responsive-{width}-Light.png");
            }
            await AssertPageScrollAsync(surface!, width, capture ? CaptureQualifiedViewport : null);
            if (capture && (actionsBelowTitle || width == 937))
            {
                var rowOffset = await ui.RunOnUIAsync(() =>
                {
                    var scroll = Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker"));
                    var row = Bounds(Find(surface!, "AutomationExpander:morning-brief"), surface!);
                    return Task.FromResult(Math.Clamp(scroll.VerticalOffset + row.Top - Bounds(scroll, surface!).Top,
                        0, scroll.ScrollableHeight));
                });
                await ScrollPageAsync(surface!, rowOffset);
                await ui.RunOnUIAsync(async () =>
                {
                    var scroll = Assert.IsType<ScrollView>(Find(surface!, "CronPageMarker"));
                    var targets = new[] { "AutomationRowHeader:morning-brief", "AutomationRowChat:morning-brief",
                        "CronCommands:morning-brief", "CronMore:morning-brief", "AutomationScheduleSummary" };
                    await WaitForRenderedConditionAsync(() =>
                    {
                        var viewportBounds = Bounds(scroll, surface!);
                        var pixel = 1 / surface!.XamlRoot.RasterizationScale;
                        return targets.All(id =>
                        {
                            var element = Find(surface, id);
                            var bounds = Bounds(element, surface);
                            return element.IsLoaded && bounds.Width > 0 && bounds.Height > 0
                                && bounds.Left >= viewportBounds.Left - pixel && bounds.Right <= viewportBounds.Right + pixel
                                && bounds.Top >= viewportBounds.Top - pixel && bounds.Bottom <= viewportBounds.Bottom + pixel;
                        });
                    }, "complete Morning brief header, chat and leading detail actions visible");
                    File.WriteAllText(Path.Combine(artifacts, $"automations-header-action-{width}.json"),
                        JsonSerializer.Serialize(new
                        {
                            width,
                            offset = scroll.VerticalOffset,
                            viewport = Bounds(scroll, surface!),
                            headerHeightDip = Find(surface!, "AutomationRowHeader:morning-brief").ActualHeight,
                            targets = targets.Select(id => new { id, bounds = Bounds(Find(surface!, id), surface!) }),
                        }));
                });
                await CaptureViewportFrameAsync(surface!, rowOffset, CaptureQualifiedViewport,
                    $"automations-header-action-{width}-Light.png");
                await ScrollPageAsync(surface!, 0);
            }
            if (width == 1400)
            {
                Button? retiredMore = null;
                foreach (var reusedWidth in new double[] { 937, 1008, 754, 737, 1386, 937 })
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        surface!.Width = reusedWidth;
                        requestedScale = surface.XamlRoot.RasterizationScale;
                        requestedClient = new global::Windows.Graphics.SizeInt32(
                            (int)Math.Ceiling(reusedWidth * requestedScale), (int)Math.Ceiling(ProofClientHeight * requestedScale));
                        proofWindow!.AppWindow.ResizeClient(requestedClient);
                    });
                    await ui.RunOnUIAsync(async () =>
                    {
                        await WaitForRenderedConditionAsync(() =>
                            Math.Abs(Find(surface!, "AutomationsRoot").ActualWidth - reusedWidth) < 1
                            && Math.Abs(Find(surface!, "AutomationsMast").ActualWidth - PageResponsiveLayout.ContentWidth(reusedWidth)) < 1,
                            "same mounted page resized without replacing its element pool",
                            () => RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, "resize-timeout"));
                        RecordViewportGeometry(proofWindow!, surface!, requestedClient, requestedScale, artifacts, "resize-ready");
                    });
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
                Button? emptyNew = null;
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    emptyNew = Controls(surface!).OfType<Button>().SingleOrDefault(element =>
                        AutomationProperties.GetAutomationId(element) == "AutomationsEmptyNew");
                    return emptyNew is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0,
                        Content: FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } }
                        && Controls(emptyNew).OfType<TextBlock>().Any(text => text.IsLoaded && text.ActualWidth > 0);
                }, "same populated page enters the empty state with arranged button content",
                    () => emptyNew is null ? "Empty-state button absent."
                        : $"button loaded={emptyNew.IsLoaded}, size={emptyNew.ActualWidth}x{emptyNew.ActualHeight}; "
                            + $"content={emptyNew.Content?.GetType().Name}; "
                            + $"text visuals={Controls(emptyNew).OfType<TextBlock>().Count()}; "
                            + $"add glyphs={Controls(emptyNew).OfType<TextBlock>().Count(text => text.Text == "\uE710")}"));
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
                    Controls(surface!).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationExpander:morning-brief"),
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

    private string RecordViewportGeometry(Window window, FrameworkElement surface,
        global::Windows.Graphics.SizeInt32 requestedClient, double requestedScale, string artifacts, string phase)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var native = OwnedWindowCapture.ReadCurrentProcessGeometry(handle);
        var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var xamlRoot = surface.XamlRoot;
        var json = JsonSerializer.Serialize(new
        {
            phase,
            timestampUtc = DateTimeOffset.UtcNow,
            requestedDip = new { width = surface.Width, height = surface.Height },
            requestedScale,
            requestedClientPixels = new { requestedClient.Width, requestedClient.Height },
            native,
            monitorWorkAreaPixels = new { work.X, work.Y, work.Width, work.Height },
            xamlRoot = new { xamlRoot.RasterizationScale, xamlRoot.Size.Width, xamlRoot.Size.Height },
            surfaceDip = new { surface.ActualWidth, surface.ActualHeight, surface.IsLoaded },
            window.ExtendsContentIntoTitleBar,
        });
        File.AppendAllText(Path.Combine(artifacts, "viewport-geometry.jsonl"), json + Environment.NewLine);
        output.WriteLine($"automations-viewport: {json}");
        return json;
    }

    private async Task AssertPageScrollAsync(Border surface, double width, Action<string>? save)
    {
        var atTop = await CaptureViewportFrameAsync(surface, 0, save, $"automations-viewport-{width}-Light-top.png");
        Assert.InRange(Math.Abs(atTop.Offset), 0, 0.5);
        Assert.True(atTop.ScrollableHeight > 100, $"The mounted page must scroll; {atTop}.");
        var offset = Math.Min(240, atTop.ScrollableHeight);
        var scrolled = await CaptureViewportFrameAsync(surface, offset, save, $"automations-viewport-{width}-Light-scrolled.png");
        Assert.InRange(Math.Abs(scrolled.Offset - offset), 0, 0.5);
        Assert.Equal(atTop.Viewport, scrolled.Viewport);
        Assert.True(Math.Abs(atTop.Mast.Top - offset - scrolled.Mast.Top) <= 1,
            $"The hero should scroll with the page: {atTop} -> {scrolled}.");
        await ScrollPageAsync(surface, 0);
    }

    private readonly record struct ViewportFrame(double Offset, double ScrollableHeight,
        global::Windows.Foundation.Rect Viewport, global::Windows.Foundation.Rect Mast, int ScrollRequests);

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
            if (after.Viewport == frame.Viewport && Math.Abs(after.Offset - frame.Offset) < 0.5
                && Math.Abs(after.Mast.Top - frame.Mast.Top) < 0.5) return frame;
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
            var scroll = Assert.IsType<ScrollView>(Find(surface, "CronPageMarker"));
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
        var scroll = Assert.IsType<ScrollView>(Find(surface, "CronPageMarker"));
        return new(scroll.VerticalOffset, scroll.ScrollableHeight,
            Bounds(scroll, surface), Bounds(Find(surface, "AutomationsMast"), surface), scrollRequests);
    }

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
        var summary = Assert.IsType<HyperlinkButton>(Controls(detail).First(element =>
            AutomationProperties.GetAutomationId(element).StartsWith("AutomationRunExpand:", StringComparison.Ordinal)));
        Assert.Equal("Summary", summary.Content);
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
    }

    private async Task AssertRowAllocationAcrossExpansionAsync(Border surface)
    {
        (double RowWidth, double ExpanderWidth, double Height, global::Windows.Foundation.Rect Header,
            global::Windows.Foundation.Rect Chat) Read()
        {
            var row = Find(surface, "AutomationCard:morning-brief");
            var expander = Find(surface, "AutomationExpander:morning-brief");
            return (row.ActualWidth, expander.ActualWidth, expander.ActualHeight,
                Bounds(Find(surface, "AutomationRowHeader:morning-brief"), row),
                Bounds(Find(surface, "AutomationRowChat:morning-brief"), row));
        }
        var initial = await ui.RunOnUIAsync(() => Task.FromResult(Read()));
        foreach (var expanded in new[] { false, true })
        {
            await ui.RunOnUIAsync(() => Assert.IsType<Expander>(Find(surface, "AutomationExpander:morning-brief")).IsExpanded = expanded);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                surface.UpdateLayout();
                var current = Read();
                return Assert.IsType<Expander>(Find(surface, "AutomationExpander:morning-brief")).IsExpanded == expanded
                    && (expanded ? Math.Abs(current.Height - initial.Height) < 1 : current.Height < initial.Height);
            }, $"native row allocation after {(expanded ? "expansion" : "collapse")}"));
            await ui.RunOnUIAsync(() =>
            {
                var current = Read();
                var scale = surface.XamlRoot.RasterizationScale;
                AssertWithinOnePhysicalPixel(current.RowWidth, initial.RowWidth, scale);
                AssertWithinOnePhysicalPixel(current.ExpanderWidth, initial.ExpanderWidth, scale);
                foreach (var pair in new[] { (current.Header, initial.Header), (current.Chat, initial.Chat) })
                {
                    AssertWithinOnePhysicalPixel(pair.Item1.Left, pair.Item2.Left, scale);
                    AssertWithinOnePhysicalPixel(pair.Item1.Top, pair.Item2.Top, scale);
                    AssertWithinOnePhysicalPixel(pair.Item1.Width, pair.Item2.Width, scale);
                    AssertWithinOnePhysicalPixel(pair.Item1.Height, pair.Item2.Height, scale);
                }
            });
        }
    }

    // Measures arranged row edges, so a margin added on top of panel spacing fails as a doubled gap.
    private static void AssertSeparatedInventoryRows(Border surface, string artifacts, double width)
    {
        var measured = new List<(string List, string Above, string Below, double AboveHeight, double Gap)>();
        foreach (var listId in new[] { "AutomationsScheduledList", "AutomationsPausedList" })
        {
            var list = Find(surface, listId);
            var listBounds = Bounds(list, surface);
            var rows = Controls(list)
                .Where(element => AutomationProperties.GetAutomationId(element).StartsWith("AutomationCard:", StringComparison.Ordinal))
                .Select(element => (Row: Assert.IsType<Grid>(element), Bounds: Bounds(element, surface)))
                .OrderBy(row => row.Bounds.Top).ToArray();
            Assert.True(rows.Length >= 2, $"{listId} needs adjacent rows to prove separation; found {rows.Length}.");
            double? rightEdge = null;
            foreach (var (row, bounds) in rows)
            {
                var id = AutomationProperties.GetAutomationId(row)["AutomationCard:".Length..];
                Assert.Null(row.Background);
                Assert.InRange(Math.Abs(bounds.Left - listBounds.Left), 0, 1);
                Assert.InRange(Math.Abs(bounds.Width - listBounds.Width), 0, 1);
                var expander = Assert.Single(Controls(row).OfType<Expander>(), element =>
                    AutomationProperties.GetAutomationId(element) == $"AutomationExpander:{id}");
                AssertNoPaintedAncestorBetween(expander, list);
                var chat = Controls(row).FirstOrDefault(element =>
                    AutomationProperties.GetAutomationId(element) == $"AutomationRowChat:{id}");
                var expanderBounds = Bounds(expander, surface);
                var availableRight = bounds.Left + row.ColumnDefinitions[0].ActualWidth;
                var pixel = 1 / surface.XamlRoot.RasterizationScale;
                AssertWithinOnePhysicalPixel(expanderBounds.Left, bounds.Left, surface.XamlRoot.RasterizationScale);
                AssertWithinOnePhysicalPixel(expanderBounds.Right, availableRight, surface.XamlRoot.RasterizationScale);
                if (rightEdge is { } previousRight)
                    AssertWithinOnePhysicalPixel(expanderBounds.Right, previousRight, surface.XamlRoot.RasterizationScale);
                rightEdge = expanderBounds.Right;
                if (chat is not null)
                {
                    Assert.True(Bounds(chat, surface).Left >= expanderBounds.Right - pixel,
                        $"{id} chat action must remain a sibling of its Expander.");
                    var header = Bounds(Find(row, $"AutomationRowHeader:{id}"), surface);
                    var chatBounds = Bounds(chat, surface);
                    Assert.InRange(Math.Abs(chatBounds.Top + chatBounds.Height / 2 - header.Top - header.Height / 2), 0, 1);
                }
                Assert.Equal(id == "morning-brief",
                    Controls(row).Any(element => AutomationProperties.GetAutomationId(element) == "AutomationDetailColumns"));
            }
            for (var index = 1; index < rows.Length; index++)
                measured.Add((listId, AutomationProperties.GetAutomationId(rows[index - 1].Row),
                    AutomationProperties.GetAutomationId(rows[index].Row), rows[index - 1].Bounds.Height,
                    rows[index].Bounds.Top - rows[index - 1].Bounds.Bottom));
        }
        File.WriteAllText(Path.Combine(artifacts, $"automations-row-gaps-{width}.json"),
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

    private static void AssertSearchFilterGeometry(Border surface, int filterRows)
    {
        var filters = Assert.IsType<Grid>(Find(surface, "AutomationsFilters"));
        var status = Assert.IsType<SelectorBar>(Find(surface, "AutomationsStatusFilter"));
        var agent = Assert.IsType<ComboBox>(Find(surface, "AutomationsAgentFilter"));
        var search = Assert.IsType<AutoSuggestBox>(Find(surface, "AutomationsSearch"));
        var scale = surface.XamlRoot!.RasterizationScale;
        Assert.Equal(VerticalAlignment.Center, search.VerticalAlignment);
        Assert.True(search.DesiredSize.Height > 0);
        AssertWithinOnePhysicalPixel(search.ActualHeight,
            search.DesiredSize.Height - search.Margin.Top - search.Margin.Bottom, scale);
        AssertWithinOnePhysicalPixel(search.ActualHeight,
            agent.DesiredSize.Height - agent.Margin.Top - agent.Margin.Bottom, scale);

        var searchBounds = Bounds(search, surface);
        var row = Microsoft.UI.Xaml.Controls.Grid.GetRow(search);
        Assert.Equal(filterRows - 1, row);
        var rowTop = Bounds(filters, surface).Top
            + filters.RowDefinitions.Take(row).Sum(definition => definition.ActualHeight);
        var rowHeight = filters.RowDefinitions[row].ActualHeight;
        var expectedCenter = rowTop + search.Margin.Top
            + (rowHeight - search.Margin.Top - search.Margin.Bottom) / 2;
        AssertWithinOnePhysicalPixel(searchBounds.Top + searchBounds.Height / 2, expectedCenter, scale);

        if (filterRows == 1)
        {
            var searchCenter = searchBounds.Top + searchBounds.Height / 2;
            foreach (var filter in new FrameworkElement[] { status, agent })
            {
                var bounds = Bounds(filter, surface);
                AssertWithinOnePhysicalPixel(searchCenter, bounds.Top + bounds.Height / 2, scale);
            }
        }
    }

    // WinUI exposes float layout coordinates; compare their rendered device-pixel positions.
    private static void AssertWithinOnePhysicalPixel(double actual, double expected, double scale) =>
        Assert.InRange(Math.Abs(Math.Round(actual * scale) - Math.Round(expected * scale)), 0, 1);

    private static void AssertNoPaintedAncestorBetween(DependencyObject child, DependencyObject list)
    {
        var ancestor = VisualTreeHelper.GetParent(child);
        while (ancestor is not null && !ReferenceEquals(ancestor, list))
        {
            if (ancestor is Border border)
            {
                Assert.Null(border.Background);
                Assert.Equal(new Thickness(0), border.BorderThickness);
            }
            if (ancestor is Panel panel) Assert.Null(panel.Background);
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        Assert.Same(list, ancestor);
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
