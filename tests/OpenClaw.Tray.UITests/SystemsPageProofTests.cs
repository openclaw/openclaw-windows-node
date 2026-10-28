using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using OpenClawTray.Systems;
using Windows.Foundation;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SystemsPageProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
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
                scroll = Find<ScrollViewer>(surface, "SystemsInventoryScroll");
                Assert.Contains(TestSupport.FindDescendants<TextBox>(scroll),
                    box => AutomationProperties.GetAutomationId(box) == "SystemsSearchBox");
                Assert.True(scroll.ViewportHeight > 0);
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                    "Systems inventory scrolled to its machine rows");
                var item = Find<FrameworkElement>(scroll, "SystemsItem_node:studio");
                var bounds = item.TransformToVisual(scroll)
                    .TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                Assert.True(item.ActualHeight > 0);
                Assert.InRange(bounds.Top, -0.5, scroll.ViewportHeight - 1);
                Assert.InRange(bounds.Bottom, 1, scroll.ViewportHeight + 0.5);
                Find<ListView>(scroll, "SystemsGroup_Node").SelectedIndex = 0;
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
                var banner = Find<InfoBar>(surface, "SystemsPendingPairingBanner");
                Assert.True(banner.IsOpen);
                Assert.Equal(SystemsText.S("Pairing_OwnNodeMessage"), banner.Message);
                Assert.DoesNotContain(TestSupport.FindDescendants<Button>(banner),
                    button => AutomationProperties.GetAutomationId(button).StartsWith("SystemsPairApprove_", StringComparison.Ordinal));
                var review = Find<Button>(banner, "SystemsOwnNodePairingReview");
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(review);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(["connection"], navigated);
                output.WriteLine("Own-node-only pairing: recovery banner and Connection action are visible; no approve prompt.");
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
            async (surface, _, _) => await ui.RunOnUIAsync(() =>
            {
                var declaration = Find<TextBlock>(surface, "SystemsPendingDeclarations");
                Assert.Contains(capability ? "screen" : "system.run", declaration.Text, StringComparison.Ordinal);
                Assert.True(declaration.ActualHeight > 0);
                output.WriteLine($"Pending declaration visible: approval={approval}, capability={capability}, height={declaration.ActualHeight:F2}.");
            }), node);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairingConfirmation_AfterGatewayChange_CannotSubmit(bool approve)
    {
        var writes = 0;
        await WithPageAsync(600, controller => controller.UpdateInputs(Inputs() with
        {
            PendingApprovals = [PendingApproval.FromNode(new PairingRequest { RequestId = "reviewed", NodeId = "other" })],
        }), async (surface, controller, _) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                var button = Find<Button>(surface, approve ? "SystemsPairApprove_other" : "SystemsPairReject_other");
                Invoke(button);
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 1,
                    "Systems pairing confirmation opened");
                var dialog = Assert.Single(OpenDialogs(surface));
                Assert.Equal(ContentDialogButton.Close, dialog.DefaultButton);
                var primary = Assert.Single(TestSupport.FindDescendants<Button>(dialog),
                    element => element.Name == "PrimaryButton");
                // Change authority, then invoke the old confirmation before Reactor can redraw it.
                controller.UpdateInputs(Inputs() with { GatewayId = "replacement" });
                Invoke(primary);
                await TestSupport.WaitForRenderedConditionAsync(() => OpenDialogs(surface).Length == 0,
                    "stale Systems confirmation closed");
                Assert.Equal(0, writes);
                output.WriteLine($"Gateway switch with {(approve ? "approve" : "reject")} confirmation: closed, writes={writes}.");
            });
        }, onPairingWrite: () => writes++);
    }

    private static SystemsInputs Inputs() => new()
    {
        Connected = true,
        GatewayId = "fixture",
        Scopes = ["operator.admin"],
    };

    private async Task WithPageAsync(double height, Action<SystemsController> configure,
        Func<Border, SystemsController, List<string>, Task> assertion, GatewayNodeInfo? node = null,
        Action? onPairingWrite = null)
    {
        var gateway = new PageGateway(node ?? new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio" });
        using var controller = new SystemsController(() => gateway);
        Border surface = null!;
        ReactorHostControl host = null!;
        ResourceDictionary resources = null!;
        var navigated = new List<string>();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
                resources = (ResourceDictionary)XamlReader.Load(File.ReadAllText(Path.Combine(
                    root, "src", "OpenClaw.Tray.WinUI", "Themes", "SystemsResources.xaml")));
                Application.Current.Resources.MergedDictionaries.Add(resources);
                controller.UpdateInputs(Inputs());
                configure(controller);
                var actions = new SystemsPageActions
                {
                    Navigate = navigated.Add,
                    OpenSession = _ => throw new InvalidOperationException("Unexpected session navigation."),
                    CopyText = _ => throw new InvalidOperationException("Unexpected clipboard write."),
                    RenameNode = (_, _) => throw new InvalidOperationException("Unexpected node rename."),
                    ForgetNode = _ => throw new InvalidOperationException("Unexpected node removal."),
                    DecidePairing = (_, _) =>
                    {
                        if (onPairingWrite is null)
                            throw new InvalidOperationException("Unexpected pairing mutation.");
                        onPairingWrite();
                        return Task.FromResult<string?>(null);
                    },
                    EnableHostDesktop = () => throw new InvalidOperationException("Unexpected desktop mutation."),
                };
                surface = new Border { Width = 740, Height = height };
                host = new ReactorHostControl();
                surface.Child = host;
                ui.Container.Children.Add(surface);
                host.Mount(_ => Component<SystemsPageView, SystemsPageProps>(new(controller, actions)));
            });
            await ui.YieldToRenderAsync();
            await ui.YieldToRenderAsync();
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
            });
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindDescendants<T>(root),
            element => AutomationProperties.GetAutomationId(element) == id);

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static ContentDialog[] OpenDialogs(FrameworkElement surface) =>
        surface.XamlRoot is null ? [] : VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .SelectMany(popup => popup.Child is ContentDialog dialog
                ? new[] { dialog }
                : popup.Child is { } child ? TestSupport.FindDescendants<ContentDialog>(child) : [])
            .ToArray();

    private sealed class PageGateway(GatewayNodeInfo node) : ISystemsGateway
    {
        private readonly SystemsInventorySnapshot _snapshot = new()
        {
            Environments =
            [
                new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
                new SystemEnvironment { Id = "node:studio", Type = "node", Status = SystemStatus.Online },
            ],
            Nodes = [node],
        };

        public Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken ct) => Task.FromResult(_snapshot);
        public Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken ct) =>
            Task.FromResult<(GatewaySystemInfo, DateTimeOffset)?>(null);
        public Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken ct) => Task.FromResult(_snapshot.Nodes);
        public Task<BackupStatus?> ReadBackupStatusAsync(CancellationToken ct) => Task.FromResult<BackupStatus?>(new([], [], []));
        public Task<StorageProbeResult> ProbeStorageAsync(string name, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected storage probe.");
        public Task EnableHostDesktopAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected desktop mutation.");
    }
}
