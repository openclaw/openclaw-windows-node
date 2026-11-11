using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClaw.TestSupport;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Systems;
using Windows.Foundation;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SystemsPageProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "clean")]
    [InlineData(true, "clean")]
    [InlineData(false, "issues")]
    [InlineData(true, "issues")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "disconnected")]
    [InlineData(true, "disconnected")]
    public async Task Hero_StateMatrixRetainsFocusAndGeometryAcrossMountedWidths(bool dark, string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var empty = scenario == "empty";
        var issues = scenario == "issues";
        GatewayNodeInfo[] nodes = empty ? [] : Enumerable.Range(1, 10).Select(index => new GatewayNodeInfo
        {
            NodeId = $"hero-{index}",
            DisplayName = $"Fixture machine {index}",
            Platform = "win32",
            IsOnline = !(issues || scenario == "disconnected") || index != 2,
        }).ToArray();
        var inventory = new SystemsInventorySnapshot
        {
            Nodes = nodes,
            Environments = nodes.Select(node => new SystemEnvironment
            {
                Id = "node:" + node.NodeId, Type = "node", Label = node.DisplayName, Platform = node.Platform,
                Status = node.IsOnline ? SystemStatus.Online : SystemStatus.Offline,
                LastDisconnectedAt = node.IsOnline ? null : now.AddMinutes(-15),
                LastSeenAt = now,
            }).ToArray(),
        };
        await WithPageAsync(650, controller => controller.UpdateInputs(Inputs() with
        {
            GatewayName = "Fixture home",
            LocalNodeId = empty ? null : "hero-1",
            PendingApprovals = issues ? [PendingApproval.FromNode(new PairingRequest
                { RequestId = "hero-join", NodeId = "joining", DisplayName = "Fixture joining PC" })] : [],
        }), async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(() => controller.State.HasLoaded,
                "external fixture inventory loaded before state capture");
            if (scenario == "disconnected")
            {
                controller.UpdateInputs(Inputs() with { Connected = false, GatewayName = "Fixture home", LocalNodeId = "hero-1" });
                await TestSupport.WaitForRenderedConditionAsync(() => TryFind<FrameworkElement>(surface,
                    "SystemsDisconnectedBanner") is { ActualHeight: > 0 }, "retained inventory has explicit disconnect feedback");
            }
            var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
            var focusTarget = empty || scenario == "disconnected"
                ? Find<Button>(surface, "SystemsAddSystem")
                : Find<Button>(surface, "SystemsRefreshButton");
            Assert.True(focusTarget.Focus(FocusState.Programmatic));
            var initialAddButton = Find<Button>(surface, "SystemsAddSystem");
            var initialActionHost = Find<FrameworkElement>(surface, "SystemsMastActions");
            var initialActionGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(initialActionHost));
            var initialHeroGrid = Find<Grid>(surface, "SystemsHeroGrid");
            Assert.Same(initialActionHost, VisualTreeHelper.GetParent(initialAddButton));
            Assert.Same(initialHeroGrid, VisualTreeHelper.GetParent(initialActionGrid));
            Assert.Same(focusTarget, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot));
            var initialOfflineBadge = issues || scenario == "disconnected"
                ? Find<FrameworkElement>(surface, "SystemsBadge_node:hero-2_0") : null;
            var initialBadgeHost = initialOfflineBadge is null ? null
                : VisualTreeHelper.GetParent(initialOfflineBadge);
            var initialBadgeHostParent = initialBadgeHost is null ? null
                : VisualTreeHelper.GetParent(initialBadgeHost);
            if (initialOfflineBadge is not null)
            {
                Assert.IsType<Microsoft.UI.Reactor.Layout.FlexPanel>(initialBadgeHost);
                Assert.IsType<StackPanel>(initialBadgeHostParent);
            }
            var resizeStep = 0;
            foreach (var width in new[] { 1100d, 760d, 560d, 480d, 760d, 1100d })
            {
                surface.Width = width;
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    Math.Abs(Find<FrameworkElement>(surface, "SystemsPageRoot").ActualWidth - width) <= .5 &&
                    Math.Abs(Find<FrameworkElement>(surface, "SystemsContentColumn").ActualWidth -
                        PageResponsiveLayout.ContentWidth(width)) <= .5,
                    "retained mounted Systems viewport resized");
                Assert.Same(initialAddButton, Find<Button>(surface, "SystemsAddSystem"));
                Assert.Same(initialActionHost, Find<FrameworkElement>(surface, "SystemsMastActions"));
                Assert.Same(initialActionHost, VisualTreeHelper.GetParent(initialAddButton));
                Assert.Same(initialActionGrid, VisualTreeHelper.GetParent(initialActionHost));
                Assert.Same(initialHeroGrid, Find<Grid>(surface, "SystemsHeroGrid"));
                Assert.Same(initialHeroGrid, VisualTreeHelper.GetParent(initialActionGrid));
                Assert.True(initialAddButton.IsLoaded);
                Assert.True(initialActionHost.IsLoaded);
                if (initialOfflineBadge is not null)
                {
                    var badge = Find<FrameworkElement>(surface, "SystemsBadge_node:hero-2_0");
                    Assert.Same(initialOfflineBadge, badge);
                    Assert.Same(initialBadgeHost, VisualTreeHelper.GetParent(badge));
                    Assert.Same(initialBadgeHostParent, VisualTreeHelper.GetParent(initialBadgeHost!));
                    Assert.True(badge.IsLoaded);
                    Assert.Equal(Visibility.Visible, badge.Visibility);
                    var item = Find<FrameworkElement>(surface, "SystemsItem_node:hero-2");
                    var subtitle = Find<TextBlock>(surface, "SystemsSubtitle_node:hero-2");
                    Assert.True(Bounds(badge, item).Top >= Bounds(subtitle, item).Bottom);
                }
                var content = Find<FrameworkElement>(surface, "SystemsContentColumn");
                var hero = Find<FrameworkElement>(surface, "SystemsHero");
                var summary = Find<Grid>(surface, "SystemsSummary");
                Assert.Equal(2, summary.ColumnDefinitions.Count);
                AssertSiblingsDoNotOverlap(summary);
                var onlineTile = Find<FrameworkElement>(summary, "SystemsOnlineTile");
                var issuesTile = Find<FrameworkElement>(summary, "SystemsIssuesTile");
                var onlineBounds = Bounds(onlineTile, summary);
                var issuesBounds = Bounds(issuesTile, summary);
                Assert.InRange(Math.Abs(onlineBounds.Width - issuesBounds.Width), 0, 1);
                Assert.InRange(onlineBounds.Left, -.5, .5);
                Assert.InRange(issuesBounds.Right, summary.ActualWidth - .5, summary.ActualWidth + .5);
                Assert.InRange(issuesBounds.Left - onlineBounds.Right, 11, 13);
                foreach (var tile in new[] { onlineTile, issuesTile })
                {
                    var icon = Find<Border>(tile, AutomationProperties.GetAutomationId(tile) + ":IconWell");
                    Assert.InRange(icon.ActualWidth, 35.5, 36.5);
                    Assert.InRange(icon.ActualHeight, 35.5, 36.5);
                    var iconBounds = Bounds(icon, tile);
                    Assert.True(iconBounds.Left >= 0 && iconBounds.Right <= tile.ActualWidth);
                }
                foreach (var id in new[] { "SystemsHero", "SystemsSummary", "SystemsMastOpenWeb", "SystemsAddSystem" })
                {
                    var bounds = Bounds(Find<FrameworkElement>(surface, id), content);
                    Assert.InRange(bounds.Left, -.5, content.ActualWidth);
                    Assert.InRange(bounds.Right, 0, content.ActualWidth + .5);
                }
                Assert.True(Bounds(summary, content).Top >= Bounds(hero, content).Bottom);
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.NotEqual(FocusState.Unfocused, focusTarget.FocusState);
                Assert.Same(focusTarget, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot));
                var animationsEnabled = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
                foreach (var element in new[] { hero, (FrameworkElement)summary })
                {
                    if (!animationsEnabled)
                        Assert.Empty(element.Transitions);
                    else
                    {
                        Assert.Contains(element.Transitions, transition =>
                            transition is Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition);
                    }
                }
                if (animationsEnabled)
                {
                    Assert.Contains(summary.ChildrenTransitions, transition =>
                        transition is Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { IsStaggeringEnabled: true });
                    Assert.Contains(summary.ChildrenTransitions, transition =>
                        transition is Microsoft.UI.Xaml.Media.Animation.AddDeleteThemeTransition);
                }
                else
                    Assert.Empty(summary.ChildrenTransitions);
                foreach (var badge in TestSupport.FindDescendants<Border>(surface).Where(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("SystemsBadge_", StringComparison.Ordinal)))
                {
                    foreach (var text in TestSupport.FindDescendants<TextBlock>(badge))
                        Assert.Equal(TextTrimming.None, text.TextTrimming);
                    Assert.True(badge.ActualWidth > 0);
                }
                if (empty)
                {
                    Assert.NotNull(Find<FrameworkElement>(surface, "SystemsEmpty"));
                    Assert.DoesNotContain(AutomationIds(surface), id => id.StartsWith("SystemsRow_", StringComparison.Ordinal));
                    Assert.Contains("0 of 0", string.Join(" ", TestSupport.FindDescendants<TextBlock>(
                        Find<FrameworkElement>(surface, "SystemsOnlineTile")).Select(text => text.Text)), StringComparison.Ordinal);
                    Assert.False(Find<Button>(surface, "SystemsIssuesTile").IsEnabled);
                }
                if (scenario == "disconnected")
                {
                    Assert.Equal(SystemsText.S("Hero_LastReportedOnline"),
                        Assert.Single(TestSupport.FindDescendants<TextBlock>(
                            Find<FrameworkElement>(surface, "SystemsOnlineTile")),
                            text => text.Text == SystemsText.S("Hero_LastReportedOnline")).Text);
                    Assert.Contains("Fixture home", Find<TextBlock>(surface, "SystemsMastSubtitle").Text, StringComparison.Ordinal);
                    Assert.Single(TestSupport.FindDescendants<TextBlock>(
                        Find<FrameworkElement>(surface, "SystemsMeta_node:hero-1")),
                        text => text.Text == SystemsText.S("LastKnownOnline"));
                    Assert.False(Find<Button>(surface, "SystemsRefreshButton").IsEnabled);
                    Assert.NotNull(Find<FrameworkElement>(surface, "SystemsDisconnectedBanner"));
                    Assert.Contains(SystemsText.S("LastKnownOffline"), string.Join(" ",
                        TestSupport.FindDescendants<TextBlock>(Find<FrameworkElement>(surface, "SystemsBadge_node:hero-2_0"))
                            .Select(text => text.Text)), StringComparison.Ordinal);
                }
                Assert.DoesNotContain(AutomationIds(surface), id => id is "SystemsNeedsYou" or "SystemsIssuesView");
                await MountedControlCapture.OptionalAsync(ui.TestWindow,
                    $"systems-hero-{scenario}-{(dark ? "dark" : "light")}-{resizeStep++}-{width}");
            }
            if (!empty)
            {
                scroll.ChangeView(null, 64, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(() => Math.Abs(scroll.VerticalOffset - 64) <= .5,
                    "inventory has a known retained scroll offset");
                surface.Width = 560;
                await TestSupport.WaitForRenderedConditionAsync(() => Math.Abs(
                    Find<FrameworkElement>(surface, "SystemsPageRoot").ActualWidth - 560) <= .5,
                    "resize with an existing inventory offset");
                Assert.InRange(Math.Abs(scroll.VerticalOffset - 64), 0, .5);
            }
            if (issues)
            {
                var issueTile = Find<Button>(surface, "SystemsIssuesTile");
                Assert.Equal(SystemsText.F("Hero_ShowIssues", "2"), AutomationProperties.GetName(issueTile));
                controller.UpdateInputs(Inputs() with { GatewayName = "Fixture home", LocalNodeId = "hero-1" });
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TryFind<FrameworkElement>(surface, "SystemsPairingRows") is null &&
                    AutomationProperties.GetName(Find<Button>(surface, "SystemsIssuesTile")) ==
                        SystemsText.F("Hero_ShowIssues", "1"),
                    "resolved request removes its real row while the offline machine remains badged");
                Assert.NotNull(Find<FrameworkElement>(surface, "SystemsBadge_node:hero-2_0"));
                Assert.Same(issueTile, Find<Button>(surface, "SystemsIssuesTile"));
                await MountedControlCapture.OptionalAsync(ui.TestWindow,
                    $"systems-hero-issues-{(dark ? "dark" : "light")}-pairing-resolved");
            }
            output.WriteLine($"{scenario}, {(dark ? "Dark" : "Light")}: live mounted 1100/760/560/480/760/1100; " +
                "synthetic external inventory, retained focus/scroll, no production minimum change.");
        }), inventory: inventory, width: 1100, theme: dark ? ElementTheme.Dark : ElementTheme.Light, fitNativeViewport: true);
    }

    [Fact]
    public async Task UnknownCapabilities_UseSentenceCaseLabelsInsteadOfRawIds()
    {
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                var section = await WaitForRowSectionAsync<FrameworkElement>(surface, "node:studio", "SystemsCapabilities");
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
            // Inventory chooses the first node even without an explicit Select. Establish
            // that initial realization before preparing either side of this state matrix.
            Assert.Equal("node:gap-1", controller.State.SelectedId);
            await WaitForRowSectionAsync<FrameworkElement>(surface, "node:gap-1", "SystemsDetails");
            var cards = group.Children.OfType<Expander>().ToArray();
            Border ContentChrome(Expander card)
            {
                var chrome = Assert.IsType<Grid>(VisualTreeHelper.GetChild(card, 0));
                var clip = Assert.Single(chrome.Children.OfType<Border>(), element => element.Name == "ExpanderContentClip");
                return Assert.IsType<Border>(clip.Child);
            }
            if (!expanded)
            {
                // State-changing preparation is separate from the read-only readiness helper.
                foreach (var card in cards)
                    card.IsExpanded = false;
            }
            await TestSupport.WaitForRenderedConditionAsync(() =>
            {
                if (expanded)
                {
                    var details = TryFind<FrameworkElement>(cards[0], "SystemsDetails");
                    if (!cards[0].IsExpanded || details is not { ActualHeight: > 0 }) return false;
                    var bounds = Bounds(details, cards[0]);
                    if (bounds.Top < 0 || bounds.Bottom > cards[0].ActualHeight + .5) return false;
                }
                foreach (var card in expanded ? cards.Skip(1) : cards)
                    if (card.IsExpanded || ContentChrome(card).Visibility != Visibility.Collapsed) return false;
                return cards.Zip(cards.Skip(1), (previous, current) =>
                    Math.Abs(Bounds(current, group).Top - Bounds(previous, group).Bottom - 8) <= .5).All(gap => gap);
            }, expanded ? "expanded row translation and eight-DIP gaps settle" : "all rows collapse and eight-DIP gaps settle",
                () => string.Join("; ", cards.Select(card =>
                    $"{AutomationProperties.GetAutomationId(card)} expanded={card.IsExpanded}, size={card.ActualSize}, content={ContentChrome(card).Visibility}")));
            Assert.Equal(expanded, cards[0].IsExpanded);
            Assert.All(cards.Skip(1), card => Assert.False(card.IsExpanded));
            if (!expanded)
                Assert.Null(controller.State.SelectedId);
            Assert.Equal(OpenClawTray.Presentation.PageResponsiveLayout.GapInner, group.Spacing);
            for (var index = 0; index < cards.Length; index++)
            {
                // The stock Expander composes its outline from a complete header border
                // and a content border without a top edge. Do not impose the retired card style.
                Assert.Same(DependencyProperty.UnsetValue, cards[index].ReadLocalValue(Control.BorderThicknessProperty));
                Assert.Same(DependencyProperty.UnsetValue, cards[index].ReadLocalValue(Control.CornerRadiusProperty));
                var chrome = Assert.IsType<Grid>(VisualTreeHelper.GetChild(cards[index], 0));
                var header = Assert.Single(chrome.Children.OfType<ToggleButton>(), element => element.Name == "ExpanderHeader");
                var clip = Assert.Single(chrome.Children.OfType<Border>(), element => element.Name == "ExpanderContentClip");
                var content = Assert.IsType<Border>(clip.Child);
                Assert.Equal("ExpanderContent", content.Name);
                Assert.Equal(new Thickness(1), header.BorderThickness);
                Assert.Equal(new Thickness(1, 0, 1, 1), content.BorderThickness);
                Assert.True(header.CornerRadius.TopLeft > 0 && header.CornerRadius.TopRight > 0);
                Assert.True(content.CornerRadius.BottomLeft > 0 && content.CornerRadius.BottomRight > 0);
                if (index == 0) continue;
                var previous = cards[index - 1].TransformToVisual(group).TransformBounds(
                    new Rect(0, 0, cards[index - 1].ActualWidth, cards[index - 1].ActualHeight));
                var current = cards[index].TransformToVisual(group).TransformBounds(
                    new Rect(0, 0, cards[index].ActualWidth, cards[index].ActualHeight));
                Assert.InRange(Math.Abs(current.Top - previous.Bottom - 8), 0, .5);
            }
            if (expanded)
            {
                var details = Find<FrameworkElement>(cards[0], "SystemsDetails");
                var bounds = Bounds(details, cards[0]);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= cards[0].ActualHeight + .5,
                    "Expanded contents stay inside their own bordered row.");
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
    public async Task HeroIssues_JumpsToBadgedPairingWithoutHidingInventory()
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
                () => TryFind<Button>(surface, "SystemsIssuesTile") is { IsEnabled: true },
                "issues summary and row-level problems rendered");
            Assert.Equal(SystemsText.F("DevicesAndGatewayCount", "4"),
                Find<TextBlock>(surface, "SystemsDevicesAndGatewayHeading").Text);
            Assert.Equal(SystemsText.F("Hero_ShowIssues", "4"),
                AutomationProperties.GetName(Find<Button>(surface, "SystemsIssuesTile")));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsBadge_node:offline_0"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsBadge_node:update_0"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsBadge_node:permission_0"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsWorkersGroup"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsConnectedAppsGroup"));
            var pairing = Find<Button>(surface, "SystemsPairing_NodePair:join");
            var background = pairing.Background;
            Invoke(Find<Button>(surface, "SystemsIssuesTile"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => Find<Button>(surface, "SystemsPairing_NodePair:join").FocusState != FocusState.Unfocused,
                "issues summary focuses the first badged pairing row");
            Assert.Same(background, pairing.Background);
            Assert.DoesNotContain(AutomationIds(surface), id => id is "SystemsNeedsYou" or "SystemsIssuesView");
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsWorkersGroup"));
            Assert.NotNull(Find<FrameworkElement>(surface, "SystemsConnectedAppsGroup"));
            Assert.Equal(6, AutomationIds(surface).Count(id => id.StartsWith("SystemsRow_", StringComparison.Ordinal)));
        }), inventory: inventory, width: 480, fitNativeViewport: true);
    }

    [Fact]
    public async Task PendingApprovalWithMatchingInventoryRow_PreservesRequestAndAuthoritativeMachine()
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
                () => TryFind<Button>(surface, "SystemsPairing_NodePair:pending-request") is not null,
                "one pending request remains separate from its authoritative machine");
            Assert.Single(TestSupport.FindDescendants<Button>(surface), card =>
                AutomationProperties.GetAutomationId(card).StartsWith("SystemsPairing_", StringComparison.Ordinal));
            Assert.NotNull(Find<Expander>(surface, "SystemsRow_node:pending-device"));
            Assert.Contains(SystemsText.S("Badge_Approval"),
                AutomationProperties.GetName(Find<Expander>(surface, "SystemsRow_node:pending-device")));
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
    public async Task LocalPermissionBadge_OpensPermissionsEvenWhenDisconnected(bool connected)
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
            controller.Select("node:studio");
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<HyperlinkButton>(surface, "SystemsIssueAction_node:studio_0") is not null,
                "local permission action remains available in its own row");
            var action = Find<HyperlinkButton>(surface, "SystemsIssueAction_node:studio_0");
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
                () => AutomationProperties.GetName(Find<Expander>(surface, "SystemsRow_node:studio"))
                    .Contains(SystemsText.S("LastKnownOnline"), StringComparison.Ordinal),
                "disconnected inventory is relabeled as last known");
            var row = Find<Expander>(surface, "SystemsRow_node:studio");
            Assert.Contains(SystemsText.S("LastKnownOnline"), AutomationProperties.GetName(row), StringComparison.Ordinal);
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
            Assert.NotNull(TryFind<TextBlock>(surface, "SystemsMastSubtitle"));
            Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsAttention_connection");
            Assert.Single(TestSupport.FindDescendants<TextBlock>(Find<FrameworkElement>(row, "SystemsMeta_node:studio")),
                text => text.Text == SystemsText.S("LastKnownOnline"));
            var show = Find<HyperlinkButton>(surface, "SystemsIssueAction_node:studio_0");
            Assert.True(show.IsEnabled);
            Assert.Equal(SystemsText.S("OpenWeb"), AutomationProperties.GetName(show));
        }), new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio", IsOnline = true,
            Permissions = new() { ["camera"] = false } });
    }

    [Fact]
    public async Task IssuesTile_DisablesWhenPendingRequestsResolve()
    {
        await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = Enumerable.Range(1, 3).Select(index => PendingApproval.FromNode(
                new PairingRequest { RequestId = $"join-{index}", NodeId = $"new-{index}" })).ToArray(),
        }), async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<Button>(surface, "SystemsIssuesTile") is { IsEnabled: true },
                "pending requests enable the issues summary");

            controller.UpdateInputs(Inputs());
            await TestSupport.WaitForRenderedConditionAsync(
                () => TryFind<Button>(surface, "SystemsIssuesTile") is { IsEnabled: false } &&
                    TryFind<FrameworkElement>(surface, "SystemsPairingRows") is null,
                "resolved requests remove their rows and disable the jump");
            Assert.Equal(SystemsText.F("Hero_ShowIssues", "0"),
                AutomationProperties.GetName(Find<Button>(surface, "SystemsIssuesTile")));
            Assert.DoesNotContain(AutomationIds(surface), id => id is "SystemsNeedsYou" or "SystemsIssuesView");
        }));
    }

    [Fact]
    public async Task PendingPairingRow_WrapsGuidanceAndKeepsItsBadgeWithinNarrowRoute()
    {
        const string deviceName = "Studio node awaiting approval from the remote Gateway";
        const string platform = "Windows 11 Enterprise on a remotely managed development workstation";
        var expectedMessage = SystemsText.S("Pairing_Message");
        await WithPageAsync(900, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest
                { RequestId = "join", NodeId = "new-device", DisplayName = deviceName, Platform = platform })],
        }), async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(
                () => TestSupport.FindDescendants<FrameworkElement>(surface).Any(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("SystemsPairing_", StringComparison.Ordinal)),
                "pending pairing row rendered at the narrow route width");
            var card = Find<Button>(surface, "SystemsPairing_NodePair:join");
            var message = Assert.Single(TestSupport.FindDescendants<TextBlock>(card), text =>
                text.Text.Contains(expectedMessage, StringComparison.Ordinal));
            Assert.Single(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == deviceName);
            Assert.DoesNotContain(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == expectedMessage + " " + deviceName);
            var action = Assert.Single(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == SystemsText.S("Pairing_Review"));
            Assert.Contains(platform, message.Text, StringComparison.Ordinal);
            await TestSupport.WaitForRenderedConditionAsync(() => message.ActualWidth > 0 && action.ActualHeight > 0,
                "reported platform and pairing guidance laid out");
            var cardBounds = Bounds(card, surface);
            var messageBounds = Bounds(message, surface);
            var actionBounds = Bounds(action, surface);

            Assert.True(cardBounds.Left >= 0 && cardBounds.Right <= surface.ActualWidth,
                "The pairing row stays within the allocated route width.");
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
    public async Task Hero_ScrollsWithInventoryWithoutAFullPageBackdrop(double width)
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
                Find<Border>(root, "SystemsHero").ActualHeight > 32,
                "hero and scroll extent laid out");
            scroll.ChangeView(null, 0, null, true);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.VerticalOffset == 0 &&
                Find<TextBlock>(root, "SystemsPageMarker").ActualWidth > 0, "Systems at scroll top");
            // Host and mast size effects can remount descendants. Resolve the current
            // title after those renders instead of holding a displaced pooled TextBlock.
            var mastBefore = Find<TextBlock>(root, "SystemsPageMarker").TransformToVisual(root).TransformPoint(new Point()).Y;
            var hero = Find<Border>(root, "SystemsHero");
            Assert.DoesNotContain(AutomationIds(root), id => id == "SystemsPageBackdrop");
            Assert.Contains(hero, TestSupport.FindDescendants<Border>(scroll));
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(scroll),
                text => AutomationProperties.GetAutomationId(text) == "SystemsPageMarker");
            var wash = Find<Border>(hero, "PageHeroAccentWash");
            Assert.False(wash.IsHitTestVisible);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(wash));
            scroll.ChangeView(null, 200, null, true);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.VerticalOffset >= 199 &&
                Find<TextBlock>(root, "SystemsPageMarker").TransformToVisual(root).TransformPoint(new Point()).Y < mastBefore - 150,
                "hero moves with the ordinary inventory scroll");
        }), width: width, fitNativeViewport: true, inventory: inventory);
    }

    [Fact]
    public async Task Hero_UsesSharedTitleAndDecorativeSidebarIcon()
    {
        await WithPageAsync(900, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var hero = Find<Border>(surface, "SystemsHero");
            await TestSupport.WaitForRenderedConditionAsync(() => hero.ActualHeight > 0 &&
                Math.Abs(Find<FrameworkElement>(surface, "SystemsContentColumn").ActualWidth -
                    PageResponsiveLayout.ContentWidth(surface.Width)) <= .5,
                "Systems hero uses the measured responsive width");
            var icon = Assert.IsType<Image>(Find<Border>(hero, "SystemsHero:Glyph").Child);
            Assert.Equal("ms-appx:///Assets/SidebarIcons/Instances.svg",
                Assert.IsType<Microsoft.UI.Xaml.Media.Imaging.SvgImageSource>(icon.Source).UriSource.AbsoluteUri);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(icon));
            var title = Find<TextBlock>(hero, "SystemsPageMarker");
            Assert.Equal(SystemsText.S("Title"), title.Text);
            Assert.Equal(28, title.FontSize);
            Assert.Equal(Microsoft.UI.Text.FontWeights.SemiBold, title.FontWeight);
            Assert.NotNull(title.Foreground);
            Assert.Equal(AutomationHeadingLevel.Level1,
                AutomationProperties.GetHeadingLevel(Find<TextBlock>(surface, "SystemsPageMarker")));
        }));
    }

    [Theory]
    [InlineData(1400, 3, 4)]
    [InlineData(1000, 3, 3)]
    [InlineData(999, 1, 5)]
    [InlineData(760, 1, 3)]
    [InlineData(520, 1, 2)]
    public async Task ResizedHost_KeepsActionsRowsAndResourceTilesInTheirLanes(
        double width, int detailColumns, int statColumns)
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
                    "Host-driven responsive layout reconciled",
                    () => $"host={Find<FrameworkElement>(surface, "SystemsPageRoot").ActualWidth}, " +
                        $"details={Find<Grid>(surface, "SystemsDetailsGrid").ActualWidth}, " +
                        $"statistics={Find<StackPanel>(surface, "SystemsStatRows").ActualWidth}, " +
                        $"columns={string.Join(",", Find<StackPanel>(surface, "SystemsStatRows").Children.OfType<Grid>().Select(grid => grid.ColumnDefinitions.Count))}");
                var root = Find<FrameworkElement>(surface, "SystemsPageRoot");
                // Fractional physical pixels can round a 999 DIP host by less than half a DIP.
                Assert.InRange(Math.Abs(width - root.ActualWidth), 0, .5);
                var content = Find<FrameworkElement>(surface, "SystemsContentColumn");
                Assert.InRange(content.ActualWidth, 1, OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(root.ActualWidth) + .5);
                Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsPageBackdrop");
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
                var summary = Find<Grid>(surface, "SystemsSummary");
                Assert.Equal(2, summary.ColumnDefinitions.Count);
                AssertSiblingsDoNotOverlap(summary);
                Assert.DoesNotContain(AutomationIds(surface), id => id is "SystemsNeedsYou" or "SystemsIssuesView");
                var mast = Find<Panel>(surface, "SystemsMastActions");
                AssertSiblingsDoNotOverlap(mast);
                Assert.InRange(mast.ActualHeight, 1, 96);
                var mastActions = TestSupport.FindDescendants<Button>(mast)
                    .Where(button => AutomationProperties.GetAutomationId(button) is "SystemsMastOpenWeb" or "SystemsAddSystem").ToArray();
                Assert.Equal(2, mastActions.Length);
                foreach (var action in mastActions)
                {
                    var actionBounds = Bounds(action, mast);
                    Assert.InRange(actionBounds.Top, 0, mast.ActualHeight);
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
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    OpenDialogs(surface) is [{ IsLoaded: true } dialog] &&
                    TestSupport.FindDescendants<FrameworkElement>(dialog)
                        .Any(element => element.Name == "BackgroundElement" && element.IsLoaded), "Add system dialog");
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
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    OpenDialogs(surface) is [{ IsLoaded: true } dialog] &&
                    TestSupport.FindDescendants<FrameworkElement>(dialog)
                        .Any(element => element.Name == "BackgroundElement" && element.IsLoaded), "Forget dialog");
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
    public async Task HeroAndRowBadges_UseReportedStateAndResponsiveNativeLayout()
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
                Assert.Equal(SystemsText.S("Title"), headline.Text);
                var sub = Find<TextBlock>(surface, "SystemsMastSubtitle");
                Assert.Contains("connected to Home as a node", sub.Text, StringComparison.Ordinal);
                Assert.Contains("3 of 4", string.Join(" ", TestSupport.FindDescendants<TextBlock>(
                    Find<FrameworkElement>(surface, "SystemsOnlineTile")).Select(text => text.Text)), StringComparison.Ordinal);
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
                var pairing = Assert.Single(TestSupport.FindDescendants<Button>(surface), card =>
                    AutomationProperties.GetAutomationId(card).StartsWith("SystemsPairing_", StringComparison.Ordinal));
                var offline = Find<Expander>(surface, "SystemsRow_node:machine-3");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    var pairingPosition = pairing.TransformToVisual(surface).TransformPoint(new Point());
                    var offlinePosition = offline.TransformToVisual(surface).TransformPoint(new Point());
                    return pairing.ActualWidth > 0 && offline.ActualWidth > 0 &&
                        offlinePosition.Y > pairingPosition.Y + pairing.ActualHeight;
                }, "pending pairing precedes the badged machine inventory at every width");
                var p = pairing.TransformToVisual(surface).TransformPoint(new Point());
                var o = offline.TransformToVisual(surface).TransformPoint(new Point());
                Assert.True(o.Y > p.Y + pairing.ActualHeight);
                offline.IsExpanded = true;
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TryFind<HyperlinkButton>(offline, "SystemsIssueAction_node:machine-3_0") is { IsLoaded: true },
                    "offline row owns its recovery action");
                Invoke(Find<HyperlinkButton>(offline, "SystemsIssueAction_node:machine-3_0"));
                Assert.Equal(["connection"], navigated);
                var deviceGroup = Find<StackPanel>(surface, "SystemsDevicesGroup");
                controller.UpdateInputs(Inputs() with { LocalNodeId = "not-reported" });
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    !Find<TextBlock>(surface, "SystemsMastSubtitle").Text.Contains("connected to Home as a node", StringComparison.Ordinal),
                    "An identity without a reported live node does not claim node connectivity");
                Assert.Same(deviceGroup, Find<StackPanel>(surface, "SystemsDevicesGroup"));
                Assert.Same(offline, Find<Expander>(surface, "SystemsRow_node:machine-3"));
                Assert.True(offline.IsLoaded, "Removing the pairing group must not detach an existing machine row.");
                controller.Select("node:machine-1");
                var selected = Find<Expander>(surface, "SystemsRow_node:machine-1");
                await TestSupport.WaitForRenderedConditionAsync(() => selected.IsExpanded &&
                    TestSupport.FindDescendants<FrameworkElement>(selected)
                        .Any(element => AutomationProperties.GetAutomationId(element) == "SystemsCapabilities" && element.ActualHeight > 0),
                    "Expanded machine sections are laid out");
                // Selection keeps the Gateway and offline rows open, so each expanded row owns its own sections.
                Assert.True(offline.IsExpanded);
                var gatewayRow = Find<Expander>(surface, "SystemsRow_gateway");
                Assert.True(gatewayRow.IsExpanded);
                foreach (var row in new[] { gatewayRow, offline, selected })
                {
                    var rowId = AutomationProperties.GetAutomationId(row)["SystemsRow_".Length..];
                    await WaitForRowSectionAsync<FrameworkElement>(surface, rowId, "SystemsCapabilities");
                    await WaitForRowSectionAsync<FrameworkElement>(surface, rowId, "SystemsStatistics");
                }
                var capabilities = Find<FrameworkElement>(selected, "SystemsCapabilities");
                var resources = Find<FrameworkElement>(selected, "SystemsStatistics");
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
    public async Task DelayedInitialInventory_ExpandsDefaultRowWithoutAnotherSelection()
    {
        var gateway = new PageGateway(new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio" },
            legacyInventory: false, inventory: null, inventoryError: null);
        gateway.HoldNextLoad();
        await WithPageAsync(600, _ => { }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TryFind<FrameworkElement>(surface, "SystemsLoading") is { IsLoaded: true, ActualHeight: > 0 },
                "initial inventory is still pending after the loading view mounts");
            await ui.YieldToRenderAsync();
            Assert.Null(controller.State.SelectedId);
            gateway.CompleteHeldLoad();
            await WaitForRowSectionAsync<FrameworkElement>(surface, "gateway", "SystemsCapabilities");
            Assert.Equal("gateway", controller.State.SelectedId);
            var row = Find<Expander>(surface, "SystemsRow_gateway");
            row.IsExpanded = false;
            await ui.YieldToRenderAsync();
            Assert.Null(controller.State.SelectedId);
            await controller.RefreshAsync();
            await ui.YieldToRenderAsync();
            Assert.Same(row, Find<Expander>(surface, "SystemsRow_gateway"));
            Assert.False(row.IsExpanded, "A later inventory refresh must not undo the user's collapse.");
        }), pageGateway: gateway);
    }

    [Fact]
    public async Task DeepLinkedRow_CreatedExpanded_RealizesDetailsAndKeepsUserCollapse()
    {
        var nodes = Enumerable.Range(1, 2).Select(index => new GatewayNodeInfo
        {
            NodeId = "machine-" + index, DisplayName = "Machine " + index, IsOnline = true,
        }).ToArray();
        var inventory = new SystemsInventorySnapshot
        {
            Nodes = nodes,
            Environments = nodes.Select(node => new SystemEnvironment
            {
                Id = "node:" + node.NodeId, Type = "node", Label = node.DisplayName, Status = SystemStatus.Online,
            }).Prepend(new SystemEnvironment { Id = "gateway", Type = "local", Label = "Home", Status = SystemStatus.Online }).ToArray(),
        };
        // systems:<id> selects the row before the page view mounts, so its Expander is created expanded.
        await WithPageAsync(900, controller => controller.Select("node:machine-2"),
            async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
            {
                var linked = Find<Expander>(surface, "SystemsRow_node:machine-2");
                await TestSupport.WaitForRenderedConditionAsync(() => linked.IsExpanded &&
                    TryFind<FrameworkElement>(linked, "SystemsCapabilities") is { ActualHeight: > 0 } &&
                    TryFind<FrameworkElement>(linked, "SystemsStatistics") is { ActualHeight: > 0 },
                    "the deep-linked row realizes its details");
                linked.IsExpanded = false;
                await ui.YieldToRenderAsync();
                controller.Select("node:machine-1");
                var other = Find<Expander>(surface, "SystemsRow_node:machine-1");
                await TestSupport.WaitForRenderedConditionAsync(() => other.IsExpanded &&
                    TryFind<FrameworkElement>(other, "SystemsCapabilities") is { ActualHeight: > 0 },
                    "a later selection expands its own loaded row");
                Assert.False(linked.IsExpanded, "An intentional collapse survives later selections and renders.");
                linked.IsExpanded = true;
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    TryFind<FrameworkElement>(linked, "SystemsCapabilities") is { ActualHeight: > 0 },
                    "the collapsed row expands again on request");
            }), inventory: inventory, width: 1000);
    }

    [Fact]
    public async Task ExpandedRow_RemountedIntoAnotherGroup_RealizesDetailsOnItsNewControl()
    {
        var nodes = Enumerable.Range(1, 2).Select(index => new GatewayNodeInfo
        {
            NodeId = "machine-" + index, DisplayName = "Machine " + index, IsOnline = true,
        }).ToArray();
        SystemsInventorySnapshot Snapshot(string machine2Type) => new()
        {
            Nodes = machine2Type == "node" ? nodes : nodes.Take(1).ToArray(),
            Environments = nodes.Select(node => new SystemEnvironment
            {
                Id = "node:" + node.NodeId, Type = node.NodeId == "machine-2" ? machine2Type : "node",
                Label = node.DisplayName, Status = SystemStatus.Online,
            }).Prepend(new SystemEnvironment { Id = "gateway", Type = "local", Label = "Home", Status = SystemStatus.Online }).ToArray(),
        };
        var gateway = new PageGateway(nodes[0], legacyInventory: false, Snapshot("node"), inventoryError: null);
        await WithPageAsync(900, controller => controller.Select("node:machine-2"),
            async (surface, controller, _) =>
            {
                Expander first = null!;
                await ui.RunOnUIAsync(async () =>
                {
                    first = Find<Expander>(surface, "SystemsRow_node:machine-2");
                    await TestSupport.WaitForRenderedConditionAsync(() => first.IsExpanded &&
                        TryFind<FrameworkElement>(first, "SystemsCapabilities") is { ActualHeight: > 0 },
                        "the expanded row realizes its details before it moves");
                });
                // A changed environment type moves the same row id to Workers, so Reactor mounts a new Expander.
                gateway.Replacement = Snapshot("ssh");
                await controller.RefreshAsync();
                Expander moved = null!;
                await ui.RunOnUIAsync(async () =>
                {
                    await TestSupport.WaitForRenderedConditionAsync(() =>
                        TryFind<Expander>(surface, "SystemsRow_node:machine-2") is { } row && !ReferenceEquals(row, first) &&
                        TryFind<FrameworkElement>(surface, "SystemsWorkersGroup") is { } workers &&
                        TestSupport.FindDescendants<Expander>(workers).Contains(row),
                        "the expanded row remounts into the Workers group");
                    moved = Find<Expander>(surface, "SystemsRow_node:machine-2");
                    await TestSupport.WaitForRenderedConditionAsync(() => moved.IsExpanded &&
                        TryFind<FrameworkElement>(moved, "SystemsCapabilities") is { ActualHeight: > 0 } &&
                        TryFind<FrameworkElement>(moved, "SystemsStatistics") is { ActualHeight: > 0 },
                        "the remounted row realizes its details on its new control");
                    moved.IsExpanded = false;
                    await ui.YieldToRenderAsync();
                });
                gateway.Replacement = Snapshot("node");
                await controller.RefreshAsync();
                await ui.RunOnUIAsync(async () =>
                {
                    await TestSupport.WaitForRenderedConditionAsync(() =>
                        TryFind<Expander>(surface, "SystemsRow_node:machine-2") is { IsLoaded: true } row &&
                        !ReferenceEquals(row, moved) && TryFind<FrameworkElement>(surface, "SystemsDevicesGroup") is { } devices &&
                        TestSupport.FindDescendants<Expander>(devices).Contains(row),
                        "the collapsed row remounts back into Devices");
                    await ui.YieldToRenderAsync();
                    Assert.False(Find<Expander>(surface, "SystemsRow_node:machine-2").IsExpanded,
                        "An intentional collapse survives a remount.");
                });
            }, inventory: Snapshot("node"), width: 1000, pageGateway: gateway);
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
                var card = Find<Button>(surface, "SystemsPairing_NodePair:own-full-id");
                Assert.True(card.ActualHeight > 0);
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(card),
                    text => text.Text.Contains(SystemsText.S("Pairing_Message"), StringComparison.Ordinal));
                Assert.DoesNotContain(TestSupport.FindDescendants<Button>(card),
                    button => AutomationProperties.GetAutomationId(button).StartsWith("SystemsPairApprove_", StringComparison.Ordinal));
                var review = card;
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(review);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(["connection"], navigated);
                output.WriteLine("Own-node-only pairing: request row opens Connection; no approve prompt.");
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
                var declaration = await WaitForRowSectionAsync<TextBlock>(surface, "node:studio", "SystemsPendingDeclarations");
                Assert.Empty(node.Capabilities);
                Assert.Empty(node.Commands);
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
            var card = Find<Button>(surface, "SystemsPairing_NodePair:reviewed");
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(card),
                text => text.Text == (displayName is null ? SystemsText.S("Pairing_TitleOne")
                    : displayName));
            // The Connection page owns approve and reject, so Systems offers no decision of its own.
            Assert.Contains(SystemsText.S("Badge_Join"), AutomationProperties.GetName(card), StringComparison.Ordinal);
            Invoke(card);
            Assert.Equal(["connection"], navigated);
            output.WriteLine("Pending approval: request row routes to Connection; no inline approve or reject.");
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
    public async Task OpenWeb_BelowTheWideTierIsOneVisibleDirectAction(double width)
    {
        var opened = new List<string?>();
        await WithPageAsync(900, controller => controller.Select("node:studio"),
            async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => WebHandoffs(surface) == "SystemsMastOpenWeb" && controller.State.HasWebSystemsPage,
                    "single direct Open in web action",
                    () => $"handoffs=[{WebHandoffs(surface)}] modern={controller.State.HasWebSystemsPage}");
                var action = Find<Button>(surface, "SystemsMastOpenWeb");
                var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
                scroll.ChangeView(null, 0, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(() => action.IsLoaded &&
                    Bounds(action, surface).Top >= Bounds(scroll, surface).Top &&
                    Bounds(action, surface).Bottom <= Bounds(scroll, surface).Bottom &&
                    Bounds(action, surface).Left >= Bounds(scroll, surface).Left &&
                    Bounds(action, surface).Right <= Bounds(scroll, surface).Right &&
                    action.ActualWidth > 0 && action.ActualHeight > 0,
                    "direct action is loaded and entirely visible in the scroll viewport",
                    () => $"action={Bounds(action, surface)}, viewport={Bounds(scroll, surface)}, loaded={action.IsLoaded}");
                // The visible label stays short; the accessible name states the Systems destination.
                Assert.Equal(SystemsText.S("OpenWeb_Name"), AutomationProperties.GetName(action));
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(action),
                    text => text.Text == SystemsText.S("OpenWeb") && text.ActualWidth > 0);
                Assert.Null(action.Flyout);
                Invoke(action);
                Assert.Equal(new string?[] { "systems" }, opened);
                output.WriteLine($"Open in web at {width} DIP: one visible direct action opened systems. This does not reprove the retired overflow popup.");
            }), openWeb: opened.Add, width: width, fitNativeViewport: true);
    }

    [Theory]
    [InlineData(1100, "SystemsMastOpenWeb")]
    [InlineData(740, "SystemsMastOpenWeb")]
    [InlineData(560, "SystemsMastOpenWeb")]
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
            async (surface, _, _) => await ui.RunOnUIAsync(async () =>
            {
                var capabilities = await WaitForRowSectionAsync<FrameworkElement>(surface, "node:studio", "SystemsCapabilities");
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
                await WaitForSelectedRowAsync(surface, controller, "node:studio", SystemKind.Node);
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
    [InlineData(4, 560, 2)]
    [InlineData(5, 560, 2)]
    [InlineData(4, 1000, 3)]
    [InlineData(5, 1000, 3)]
    public async Task ResourceTiles_StretchOnlyASingleTrailingTile(int tileCount, double width, int columns)
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
                    rows.Children.OfType<Grid>().All(grid => grid.ColumnDefinitions.Count == columns * 2 - 1) &&
                    TestSupport.FindDescendants<Border>(rows).Count(tile =>
                        AutomationProperties.GetAutomationId(tile).StartsWith("SystemsStat_", StringComparison.Ordinal) &&
                        tile.ActualWidth >= 160) == tileCount,
                    "all reported resource tiles settle into the expected minimum-width columns",
                    () => $"host={width}, statistics={TryFind<StackPanel>(surface, "SystemsStatRows")?.ActualWidth}, expectedColumns={columns}");
                var rows = Find<StackPanel>(surface, "SystemsStatRows");
                var tiles = TestSupport.FindDescendants<Border>(rows).Where(tile =>
                    AutomationProperties.GetAutomationId(tile).StartsWith("SystemsStat_", StringComparison.Ordinal)).ToArray();
                var expectedIds = new[] { "SystemsStat_cpu", "SystemsStat_memory" }.Concat(
                    Enumerable.Range(0, tileCount - 2).Select(index => "SystemsStat_disk:/disk-" + index)).ToArray();
                Assert.Equal(expectedIds, tiles.Select(AutomationProperties.GetAutomationId).ToArray());
                foreach (var (tile, index) in tiles.Select((tile, index) => (tile, index)))
                {
                    var trailingSingle = tileCount % columns == 1 && index == tileCount - 1;
                    Assert.Equal(trailingSingle ? columns * 2 - 1 : 1, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(tile));
                    Assert.InRange(Math.Abs(tile.ActualWidth -
                        (trailingSingle ? rows.ActualWidth : tiles[0].ActualWidth)), 0, 1);
                }
                foreach (var row in rows.Children.OfType<Grid>())
                    AssertSiblingsDoNotOverlap(row);
            }), node, width: width);
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
                Assert.Same(DependencyProperty.UnsetValue, row.ReadLocalValue(Control.BorderThicknessProperty));
                Assert.Same(DependencyProperty.UnsetValue, row.ReadLocalValue(Control.CornerRadiusProperty));
                var header = Find<Grid>(row, "SystemsItem_" + id);
                Assert.Empty(TestSupport.FindDescendants<Button>(header));
                var subtitleBounds = Bounds(Find<TextBlock>(header, "SystemsSubtitle_" + id), header);
                Assert.True(header.ActualHeight - subtitleBounds.Bottom >= 7.5,
                    "Header content keeps at least eight DIPs of bottom breathing room.");
                Assert.Equal(3, header.ColumnDefinitions.Count);
                Assert.DoesNotContain(TestSupport.FindDescendants<FontIcon>(header),
                    glyph => glyph.Glyph is "\uE70D" or "\uE70E");
            }
            Assert.InRange(Math.Abs(Bounds(Find<FrameworkElement>(gateway, "SystemsMeta_gateway"), gateway).Right -
                Bounds(Find<FrameworkElement>(studio, "SystemsMeta_node:studio"), studio).Right), 0, 1);
        }), width: 1000);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(760)]
    [InlineData(1100)]
    public async Task InventoryHeaders_KeepPaddedBadgesAndAlignedCompactStatus(double width)
    {
        var inventory = new SystemsInventorySnapshot
        {
            Environments =
            [
                new SystemEnvironment
                {
                    Id = "node:update", Type = "node", Label = "Fixture Raspberry Pi",
                    Status = SystemStatus.Online, Issues = [new RuntimeTargetIssue("update-required", null)],
                },
                new SystemEnvironment { Id = "node:plain", Type = "node", Label = "Fixture online", Status = SystemStatus.Online },
                new SystemEnvironment
                {
                    Id = "node:offline", Type = "node", Label = "Fixture offline",
                    Status = SystemStatus.Offline, LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2),
                },
            ],
        };
        await WithPageAsync(900, _ => { }, async (surface, controller, _) => await ui.RunOnUIAsync(async () =>
        {
            await TestSupport.WaitForRenderedConditionAsync(() =>
                controller.State.HasLoaded &&
                TryFind<TextBlock>(surface, "SystemsIssueSummary_node:update") is { ActualWidth: > 0, ActualHeight: > 0 } &&
                Math.Abs(Find<FrameworkElement>(surface, "SystemsContentColumn").ActualWidth -
                    PageResponsiveLayout.ContentWidth(width)) <= .5,
                "inventory issue summary is rendered");
            var item = Find<Grid>(surface, "SystemsItem_node:update");
            var meta = Find<FrameworkElement>(surface, "SystemsMeta_node:update");
            var summary = Find<TextBlock>(surface, "SystemsIssueSummary_node:update");
            var name = Assert.Single(TestSupport.FindDescendants<TextBlock>(item),
                text => text.Text == "Fixture Raspberry Pi");
            var badge = Find<FrameworkElement>(surface, "SystemsBadge_node:update_0");
            Assert.Equal(SystemsText.S("Badge_Update"), summary.Text);
            Assert.Equal(SystemsText.S("Issue_UpdateRequired"), ToolTipService.GetToolTip(summary));
            Assert.Equal(TextWrapping.Wrap, summary.TextWrapping);
            Assert.Equal(TextTrimming.None, summary.TextTrimming);
            var summaryBounds = Bounds(summary, item);
            var metaBounds = Bounds(meta, item);
            Assert.InRange(summaryBounds.Left, -.5, item.ActualWidth);
            Assert.InRange(summaryBounds.Right, 0, item.ActualWidth + .5);
            Assert.InRange(summaryBounds.Bottom, 0, item.ActualHeight + .5);
            foreach (var other in new FrameworkElement[] { name, badge })
            {
                var otherBounds = Bounds(other, item);
                Assert.True(metaBounds.Right <= otherBounds.Left + .5 || metaBounds.Left >= otherBounds.Right - .5 ||
                    metaBounds.Bottom <= otherBounds.Top + .5 || metaBounds.Top >= otherBounds.Bottom - .5,
                    $"width={width}, metadata={metaBounds}, other={otherBounds}");
            }
            var statusLeft = Bounds(Find<FrameworkElement>(item, "SystemsStatusBadge"), surface).Left;
            foreach (var id in new[] { "node:update", "node:plain", "node:offline" })
            {
                var header = Find<Grid>(surface, "SystemsItem_" + id);
                var status = Find<FrameworkElement>(header, "SystemsStatusBadge");
                Assert.InRange(Math.Abs(Bounds(status, surface).Left - statusLeft), 0, 1);
                foreach (var pill in TestSupport.FindDescendants<Border>(header).Where(element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("SystemsBadge_", StringComparison.Ordinal)))
                {
                    var bounds = Bounds(pill, header);
                    Assert.True(bounds.Top >= 7.5 && header.ActualHeight - bounds.Bottom >= 7.5,
                        $"Badge needs vertical breathing room: header={header.ActualHeight}, badge={bounds}");
                }
                if (TryFind<TextBlock>(header, "SystemsIssueSummary_" + id) is { } caption)
                    Assert.InRange(Math.Abs(Bounds(caption, surface).Left - statusLeft), 0, 1);
            }
            Find<Expander>(surface, "SystemsRow_node:update").IsExpanded = true;
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TestSupport.FindDescendants<TextBlock>(Find<Expander>(surface, "SystemsRow_node:update"))
                    .Any(text => text.Text == SystemsText.S("Issue_UpdateRequired") && text.ActualHeight > 0),
                "expanded update details retain the full guidance");
        }), inventory: inventory, width: width);
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
        await WithPageAsync(600, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            await WaitForRowSectionAsync<FrameworkElement>(surface, "gateway", "SystemsStat_cpu");
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
                var bar = await WaitForRowSectionAsync<Microsoft.UI.Xaml.Controls.CommandBar>(surface, "node:studio", "SystemsCommandBar");
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
    public async Task ShellInset_UpdatesRetainedScrollAtConstantWidthWithoutMovingRoot()
    {
        var viewport = new SystemsViewport();
        viewport.ApplyTopInset(56);
        await WithPageAsync(600, _ => { }, async (surface, _, _) => await ui.RunOnUIAsync(async () =>
        {
            var root = Find<Grid>(surface, "SystemsPageRoot");
            var scroll = Find<ScrollViewer>(surface, "SystemsPageScroll");
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 56,
                "pre-mounted Systems inset retained");
            Assert.Equal(new Thickness(0), root.Margin);
            Assert.DoesNotContain(AutomationIds(surface), id => id == "SystemsPageBackdrop");
            viewport.ApplyTopInset(0);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 0,
                "Systems inset zero reset");
            Assert.Same(root, Find<Grid>(surface, "SystemsPageRoot"));
            Assert.Same(scroll, Find<ScrollViewer>(surface, "SystemsPageScroll"));
            viewport.ApplyTopInset(56);
            await TestSupport.WaitForRenderedConditionAsync(() => scroll.Margin.Top == 56,
                "inset-only update at constant width");
            Assert.Equal(new Thickness(0), root.Margin);
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
        SystemRow? initialSelection = null;
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
                initialSelection = controller.State.CanRead ? controller.State.Selected : null;
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
                "Systems page mounted and laid out",
                () => $"requested={width}x{height}, fitNativeViewport={fitNativeViewport}, " +
                    $"surface={surface.ActualWidth}x{surface.ActualHeight}, xamlRoot={surface.XamlRoot?.Size}, " +
                    $"window={ui.TestWindow.AppWindow.Size}, root={TryFind<FrameworkElement>(surface, "SystemsPageRoot")?.ActualSize}"));
            // A laid-out root precedes Loaded-driven expansion and the selected header render.
            // Loading, empty, inaccessible and unresolved-selection scenarios have no row to await.
            if (initialSelection is { } selected)
                await ui.RunOnUIAsync(() => WaitForSelectedRowAsync(surface, controller, selected.Id, selected.Kind));
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

    // Observe the requested selection, never repair it. Controller publication precedes its native header.
    private static async Task WaitForSelectedRowAsync(FrameworkElement surface, SystemsController controller,
        string rowId, SystemKind kind)
    {
        await WaitForRowSectionAsync<FrameworkElement>(surface, rowId,
            kind == SystemKind.Client ? "SystemsPresenceDetails" : "SystemsDetails");
        await TestSupport.WaitForRenderedConditionAsync(() =>
        {
            var row = TryFind<Expander>(surface, "SystemsRow_" + rowId);
            return controller.State.SelectedId == rowId && row is { IsLoaded: true, IsExpanded: true } &&
                TryFind<TextBlock>(row, "SystemsSelectedName") is
                    { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible };
        }, $"selected header is rendered on {rowId}",
            () => $"selected={controller.State.SelectedId}, rendered={TryFind<TextBlock>(surface, "SystemsSelectedName")?.Text}");
        Assert.Equal(rowId, controller.State.SelectedId);
        Assert.Same(Find<TextBlock>(Find<Expander>(surface, "SystemsRow_" + rowId), "SystemsSelectedName"),
            Find<TextBlock>(surface, "SystemsSelectedName"));
    }

    // Loaded row expansion deliberately takes another Reactor render after the page root mounts.
    // Wait for the requested row's actual visible section; never expand it or change selection here.
    private static async Task<T> WaitForRowSectionAsync<T>(FrameworkElement surface, string rowId, string sectionId)
        where T : FrameworkElement
    {
        await TestSupport.WaitForRenderedConditionAsync(() =>
        {
            var row = TryFind<Expander>(surface, "SystemsRow_" + rowId);
            return row is { IsLoaded: true, IsExpanded: true } &&
                TryFind<T>(row, sectionId) is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible };
        }, $"{rowId} realizes visible {sectionId}", () =>
        {
            var row = TryFind<Expander>(surface, "SystemsRow_" + rowId);
            return $"rowLoaded={row?.IsLoaded}, expanded={row?.IsExpanded}, rowSize={row?.ActualSize}, " +
                $"sectionSize={(row is null ? null : TryFind<T>(row, sectionId)?.ActualSize)}";
        });
        return Find<T>(Find<Expander>(surface, "SystemsRow_" + rowId), sectionId);
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

        public SystemsInventorySnapshot? Replacement { get; set; }

        public void HoldNextLoad() => Volatile.Write(ref _holdNextLoad, 1);

        public void CompleteHeldLoad() => _heldLoad.TrySetResult(_snapshot);

        public async Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
        {
            var request = Interlocked.Increment(ref _loadCount);
            if (request == 1 && inventoryError is not null)
                throw inventoryError;
            if (Interlocked.Exchange(ref _holdNextLoad, 0) != 0)
                return await _heldLoad.Task.WaitAsync(cancellationToken);
            return Replacement ?? _snapshot;
        }

        public Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(GatewaySystemInfo, DateTimeOffset)?>(null);

        public Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult((Replacement ?? _snapshot).Nodes);
    }
}
