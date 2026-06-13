using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPagePlanApprovalBehaviorTests : IDisposable
{
    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), "openclaw-connection-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_settingsDirectory, true); } catch { }
    }

    [Theory]
    [InlineData(GatewayNodeApprovalState.PendingApproval)]
    [InlineData(GatewayNodeApprovalState.PendingReapproval)]
    public void NodeListTrust_OverridesGenericNodePairingAndSuppressesGenericCommand(
        GatewayNodeApprovalState approvalState)
    {
        var expectedCard = approvalState == GatewayNodeApprovalState.PendingApproval
            ? NodeCardState.OnNodeApprovalRequired
            : NodeCardState.OnNodeReapprovalRequired;

        var plan = Build(
            PairingApprovalKind.NodePair,
            new GatewayNodeInfo
            {
                ApprovalState = approvalState,
                PendingRequestId = "trust-request",
                Capabilities = ["system"],
                Commands = ["system.notify"],
                Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["system.notify"] = true
                },
                PendingDeclaredCapabilities = ["system", "camera"],
                PendingDeclaredCommands = ["system.notify", "camera.snap"],
                PendingDeclaredPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["system.notify"] = true,
                    ["camera.snap"] = false
                }
            });

        Assert.Equal(expectedCard, plan.NodeCard);
        Assert.Null(plan.NodeApproveCommand);
        Assert.Equal("openclaw nodes approve trust-request", plan.NodeTrustApproveCommand);
        Assert.True(plan.NodeTrustCommandApprovesRequest);
        Assert.Equal(["system"], plan.NodeEffectiveCapabilities);
        Assert.True(plan.NodeEffectivePermissions["system.notify"]);
        Assert.Equal(["system", "camera"], plan.NodePendingDeclaredCapabilities);
        Assert.False(plan.NodePendingDeclaredPermissions["camera.snap"]);
    }

    [Fact]
    public void NodeListTrust_WithDiscoveryCommand_SuppressesGenericCommandWithoutClaimingApproval()
    {
        var plan = Build(
            PairingApprovalKind.Unknown,
            new GatewayNodeInfo
            {
                ApprovalState = GatewayNodeApprovalState.PendingReapproval,
                PendingRequestId = "unsafe;request",
                PendingDeclaredCommands = ["camera.snap"]
            });

        Assert.Equal(NodeCardState.OnNodeReapprovalRequired, plan.NodeCard);
        Assert.Null(plan.NodeApproveCommand);
        Assert.Equal("openclaw nodes pending", plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
    }

    [Fact]
    public void NodeListTrust_OverridesNodeConnectingWaitState()
    {
        var plan = Build(
            new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Connecting,
                OperatorState = RoleConnectionState.Connected,
                NodeState = RoleConnectionState.Connecting
            },
            PendingReapprovalNode());

        Assert.Equal(NodeCardState.OnNodeReapprovalRequired, plan.NodeCard);
        Assert.Equal("openclaw nodes approve trust-request", plan.NodeTrustApproveCommand);
        Assert.True(plan.NodeTrustCommandApprovesRequest);
    }

    [Fact]
    public void SnapshotNodeTrust_OwnsCopyOnlyApprovalBeforeNodeListArrives()
    {
        var plan = Build(PairingApprovalKind.NodePair, localNode: null, requestId: "trust-request");

        Assert.Equal(NodeCardState.OnNodeApprovalRequired, plan.NodeCard);
        Assert.Null(plan.NodeApproveCommand);
        Assert.Equal("openclaw nodes approve trust-request", plan.NodeTrustApproveCommand);
        Assert.True(plan.NodeTrustCommandApprovesRequest);
    }

    [Fact]
    public void UnknownSnapshotWithoutNodeList_UsesDiscoveryWithoutExactApproval()
    {
        var plan = Build(PairingApprovalKind.Unknown, localNode: null, requestId: "ambiguous-request");

        Assert.Equal(NodeCardState.OnNodePairingRequired, plan.NodeCard);
        Assert.Equal("openclaw devices list", plan.NodeApproveCommand);
        Assert.Null(plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
    }

    [Fact]
    public void SnapshotNodeTrust_WithStaleNodeList_PreservesEffectiveSurfaces()
    {
        var plan = Build(
            PairingApprovalKind.NodePair,
            new GatewayNodeInfo
            {
                ApprovalState = GatewayNodeApprovalState.Approved,
                Capabilities = ["system"],
                Commands = ["system.notify"],
                Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["system.notify"] = true
                }
            },
            requestId: "trust-request");

        Assert.Equal(NodeCardState.OnNodeApprovalRequired, plan.NodeCard);
        Assert.Null(plan.NodeApproveCommand);
        Assert.Equal("openclaw nodes approve trust-request", plan.NodeTrustApproveCommand);
        Assert.Equal(["system"], plan.NodeEffectiveCapabilities);
        Assert.Equal(["system.notify"], plan.NodeEffectiveCommands);
        Assert.True(plan.NodeEffectivePermissions["system.notify"]);
    }

    [Fact]
    public void ExistingPositionalUserIntentCall_RemainsSourceCompatible()
    {
        var plan = ConnectionPagePlan.Build(
            GatewayConnectionSnapshot.Idle,
            null,
            null,
            null,
            0,
            UserIntent.AddingGateway);

        Assert.Equal(ConnectionPageMode.AddGateway, plan.Mode);
    }

    [Fact]
    public void ExplicitDevicePairRoleUpgrade_RemainsPrimaryOverNodeListTrust()
    {
        var plan = Build(
            PairingApprovalKind.DevicePair,
            PendingReapprovalNode());

        Assert.Equal(NodeCardState.OnNodePairingRequired, plan.NodeCard);
        Assert.Equal("openclaw devices approve pairing-request", plan.NodeApproveCommand);
        Assert.Null(plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
    }

    [Fact]
    public void NodeModeOff_RemainsOffDespiteStalePendingReapproval()
    {
        var plan = Build(
            new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Ready,
                OperatorState = RoleConnectionState.Connected,
                NodeState = RoleConnectionState.Connected
            },
            PendingReapprovalNode(),
            enableNodeMode: false);

        AssertTrustDoesNotOverride(plan, NodeCardState.Off);
    }

    [Fact]
    public void OperatorOff_RemainsOffDespiteStalePendingReapproval()
    {
        var plan = Build(
            new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Connected,
                OperatorState = RoleConnectionState.Idle,
                NodeState = RoleConnectionState.Connected
            },
            PendingReapprovalNode());

        AssertTrustDoesNotOverride(plan, NodeCardState.Off);
    }

    [Fact]
    public void OperatorConnecting_RemainsHiddenDespiteStalePendingReapproval()
    {
        var plan = Build(
            new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Connecting,
                OperatorState = RoleConnectionState.Connecting,
                NodeState = RoleConnectionState.Idle
            },
            PendingReapprovalNode());

        AssertTrustDoesNotOverride(plan, NodeCardState.Hidden);
    }

    [Fact]
    public void NodeError_RemainsErrorDespiteStalePendingReapproval()
    {
        var plan = Build(
            new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Degraded,
                OperatorState = RoleConnectionState.Connected,
                NodeState = RoleConnectionState.Error,
                NodeError = "transport failed"
            },
            PendingReapprovalNode());

        AssertTrustDoesNotOverride(plan, NodeCardState.OnNodeError);
    }

    private ConnectionPagePlan Build(
        PairingApprovalKind pairingApprovalKind,
        GatewayNodeInfo? localNode,
        string? requestId = "pairing-request")
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.PairingRequired,
            NodePairingStatus = PairingStatus.Pending,
            NodePairingApprovalKind = pairingApprovalKind,
            NodePairingRequestId = requestId,
            NodeDeviceId = "local-node"
        };

        return Build(snapshot, localNode);
    }

    private ConnectionPagePlan Build(
        GatewayConnectionSnapshot snapshot,
        GatewayNodeInfo? localNode,
        bool enableNodeMode = true)
    {
        var settings = new SettingsManager(_settingsDirectory)
        {
            EnableNodeMode = enableNodeMode
        };

        return ConnectionPagePlan.Build(
            snapshot,
            activeRecord: null,
            self: null,
            settings: settings,
            savedGatewayCount: 1,
            localNode: localNode);
    }

    private static GatewayNodeInfo PendingReapprovalNode() => new()
    {
        ApprovalState = GatewayNodeApprovalState.PendingReapproval,
        PendingRequestId = "trust-request",
        PendingDeclaredCommands = ["camera.snap"]
    };

    private static void AssertTrustDoesNotOverride(
        ConnectionPagePlan plan,
        NodeCardState expectedCard)
    {
        Assert.Equal(expectedCard, plan.NodeCard);
        Assert.Null(plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
    }
}
