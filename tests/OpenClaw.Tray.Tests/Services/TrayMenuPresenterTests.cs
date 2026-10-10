using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class TrayMenuPresenterTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 2, 19, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, ConnectionStatus.Connected, true, true, "Connected - toggle off to disconnect")]
    [InlineData(null, ConnectionStatus.Connecting, true, false, "Connecting - toggle off to disconnect")]
    [InlineData(null, ConnectionStatus.Disconnected, false, true, "Disconnected - toggle on to connect")]
    [InlineData(null, ConnectionStatus.Error, false, true, "Connection error - toggle on to connect")]
    [InlineData(OverallConnectionState.Connected, ConnectionStatus.Disconnected, true, true, "Connected - toggle off to disconnect")]
    [InlineData(OverallConnectionState.Ready, ConnectionStatus.Disconnected, true, true, "Connected - toggle off to disconnect")]
    [InlineData(OverallConnectionState.Connecting, ConnectionStatus.Disconnected, true, false, "Connecting - toggle off to disconnect")]
    [InlineData(OverallConnectionState.Disconnecting, ConnectionStatus.Connected, false, false, "Disconnecting - toggle on to connect")]
    [InlineData(OverallConnectionState.Degraded, ConnectionStatus.Disconnected, true, true, "Degraded - toggle off to disconnect")]
    [InlineData(OverallConnectionState.PairingRequired, ConnectionStatus.Disconnected, true, true, "Pairing required - toggle off to disconnect")]
    [InlineData(OverallConnectionState.Error, ConnectionStatus.Connected, false, true, "Connection error - toggle on to connect")]
    [InlineData(OverallConnectionState.Idle, ConnectionStatus.Connected, false, true, "Disconnected - toggle on to connect")]
    public void ConnectionToggle_ProjectsOverallFallbackAndTransientStates(
        OverallConnectionState? overall,
        ConnectionStatus fallback,
        bool expectedOn,
        bool expectedEnabled,
        string expectedToolTip)
    {
        var result = ConnectionTogglePresenter.Present(fallback, overall);

        Assert.Equal(expectedOn, result.IsOn);
        Assert.Equal(expectedEnabled, result.IsEnabled);
        Assert.Equal(expectedToolTip, result.ToolTip);
        Assert.Equal("Gateway connection", result.AutomationName);
    }

    [Fact]
    public void Connected_ProjectsExactTopLevelAndNestedOrder()
    {
        var presentation = Present(DetailedSnapshot());

        Assert.Equal(
            [
                "BrandHeader:OpenClaw",
                "DashboardGlance:Authentication failed · localhost:7070",
                "Action:Pairing approval pending (3)",
                "GatewayCard:Gateway",
                "DeviceCard:Work PC",
                "Separator:",
                "SessionsSummary:Sessions",
                "UsageSummary:Usage",
                "Separator:",
                "Flyout:Permissions",
                "Action:Dashboard",
                "Action:Chat",
                "Action:Canvas",
                "Action:Diagnostics",
                "Action:Reconfigure...",
                "Separator:",
                "Action:Companion Settings...",
                "Action:About",
                "Action:Close",
            ],
            presentation.Items.Select(Key));

        var gateway = Find(presentation, "connection");
        Assert.Equal(
            [
                "Header:Gateway",
                "StatusCard:Connected · localhost:7070",
                "ErrorText:token expired",
                "Header:Server",
                "KeyValue:Version",
                "KeyValue:Auth",
                "KeyValue:Protocol",
                "KeyValue:Uptime",
                "KeyValue:Conn ID",
                "Header:Clients (1)",
                "KeyValue:desktop",
                "Header:Pending approval",
                "KeyValue:Nodes",
                "KeyValue:Devices",
                "Spacer:",
            ],
            gateway.Children.Select(Key));

        var device = Find(presentation, "nodes");
        Assert.Equal(
            [
                "Header:Work PC",
                "StatusCard:Online · Windows · node",
                "Header:Capabilities (2) · Commands (3)",
                "Capability:camera",
                "Capability:screen",
                "Capability:system",
                "Spacer:",
            ],
            device.Children.Select(Key));

        var sessions = Find(presentation, "sessions");
        Assert.Equal(["Header:Sessions (1)", "SessionCard:Research"], sessions.Children.Select(Key));

        var usage = Find(presentation, "usage");
        Assert.Equal(
            [
                "Header:Usage",
                "UsageTotals:$2.50",
                "Header:Providers",
                "UsageProvider:Anthropic · Pro",
                "Header:By Model",
                "KeyValue:claude-sonnet",
                "Spacer:",
            ],
            usage.Children.Select(Key));
    }

    [Fact]
    public void Disconnected_OmitsOnlineOnlyDevicesAndUsageButKeepsExactActions()
    {
        var snapshot = DetailedSnapshot() with
        {
            CurrentStatus = ConnectionStatus.Disconnected,
            OverallState = OverallConnectionState.Idle,
            Nodes =
            [
                DetailedSnapshot().Nodes[0] with { IsOnline = false },
            ],
            NodePendingPairCount = 0,
            DevicePendingPairCount = 0,
            AuthFailureMessage = null,
        };

        var presentation = Present(snapshot);

        Assert.Equal(
            [
                "BrandHeader:OpenClaw",
                "DashboardGlance:Disconnected · localhost:7070",
                "GatewayCard:Gateway",
                "Separator:",
                "SessionsSummary:Sessions",
                "Separator:",
                "Flyout:Permissions",
                "Action:Dashboard",
                "Action:Chat",
                "Action:Canvas",
                "Action:Diagnostics",
                "Action:Reconfigure...",
                "Separator:",
                "Action:Companion Settings...",
                "Action:About",
                "Action:Close",
            ],
            presentation.Items.Select(Key));
        Assert.DoesNotContain(presentation.Items, item => item.Kind == TrayMenuElementKind.DeviceCard);
        Assert.DoesNotContain(presentation.Items, item => item.Kind == TrayMenuElementKind.UsageSummary);
    }

    [Fact]
    public void GatewayPairingAuthPresenceSelfAndDeviceCapabilities_AreFullyProjected()
    {
        var presentation = Present(DetailedSnapshot());
        var pairing = presentation.Items.Single(item => item.ActionId == "hub");
        var gateway = Find(presentation, "connection");
        var device = Find(presentation, "nodes");

        Assert.Equal(TrayMenuIconIdentity.Approvals, pairing.Icon);
        Assert.Equal("localhost:7070 · connected · 1 client · node paired", gateway.Detail);
        Assert.Equal("token expired", gateway.Error);
        Assert.Equal("Local", gateway.Badge);
        Assert.Equal(TrayMenuAccent.Success, gateway.Accent);
        Assert.Equal("Gateway Connected. Activate to open connection settings.", gateway.AutomationName);

        Assert.Equal("Online · node · Desktop · app 1.2.3", device.Detail);
        Assert.Equal("windows", device.Badge);
        Assert.Equal("Last seen 4m ago", device.Children[1].Detail);
        Assert.Equal(TrayMenuIconIdentity.Camera, device.Children[3].Icon);
        Assert.Equal("snap, stream", device.Children[3].Detail);
        Assert.Equal(TrayMenuIconIdentity.System, device.Children[5].Icon);
        Assert.Equal("run", device.Children[5].Detail);
    }

    [Fact]
    public void SessionsAndUsage_ProjectCountsCardsTokensAgesProvidersWindowsModelsAndErrors()
    {
        var presentation = Present(DetailedSnapshot());
        var sessions = Find(presentation, "sessions");
        var card = sessions.Children.Single(item => item.Kind == TrayMenuElementKind.SessionCard);
        var usage = Find(presentation, "usage");
        var totals = usage.Children.Single(item => item.Kind == TrayMenuElementKind.UsageTotals);
        var provider = usage.Children.Single(item => item.Kind == TrayMenuElementKind.UsageProvider);

        Assert.Equal("1 working · 12.0K tokens", sessions.Detail);
        Assert.Equal("claude-sonnet", card.Detail);
        Assert.Equal("12.0K/100.0K (12%)", card.Secondary);
        Assert.Equal("5m ago", card.Tertiary);
        Assert.Equal(12, card.ProgressPercent);

        Assert.Equal("$2.50 · 20.0K tokens", usage.Detail);
        Assert.Equal("20.0K tokens · in 8.0K · out 4.0K · 3 requests", totals.Detail);
        Assert.Equal("rate limited", provider.Error);
        Assert.Collection(
            provider.Children,
            window =>
            {
                Assert.Equal("5 hours", window.Text);
                Assert.Equal("81%", window.Detail);
                Assert.Equal(81.9, window.ProgressPercent);
            },
            window =>
            {
                Assert.Equal("Week", window.Text);
                Assert.Equal("20%", window.Detail);
                Assert.Equal(20.5, window.ProgressPercent);
            });
    }

    [Fact]
    public void Sessions_UseGatewayRunLivenessAndBackgroundClassification()
    {
        var foreground = DetailedSnapshot().Sessions[0] with
        {
            Status = "completed",
            HasActiveRun = true,
        };
        var background = foreground with
        {
            Key = "agent:main:tui-id:heartbeat",
            Classification = "heartbeat",
            IsBackground = true,
        };
        var presentation = Present(DetailedSnapshot() with { Sessions = [foreground, background] });
        var sessions = Find(presentation, "sessions");

        Assert.Equal("1 working · 12.0K tokens", sessions.Detail);
        Assert.Single(sessions.Children, item => item.Kind == TrayMenuElementKind.SessionCard);
    }

    [Fact]
    public void Permissions_ProjectAllTenTogglesInOrderWithStableStoreBeforeReconnectActions()
    {
        var permissions = Find(Present(DetailedSnapshot()), "permissions");
        var toggles = permissions.Children.Where(item => item.Kind == TrayMenuElementKind.Toggle).ToArray();

        Assert.Equal(
            [
                "Windows node",
                "System tools",
                "Browser control",
                "Camera",
                "Canvas",
                "Screen capture",
                "Location",
                "Voice (TTS)",
                "Speech-to-text (STT)",
                "Ollama",
            ],
            toggles.Select(item => item.Text));
        Assert.Equal(
            [
                "perm-toggle|Windows node",
                "perm-toggle|System tools",
                "perm-toggle|Browser control",
                "perm-toggle|Camera",
                "perm-toggle|Canvas",
                "perm-toggle|Screen capture",
                "perm-toggle|Location",
                "perm-toggle|Voice (TTS)",
                "perm-toggle|Speech-to-text (STT)",
                "perm-toggle|Ollama",
            ],
            toggles.Select(item => item.ActionId));
        Assert.Equal([true, false, true, false, true, false, true, false, true, false], toggles.Select(item => item.IsChecked));
        Assert.All(toggles, toggle => Assert.Equal(toggle.Text, toggle.AutomationName));
        Assert.Equal("Let agents use Ollama models installed separately on this PC", toggles[^1].Detail);
    }

    [Fact]
    public void SetupAndStandardActions_PreserveVisibilityLabelsIconsAcceleratorAndAccessibility()
    {
        var presentation = Present(DetailedSnapshot());
        var actions = presentation.Items.Where(item => item.Kind == TrayMenuElementKind.Action).ToArray();

        Assert.Contains(actions, item => item.Text == "Reconfigure..." && item.Icon == TrayMenuIconIdentity.Setup);
        Assert.Contains(actions, item =>
            item.Text == "Companion Settings..." &&
            item.Icon == TrayMenuIconIdentity.Settings &&
            item.Accelerator == "Ctrl+Alt+;" &&
            item.ActionId == "companion");
        Assert.Contains(actions, item => item.Text == "Dashboard" && item.Icon == TrayMenuIconIdentity.Dashboard);
        Assert.Contains(actions, item => item.Text == "Chat" && item.Icon == TrayMenuIconIdentity.Chat);
        Assert.Contains(actions, item => item.Text == "Canvas" && item.Icon == TrayMenuIconIdentity.Canvas);
        Assert.Contains(actions, item => item.Text == "Diagnostics" && item.Icon == TrayMenuIconIdentity.Diagnostics);
        Assert.Contains(actions, item => item.Text == "About" && item.Icon == TrayMenuIconIdentity.About);
        Assert.Contains(actions, item => item.Text == "Close" && item.Icon == TrayMenuIconIdentity.Close);
        Assert.All(
            presentation.Items.Where(item => item.Kind is not TrayMenuElementKind.Separator),
            item => Assert.False(string.IsNullOrWhiteSpace(item.AutomationName)));

        var hidden = Present(DetailedSnapshot() with { ShowSetupMenuEntry = false });
        Assert.DoesNotContain(hidden.Items, item => item.ActionId == "setup");
    }

    [Fact]
    public void EqualSnapshotAndClock_ProduceEqualPresentation()
    {
        var snapshot = DetailedSnapshot();

        var first = Present(snapshot);
        var second = Present(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    private static TrayMenuPresentation Present(TrayMenuSnapshot snapshot) =>
        new TrayMenuPresenter(snapshot, NowUtc).Present();

    private static TrayMenuElement Find(TrayMenuPresentation presentation, string actionId) =>
        presentation.Items.Single(item => item.ActionId == actionId);

    private static string Key(TrayMenuElement item) => $"{item.Kind}:{item.Text}";

    private static TrayMenuSnapshot DetailedSnapshot() => new()
    {
        CurrentStatus = ConnectionStatus.Connected,
        OverallState = OverallConnectionState.Connected,
        AuthFailureMessage = "token expired",
        GatewayUrl = "http://localhost:7070",
        GatewaySelf = new TrayGatewaySelfSnapshot("2.0", "conn-1", 3, 3_720_000, "device"),
        Presence = [new TrayPresenceSnapshot("desktop", "windows", "1.0", "operator")],
        EnableNodeMode = true,
        NodeIsPaired = true,
        NodeIsPendingApproval = false,
        NodeIsConnected = true,
        NodePendingPairCount = 1,
        DevicePendingPairCount = 2,
        Nodes =
        [
            new TrayNodeSnapshot(
                "node-123456789012345",
                "Work PC",
                "node",
                "Windows",
                NowUtc.AddMinutes(-4),
                true,
                2,
                3,
                ["camera", "screen"],
                ["camera.snap", "camera.stream", "system.run"],
                "1.2.3",
                "Desktop"),
        ],
        Sessions =
        [
            new TraySessionSnapshot(
                "agent:main:research",
                true,
                "Research",
                "active",
                false,
                "claude-sonnet",
                null,
                null,
                null,
                null,
                null,
                8_000,
                4_000,
                12_000,
                100_000,
                NowUtc.AddMinutes(-5),
                NowUtc.AddMinutes(-5)),
        ],
        Usage = new TrayUsageSnapshot(8_000, 4_000, 20_000, 2.50, 3),
        UsageStatus = new TrayUsageStatusSnapshot(
        [
            new TrayUsageProviderSnapshot(
                "anthropic",
                "Anthropic",
                "Pro",
                "rate limited",
                [
                    new TrayUsageWindowSnapshot("5 hours", 81.9),
                    new TrayUsageWindowSnapshot("Week", 20.5),
                ]),
        ]),
        UsageCost = new TrayUsageCostSnapshot(19_000, 2.25),
        Settings = new TrayMenuSettingsSnapshot(
            true,
            false,
            false,
            true,
            false,
            true,
            false,
            true,
            false,
            true,
            false),
        SetupMenuLabel = "Reconfigure...",
        ShowSetupMenuEntry = true,
        LastUpdated = NowUtc.AddSeconds(-10),
        IsMcpRunning = false,
        McpStartupError = null,
    };
}
