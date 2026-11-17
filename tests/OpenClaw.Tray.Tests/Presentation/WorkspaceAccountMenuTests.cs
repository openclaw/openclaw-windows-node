using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceAccountMenuTests
{
    private static readonly GatewayRecord Home = new() { Id = "home", Url = "ws://127.0.0.1:18789", FriendlyName = "Home Gateway" };
    private static readonly GatewayRecord Office = new() { Id = "office", Url = "wss://office.example.com" };
    private static readonly GatewayRecord Lab = new() { Id = "lab", Url = "ws://lab.local:9000", FriendlyName = "  Lab VM " };

    private static WorkspaceAccountMenuModel Project(
        OverallConnectionState? overall = OverallConnectionState.Connected,
        ConnectionStatus status = ConnectionStatus.Connected,
        string? activeId = "home",
        string? version = "2026.10.9",
        UpdateCommandCenterInfo? update = null,
        bool? operatorConnected = null,
        params GatewayRecord[] gateways) =>
        WorkspaceAccountMenu.Project(new(
            gateways.Length == 0 ? [Office, Home, Lab] : gateways, activeId, overall, status, version, update,
            operatorConnected ?? status == ConnectionStatus.Connected));

    [Fact]
    public void ConnectedGateway_ShowsNameStatusAndVersion_WithoutRetry()
    {
        var menu = Project();

        Assert.Equal("Home Gateway", menu.ActiveGatewayName);
        Assert.Equal("StatusDisplay_Connected", menu.StatusLabelKey);
        Assert.Equal(ConnectionStatusAccent.Success, menu.StatusAccent);
        Assert.Equal("2026.10.9", menu.ServerVersion);
        Assert.False(menu.ShowRetry);
    }

    [Fact]
    public void ActiveGatewayIsListedFirst_ThenSavedGatewaysByName()
    {
        var menu = Project();

        Assert.Equal(new[] { "Home Gateway", "Lab VM", "office.example.com" }, menu.Gateways.Select(g => g.Name));
        Assert.Equal(new[] { true, false, false }, menu.Gateways.Select(g => g.IsActive));
        Assert.Equal(new[] { "127.0.0.1:18789", "lab.local:9000", "office.example.com" }, menu.Gateways.Select(g => g.Host));
    }

    [Theory]
    [InlineData(OverallConnectionState.Idle, ConnectionStatus.Disconnected, true)]
    [InlineData(OverallConnectionState.Error, ConnectionStatus.Error, true)]
    [InlineData(OverallConnectionState.Connecting, ConnectionStatus.Connecting, false)]
    [InlineData(OverallConnectionState.PairingRequired, ConnectionStatus.Error, false)]
    [InlineData(OverallConnectionState.Degraded, ConnectionStatus.Connected, false)]
    public void RetryNow_ShowsOnlyWhileTheActiveGatewayIsOfflineOrFailed(
        OverallConnectionState overall, ConnectionStatus status, bool expected)
    {
        var menu = Project(overall, status);

        Assert.Equal(expected, menu.ShowRetry);
    }

    [Fact]
    public void OfflineGateway_HidesVersionAndUsesTheStatusLabel()
    {
        var menu = Project(OverallConnectionState.Idle, ConnectionStatus.Disconnected);

        Assert.Null(menu.ServerVersion);
        Assert.Equal("StatusDisplay_Disconnected", menu.StatusLabelKey);
    }

    [Fact]
    public void NoActiveGateway_HasNoNameAndNoRetry()
    {
        var menu = Project(OverallConnectionState.Idle, ConnectionStatus.Disconnected, activeId: null);

        Assert.Null(menu.ActiveGatewayName);
        Assert.False(menu.ShowRetry);
        Assert.All(menu.Gateways, gateway => Assert.False(gateway.IsActive));
    }

    [Fact]
    public void ProfileAndVersion_FollowTheOperatorChannel_NotTheAggregateState()
    {
        // Operator still live while node pairing degrades the aggregate state.
        var degraded = Project(OverallConnectionState.PairingRequired, ConnectionStatus.Error, operatorConnected: true);
        Assert.True(degraded.OperatorConnected);
        Assert.Equal("2026.10.9", degraded.ServerVersion);

        // Aggregate Connected from the node alone must not claim the operator profile or version.
        var nodeOnly = Project(OverallConnectionState.Connected, ConnectionStatus.Connected, operatorConnected: false);
        Assert.False(nodeOnly.OperatorConnected);
        Assert.Null(nodeOnly.ServerVersion);
    }

    [Theory]
    [InlineData(RoleConnectionState.Connected, ConnectionStatus.Disconnected, true)]
    [InlineData(RoleConnectionState.Idle, ConnectionStatus.Connected, false)]
    public void OperatorConnection_PrefersTheSnapshotOverTheLegacyStatus(
        RoleConnectionState operatorState, ConnectionStatus status, bool expected)
    {
        var snapshot = new GatewayConnectionSnapshot { OperatorState = operatorState };

        Assert.Equal(expected, WorkspaceAccountMenuInput.IsOperatorConnected(snapshot, status));
        Assert.Equal(status == ConnectionStatus.Connected, WorkspaceAccountMenuInput.IsOperatorConnected(null, status));
    }

    [Fact]
    public void ProfileDetail_HidesAnEmailThatIsAlreadyTheLabel()
    {
        Assert.Equal("ada@example.test", WorkspaceAccountMenu.ProfileDetail("Ada", "ada@example.test"));
        Assert.Null(WorkspaceAccountMenu.ProfileDetail("Ada@Example.test", " ada@example.test "));
        Assert.Null(WorkspaceAccountMenu.ProfileDetail("Ada", null));
        Assert.Null(WorkspaceAccountMenu.ProfileDetail("Ada", "  "));
    }

    [Theory]
    [InlineData(OverallConnectionState.Idle, ConnectionStatus.Disconnected, true)]
    [InlineData(OverallConnectionState.Error, ConnectionStatus.Error, true)]
    [InlineData(OverallConnectionState.Connected, ConnectionStatus.Connected, false)]
    [InlineData(OverallConnectionState.Connecting, ConnectionStatus.Connecting, false)]
    public void RetryClick_RechecksTheLiveState_SoAStaleRowCannotDropARecoveredConnection(
        OverallConnectionState overall, ConnectionStatus status, bool expected) =>
        Assert.Equal(expected, WorkspaceAccountMenu.NeedsRetry(overall, status));

    [Fact]
    public void LeadingVersionPrefixIsNotDoubled()
    {
        Assert.Equal("2026.10.9", Project(version: "v2026.10.9").ServerVersion);
        Assert.Null(Project(version: "  ").ServerVersion);
    }

    [Theory]
    [InlineData("Available", "prompted", true)]
    [InlineData("Available", "download requested", true)]
    [InlineData("Available", UpdateCommandCenterInfo.SkippedByUserDetail, false)]
    [InlineData("Skipped", "managed by the Microsoft Store", false)]
    [InlineData("Failed", null, false)]
    [InlineData("Not checked", null, false)]
    public void UpdateBadge_FollowsAnActionableCompanionUpdate_AndRoutesSettingsToAbout(
        string status, string? detail, bool expected)
    {
        var menu = Project(update: new UpdateCommandCenterInfo { Status = status, Detail = detail });

        Assert.Equal(expected, menu.UpdateAvailable);
        Assert.Equal(expected ? CompanionPageId.About : CompanionPageId.Settings, menu.SettingsTarget);
    }

    [Fact]
    public void DashboardTargets_UseAliasesDefinedByEveryReleasedGateway()
    {
        // Verified in openclaw/openclaw ui/src/app-route-paths.ts for v2026.8.35 through v2026.11.1.
        Assert.Equal("nodes", WorkspaceAccountMenu.PairDeviceDashboardPath);
        Assert.Equal("profile", WorkspaceAccountMenu.ProfileDashboardPath);
        Assert.Equal("config", WorkspaceAccountMenu.GatewaySettingsDashboardPath);
    }

    [Fact]
    public void GatewayName_FallsBackToHostThenRawUrl()
    {
        Assert.Equal("office.example.com", WorkspaceAccountMenu.GatewayName(Office));
        Assert.Equal("not a url", WorkspaceAccountMenu.GatewayName(new GatewayRecord { Id = "x", Url = "not a url" }));
    }
}
