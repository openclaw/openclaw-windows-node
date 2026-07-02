using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Xml.Linq;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionStatusPresenterTests
{
    [Theory]
    [InlineData(OverallConnectionState.Connected, "StatusDisplay_Connected", (int)ConnectionStatusAccent.Success)]
    [InlineData(OverallConnectionState.Ready, "StatusDisplay_Connected", (int)ConnectionStatusAccent.Success)]
    [InlineData(OverallConnectionState.Connecting, "StatusDisplay_Connecting", (int)ConnectionStatusAccent.Caution)]
    [InlineData(OverallConnectionState.Degraded, "HubWindow_Pill_Degraded", (int)ConnectionStatusAccent.Caution)]
    [InlineData(OverallConnectionState.PairingRequired, "HubWindow_Pill_PairingRequired", (int)ConnectionStatusAccent.Caution)]
    [InlineData(OverallConnectionState.Error, "StatusDisplay_Error", (int)ConnectionStatusAccent.Critical)]
    [InlineData(OverallConnectionState.Idle, "StatusDisplay_Disconnected", (int)ConnectionStatusAccent.Neutral)]
    [InlineData(OverallConnectionState.Disconnecting, "StatusDisplay_Disconnected", (int)ConnectionStatusAccent.Neutral)]
    public void Pill_MapsOverallState(OverallConnectionState overall, string expectedKey, int expectedAccent)
    {
        var (labelKey, accent) = ConnectionStatusPresenter.Pill(overall);
        Assert.Equal(expectedKey, labelKey);
        Assert.Equal(expectedAccent, (int)accent);
    }

    [Fact]
    public void Pill_ReadyAndConnected_BothReadConnected()
    {
        Assert.Equal(ConnectionStatusPresenter.Pill(OverallConnectionState.Connected),
                     ConnectionStatusPresenter.Pill(OverallConnectionState.Ready));
    }

    [Theory]
    [InlineData(OverallConnectionState.Connected, ConnectionStatus.Connected)]
    [InlineData(OverallConnectionState.Ready, ConnectionStatus.Connected)]
    [InlineData(OverallConnectionState.Connecting, ConnectionStatus.Connecting)]
    [InlineData(OverallConnectionState.Degraded, ConnectionStatus.Connected)]
    [InlineData(OverallConnectionState.PairingRequired, ConnectionStatus.Error)]
    [InlineData(OverallConnectionState.Error, ConnectionStatus.Error)]
    [InlineData(OverallConnectionState.Idle, ConnectionStatus.Disconnected)]
    public void ToLegacyStatus_PreservesOperatorLiveCompatibility(
        OverallConnectionState overall,
        ConnectionStatus expected)
    {
        Assert.Equal(expected, ConnectionStatusPresenter.ToLegacyStatus(overall));
    }

    [Fact]
    public void PlainText_UsesDistinctBlockedLabels()
    {
        Assert.Equal("Degraded", ConnectionStatusPresenter.PlainText(OverallConnectionState.Degraded, ConnectionStatus.Connected));
        Assert.Equal("Pairing required", ConnectionStatusPresenter.PlainText(OverallConnectionState.PairingRequired, ConnectionStatus.Connecting));
    }

    [Theory]
    [InlineData(OverallConnectionState.Connected, true)]
    [InlineData(OverallConnectionState.Ready, true)]
    [InlineData(OverallConnectionState.Degraded, true)]
    [InlineData(OverallConnectionState.PairingRequired, true)]
    [InlineData(OverallConnectionState.Connecting, true)]
    [InlineData(OverallConnectionState.Error, false)]
    [InlineData(OverallConnectionState.Idle, false)]
    public void IsLiveOrPending_IncludesBlockedLiveStates(OverallConnectionState overall, bool expected)
    {
        Assert.Equal(expected, ConnectionStatusPresenter.IsLiveOrPending(overall, ConnectionStatus.Disconnected));
    }

    [Fact]
    public void ToLegacyStatus_SnapshotKeepsOperatorChannelLiveDuringNodeDegraded()
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Degraded,
            OperatorState = RoleConnectionState.Connected,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.Error
        };

        Assert.Equal(ConnectionStatus.Connected, ConnectionStatusPresenter.ToLegacyStatus(snapshot));
        Assert.True(ConnectionStatusPresenter.IsOperatorChannelLive(snapshot));
    }

    [Fact]
    public void ToLegacyStatus_SnapshotKeepsOperatorChannelLiveDuringNodePairing()
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.Connected,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.PairingRequired
        };

        Assert.Equal(ConnectionStatus.Connected, ConnectionStatusPresenter.ToLegacyStatus(snapshot));
    }

    [Fact]
    public void ToLegacyStatus_SnapshotKeepsOperatorPairingBlocked()
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.PairingRequired,
            NodeState = RoleConnectionState.Idle
        };

        Assert.Equal(ConnectionStatus.Error, ConnectionStatusPresenter.ToLegacyStatus(snapshot));
        Assert.False(ConnectionStatusPresenter.IsOperatorChannelLive(snapshot));
    }

    [Fact]
    public void NodeRow_NodeModeDisabled_ReadsDisabled_EvenWhenTransportConnected()
    {
        var snap = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
        };

        var (labelKey, accent) = ConnectionStatusPresenter.NodeRow(snap, nodeModeEnabled: false, enabledCapabilityCount: 7);

        Assert.Equal("HubWindow_Role_Disabled", labelKey);
        Assert.Equal(ConnectionStatusAccent.Neutral, accent);
    }

    [Fact]
    public void NodeRow_OperatorNotConnected_ReadsDisabled()
    {
        var snap = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connecting,
            NodeState = RoleConnectionState.Connected,
        };

        var (labelKey, accent) = ConnectionStatusPresenter.NodeRow(snap, nodeModeEnabled: true, enabledCapabilityCount: 7);

        Assert.Equal("HubWindow_Role_Disabled", labelKey);
        Assert.Equal(ConnectionStatusAccent.Neutral, accent);
    }

    [Fact]
    public void NodeRow_NodeModeEnabledAndConnected_ReadsConnected()
    {
        var snap = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
        };

        var (labelKey, accent) = ConnectionStatusPresenter.NodeRow(snap, nodeModeEnabled: true, enabledCapabilityCount: 1);

        Assert.Equal("StatusDisplay_Connected", labelKey);
        Assert.Equal(ConnectionStatusAccent.Success, accent);
    }

    [Fact]
    public void NodeRow_NodeModeEnabledConnectedAndNoCapabilities_ReadsPermissionsIncomplete()
    {
        var snap = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
        };

        var (labelKey, accent) = ConnectionStatusPresenter.NodeRow(snap, nodeModeEnabled: true, enabledCapabilityCount: 0);

        Assert.Equal("HubWindow_Role_PermissionsIncomplete", labelKey);
        Assert.Equal(ConnectionStatusAccent.Caution, accent);
    }

    [Fact]
    public void NodeNeedsApproval_OnlyWhenEnabledOperatorConnectedAndPairing()
    {
        var pairing = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.PairingRequired,
        };

        Assert.True(ConnectionStatusPresenter.NodeNeedsApproval(pairing, nodeModeEnabled: true));
        Assert.False(ConnectionStatusPresenter.NodeNeedsApproval(pairing, nodeModeEnabled: false));
    }

    [Fact]
    public void Presenter_ReturnedResourceKeys_ExistInEnUsResources()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var overall in Enum.GetValues<OverallConnectionState>())
            keys.Add(ConnectionStatusPresenter.Pill(overall).LabelKey);

        foreach (var state in Enum.GetValues<RoleConnectionState>())
            keys.Add(ConnectionStatusPresenter.RoleStateLabelKey(state));

        var connected = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
        };
        keys.Add(ConnectionStatusPresenter.NodeRow(connected, nodeModeEnabled: false, enabledCapabilityCount: 0).LabelKey);
        keys.Add(ConnectionStatusPresenter.NodeRow(connected, nodeModeEnabled: true, enabledCapabilityCount: 0).LabelKey);
        keys.Add(ConnectionStatusPresenter.NodeRow(connected, nodeModeEnabled: true, enabledCapabilityCount: 1).LabelKey);

        var reswPath = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");
        var resourceKeys = XDocument.Load(reswPath)
            .Descendants("data")
            .Select(e => (string?)e.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var missing = keys.Where(key => !resourceKeys.Contains(key)).OrderBy(key => key).ToArray();
        Assert.Empty(missing);
    }
}
