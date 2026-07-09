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
    private static string ReadPlanSource()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");
        return File.ReadAllText(path);
    }

    private static string ReadConnectionPageSource()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        return File.ReadAllText(path);
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        var callerFile = ThisFile.Path;
        if (!string.IsNullOrEmpty(callerFile) && File.Exists(callerFile))
        {
            var probe = new DirectoryInfo(Path.GetDirectoryName(callerFile)!);
            while (probe != null)
            {
                if (File.Exists(Path.Combine(probe.FullName, "openclaw-windows-node.slnx")) &&
                    Directory.Exists(Path.Combine(probe.FullName, "src")))
                {
                    return probe.FullName;
                }

                probe = probe.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static class ThisFile
    {
        public static readonly string Path = Capture();
        private static string Capture([System.Runtime.CompilerServices.CallerFilePath] string filePath = "")
            => filePath;
    }

    [Fact]
    public void NodeTrustApproveCommand_UsesNounFirstSubcommandBeforeNodeListArrives()
    {
        var plan = BuildNodePairingPlan(requestId: "node-req-123", PairingApprovalKind.NodePair);

        Assert.Null(plan.NodeApproveCommand);
        Assert.Equal("openclaw nodes approve node-req-123", plan.NodeTrustApproveCommand);
        Assert.True(plan.NodeTrustCommandApprovesRequest);
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

    [Fact]
    public void UnknownNodePairingKind_UsesBothDiscoveryQueuesEvenWithRequestId()
    {
        var plan = BuildNodePairingPlan("ambiguous-request", PairingApprovalKind.Unknown);

        AssertShellSafeCommand(
            CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands(),
            plan.NodeApproveCommand);
        Assert.Null(plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("node-device-789")]
    public void MissingDevicePairRequestId_EmitsDiscoveryCommand_NotDeviceId(
        string? nodeDeviceId)
    {
        var plan = BuildNodePairingPlan(null, PairingApprovalKind.DevicePair, nodeDeviceId);

        AssertShellSafeCommand("openclaw devices list", plan.NodeApproveCommand);
    }

    [Fact]
    public void GatewayCredentialDisplay_PrefersOperatorCredentialOverNodeCredential()
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OperatorCredentialSource = CredentialResolver.SourceSharedGatewayToken,
            OperatorCredentialStatus = GatewayCredentialResolutionStatus.Resolved,
            NodeCredentialSource = CredentialResolver.SourceNodeDeviceToken,
            NodeCredentialStatus = GatewayCredentialResolutionStatus.FallbackUsed,
            NodeCredentialFallbackUsed = true
        };

        var summary = ConnectionPagePlan.FormatCredentialSummary(snapshot);

        Assert.Equal("shared token", summary);
    }

    [Fact]
    public void MissingNodeTrustRequestId_EmitsShellSafeDiscoveryCommand_NotBareApprove()
    {
        var plan = BuildNodePairingPlan(null, PairingApprovalKind.NodePair);

        Assert.Null(plan.NodeApproveCommand);
        AssertShellSafeCommand("openclaw nodes pending", plan.NodeTrustApproveCommand);
        Assert.False(plan.NodeTrustCommandApprovesRequest);
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
