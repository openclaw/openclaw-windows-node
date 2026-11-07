using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClaw.TestSupport;
using OpenClawTray.Helpers;
using OpenClawTray.Systems;
using Windows.Foundation;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SystemsPageProofTests(UIThreadFixture ui, ITestOutputHelper output)
{

    [Fact]
    public async Task UnknownCapabilities_UseSentenceCaseLabelsInsteadOfRawIds()
    {
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(() =>
            {
                var section = Find<FrameworkElement>(surface, "SystemsCapabilities");
                var labels = TestSupport.FindDescendants<TextBlock>(section).Select(text => text.Text).ToArray();
                Assert.Contains("Files", labels);
                Assert.Contains("Remote files", labels);
                Assert.Contains("Custom sensor", labels);
                Assert.DoesNotContain("files", labels);
                Assert.DoesNotContain("remote-files", labels);
                Assert.DoesNotContain("custom_sensor", labels);
                var files = Assert.Single(TestSupport.FindDescendants<TextBlock>(section), text => text.Text == "Files");
                Assert.Equal("files", ToolTipService.GetToolTip(files));
            }), node: new GatewayNodeInfo
            {
                NodeId = "studio", DisplayName = "Studio", Capabilities = ["files", "remote-files", "custom_sensor"],
            }, width: 1000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryRows_KeepEightDipGapsIncludingExpandedRows(bool expanded)
    {
        var environments = Enumerable.Range(1, 3).Select(index => new SystemEnvironment
        {
            Id = $"node:gap-{index}", Label = $"Gap machine {index}", Type = "node",
            Status = SystemStatus.Online,
        }).ToArray();
        await WithPageAsync(900, controller =>
        {
            if (expanded) controller.Select("node:gap-1");
        }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            var group = Find<StackPanel>(surface, "SystemsDevicesGroup");
            await TestSupport.WaitForRenderedConditionAsync(() => group.Children.OfType<Expander>().Count() == 3 &&
                group.Children.OfType<Expander>().All(card => card.ActualHeight > 0), "three separate native inventory rows rendered");
            Assert.Equal(OpenClawTray.Presentation.PageResponsiveLayout.GapInner, group.Spacing);
            var cards = group.Children.OfType<Expander>().ToArray();
            for (var index = 0; index < cards.Length; index++)
            {
                Assert.Equal(new Thickness(1), cards[index].BorderThickness);
                Assert.Equal(new CornerRadius(8), cards[index].CornerRadius);
                if (index == 0) continue;
                var previous = cards[index - 1].TransformToVisual(group).TransformBounds(
                    new Rect(0, 0, cards[index - 1].ActualWidth, cards[index - 1].ActualHeight));
                var current = cards[index].TransformToVisual(group).TransformBounds(
                    new Rect(0, 0, cards[index].ActualWidth, cards[index].ActualHeight));
                Assert.InRange(Math.Abs(current.Top - previous.Bottom - 8), 0, .5);
            }
            if (expanded)
            {
                Assert.Equal("node:gap-1", controller.State.SelectedId);
                Assert.NotNull(Find<FrameworkElement>(cards[0], "SystemsDetails"));
            }
            Find<Expander>(surface, "SystemsRow_node:gap-2").IsExpanded = true;
            await TestSupport.WaitForRenderedConditionAsync(() => controller.State.SelectedId == "node:gap-2",
                "separated row retains its independent expansion");
        }), inventory: new SystemsInventorySnapshot { Environments = environments }, width: 1000);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1280)]
    public async Task SystemsPage_HasNoConnectionMapAtAnyWidth(double width)
    {
        await WithPageAsync(900, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(() =>
        {
            Assert.DoesNotContain(AutomationIds(surface), id =>
                id.StartsWith("SystemsMap", StringComparison.Ordinal) || id == "SystemsConstellation");
            Assert.NotNull(Find<TextBlock>(surface, "SystemsDevicesAndGatewayHeading"));
        }), width: width);
    }

    [Fact]
    public async Task NeedsYouOverflow_OpensAllIssuesAndKeepsFullInventoryOutsideIssues()
    {
        var environments = new[]
        {
            new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
            new SystemEnvironment { Id = "node:offline", Type = "node", Status = SystemStatus.Offline },
            new SystemEnvironment { Id = "node:update", Type = "node", Status = SystemStatus.Online,
                Issues = [new RuntimeTargetIssue("update-required", null)] },
            new SystemEnvironment { Id = "node:permission", Type = "node", Status = SystemStatus.Online },
            new SystemEnvironment { Id = "worker:build", Type = "worker", Status = SystemStatus.Online },
        };
        var inventory = new SystemsInventorySnapshot
        {
            Environments = environments,
            Nodes =
            [
                new GatewayNodeInfo { NodeId = "offline", DisplayName = "Offline", IsOnline = false },
                new GatewayNodeInfo { NodeId = "update", DisplayName = "Update", IsOnline = true },
                new GatewayNodeInfo { NodeId = "permission", DisplayName = "Permission", IsOnline = true,
                    Permissions = new() { ["camera"] = false } },
            ],
        };
        await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
        {
            Presence = [new PresenceEntry { DeviceId = "browser", Host = "Browser", Mode = "webchat",
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest
                { RequestId = "join", NodeId = "new-device", DisplayName = "New device" })],
        }), async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<Border>(surface, "SystemsNeedsYouCount")?.Child is TextBlock { Text: "4" },
                "all four actionable problems rendered");
            Assert.Equal(SystemsText.F("DevicesAndGatewayCount", "4"),
                Find<TextBlock>(surface, "SystemsDevicesAndGatewayHeading").Text);
            Assert.Equal("4", Assert.IsType<TextBlock>(Find<Border>(surface, "SystemsNeedsYouCount").Child).Text);
            Assert.Equal(2, TestSupport.FindDescendants<Border>(surface).Count(element =>
                AutomationProperties.GetAutomationId(element).StartsWith("SystemsAttention_", StringComparison.Ordinal)));
            var offlineCard = Find<Border>(surface, "SystemsAttention_offline-node:offline");
            Assert.InRange(Find<Border>(offlineCard, "SystemsAttentionStripe").ActualWidth, 3.5, 4.5);
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(Find<Border>(surface, "SystemsNeedsYouMore")),
                text => text.Text == SystemsText.F("NeedsYou_More", "2"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsWorkersGroup"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsConnectedAppsGroup"));
            Invoke(Find<Button>(surface, "SystemsNeedsYouMore_Action"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsIssuesView") is not null,
                "overflow opens the issues page");
            var issueHeading = Find<FrameworkElement>(surface, "SystemsIssuesHeading");
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(issueHeading), text => text.Text == SystemsText.S("Issues"));
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(issueHeading), text => text.Text == "4");
            Assert.Equal(4, TestSupport.FindDescendants<Border>(surface).Count(element =>
                AutomationProperties.GetAutomationId(element).StartsWith("SystemsIssue_", StringComparison.Ordinal)));
            Assert.NotNull(Find<HyperlinkButton>(surface, "SystemsIssuesBack"));
            Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsNeedsYou");
            Assert.DoesNotContain(AutomationIds(surface), id =>
                id is "SystemsWorkersGroup" or "SystemsConnectedAppsGroup");
            Invoke(Find<HyperlinkButton>(surface, "SystemsIssuesBack"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsDevicesAndGatewayHeading") is not null,
                "All devices returns to the complete inventory");
            Assert.Equal(6, AutomationIds(surface).Count(id => id.StartsWith("SystemsRow_", StringComparison.Ordinal)));
        }), inventory: inventory, width: 400);
    }

    [Fact]
    public async Task PendingApprovalWithMatchingInventoryRow_ShowsOneAttentionItem()
    {
        var pendingNode = new GatewayNodeInfo
        {
            NodeId = "pending-device",
            DisplayName = "Pending device",
            IsOnline = true,
            ApprovalState = GatewayNodeApprovalState.PendingApproval,
        };
        var inventory = new SystemsInventorySnapshot
        {
            Environments =
            [
                new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
                new SystemEnvironment
                {
                    Id = "node:pending-device", Type = "node", Status = SystemStatus.Online,
                },
            ],
            Nodes = [pendingNode],
        };
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest
                { RequestId = "pending-request", NodeId = "pending-device", DisplayName = "Pending device" })],
        }), async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TestSupport.FindDescendants<Border>(surface).Count(card =>
                    AutomationProperties.GetAutomationId(card).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal)) == 1,
                "matching row and pairing approval produce one attention card");
            Assert.Equal("1", Assert.IsType<TextBlock>(Find<Border>(surface, "SystemsNeedsYouCount").Child).Text);
            Assert.Single(TestSupport.FindDescendants<Border>(surface), card =>
                AutomationProperties.GetAutomationId(card).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal));
        }), inventory: inventory);
    }

    [Fact]
    public async Task SparseInventory_ShowsNonemptyWorkerAndConnectedAppsGroups()
    {
        var inventory = new SystemsInventorySnapshot
        {
            Environments =
            [
                new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
                new SystemEnvironment
                {
                    Id = "worker:build", Type = "worker", Label = "Build worker",
                    Status = SystemStatus.Online,
                },
            ],
        };
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            Presence = [new PresenceEntry { DeviceId = "browser", Host = "Browser", Mode = "webchat",
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
        }), async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsConnectedAppsGroup") is not null,
                "nonempty connected-app group is shown in sparse inventory");
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsWorkersGroup"));
            Assert.Equal(3, AutomationIds(surface).Count(id => id.StartsWith("SystemsRow_", StringComparison.Ordinal)));
        }), inventory: inventory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalPermissionAttention_OpensPermissionsEvenWhenDisconnected(bool connected)
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "studio", DisplayName = "Studio",
            Permissions = new Dictionary<string, bool> { ["camera"] = false },
        };
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            LocalNodeId = "studio",
        }), async (surface, controller, navigated) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(() => controller.State.HasLoaded,
                "node permissions have loaded");
            controller.UpdateInputs(Inputs() with { LocalNodeId = "studio", Connected = connected });
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<Button>(surface, "SystemsAttention_permission-node:studio-camera_Action") is not null,
                "local permission attention remains available");
            var action = Find<Button>(surface, "SystemsAttention_permission-node:studio-camera_Action");
            Assert.True(action.IsEnabled);
            Invoke(action);
            Assert.Equal(["permissions"], navigated);
        }), node: node);
    }

    [Fact]
    public async Task Retry_ShowsLoadingThenRecoversInventory()
    {
        var gateway = new PageGateway(new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio" },
            legacyInventory: false, inventory: null, inventoryError: new InvalidOperationException("initial failure"));
        gateway.HoldNextLoad();
        await WithPageAsync(600, _ => { }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsInventoryErrorBanner") is not null,
                "initial inventory failure is rendered");
            Invoke(Find<Button>(surface, "SystemsRetry"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => gateway.LoadCount == 2 && controller.State.Loading &&
                    TryFind<FrameworkElement>(surface, "SystemsLoading") is not null &&
                    TryFind<FrameworkElement>(surface, "SystemsInventoryErrorBanner") is null,
                "Retry starts a new inventory load");
            Assert.NotNull(TryFind<FrameworkElement>(surface, "SystemsLoading"));
            Assert.Null(TryFind<FrameworkElement>(surface, "SystemsInventoryErrorBanner"));

            gateway.CompleteHeldLoad();
            await TestSupport.WaitForRenderedConditionAsync(
                () => controller.State.HasLoaded && !controller.State.Loading &&
                    TryFind<FrameworkElement>(surface, "SystemsRow_gateway") is not null,
                "successful retry replaces loading state with inventory");
        }), width: 600, pageGateway: gateway);
    }

    [Fact]
    public async Task DisconnectedInventory_ShowsLastKnownLabelAndDisabledActionReason()
    {
        await WithPageAsync(600, _ => { }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => controller.State.HasLoaded && TryFind<FrameworkElement>(surface, "SystemsRow_node:studio") is not null,
                "initial online inventory is loaded before disconnect");
            controller.Select("node:studio");
            controller.UpdateInputs(Inputs() with { Connected = false });
            await TestSupport.WaitForRenderedConditionAsync(
                () => AutomationProperties.GetName(Find<Expander>(surface, "SystemsRow_node:studio")) ==
                    "Studio, " + SystemsText.S("LastKnownOnline"),
                "disconnected inventory is relabeled as last known");
            var row = Find<Expander>(surface, "SystemsRow_node:studio");
            Assert.Equal("Studio, " + SystemsText.S("LastKnownOnline"), AutomationProperties.GetName(row));
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsManageRequiresPairing") is not null,
                "unavailable node actions include their reason");
            Assert.False(FindCommand(surface, "SystemsRenameNode").IsEnabled);
            Assert.Equal(SystemsText.S("ConnectionRequired"),
                Find<TextBlock>(surface, "SystemsManageRequiresPairing").Text);
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TestSupport.FindDescendants<TextBlock>(surface).Count(text =>
                    text.Text == SystemsText.S("Disconnected_Message")) == 1,
                "one disconnected explanation is rendered in the recovery banner");
            Assert.Null(TryFind<TextBlock>(surface, "SystemsMastSubtitle"));
            Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsAttention_connection");
            Assert.Single(TestSupport.FindDescendants<TextBlock>(Find<FrameworkElement>(row, "SystemsMeta_node:studio")),
                text => text.Text == SystemsText.S("LastKnownOnline"));
            var show = Find<Button>(surface, "SystemsAttention_permission-node:studio-camera_Action");
            Assert.False(show.IsEnabled);
            Assert.Equal(SystemsText.S("ConnectionRequired"), AutomationProperties.GetHelpText(show));
            Assert.Equal(SystemsText.S("ConnectionRequired"), ToolTipService.GetToolTip(show));
        }), new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio", IsOnline = true,
            Permissions = new() { ["camera"] = false } });
    }

    [Fact]
    public async Task IssuesView_ShowsEmptyStateWhenAttentionResolves()
    {
        await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = Enumerable.Range(1, 3).Select(index => PendingApproval.FromNode(
                new PairingRequest { RequestId = $"join-{index}", NodeId = $"new-{index}" })).ToArray(),
        }), async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsNeedsYouMore") is not null,
                "attention overflow card rendered");
            Invoke(Find<Button>(surface, "SystemsNeedsYouMore_Action"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsIssuesView") is not null,
                "Issues view opened");

            controller.UpdateInputs(Inputs());
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<TextBlock>(surface, "SystemsIssuesEmpty") is not null,
                "resolved attention shows the empty Issues state");
            Assert.Equal(SystemsText.S("NothingNeedsYou"), Find<TextBlock>(surface, "SystemsIssuesEmpty").Text);
            Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsNeedsYou");
        }));
    }

    [Fact]
    public async Task PendingPairingAttentionCard_WrapsCopyWithinNarrowRoute()
    {
        const string deviceName = "Studio node awaiting approval from the remote Gateway";
        var expectedMessage = SystemsText.S("Pairing_Message");
        await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest
                { RequestId = "join", NodeId = "new-device", DisplayName = deviceName })],
        }), async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TestSupport.FindDescendants<FrameworkElement>(surface).Any(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal)),
                "pairing attention card rendered at the narrow route width");
            var card = Assert.Single(TestSupport.FindDescendants<Border>(surface),
                element => AutomationProperties.GetAutomationId(element).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal));
            var message = Assert.Single(TestSupport.FindDescendants<TextBlock>(card), text => text.Text == expectedMessage);
            Assert.Single(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == SystemsText.F("Pairing_NamedTitle", deviceName));
            Assert.DoesNotContain(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == expectedMessage + " " + deviceName);
            var action = Assert.Single(TestSupport.FindDescendants<Button>(card),
                button => AutomationProperties.GetAutomationId(button).EndsWith("_Action", StringComparison.Ordinal));
            var cardBounds = Bounds(card, surface);
            var messageBounds = Bounds(message, surface);
            var actionBounds = Bounds(action, surface);

            Assert.True(cardBounds.Left >= 0 && cardBounds.Right <= surface.ActualWidth,
                "The attention card stays within the allocated route width.");
            Assert.True(message.ActualHeight > SingleLineHeight(message) + 1,
                "Long pairing guidance should wrap instead of keeping a fixed-width warning row.");
            Assert.True(messageBounds.Left >= cardBounds.Left && messageBounds.Right <= cardBounds.Right);
            Assert.True(actionBounds.Left >= cardBounds.Left && actionBounds.Right <= cardBounds.Right);
            Assert.True(actionBounds.Top >= messageBounds.Bottom - .5,
                "The action stays below its warning copy at a narrow route width.");
        }), width: 400);
    }

    [Theory]
    [InlineData(520, true)]
    [InlineData(1000, false)]
    public async Task InventoryFailureActions_StackOnlyAtNarrowRouteWidth(double width, bool stacked)
    {
        await WithPageAsync(900, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<FrameworkElement>(surface, "SystemsInventoryErrorBanner") is not null &&
                    Find<Button>(surface, "SystemsRetry").Parent is StackPanel panel &&
                    panel.Orientation == (stacked ? Orientation.Vertical : Orientation.Horizontal),
                "inventory failure banner rendered");
            var banner = Find<FrameworkElement>(surface, "SystemsInventoryErrorBanner");
            var retry = Find<Button>(banner, "SystemsRetry");
            var connection = Find<Button>(banner, "SystemsInventoryConnection");
            await TestSupport.WaitForRenderedConditionAsync(() =>
            {
                var retryRect = Bounds(retry, surface);
                var connectionRect = Bounds(connection, surface);
                return retry.ActualWidth > 0 && connection.ActualWidth > 0 &&
                    (stacked ? connectionRect.Top >= retryRect.Bottom - .5
                        : Math.Abs(connectionRect.Top - retryRect.Top) <= .5 &&
                            connectionRect.Left > retryRect.Left);
            }, "inventory recovery actions finish their native responsive arrangement");
            var bannerBounds = Bounds(banner, surface);
            var retryBounds = Bounds(retry, surface);
            var connectionBounds = Bounds(connection, surface);

            Assert.True(retryBounds.Left >= bannerBounds.Left && retryBounds.Right <= bannerBounds.Right);
            Assert.True(connectionBounds.Left >= bannerBounds.Left && connectionBounds.Right <= bannerBounds.Right);
            if (stacked)
                Assert.True(connectionBounds.Top >= retryBounds.Bottom - .5);
            else
            {
                Assert.InRange(Math.Abs(connectionBounds.Top - retryBounds.Top), 0, .5);
                Assert.True(connectionBounds.Left > retryBounds.Left);
            }
        }), width: width, inventoryError: new InvalidOperationException("Gateway inventory request failed with a long diagnostic message."));
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(1000)]
    public async Task Backdrop_StaysAtViewportTopWhileMastScrolls(double width)
    {
        var inventory = new SystemsInventorySnapshot
        {
            Environments = Enumerable.Range(0, 12).Select(index => new SystemEnvironment
                { Id = $"node:scroll-{index}", Type = "node", Status = SystemStatus.Online }).ToArray(),
        };
        await WithPageAsync(600, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var root = Find<Grid>(surface, "SystemsPageRoot");
            var scroll = Find<ScrollViewer>(root, "SystemsPageScroll");
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.ScrollableHeight > 200 &&
                Find<Border>(root, "SystemsPageBackdrop").ActualHeight > 32 &&
                Math.Abs(Find<Border>(root, "SystemsPageBackdrop").ActualHeight -
                    (Find<FrameworkElement>(root, "SystemsMastGrid").ActualHeight + 32)) <= .5,
                "viewport backdrop and scroll extent laid out");
            scroll.ChangeView(null, 0, null, true);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.VerticalOffset == 0 &&
                Find<TextBlock>(root, "SystemsPageMarker").ActualWidth > 0, "Systems at scroll top");
            // Host and mast size effects can remount descendants. Resolve the current
            // title after those renders instead of holding a displaced pooled TextBlock.
            var mastBefore = Find<TextBlock>(root, "SystemsPageMarker").TransformToVisual(root).TransformPoint(new Point()).Y;
            var backdrop = Find<Border>(root, "SystemsPageBackdrop");
            var before = backdrop.TransformToVisual(root).TransformPoint(new Point());
            var height = backdrop.ActualHeight;
            Assert.Contains(backdrop, root.Children);
            Assert.DoesNotContain(backdrop, TestSupport.FindDescendants<Border>(scroll));
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(scroll),
                text => AutomationProperties.GetAutomationId(text) == "SystemsPageMarker");
            Assert.False(backdrop.IsHitTestVisible);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(backdrop));
            Assert.InRange(Math.Abs(before.X) + Math.Abs(before.Y), 0, .5);
            Assert.InRange(Math.Abs(backdrop.ActualWidth - root.ActualWidth), 0, .5);
            scroll.ChangeView(null, 200, null, true);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.VerticalOffset >= 199 &&
                Find<TextBlock>(root, "SystemsPageMarker").TransformToVisual(root).TransformPoint(new Point()).Y < mastBefore - 150,
                "ordinary content scrolled independently of viewport backdrop");
            backdrop = Find<Border>(root, "SystemsPageBackdrop");
            var after = backdrop.TransformToVisual(root).TransformPoint(new Point());
            Assert.InRange(Math.Abs(after.X - before.X) + Math.Abs(after.Y - before.Y), 0, .5);
            Assert.InRange(Math.Abs(backdrop.ActualHeight - height), 0, .5);
        }), width: width, fitNativeViewport: true, inventory: inventory);
    }

    [Fact]
    public async Task Eyebrow_UsesDecorativeSidebarArtworkAndSharedTitleStyle()
    {
        await WithPageAsync(900, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var row = Find<StackPanel>(surface, "SystemsEyebrow");
            await TestSupport.WaitForRenderedConditionAsync(() => row.ActualHeight > 0, "Systems eyebrow laid out");
            Assert.Equal(Orientation.Horizontal, row.Orientation);
            Assert.Equal(OpenClawTray.Presentation.PageResponsiveLayout.GapInner, row.Spacing);
            var image = Assert.Single(TestSupport.FindDescendants<Image>(row));
            Assert.Equal(20, image.Width);
            Assert.Equal(20, image.Height);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(image));
            var source = Assert.IsType<Microsoft.UI.Xaml.Media.Imaging.SvgImageSource>(image.Source);
            Assert.Equal("ms-appx:///Assets/SidebarIcons/Instances.svg", source.UriSource.AbsoluteUri);
            var title = Find<TextBlock>(row, "SystemsEyebrowTitle");
            Assert.Equal(SystemsText.S("Title"), title.Text);
            Assert.Equal(14, title.FontSize);
            Assert.Equal(Microsoft.UI.Text.FontWeights.SemiBold, title.FontWeight);
            Assert.NotNull(title.Foreground);
            Assert.Equal(AutomationHeadingLevel.Level1,
                AutomationProperties.GetHeadingLevel(Find<TextBlock>(surface, "SystemsPageMarker")));
        }));
    }

    [Theory]
    [InlineData(1400, 3, 4, 2)]
    [InlineData(1000, 3, 2, 2)]
    [InlineData(999, 1, 5, 1)]
    [InlineData(760, 1, 3, 1)]
    [InlineData(520, 1, 2, 1)]
    public async Task ResizedHost_KeepsActionsRowsAndResourceTilesInTheirLanes(
        double width, int detailColumns, int statColumns, int attentionColumns)
    {
        var now = DateTimeOffset.UtcNow;
        const string longName = "Atelier de développement international - infrastructure et automatisation";
        var environments = Enumerable.Range(1, 7).Select(index => new SystemEnvironment
        {
            Id = $"node:responsive-{index}", Label = longName + " " + index, Type = "node",
            Platform = "Windows 11 Entreprise développement international",
            Status = index < 3 ? SystemStatus.Offline : SystemStatus.Online,
            LastSeenAt = now.AddHours(-2), Capabilities = ["system", "camera", "screen"],
        }).ToArray();
        var nodes = environments.Select(environment => new GatewayNodeInfo
        {
            NodeId = environment.NodeId!, DisplayName = environment.Label!, HasExplicitDisplayName = true,
            IsOnline = environment.Status == SystemStatus.Online,
            Capabilities = ["system", "camera", "screen"],
            HostStats = new(new HostMeasurements
            {
                CpuCount = 128, MemoryTotalBytes = 512L << 30, MemoryFreeBytes = 123L << 30,
                DiskTotalBytes = 8L << 40, DiskAvailableBytes = 3L << 40,
            }, now),
        }).ToArray();
        await WithPageAsync(1000, controller =>
            {
                controller.UpdateInputs(Inputs() with
                {
                    PendingApprovals = [PendingApproval.FromNode(new PairingRequest { RequestId = "responsive-join", NodeId = "new" })],
                });
                controller.Select("node:responsive-3");
            },
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TestSupport.FindDescendants<FrameworkElement>(surface).Any(element =>
                        AutomationProperties.GetAutomationId(element) == "SystemsStatRows"), "Expanded responsive resources");
                await ui.YieldToRenderAsync();
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    Find<Grid>(surface, "SystemsDetailsGrid").ColumnDefinitions.Count == detailColumns &&
                    Find<FrameworkElement>(surface, "SystemsContentColumn").ActualWidth <=
                        OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(Find<FrameworkElement>(surface, "SystemsPageRoot").ActualWidth) + .5 &&
                    Find<Border>(surface, "SystemsStat_cpu").ActualWidth > 0 &&
                    Find<Border>(surface, "SystemsStat_memory").ActualWidth > 0 &&
                    Find<Border>(surface, "SystemsStat_disk").ActualWidth > 0 &&
                    Find<StackPanel>(surface, "SystemsStatRows").Children.OfType<Grid>()
                        .All(grid => grid.ColumnDefinitions.Count == statColumns * 2 - 1),
                    "Host-driven responsive layout reconciled");
                var root = Find<FrameworkElement>(surface, "SystemsPageRoot");
                // Fractional physical pixels can round a 999 DIP host by less than half a DIP.
                Assert.InRange(Math.Abs(width - root.ActualWidth), 0, .5);
                var content = Find<FrameworkElement>(surface, "SystemsContentColumn");
                Assert.InRange(content.ActualWidth, 1, OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(root.ActualWidth) + .5);
                var backdrop = Find<Border>(surface, "SystemsPageBackdrop");
                await TestSupport.WaitForRenderedConditionAsync(() => Math.Abs(backdrop.ActualWidth - root.ActualWidth) <= .5,
                    "full-host backdrop reconciled independently of centered content");
                Assert.InRange(Math.Abs(backdrop.TransformToVisual(root).TransformPoint(new Point()).X), 0, .5);
                Assert.InRange(Math.Abs(backdrop.ActualWidth - root.ActualWidth), 0, .5);
                Assert.InRange(Math.Abs(content.ActualWidth - OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(root.ActualWidth)), 0, .5);
                Assert.InRange(Math.Abs(content.TransformToVisual(root).TransformPoint(new Point()).X -
                    (root.ActualWidth - content.ActualWidth) / 2), 0, .5);
                var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
                Assert.Equal(ScrollMode.Disabled, scroll.HorizontalScrollMode);
                Assert.Equal(0, scroll.ScrollableWidth);
                var details = Find<Grid>(surface, "SystemsDetailsGrid");
                Assert.Equal(detailColumns, details.ColumnDefinitions.Count);
                AssertSiblingsDoNotOverlap(details);
                var statRows = Find<StackPanel>(surface, "SystemsStatRows");
                var tileGrids = statRows.Children.OfType<Grid>().ToArray();
                Assert.NotEmpty(tileGrids);
                foreach (var grid in tileGrids)
                {
                    Assert.Equal(statColumns * 2 - 1, grid.ColumnDefinitions.Count);
                    AssertSiblingsDoNotOverlap(grid);
                }
                var tiles = TestSupport.FindDescendants<Border>(statRows).Where(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("SystemsStat_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(3, tiles.Length);
                foreach (var (tile, index) in tiles.Select((tile, index) => (tile, index)))
                {
                    Assert.True(tile.ActualWidth >= 160, $"Tile width {tile.ActualWidth} at host {width}");
                    var isTrailingSingle = statColumns > 1 && tiles.Length % statColumns == 1 &&
                        index == tiles.Length - 1;
                    Assert.InRange(Math.Abs(tile.ActualWidth -
                        (isTrailingSingle ? statRows.ActualWidth : tiles[0].ActualWidth)), 0, 1);
                    Assert.Equal(isTrailingSingle ? statColumns * 2 - 1 : 1,
                        Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(tile));
                }
                var attention = Find<StackPanel>(surface, "SystemsAttentionRows");
                var heading = Assert.Single(TestSupport.FindDescendants<TextBlock>(Find<FrameworkElement>(surface, "SystemsNeedsYou")),
                    text => text.Text == SystemsText.S("NeedsYou"));
                foreach (var grid in attention.Children.OfType<Grid>())
                {
                    Assert.Equal(attentionColumns * 2 - 1, grid.ColumnDefinitions.Count);
                    AssertSiblingsDoNotOverlap(grid);
                    var firstCard = Assert.IsAssignableFrom<FrameworkElement>(grid.Children[0]);
                    Assert.InRange(Math.Abs(firstCard.TransformToVisual(content).TransformPoint(new Point()).X -
                        heading.TransformToVisual(content).TransformPoint(new Point()).X), 0, 1);
                    if (attentionColumns == 1)
                        Assert.InRange(Math.Abs(firstCard.ActualWidth - content.ActualWidth), 0, 1);
                }
                var mast = Find<StackPanel>(surface, "SystemsMastActions");
                AssertSiblingsDoNotOverlap(Find<Panel>(surface, "SystemsMastGrid"));
                Assert.Equal(Orientation.Horizontal, mast.Orientation);
                AssertSiblingsDoNotOverlap(mast);
                Assert.InRange(mast.ActualHeight, 1, 48);
                var mastActions = mast.Children.OfType<Control>().ToArray();
                Assert.Equal(3, mastActions.Length);
                foreach (var action in mastActions)
                {
                    var actionBounds = Bounds(action, mast);
                    Assert.InRange(Math.Abs(actionBounds.Top + actionBounds.Height / 2 - mast.ActualHeight / 2), 0, 1);
                    if (action is not Button { Content: StackPanel label })
                        continue;
                    var glyph = Assert.IsType<TextBlock>(label.Children[0]);
                    var text = Assert.IsType<TextBlock>(label.Children[1]);
                    var glyphBounds = Bounds(glyph, label);
                    var textBounds = Bounds(text, label);
                    Assert.InRange(Math.Abs(glyph.ActualHeight - glyph.DesiredSize.Height), 0, 1);
                    Assert.InRange(Math.Abs(glyphBounds.Top + glyphBounds.Height / 2 -
                        (textBounds.Top + textBounds.Height / 2)), 0, 1);
                }
                Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(surface), element =>
                    AutomationProperties.GetAutomationId(element) is "SystemsFilterGrid" or "SystemsSearchBox" or
                        "SystemsStatusFilter" or "SystemsSortButton");
                Assert.Equal(7, TestSupport.FindDescendants<Expander>(surface)
                    .Count(button => AutomationProperties.GetAutomationId(button).StartsWith("SystemsRow_", StringComparison.Ordinal)));
                var name = Find<TextBlock>(surface, "SystemsSelectedName");
                var subtitle = Find<TextBlock>(surface, "SystemsSubtitle_node:responsive-3");
                foreach (var text in new[] { name, subtitle })
                {
                    Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
                    Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                    Assert.NotNull(ToolTipService.GetToolTip(text));
                    Assert.True(text.ActualWidth > 0);
                }
                var meta = Find<StackPanel>(surface, "SystemsMeta_node:responsive-3");
                Assert.Equal(width < 720 ? 1 : 0, Microsoft.UI.Xaml.Controls.Grid.GetRow(meta));
                AssertSiblingsDoNotOverlap(Find<Grid>(surface, "SystemsItem_node:responsive-3"));
                Assert.Single(TestSupport.FindDescendants<Microsoft.UI.Xaml.Controls.CommandBar>(surface));
                Assert.Equal(2, Find<TextBlock>(surface, "SystemsPageMarker").MaxLines);
            }), width: width, inventory: new SystemsInventorySnapshot { Environments = environments, Nodes = nodes });
    }

    private static void AssertSiblingsDoNotOverlap(Panel parent)
    {
        var children = parent.Children.OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible && child.ActualWidth > 0 && child.ActualHeight > 0).ToArray();
        foreach (var child in children)
        {
            var bounds = child.TransformToVisual(parent).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
            Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1 &&
                bounds.Bottom <= parent.ActualHeight + 1,
                $"Child outside its lane: {AutomationProperties.GetAutomationId(child)} {bounds}; parent {parent.ActualWidth}x{parent.ActualHeight}");
        }
        for (var first = 0; first < children.Length; first++)
        for (var second = first + 1; second < children.Length; second++)
        {
            var left = children[first].TransformToVisual(parent).TransformBounds(new Rect(0, 0, children[first].ActualWidth, children[first].ActualHeight));
            var right = children[second].TransformToVisual(parent).TransformBounds(new Rect(0, 0, children[second].ActualWidth, children[second].ActualHeight));
            Assert.True(left.Right <= right.Left + 1 || right.Right <= left.Left + 1 ||
                left.Bottom <= right.Top + 1 || right.Bottom <= left.Top + 1,
                $"Overlapping siblings: {AutomationProperties.GetAutomationId(children[first])} {left} / {AutomationProperties.GetAutomationId(children[second])} {right}");
        }
    }

    [Fact]
    public async Task NarrowDialog_StacksFactsAndFooterWithoutChangingDestructiveFocus()
    {
        await WithPageAsync(900, controller => controller.Select("node:studio"), async (surface, _, _) =>
            await ui.RunOnUIAsync(async () =>
            {
                var bar = Find<Microsoft.UI.Xaml.Controls.CommandBar>(surface, "SystemsCommandBar");
                bar.IsOpen = true;
                await ui.YieldToRenderAsync();
                Invoke(FindCommand(surface, "SystemsForgetNode"));
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 1, "Narrow forget dialog");
                var dialog = Assert.Single(OpenDialogs(surface));
                var primary = Assert.Single(TestSupport.FindDescendants<Button>(dialog), button => button.Name == "PrimaryButton");
                var cancel = Assert.Single(TestSupport.FindDescendants<Button>(dialog), button => button.Name == "CloseButton");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    cancel.TransformToVisual(dialog).TransformPoint(new Point()).Y >
                    primary.TransformToVisual(dialog).TransformPoint(new Point()).Y,
                    "Narrow dialog footer has primary above Cancel");
                Assert.InRange(DialogCard(dialog).ActualWidth, 1, surface.XamlRoot.Size.Width - 48 + 1);
                await AssertCenteredInWindowAsync(dialog, "Narrow dialog is centered in the window");
                Assert.Equal(ContentDialogButton.None, dialog.DefaultButton);
                Assert.Equal(HorizontalAlignment.Stretch, primary.HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Stretch, cancel.HorizontalAlignment);
                Assert.Same(cancel, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(dialog.XamlRoot));
                AssertSiblingsDoNotOverlap((Panel)VisualTreeHelper.GetParent(primary));
                var facts = Find<Grid>(dialog, "SystemsForgetFacts");
                Assert.Equal(0, facts.ColumnDefinitions[1].Width.Value);
                Assert.Equal(Enumerable.Range(0, 6), facts.Children.OfType<FrameworkElement>()
                    .Select(Microsoft.UI.Xaml.Controls.Grid.GetRow));

                var scale = surface.XamlRoot.RasterizationScale;
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(1100 * scale), (int)Math.Ceiling(948 * scale)));
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    DialogCard(dialog).MaxWidth == 560 && facts.ColumnDefinitions[0].Width.Value == 120 &&
                    cancel.TransformToVisual(dialog).TransformPoint(new Point()).Y ==
                    primary.TransformToVisual(dialog).TransformPoint(new Point()).Y,
                    "Open dialog adapts back to a normal footer and facts grid");
                await AssertCenteredInWindowAsync(dialog, "Resized dialog stays centered in the window");
                Assert.Same(cancel, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(dialog.XamlRoot));
                AssertSiblingsDoNotOverlap((Panel)VisualTreeHelper.GetParent(primary));
            }), width: 360, fitNativeViewport: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAndForgetDialogs_ExposeConsequencesAndSafeDefaults(bool dark)
    {
        await WithPageAsync(900, controller => controller.Select("node:studio"), async (surface, _, navigated) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                Invoke(Find<Button>(surface, "SystemsAddSystem"));
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 1, "Add system dialog");
                var add = Assert.Single(OpenDialogs(surface));
                await AssertCenteredInWindowAsync(add, "Add system dialog is centered in the window");
                Assert.Equal(SystemsText.S("OpenConnection"), add.PrimaryButtonText);
                Assert.NotNull(add.TitleTemplate);
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(add), text => text.Text == SystemsText.S("AddSystemIntro"));
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(add), text => text.Text == SystemsText.S("AddSystemSteps"));
                var openConnection = Assert.Single(TestSupport.FindDescendants<Button>(add), b => b.Name == "PrimaryButton");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    ReferenceEquals(openConnection, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(add.XamlRoot)),
                    "Open Connection receives initial focus");
                AssertInlineDialogTitle(add, "\uE710");
                Invoke(Assert.Single(TestSupport.FindDescendants<Button>(add), b => b.Name == "PrimaryButton"));
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 0, "Add dialog closed");
                await TestSupport.WaitForRenderedConditionAsync(() => navigated.Count == 1, "Connection navigation after dialog close");
                Assert.Equal(["connection"], navigated);

                var bar = Find<Microsoft.UI.Xaml.Controls.CommandBar>(surface, "SystemsCommandBar");
                bar.IsOpen = true;
                await ui.YieldToRenderAsync();
                Invoke(FindCommand(surface, "SystemsForgetNode"));
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 1, "Forget dialog");
                var forget = Assert.Single(OpenDialogs(surface));
                Assert.Equal(ContentDialogButton.None, forget.DefaultButton);
                Assert.Equal(SystemsText.F("ForgetTitle", "Studio"), forget.Title);
                Assert.NotNull(forget.TitleTemplate);
                AssertInlineDialogTitle(forget, "\uE74D");
                var cancel = Assert.Single(TestSupport.FindDescendants<Button>(forget), b => b.Name == "CloseButton");
                var primary = Assert.Single(TestSupport.FindDescendants<Button>(forget), b => b.Name == "PrimaryButton");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    ReferenceEquals(cancel, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(forget.XamlRoot)),
                    "Cancel receives safe initial focus", () =>
                        $"dialog loaded={forget.IsLoaded}; cancel loaded={cancel.IsLoaded}; cancel focus={cancel.FocusState}; focused={(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(forget.XamlRoot) as FrameworkElement)?.Name}");
                Assert.Same(Application.Current.Resources["AccentButtonStyle"], forget.PrimaryButtonStyle);
                Assert.Equal(SystemsText.S("ForgetAction"), forget.PrimaryButtonText);
                Assert.True(primary.TransformToVisual(forget).TransformPoint(new Point()).X <
                    cancel.TransformToVisual(forget).TransformPoint(new Point()).X);
                var forgetText = TestSupport.FindDescendants<TextBlock>(forget).Select(text => text.Text).ToArray();
                Assert.Contains(SystemsText.F("ForgetConsequence", SystemsText.S("Kind_Host")), forgetText);
                Assert.DoesNotContain(LocalizationHelper.GetString("NodesPage_Forget_Warning"), forgetText);
                var facts = Find<Grid>(forget, "SystemsForgetFacts");
                Assert.Equal(120, facts.ColumnDefinitions[0].Width.Value);
                var factText = TestSupport.FindDescendants<TextBlock>(facts).Select(text => text.Text).ToArray();
                Assert.Contains(SystemsText.S("Field_Paired"), factText);
                Assert.Contains("Windows", factText);
                Assert.NotNull(Find<Border>(forget, "SystemsForgetFactsSurface").Background);
                Assert.Contains(factText, text => System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+ seconds ago$"));
                Assert.Contains(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero).LocalDateTime.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture), factText);
                Invoke(Assert.Single(TestSupport.FindDescendants<Button>(forget), b => b.Name == "PrimaryButton"));
                await TestSupport.WaitForRenderedConditionAsync(() => TestSupport.FindDescendants<Border>(surface)
                    .Any(bar => AutomationProperties.GetAutomationId(bar) == "SystemsResult"), "Forget success notification");
                var result = Find<Border>(surface, "SystemsResult");
                Assert.Equal(SystemsText.F("Forgotten", "Studio"), Find<TextBlock>(result, "SystemsResultMessage").Text);
                Assert.Equal(VerticalAlignment.Bottom, result.VerticalAlignment);
                Assert.Equal(480, result.MaxWidth);
            });
        }, theme: dark ? ElementTheme.Dark : ElementTheme.Light, width: 1100, onForget: () => { }, fitNativeViewport: true,
            node: new GatewayNodeInfo
            {
                NodeId = "studio", DisplayName = "Studio", Platform = "win32", LastSeen = DateTime.UtcNow,
                ApprovedAt = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
            });
    }

    private static FrameworkElement DialogCard(ContentDialog dialog) =>
        Assert.Single(TestSupport.FindDescendants<FrameworkElement>(dialog), element => element.Name == "BackgroundElement");

    private async Task AssertCenteredInWindowAsync(ContentDialog dialog, string label)
    {
        var card = DialogCard(dialog);
        // WinUI detaches the template's SmokeLayerBackground at runtime; the card's LayoutRoot parent is the overlay.
        var layoutRoot = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(card));
        Assert.Equal("LayoutRoot", layoutRoot.Name);
        static Rect WindowBounds(FrameworkElement element) =>
            element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        static string Format(Rect bounds) =>
            $"({bounds.X:0.##}, {bounds.Y:0.##}) {bounds.Width:0.##}x{bounds.Height:0.##}";
        string Geometry() =>
            $"window {dialog.XamlRoot.Size.Width:0.##}x{dialog.XamlRoot.Size.Height:0.##}; " +
            $"overlay {Format(WindowBounds(layoutRoot))}; card {Format(WindowBounds(card))}";
        await TestSupport.WaitForRenderedConditionAsync(() =>
        {
            var window = dialog.XamlRoot.Size;
            var overlay = WindowBounds(layoutRoot);
            var box = WindowBounds(card);
            return box.Width > 0 && Math.Abs(overlay.Left) <= 1 && Math.Abs(overlay.Width - window.Width) <= 1 &&
                Math.Abs(box.Left + box.Width / 2 - window.Width / 2) <= 1 &&
                Math.Abs(box.Top + box.Height / 2 - (overlay.Top + overlay.Height / 2)) <= 1;
        }, label, Geometry);
        output.WriteLine($"{label}: {Geometry()}.");
    }

    private static void AssertInlineDialogTitle(ContentDialog dialog, string glyph)
    {
        var icon = Assert.Single(TestSupport.FindDescendants<FontIcon>(dialog), icon => icon.Glyph == glyph);
        var title = Assert.Single(TestSupport.FindDescendants<TextBlock>(dialog), text => text.Text == (string)dialog.Title);
        var iconBounds = icon.TransformToVisual(dialog).TransformBounds(new Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
        var titleBounds = title.TransformToVisual(dialog).TransformBounds(new Rect(0, 0, title.ActualWidth, title.ActualHeight));
        Assert.True(iconBounds.Right < titleBounds.Left);
        Assert.InRange(Math.Abs((iconBounds.Top + iconBounds.Bottom) / 2 - (titleBounds.Top + titleBounds.Bottom) / 2), 0, 2);
    }

    [Fact]
    public async Task MastAndAttention_UseReportedStateAndResponsiveNativeLayout()
    {
        var now = DateTimeOffset.UtcNow;
        var nodes = Enumerable.Range(1, 3).Select(index => new GatewayNodeInfo
        {
            NodeId = "machine-" + index, DisplayName = "Machine " + index, IsOnline = index < 3,
        }).ToArray();
        var inventory = new SystemsInventorySnapshot
        {
            Nodes = nodes,
            Environments = nodes.Select((node, index) => new SystemEnvironment
            {
                Id = "node:" + node.NodeId, Type = "node", Label = node.DisplayName,
                Status = index < 2 ? SystemStatus.Online : SystemStatus.Offline,
                LastSeenAt = index == 2 ? now.AddHours(-2) : now,
            }).Prepend(new SystemEnvironment { Id = "gateway", Type = "local", Label = "Home", Status = SystemStatus.Online }).ToArray(),
        };
        foreach (var width in new[] { 1000, 740, 560 })
        {
            await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
            {
                LocalNodeId = "machine-1",
                PendingApprovals = [PendingApproval.FromNode(new PairingRequest { RequestId = "join", NodeId = "new" })],
            }), async (surface, controller, navigated) => await ui.RunOnUIAsync(async () =>
            {
                var headline = Find<TextBlock>(surface, "SystemsPageMarker");
                Assert.Equal("Three of four systems are online", headline.Text);
                var sub = Find<TextBlock>(surface, "SystemsMastSubtitle");
                Assert.Contains("This PC is connected as a node.", sub.Text, StringComparison.Ordinal);
                Assert.Contains("Machine 3 was last seen 2 h ago.", sub.Text, StringComparison.Ordinal);
                var add = Find<Button>(surface, "SystemsAddSystem");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    var actionY = add.TransformToVisual(surface).TransformPoint(new Point()).Y;
                    var headlineY = headline.TransformToVisual(surface).TransformPoint(new Point()).Y;
                    return width >= 740 ? actionY <= headlineY + headline.ActualHeight
                        : actionY > headlineY + headline.ActualHeight;
                }, "Mast actions adapt to the measured page width");
                var addBounds = add.TransformToVisual(surface).TransformBounds(new Rect(0, 0, add.ActualWidth, add.ActualHeight));
                var titleBounds = headline.TransformToVisual(surface).TransformBounds(new Rect(0, 0, headline.ActualWidth, headline.ActualHeight));
                if (width >= 740)
                    Assert.InRange(addBounds.Top, titleBounds.Top - 40, titleBounds.Bottom);
                else
                    Assert.True(addBounds.Top > titleBounds.Bottom);
                var pairing = Assert.Single(TestSupport.FindDescendants<Border>(surface), card =>
                    AutomationProperties.GetAutomationId(card).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal));
                var offline = Find<Border>(surface, "SystemsAttention_offline-node:machine-3");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    var pairingPosition = pairing.TransformToVisual(surface).TransformPoint(new Point());
                    var offlinePosition = offline.TransformToVisual(surface).TransformPoint(new Point());
                    return pairing.ActualWidth > 0 && offline.ActualWidth > 0 &&
                        (width >= 1000
                            ? Math.Abs(pairingPosition.Y - offlinePosition.Y) <= 1 &&
                                offlinePosition.X > pairingPosition.X + pairing.ActualWidth
                            : offlinePosition.Y > pairingPosition.Y);
                }, "attention cards settle into their measured responsive rows");
                var p = pairing.TransformToVisual(surface).TransformPoint(new Point());
                var o = offline.TransformToVisual(surface).TransformPoint(new Point());
                if (width >= 1000)
                {
                    Assert.InRange(Math.Abs(p.Y - o.Y), 0, 1);
                    Assert.True(o.X > p.X + pairing.ActualWidth);
                }
                else
                    Assert.True(o.Y > p.Y);
                Invoke(Find<Button>(offline, "SystemsAttention_offline-node:machine-3_Action"));
                Assert.Equal(["connection"], navigated);
                controller.UpdateInputs(Inputs() with { LocalNodeId = "not-reported" });
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    !Find<TextBlock>(surface, "SystemsMastSubtitle").Text.Contains("This PC is connected as a node.", StringComparison.Ordinal),
                    "An identity without a reported live node does not claim node connectivity");
                controller.Select("node:machine-1");
                await TestSupport.WaitForRenderedConditionAsync(() => TestSupport.FindDescendants<FrameworkElement>(surface)
                    .Any(element => AutomationProperties.GetAutomationId(element) == "SystemsCapabilities" && element.ActualHeight > 0),
                    "Expanded machine sections are laid out");
                var capabilities = Find<FrameworkElement>(surface, "SystemsCapabilities");
                var resources = Find<FrameworkElement>(surface, "SystemsStatistics");
                var c = capabilities.TransformToVisual(surface).TransformPoint(new Point());
                var r = resources.TransformToVisual(surface).TransformPoint(new Point());
                if (width >= 1000)
                    Assert.True(r.X >= c.X + capabilities.ActualWidth);
                else
                    Assert.True(r.Y > c.Y);
            }), inventory: inventory, width: width);
        }
    }

    [Fact]
    public async Task MinimumHeight_WithPendingApprovals_KeepsMachinesReachable()
    {
        await WithPageAsync(400, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = Enumerable.Range(1, 3).Select(index => PendingApproval.FromNode(
                new PairingRequest { RequestId = $"request-{index}", NodeId = $"other-{index}" })).ToArray(),
        }), async (surface, controller, _) =>
        {
            ScrollViewer scroll = null!;
            await ui.RunOnUIAsync(async () =>
            {
                scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
                Assert.DoesNotContain(TestSupport.FindDescendants<ScrollViewer>(scroll),
                    nested => AutomationProperties.GetAutomationId(nested) == "SystemsInventoryScroll");
                Assert.Empty(TestSupport.FindDescendants<ListView>(scroll));
                Assert.True(scroll.ViewportHeight > 0);
                Assert.True(scroll.ScrollableHeight > 0);
                var item = Find<FrameworkElement>(scroll, "SystemsItem_node:studio");
                item.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                    var visible = item.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                    return visible.Top >= -0.5 && visible.Bottom <= scroll.ViewportHeight + .5;
                }, "Systems inventory scrolled to its machine rows",
                    () => $"offset={scroll.VerticalOffset}, extent={scroll.ScrollableHeight}, viewport={scroll.ViewportHeight}, row={item.ActualHeight}");
                var bounds = item.TransformToVisual(scroll)
                    .TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                Assert.True(item.ActualHeight > 0);
                Assert.InRange(bounds.Top, -0.5, scroll.ViewportHeight - 1);
                Assert.InRange(bounds.Bottom, 1, scroll.ViewportHeight + 0.5);
                Find<Expander>(scroll, "SystemsRow_node:studio").IsExpanded = true;
                await TestSupport.WaitForRenderedConditionAsync(() => controller.State.SelectedId == "node:studio",
                    "expanding the row selects its machine");
                Assert.Equal("node:studio", controller.State.SelectedId);
                output.WriteLine($"400px Systems: inventory viewport={scroll.ViewportHeight:F2}, " +
                    $"machine bounds={bounds.Top:F2}..{bounds.Bottom:F2}, selected={controller.State.SelectedId}.");
            });
        });
    }

    [Fact]
    public async Task OwnNodeOnlyPending_ShowsConnectionRecoveryWithoutAnApprovalPrompt()
    {
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            LocalNodeId = "own-full-id",
            OwnNodeIds = ["own-full-id", "own-node"],
            NodePairList = new PairingListInfo
            {
                Pending = [new PairingRequest { RequestId = "own-request", NodeId = "own-node" }],
            },
        }), async (surface, controller, navigated) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(controller.State.OwnNodePending);
                Assert.Empty(controller.State.PendingApprovals);
                var card = Find<Border>(surface, "SystemsAttention_own-pairing");
                Assert.True(card.ActualHeight > 0);
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(card),
                    text => text.Text == SystemsText.S("Pairing_OwnNodeMessage"));
                Assert.DoesNotContain(TestSupport.FindDescendants<Button>(card),
                    button => AutomationProperties.GetAutomationId(button).StartsWith("SystemsPairApprove_", StringComparison.Ordinal));
                var review = Find<Button>(card, "SystemsAttention_own-pairing_Action");
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(review);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(["connection"], navigated);
                output.WriteLine("Own-node-only pairing: attention card and Connection action are visible; no approve prompt.");
            });
        });
    }

    [Theory]
    [InlineData(GatewayNodeApprovalState.PendingApproval, true)]
    [InlineData(GatewayNodeApprovalState.PendingApproval, false)]
    [InlineData(GatewayNodeApprovalState.PendingReapproval, true)]
    [InlineData(GatewayNodeApprovalState.PendingReapproval, false)]
    public async Task PendingOnlyDeclarations_RemainVisible(GatewayNodeApprovalState approval, bool capability)
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "studio",
            DisplayName = "Studio",
            ApprovalState = approval,
            PendingDeclaredCapabilities = capability ? ["screen"] : [],
            PendingDeclaredCommands = capability ? [] : ["system.run"],
        };
        await WithPageAsync(600, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                var declaration = Find<TextBlock>(surface, "SystemsPendingDeclarations");
                if (capability)
                    Assert.Contains(SystemsText.S("Capability_Screen"), declaration.Text, StringComparison.Ordinal);
                else
                {
                    Find<Expander>(surface, "SystemsDiagnostics").IsExpanded = true;
                    await TestSupport.WaitForRenderedConditionAsync(() => TestSupport.FindDescendants<Expander>(surface)
                        .Any(expander => AutomationProperties.GetAutomationId(expander) == "SystemsCommands"), "diagnostic commands mounted");
                    Find<Expander>(surface, "SystemsCommands").IsExpanded = true;
                    await TestSupport.WaitForRenderedConditionAsync(() => TestSupport.FindDescendants<TextBlock>(surface)
                        .Any(block => block.Text == "system.run · " + SystemsText.S("AwaitingApproval") && block.ActualHeight > 0),
                        "pending command declaration disclosed without granting it");
                }
                await TestSupport.WaitForRenderedConditionAsync(() => declaration.ActualHeight > 0, "pending declaration laid out");
                Assert.True(declaration.ActualHeight > 0);
                output.WriteLine($"Pending declaration visible: approval={approval}, capability={capability}, height={declaration.ActualHeight:F2}.");
            }), node);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Fixture Tablet")]
    public async Task PendingApprovals_RouteToConnectionWithoutInlineDecisions(string? displayName)
    {
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest
                { RequestId = "reviewed", NodeId = "other", DisplayName = displayName })],
        }), async (surface, _, navigated) => await ui.RunOnUIAsync(() =>
        {
            var card = Assert.Single(TestSupport.FindDescendants<Border>(surface), element =>
                AutomationProperties.GetAutomationId(element).StartsWith("SystemsAttention_pairing-", StringComparison.Ordinal));
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == (displayName is null ? SystemsText.S("Pairing_TitleOne")
                    : SystemsText.F("Pairing_NamedTitle", displayName)));
            // The Connection page owns approve and reject, so Systems offers no decision of its own.
            var review = Assert.Single(TestSupport.FindDescendants<Button>(card));
            Assert.EndsWith("_Action", AutomationProperties.GetAutomationId(review), StringComparison.Ordinal);
            Invoke(review);
            Assert.Equal(["connection"], navigated);
            output.WriteLine("Pending approval: Needs you card routes to Connection; no inline approve or reject.");
        }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenWeb_IsOneMastActionThatOpensSystemsOnlyWhenTheGatewayHasIt(bool modernGateway)
    {
        var opened = new List<string?>();
        var name = SystemsText.S(modernGateway ? "OpenWeb_Name" : "OpenWeb_RootName");
        var help = SystemsText.S(modernGateway ? "OpenWeb_Help" : "OpenWeb_RootHelp");
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => WebHandoffs(surface) == "SystemsMastOpenWeb" &&
                        controller.State.HasWebSystemsPage == modernGateway &&
                        TryFind<Button>(surface, "SystemsMastOpenWeb") is { } candidate &&
                        AutomationProperties.GetName(candidate) == name,
                    "single wide mast Open in web action",
                    () => $"handoffs=[{WebHandoffs(surface)}] modern={controller.State.HasWebSystemsPage}");
                var button = Find<Button>(surface, "SystemsMastOpenWeb");
                Assert.Equal(help, AutomationProperties.GetHelpText(button));
                Assert.Equal(help, ToolTipService.GetToolTip(button));
                Invoke(button);
                Assert.Equal(modernGateway ? new string?[] { "systems" } : new string?[] { null }, opened);
                // Selecting a machine adds no second handoff beside the page-level action.
                Assert.Equal("SystemsMastOpenWeb", WebHandoffs(surface));
                output.WriteLine($"Open in web (modern={modernGateway}): one mast action opened [{string.Join(", ", opened.Select(path => path ?? "<root>"))}].");
            }), openWeb: opened.Add, legacyInventory: !modernGateway, width: 1100);
    }

    [Theory]
    [InlineData(740)]
    [InlineData(560)]
    public async Task OpenWeb_BelowTheWideTierIsOneOverflowMenuItem(double width)
    {
        var opened = new List<string?>();
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => WebHandoffs(surface) == "SystemsMastOverflow" && controller.State.HasWebSystemsPage,
                    "single mast overflow Open in web menu",
                    () => $"handoffs=[{WebHandoffs(surface)}] modern={controller.State.HasWebSystemsPage}");
                var overflow = Find<Button>(surface, "SystemsMastOverflow");
                var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
                scroll.ChangeView(null, 0, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(() => overflow.IsLoaded &&
                    Bounds(overflow, surface).Top >= Bounds(scroll, surface).Top &&
                    Bounds(overflow, surface).Bottom <= Bounds(scroll, surface).Bottom &&
                    overflow.ActualWidth > 0 && overflow.ActualHeight > 0,
                    "native overflow anchor is loaded and visible in the scroll viewport",
                    () => $"anchor={Bounds(overflow, surface)}, viewport={Bounds(scroll, surface)}, loaded={overflow.IsLoaded}");
                Assert.Equal(SystemsText.S("OpenWeb"), AutomationProperties.GetName(overflow));
                var flyout = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Controls.MenuFlyout>(overflow.Flyout);
                var item = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>(Assert.Single(flyout.Items));
                Assert.Equal(SystemsText.S("OpenWeb"), item.Text);
                var observedFrames = 0;
                void FrameObserved(object? sender, object args) => observedFrames++;
                string MenuDiagnostics()
                {
                    var currentAnchor = TryFind<Button>(surface, "SystemsMastOverflow");
                    var currentFlyout = currentAnchor?.Flyout as Microsoft.UI.Xaml.Controls.MenuFlyout;
                    var currentItem = currentFlyout?.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().FirstOrDefault();
                    static string Identity(object? value) => value is null ? "<null>"
                        : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value).ToString("X");
                    var window = ui.TestWindow.AppWindow;
                    return $"capturedAnchor={Identity(overflow)}, currentAnchor={Identity(currentAnchor)}, " +
                        $"sameAnchor={ReferenceEquals(overflow, currentAnchor)}, " +
                        $"capturedFlyout={Identity(flyout)}, currentFlyout={Identity(currentFlyout)}, " +
                        $"sameFlyout={ReferenceEquals(flyout, currentFlyout)}, " +
                        $"capturedItem={Identity(item)}, currentItem={Identity(currentItem)}, " +
                        $"sameItem={ReferenceEquals(item, currentItem)}, capturedOpen={flyout.IsOpen}, " +
                        $"currentOpen={currentFlyout?.IsOpen}, capturedItemLoaded={item.IsLoaded}, " +
                        $"currentItemLoaded={currentItem?.IsLoaded}, currentItems={currentFlyout?.Items.Count}, " +
                        $"capturedAnchorLoaded={overflow.IsLoaded}, currentAnchorLoaded={currentAnchor?.IsLoaded}, " +
                        $"anchor={Bounds(overflow, surface)}, viewport={Bounds(scroll, surface)}, " +
                        $"windowPosition={window.Position}, windowSize={window.Size}, presenter={window.Presenter.Kind}, " +
                        $"rasterizationScale={surface.XamlRoot.RasterizationScale}, observedCompositionFrames={observedFrames} " +
                        "(composition frames, not a Reactor render generation)";
                }
                CompositionTarget.Rendering += FrameObserved;
                try
                {
                    flyout.ShowAt(overflow);
                    await TestSupport.WaitForRenderedConditionAsync(() => flyout.IsOpen && item.IsLoaded,
                        "overflow menu opened",
                        MenuDiagnostics);
                    Invoke(item);
                    Assert.Equal(new string?[] { "systems" }, opened);
                    await TestSupport.WaitForRenderedConditionAsync(() => !flyout.IsOpen,
                        "overflow menu closed after its item ran");
                }
                finally
                {
                    try { output.WriteLine(MenuDiagnostics()); }
                    finally
                    {
                        CompositionTarget.Rendering -= FrameObserved;
                        flyout.Hide();
                    }
                }
                output.WriteLine($"Open in web at {width} DIP: one overflow item opened [{string.Join(", ", opened.Select(path => path ?? "<root>"))}].");
            }), openWeb: opened.Add, width: width, fitNativeViewport: true);
    }

    [Theory]
    [InlineData(1100, "SystemsMastOpenWeb")]
    [InlineData(740, "SystemsMastOverflow")]
    [InlineData(560, "SystemsMastOverflow")]
    public async Task SelectedMachine_HasNoBackups(double width, string expectedHandoff)
    {
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => WebHandoffs(surface) == expectedHandoff && AutomationIds(surface)
                        .Intersect(new[] { "SystemsCapabilities", "SystemsStatistics" }).Count() == 2,
                    "selected machine laid out at the expected tier", () => $"handoffs=[{WebHandoffs(surface)}]");
                Assert.DoesNotContain(AutomationIds(surface), id => id.StartsWith("SystemsBackups", StringComparison.Ordinal));
                Assert.Equal(expectedHandoff, WebHandoffs(surface));
                foreach (var element in TestSupport.FindDescendants<FrameworkElement>(surface))
                {
                    Assert.DoesNotContain("backup", (element as TextBlock)?.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("backup", AutomationProperties.GetName(element), StringComparison.OrdinalIgnoreCase);
                }
                output.WriteLine($"Selected machine at {width} DIP: handoff [{WebHandoffs(surface)}], no Backups section.");
            }), width: width);
    }

    [Theory]
    [InlineData(1100, true)]
    [InlineData(740, false)]
    public async Task Capabilities_AreNarrowWithAWideGapFromTheNextColumn(double width, bool twoColumns)
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "studio",
            DisplayName = "Studio",
            Capabilities = ["system", "exec", "screen", "camera", "browser", "files", "canvas", "clipboard", "notifications", "audio"],
            Permissions = new(StringComparer.Ordinal)
            {
                ["system"] = true,
                ["exec"] = true,
                ["screen"] = true,
                ["browser"] = true,
                ["files"] = true,
                ["canvas"] = true,
                ["notifications"] = true,
                ["camera"] = false,
                ["clipboard"] = false,
            },
        };
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => TryFind<Grid>(surface, "SystemsDetailsGrid") is { } details &&
                        details.ColumnDefinitions.Count == (twoColumns ? 3 : 1) &&
                        TryFind<FrameworkElement>(surface, "SystemsCapabilities") is { ActualHeight: > 0 } &&
                        TryFind<FrameworkElement>(surface, "SystemsStatistics") is { ActualHeight: > 0 },
                    "details grid laid out at the expected tier",
                    () => $"columns={TryFind<Grid>(surface, "SystemsDetailsGrid")?.ColumnDefinitions.Count}");
                var grid = Find<Grid>(surface, "SystemsDetailsGrid");
                var capabilities = Find<FrameworkElement>(surface, "SystemsCapabilities");
                var statistics = Find<FrameworkElement>(surface, "SystemsStatistics");
                var capabilityBounds = Bounds(capabilities, grid);
                var statisticsBounds = Bounds(statistics, grid);
                if (twoColumns)
                {
                    Assert.InRange(capabilities.ActualWidth, SystemsLayout.CapabilitiesWidth - .5, SystemsLayout.CapabilitiesWidth + .5);
                    Assert.InRange(statisticsBounds.Left - capabilityBounds.Right,
                        SystemsLayout.DetailsColumnGap - .5, SystemsLayout.DetailsColumnGap + .5);
                }
                else
                {
                    Assert.InRange(capabilities.ActualWidth, grid.ActualWidth - .5, grid.ActualWidth + .5);
                    Assert.InRange(statisticsBounds.Top - capabilityBounds.Bottom,
                        OpenClawTray.Presentation.PageResponsiveLayout.GapStack - .5, double.MaxValue);
                }
                // The narrower column must still show every label and status on one line.
                foreach (var text in TestSupport.FindDescendants<TextBlock>(capabilities).Where(candidate => candidate.Text.Length > 1))
                    Assert.True(text.ActualHeight <= SingleLineHeight(text) + 1,
                        $"'{text.Text}' wraps inside a {capabilities.ActualWidth:0} DIP Capabilities column.");
                output.WriteLine($"Capabilities at {width} DIP: width {capabilities.ActualWidth:0.#}, gap to Statistics {statisticsBounds.Left - capabilityBounds.Right:0.#} (two columns: {twoColumns}).");
            }), node, width: width);
    }

    [Fact]
    public async Task NodeManagement_WithoutPairingAccess_IsDisabledWithAReason()
    {
        await WithPageAsync(600, controller =>
        {
            controller.UpdateInputs(Inputs() with { Scopes = ["operator.read"] });
            controller.Select("node:studio");
        }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            Assert.False(controller.State.CanManageNodes);
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TestSupport.FindDescendants<Microsoft.UI.Xaml.Controls.CommandBar>(surface)
                    .SelectMany(bar => bar.PrimaryCommands.Concat(bar.SecondaryCommands)).OfType<AppBarButton>()
                    .Any(button => AutomationProperties.GetAutomationId(button) == "SystemsRenameNode"),
                "read-only management command mounted");
            Assert.False(FindCommand(surface, "SystemsRenameNode").IsEnabled);
            Assert.False(FindCommand(surface, "SystemsForgetNode").IsEnabled);
            var reason = Find<TextBlock>(surface, "SystemsManageRequiresPairing");
            Assert.Equal(SystemsText.S("Manage_PairingRequired"), reason.Text);
            output.WriteLine("Read-only operator: rename and forget disabled with a visible pairing-access reason.");
        }));
    }

    [Fact]
    public void CopiedDiagnostics_ExcludeRawPresenceTextCommandsAndPath()
    {
        var row = new SystemRow
        {
            Id = "node:studio", Kind = SystemKind.Node, Status = SystemStatus.Online, Name = "Studio",
            Node = new GatewayNodeInfo { NodeId = "studio", Commands = ["private.command"], PathEnv = "private-path" },
            Instance = new MergedInstance { Key = "studio", DebugText = "private.command private-path" },
        };
        var copied = SystemsText.DebugSummary(row);
        Assert.Contains("NodeId: studio", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("private.command", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_UseExactPermissionIdentityAndKeepPendingDeclarationsInactive()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "studio", DisplayName = "Studio",
            Capabilities = ["screen", "CAMERA"],
            Permissions = new(StringComparer.OrdinalIgnoreCase) { ["screen"] = false, ["camera"] = true },
            ApprovalState = GatewayNodeApprovalState.PendingReapproval,
            PendingDeclaredCapabilities = ["clipboard"],
        };
        await WithPageAsync(600, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(() =>
            {
                var capabilities = Find<FrameworkElement>(surface, "SystemsCapabilities");
                var names = TestSupport.FindDescendants<FrameworkElement>(capabilities).Select(AutomationProperties.GetName).ToArray();
                Assert.Contains(SystemsText.S("Capability_Screen") + ": " + SystemsText.S("Capability_TurnedOff"), names);
                Assert.Contains("CAMERA: " + SystemsText.S("Capability_NotReported"), names);
                Assert.Contains(SystemsText.S("Capability_Camera") + ": " + SystemsText.S("Capability_Allowed"), names);
                Assert.DoesNotContain(SystemsText.S("Capability_Clipboard") + ": " + SystemsText.S("Capability_Allowed"), names);
                Assert.Contains(SystemsText.S("Capability_Clipboard"), Find<TextBlock>(surface, "SystemsPendingDeclarations").Text);
            }), node);
    }

    [Fact]
    public async Task InlineExpanders_KeepMultipleRowsOpenWithoutChangingScrollPosition()
    {
        await WithPageAsync(600, _ => { }, async (surface, controller, _) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Empty(TestSupport.FindDescendants<ListView>(surface));
                var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
                var startingOffset = scroll.VerticalOffset;
                var gateway = Find<Expander>(surface, "SystemsRow_gateway");
                var studio = Find<Expander>(surface, "SystemsRow_node:studio");
                bool DetailsFit(Expander row)
                {
                    var details = TryFind<FrameworkElement>(row, "SystemsDetails");
                    if (details is null || details.ActualHeight <= 0)
                        return false;
                    var bounds = Bounds(details, row);
                    return bounds.Top >= 0 && bounds.Bottom <= row.ActualHeight + 1;
                }
                gateway.IsExpanded = false;
                studio.IsExpanded = false;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => !gateway.IsExpanded && !studio.IsExpanded &&
                        gateway.ActualHeight > 0 && studio.ActualHeight > 0 &&
                        !DetailsFit(gateway) && !DetailsFit(studio),
                    "both collapsed machine rows clip their details");
                gateway.IsExpanded = true;
                studio.IsExpanded = true;
                await TestSupport.WaitForRenderedConditionAsync(() => gateway.IsExpanded && studio.IsExpanded &&
                    DetailsFit(gateway) && DetailsFit(studio) &&
                    TestSupport.FindDescendants<FrameworkElement>(surface).Count(element =>
                        AutomationProperties.GetAutomationId(element) == "SystemsDetails") == 2,
                    "both independent machine details remain expanded",
                    () => $"Gateway expanded={gateway.IsExpanded}, height={gateway.ActualHeight}, " +
                        $"detailsFit={DetailsFit(gateway)}; Studio expanded={studio.IsExpanded}, " +
                        $"height={studio.ActualHeight}, detailsFit={DetailsFit(studio)}; " +
                        $"details={TestSupport.FindDescendants<FrameworkElement>(surface).Count(element => AutomationProperties.GetAutomationId(element) == "SystemsDetails")}, " +
                        $"selected={controller.State.SelectedId}.");
                Assert.Equal("node:studio", controller.State.SelectedId);
                Assert.Equal(2, TestSupport.FindDescendants<FrameworkElement>(surface)
                    .Count(element => AutomationProperties.GetAutomationId(element) == "SystemsDetails"));
                Assert.Equal("Studio", Find<TextBlock>(surface, "SystemsSelectedName").Text);
                Assert.InRange(Math.Abs(scroll.VerticalOffset - startingOffset), 0, .1);
                var expandedStudioHeight = studio.ActualHeight;
                studio.IsExpanded = false;
                await TestSupport.WaitForRenderedConditionAsync(() => !studio.IsExpanded && gateway.IsExpanded &&
                    studio.ActualHeight < expandedStudioHeight - 1 &&
                    !DetailsFit(studio) && DetailsFit(gateway),
                    "collapsing one row reduces its visible height and clips details while the other stays expanded",
                    () => $"Gateway expanded={gateway.IsExpanded}, height={gateway.ActualHeight}, " +
                        $"detailsFit={DetailsFit(gateway)}; Studio expanded={studio.IsExpanded}, " +
                        $"height={studio.ActualHeight}, expandedHeight={expandedStudioHeight}, detailsFit={DetailsFit(studio)}.");
            });
        }, fitNativeViewport: true);
    }

    [Fact]
    public async Task WithoutReadScope_ShowsAccessPanelInsteadOfEmptyInventory()
    {
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with { Scopes = ["operator.pairing"] }),
            async (surface, _, _) => await ui.RunOnUIAsync(() =>
            {
                Assert.True(Find<FrameworkElement>(surface, "SystemsReadAccessPanel").ActualHeight > 0);
                Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(surface),
                    element => AutomationProperties.GetAutomationId(element) is "SystemsInventory" or "SystemsEmpty");
            }));
    }

    [Fact]
    public async Task ExpandedResources_AfterDisconnect_RetainHeadingsAndVisibleLastKnownLabels()
    {
        await WithPageAsync(1000, controller => controller.Select("gateway"),
            async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TryFind<TextBlock>(surface, "SystemsStatisticsHeading") is { ActualHeight: > 0 },
                    "expanded reported resources render before disconnect");
                controller.UpdateInputs(Inputs() with { Connected = false });
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TestSupport.FindDescendants<TextBlock>(surface).Count(text =>
                        AutomationProperties.GetAutomationId(text) == "SystemsStatLastKnown" &&
                        text.ActualHeight > 0) == 3,
                    "each retained snapshot tile carries a visible last-known label");
                var gateway = Find<Expander>(surface, "SystemsRow_gateway");
                Assert.True(gateway.IsExpanded);
                foreach (var id in new[] { "SystemsCapabilitiesHeading", "SystemsStatisticsHeading", "SystemsStatsSampled" })
                {
                    var text = Find<TextBlock>(gateway, id);
                    Assert.True(text.ActualHeight > 0 && text.ActualWidth > 0, id);
                }
                foreach (var tile in TestSupport.FindDescendants<Border>(gateway).Where(tile =>
                    AutomationProperties.GetAutomationId(tile).StartsWith("SystemsStat_", StringComparison.Ordinal)))
                    Assert.Equal(SystemsText.S("Stats_LastKnownBadge"),
                        Find<TextBlock>(tile, "SystemsStatLastKnown").Text);
            }), inventory: new SystemsInventorySnapshot
            {
                Environments = [new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online,
                    Capabilities = ["browser", "exec"] }],
                SystemInfo = new GatewaySystemInfo { Measurements = new HostMeasurements
                    { CpuCount = 8, MemoryTotalBytes = 8192, MemoryFreeBytes = 4096,
                        DiskAvailableBytes = 4096, DiskTotalBytes = 8192 } },
                SystemInfoSampledAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            }, width: 1000, fitNativeViewport: true);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ResourceTiles_StretchOnlyASingleTrailingTile(int tileCount)
    {
        var now = DateTimeOffset.UtcNow;
        var node = new GatewayNodeInfo
        {
            NodeId = "studio", DisplayName = "Studio", IsOnline = true,
            HostStats = new(new HostMeasurements
            {
                CpuCount = 8, MemoryTotalBytes = 8192, MemoryFreeBytes = 4096,
                Disks = Enumerable.Range(0, tileCount - 2)
                    .Select(index => new DiskStats("/disk-" + index, 8192, 4096)).ToArray(),
            }, now),
        };
        await WithPageAsync(1000, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TryFind<StackPanel>(surface, "SystemsStatRows") is { } rows &&
                    rows.Children.OfType<Grid>().All(grid => grid.ColumnDefinitions.Count == 3) &&
                    TestSupport.FindDescendants<Border>(rows).Count(tile =>
                        AutomationProperties.GetAutomationId(tile).StartsWith("SystemsStat_", StringComparison.Ordinal) &&
                        tile.ActualWidth >= 160) == tileCount,
                    "all reported resource tiles settle into two minimum-width columns");
                var rows = Find<StackPanel>(surface, "SystemsStatRows");
                var tiles = TestSupport.FindDescendants<Border>(rows).Where(tile =>
                    AutomationProperties.GetAutomationId(tile).StartsWith("SystemsStat_", StringComparison.Ordinal)).ToArray();
                var expectedIds = new[] { "SystemsStat_cpu", "SystemsStat_memory" }.Concat(
                    Enumerable.Range(0, tileCount - 2).Select(index => "SystemsStat_disk:/disk-" + index)).ToArray();
                Assert.Equal(expectedIds, tiles.Select(AutomationProperties.GetAutomationId).ToArray());
                foreach (var (tile, index) in tiles.Select((tile, index) => (tile, index)))
                {
                    var trailingSingle = tileCount % 2 == 1 && index == tileCount - 1;
                    Assert.Equal(trailingSingle ? 3 : 1, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(tile));
                    Assert.InRange(Math.Abs(tile.ActualWidth -
                        (trailingSingle ? rows.ActualWidth : tiles[0].ActualWidth)), 0, 1);
                }
                foreach (var row in rows.Children.OfType<Grid>())
                    AssertSiblingsDoNotOverlap(row);
            }), node, width: 1000);
    }

    [Fact]
    public async Task InventoryRows_UseOneStretchedNativeDisclosureWithAlignedStatus()
    {
        await WithPageAsync(900, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var gateway = Find<Expander>(surface, "SystemsRow_gateway");
            var studio = Find<Expander>(surface, "SystemsRow_node:studio");
            await TestSupport.WaitForRenderedConditionAsync(() =>
                Find<Grid>(gateway, "SystemsItem_gateway").ActualWidth >= gateway.ActualWidth - 80 &&
                Find<Grid>(studio, "SystemsItem_node:studio").ActualWidth >= studio.ActualWidth - 80,
                "inventory headers fill their native full-width disclosure controls");
            foreach (var (row, id) in new[] { (gateway, "gateway"), (studio, "node:studio") })
            {
                Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(row));
                Assert.Equal(HorizontalAlignment.Stretch, row.HorizontalContentAlignment);
                var header = Find<Grid>(row, "SystemsItem_" + id);
                Assert.Equal(3, header.ColumnDefinitions.Count);
                Assert.DoesNotContain(TestSupport.FindDescendants<FontIcon>(header),
                    glyph => glyph.Glyph is "\uE70D" or "\uE70E");
            }
            Assert.InRange(Math.Abs(Bounds(Find<FrameworkElement>(gateway, "SystemsMeta_gateway"), gateway).Right -
                Bounds(Find<FrameworkElement>(studio, "SystemsMeta_node:studio"), studio).Right), 0, 1);
        }), width: 1000);
    }

    [Fact]
    public async Task ReportedResources_ShowCoreCountSeparatelyFromLoadAndAllDisks()
    {
        var stats = new HostMeasurements
        {
            CpuCount = 8, Load1 = 2,
            MemoryTotalBytes = 1024, MemoryFreeBytes = 256,
            Disks = [new DiskStats("/", 1024, 512), new DiskStats("/data", 2048, 1024)],
        };
        await WithPageAsync(600, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsStat_cpu"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsStat_load"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsStat_disk:/"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsStat_disk:/data"));
            Assert.False(Find<Expander>(surface, "SystemsDiagnostics").IsExpanded);
        }), inventory: new SystemsInventorySnapshot
        {
            Environments = [new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online }],
            SystemInfo = new GatewaySystemInfo { Measurements = stats },
            SystemInfoSampledAt = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task RelatedSessions_UseTitleNamedIconButtonsToOpenNativeChat()
    {
        var opened = new List<string>();
        await WithPageAsync(600, controller =>
        {
            controller.UpdateInputs(Inputs() with
            {
                Sessions = [new SessionInfo { Key = "related", DisplayName = "A related conversation", ExecNode = "studio" }],
            });
            controller.Select("node:studio");
        }, async (surface, _, _) => await ui.RunOnUIAsync(() =>
        {
            var button = Find<Button>(surface, "SystemsSession_related");
            Assert.Equal(SystemsText.F("OpenChat_Name", "A related conversation"), AutomationProperties.GetName(button));
            Assert.IsType<TextBlock>(button.Content);
            Assert.DoesNotContain(TestSupport.FindDescendants<HyperlinkButton>(surface),
                link => link.Content as string == "A related conversation");
            Invoke(button);
            Assert.Equal(["related"], opened);
        }), openSession: opened.Add);
    }

    [Fact]
    public async Task PresenceOnlyApps_DoNotExposeMachineActionsResourcesOrSessions()
    {
        await WithPageAsync(600, controller =>
        {
            controller.UpdateInputs(Inputs() with
            {
                Presence = [new PresenceEntry { Host = "Browser", InstanceId = "browser", Mode = "webchat",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
            });
            controller.Select(controller.State.Rows.Single(row => row.Kind == SystemKind.Client).Id);
        }, async (surface, _, _) => await ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsPresenceDetails"));
            var ids = TestSupport.FindDescendants<FrameworkElement>(surface).Select(AutomationProperties.GetAutomationId).ToArray();
            Assert.DoesNotContain("SystemsCommandBar", ids);
            Assert.DoesNotContain("SystemsStatistics", ids);
            Assert.DoesNotContain("SystemsSessions", ids);
            Assert.DoesNotContain("SystemsCapabilities", ids);
        }));
    }

    [Fact]
    public async Task ForgetConfirmation_AfterGatewayChange_CannotSubmit()
    {
        var writes = 0;
        await WithPageAsync(600, controller => controller.Select("node:studio"), async (surface, controller, _) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                var bar = Find<Microsoft.UI.Xaml.Controls.CommandBar>(surface, "SystemsCommandBar");
                bar.IsOpen = true;
                await ui.YieldToRenderAsync();
                Invoke(FindCommand(surface, "SystemsForgetNode"));
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 1,
                    "Systems forget confirmation opened");
                var dialog = Assert.Single(OpenDialogs(surface));
                Assert.Equal(ContentDialogButton.None, dialog.DefaultButton);
                var primary = Assert.Single(TestSupport.FindDescendants<Button>(dialog),
                    element => element.Name == "PrimaryButton");
                // Change authority, then invoke the old confirmation before Reactor can redraw it.
                controller.UpdateInputs(Inputs() with { GatewayId = "replacement" });
                Invoke(primary);
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 0,
                    "stale Systems confirmation closed");
                Assert.Equal(0, writes);
                output.WriteLine($"Gateway switch with forget confirmation: closed, writes={writes}.");
            });
        }, onForget: () => writes++);
    }

    [Fact]
    public async Task ShellInset_UpdatesRetainedScrollAtConstantWidthWithoutMovingBackdropOrRoot()
    {
        var viewport = new SystemsViewport();
        viewport.ApplyTopInset(56);
        await WithPageAsync(600, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var root = Find<Grid>(surface, "SystemsPageRoot");
            var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
            var backdrop = Find<Border>(surface, "SystemsPageBackdrop");
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 56 &&
                Math.Abs(backdrop.Height - (Find<FrameworkElement>(surface, "SystemsMastGrid").ActualHeight + 88)) < 0.5,
                "pre-mounted Systems inset retained");
            Assert.Equal(new Thickness(0), root.Margin);
            Assert.Equal(new Thickness(0), backdrop.Margin);
            viewport.ApplyTopInset(0);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 0 &&
                Math.Abs(backdrop.Height - (Find<FrameworkElement>(surface, "SystemsMastGrid").ActualHeight + 32)) < 0.5,
                "Systems inset zero reset");
            Assert.Same(root, Find<Grid>(surface, "SystemsPageRoot"));
            Assert.Same(scroll, Find<ScrollViewer>(surface, "SystemsPageScroll"));
            Assert.Same(backdrop, Find<Border>(surface, "SystemsPageBackdrop"));
            viewport.ApplyTopInset(56);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 56,
                "inset-only update at constant width");
            Assert.Equal(new Thickness(0), root.Margin);
            Assert.Equal(new Thickness(0), backdrop.Margin);
        }), viewport: viewport);
    }

    private static SystemsInputs Inputs() => new()
    {
        Connected = true,
        GatewayId = "fixture",
        Scopes = ["operator.admin"],
    };

    private async Task WithPageAsync(double height, Action<SystemsController> configure,
        Func<Border, SystemsController, List<string>, Task> assertion, GatewayNodeInfo? node = null,
        Action? onForget = null, Action<string?>? openWeb = null, bool legacyInventory = false,
        SystemsInventorySnapshot? inventory = null, double width = 740, Action<string>? openSession = null,
        ElementTheme theme = ElementTheme.Default, bool fitNativeViewport = false, Exception? inventoryError = null,
        PageGateway? pageGateway = null, SystemsViewport? viewport = null)
    {
        var gateway = pageGateway ?? new PageGateway(
            node ?? new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio" },
            legacyInventory, inventory, inventoryError);
        using var controller = new SystemsController(() => gateway);
        Border surface = null!;
        ReactorHostControl host = null!;
        ResourceDictionary resources = null!;
        Windows.Graphics.SizeInt32? previousWindowSize = null;
        var navigated = new List<string>();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                if (fitNativeViewport)
                {
                    // The suite's default 1x1 off-screen window can clip a dialog's
                    // Cancel button out of the native focus viewport.
                    previousWindowSize = ui.TestWindow.AppWindow.Size;
                    var scale = ui.Container.XamlRoot.RasterizationScale;
                    ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                        (int)Math.Ceiling((width + 32) * scale), (int)Math.Ceiling((height + 48) * scale)));
                }
                var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
                resources = (ResourceDictionary)XamlReader.Load(File.ReadAllText(Path.Combine(
                    root, "src", "OpenClaw.Tray.WinUI", "Themes", "SystemsResources.xaml")));
                Application.Current.Resources.MergedDictionaries.Add(resources);
                controller.UpdateInputs(Inputs());
                configure(controller);
                var actions = new SystemsPageActions
                {
                    PostUi = action => ui.Dispatcher.TryEnqueue(() => action()),
                    Navigate = navigated.Add,
                    OpenSession = openSession ?? (_ => throw new InvalidOperationException("Unexpected session navigation.")),
                    CopyText = _ => throw new InvalidOperationException("Unexpected clipboard write."),
                    OpenWeb = openWeb ?? (_ => throw new InvalidOperationException("Unexpected web handoff.")),
                    RenameNode = (_, _) => throw new InvalidOperationException("Unexpected node rename."),
                    ForgetNode = _ =>
                    {
                        if (onForget is null)
                            throw new InvalidOperationException("Unexpected node removal.");
                        onForget();
                        return Task.FromResult<string?>(null);
                    },
                };
                surface = new Border { Width = width, Height = height, RequestedTheme = theme };
                host = new ReactorHostControl();
                surface.Child = host;
                ui.Container.Children.Add(surface);
                var props = new SystemsPageProps(controller, actions) { Viewport = viewport ?? new SystemsViewport() };
                host.Mount(_ => Component<SystemsPageView, SystemsPageProps>(props));
            });
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => TestSupport.FindDescendants<FrameworkElement>(surface).Any(element =>
                    AutomationProperties.GetAutomationId(element) == "SystemsPageRoot" && element.ActualHeight > 0) &&
                    (!fitNativeViewport || surface.XamlRoot.Size.Width >= width && surface.XamlRoot.Size.Height >= height),
                "Systems page mounted and laid out"));
            await assertion(surface, controller, navigated);
        }
        finally
        {
            await ui.RunOnUIAsync(async () =>
            {
                if (surface is not null)
                {
                    foreach (var dialog in OpenDialogs(surface))
                        dialog.Hide();
                    await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 0,
                        "Systems dialogs closed before disposing their host");
                    surface.Child = null;
                    ui.Container.Children.Remove(surface);
                }
                host?.Dispose();
                if (resources is not null)
                    Application.Current.Resources.MergedDictionaries.Remove(resources);
                if (previousWindowSize is { } size)
                    ui.TestWindow.AppWindow.Resize(size);
            });
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindDescendants<T>(root),
            element => AutomationProperties.GetAutomationId(element) == id);

    private static void Invoke(UIElement element) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            FrameworkElementAutomationPeer.CreatePeerForElement(element).GetPattern(PatternInterface.Invoke)).Invoke();

    private static AppBarButton FindCommand(DependencyObject root, string id)
    {
        var commands = TestSupport.FindDescendants<Microsoft.UI.Xaml.Controls.CommandBar>(root)
            .SelectMany(bar => bar.PrimaryCommands.Concat(bar.SecondaryCommands)).OfType<AppBarButton>();
        return Assert.Single(commands,
            button => AutomationProperties.GetAutomationId(button) == id);
    }

    private static ContentDialog[] OpenDialogs(FrameworkElement surface) =>
        surface.XamlRoot is null ? [] : VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .SelectMany(popup => popup.Child is ContentDialog dialog
                ? new[] { dialog }
                : popup.Child is { } child ? TestSupport.FindDescendants<ContentDialog>(child) : [])
            .ToArray();




    private static IEnumerable<string> AutomationIds(DependencyObject root)
    {
        // Command bar items live in logical collections that can sit outside the visual tree.
        var commands = TestSupport.FindDescendants<Microsoft.UI.Xaml.Controls.CommandBar>(root)
            .SelectMany(bar => bar.PrimaryCommands.Concat(bar.SecondaryCommands)).OfType<FrameworkElement>();
        return TestSupport.FindDescendants<FrameworkElement>(root).Concat(commands).Distinct()
            .Select(AutomationProperties.GetAutomationId).Where(id => !string.IsNullOrEmpty(id));
    }

    private static string WebHandoffs(DependencyObject root) => string.Join(",", AutomationIds(root)
        .Where(id => id.Contains("OpenWeb", StringComparison.Ordinal) || id == "SystemsMastOverflow")
        .OrderBy(id => id, StringComparer.Ordinal));

    private static T? TryFind<T>(DependencyObject root, string id) where T : FrameworkElement =>
        TestSupport.FindDescendants<T>(root).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id);

    private static Rect Bounds(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static double SingleLineHeight(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = "Ag",
            FontSize = text.FontSize,
            FontFamily = text.FontFamily,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            LineHeight = text.LineHeight,
            LineStackingStrategy = text.LineStackingStrategy,
            Padding = text.Padding,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private sealed class PageGateway(
        GatewayNodeInfo node, bool legacyInventory, SystemsInventorySnapshot? inventory,
        Exception? inventoryError) : ISystemsGateway
    {
        private readonly TaskCompletionSource<SystemsInventorySnapshot> _heldLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNextLoad;
        private int _loadCount;
        private readonly SystemsInventorySnapshot _snapshot = inventory ?? new()
        {
            Environments = legacyInventory ? null :
            [
                new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
                new SystemEnvironment { Id = "node:studio", Type = "node", Status = SystemStatus.Online },
            ],
            Nodes = [node],
        };

        public int LoadCount => Volatile.Read(ref _loadCount);

        public void HoldNextLoad() => Volatile.Write(ref _holdNextLoad, 1);

        public void CompleteHeldLoad() => _heldLoad.TrySetResult(_snapshot);

        public async Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
        {
            var request = Interlocked.Increment(ref _loadCount);
            if (request == 1 && inventoryError is not null)
                throw inventoryError;
            if (Interlocked.Exchange(ref _holdNextLoad, 0) != 0)
                return await _heldLoad.Task.WaitAsync(cancellationToken);
            return _snapshot;
        }

        public Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(GatewaySystemInfo, DateTimeOffset)?>(null);

        public Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot.Nodes);
    }
}
