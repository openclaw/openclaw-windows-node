using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the CLI approve commands emitted by <c>ConnectionPagePlan</c>.
/// The OpenClaw CLI registers approve as noun-first subcommands:
/// <c>openclaw nodes approve &lt;requestId&gt;</c> and
/// <c>openclaw devices approve &lt;requestId&gt;</c>.
/// </summary>
public sealed class ConnectionPageApproveCommandTests
{
    [Fact]
    public void NodeApproveCommand_UsesNounFirstSubcommand()
    {
        var plan = BuildNodePairingPlan(requestId: "node-req-123", PairingApprovalKind.NodePair);

        Assert.Equal("openclaw nodes approve node-req-123", plan.NodeApproveCommand);
    }

    [Fact]
    public void NodeRoleUpgradeDevicePairing_UsesDevicesApproveCommand()
    {
        var plan = BuildNodePairingPlan(
            requestId: "device-req-456",
            PairingApprovalKind.DevicePair,
            nodeDeviceId: "node-device-789");

        Assert.Equal("openclaw devices approve device-req-456", plan.NodeApproveCommand);
    }

    [Fact]
    public void DevicesApproveCommand_UsesNounFirstSubcommand()
    {
        var plan = BuildOperatorPairingPlan("operator-req-123");

        Assert.Equal("openclaw devices approve operator-req-123", plan.RecoveryApproveCommand);
    }

    [Theory]
    [InlineData(PairingApprovalKind.NodePair, null, "openclaw nodes pending")]
    [InlineData(PairingApprovalKind.DevicePair, null, "openclaw devices list")]
    [InlineData(PairingApprovalKind.DevicePair, "node-device-789", "openclaw devices approve node-device-789")]
    public void MissingNodeRequestId_EmitsShellSafeDiscoveryCommand_NotBareApprove(
        PairingApprovalKind approvalKind,
        string? nodeDeviceId,
        string expected)
    {
        var plan = BuildNodePairingPlan(null, approvalKind, nodeDeviceId);

        AssertShellSafeCommand(expected, plan.NodeApproveCommand);
    }

    [Fact]
    public void MissingOperatorRequestId_EmitsShellSafeDiscoveryCommand_NotBareApprove()
    {
        var plan = BuildOperatorPairingPlan(null);

        AssertShellSafeCommand("openclaw devices list", plan.RecoveryApproveCommand);
    }

    private static ConnectionPagePlan BuildNodePairingPlan(
        string? requestId,
        PairingApprovalKind approvalKind,
        string? nodeDeviceId = null)
    {
        var snap = GatewayConnectionSnapshot.Idle with
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.PairingRequired,
            NodePairingRequestId = requestId,
            NodePairingApprovalKind = approvalKind,
            NodeDeviceId = nodeDeviceId,
        };

        return ConnectionPagePlan.Build(snap, ActiveGateway, self: null, settings: null, savedGatewayCount: 1);
    }

    private static ConnectionPagePlan BuildOperatorPairingPlan(string? requestId)
    {
        var snap = GatewayConnectionSnapshot.Idle with
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.PairingRequired,
            OperatorPairingRequired = true,
            OperatorPairingRequestId = requestId,
            NodeState = RoleConnectionState.Disabled,
        };

        return ConnectionPagePlan.Build(snap, ActiveGateway, self: null, settings: null, savedGatewayCount: 1);
    }

    private static void AssertShellSafeCommand(string expected, string? actual)
    {
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("#", actual);
        Assert.DoesNotContain("<", actual);
        Assert.DoesNotContain(">", actual);
    }

    private static GatewayRecord ActiveGateway => new()
    {
        Id = "gateway-local",
        Url = "ws://localhost:18789",
        FriendlyName = "Local gateway",
    };
}
