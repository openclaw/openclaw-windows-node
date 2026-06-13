namespace OpenClaw.Tray.Tests;

public sealed class ConnectionRegressionSourceTests
{
    [Fact]
    public void Dashboard_TokenQuery_IsLimitedToSharedGatewayToken()
    {
        var appSource = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("credentialSource == CredentialResolver.SourceSharedGatewayToken", appSource);
        Assert.DoesNotContain("if (!isBootstrapToken && !string.IsNullOrEmpty(token))", appSource);
    }

    [Fact]
    public void DirectConnect_WaitsForTerminalConnectionOutcome()
    {
        var pageSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");

        Assert.Contains("ConnectAndWaitForDirectConnectOutcomeAsync(recordId)", pageSource);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(15))", pageSource);
        Assert.Contains("RollbackDirectConnect(previousActiveId", pageSource);
    }

    [Fact]
    public void ReconnectNode_RefreshesVisibleEffectiveNodeList()
    {
        var appSource = ReadSource("src", "OpenClaw.Tray.WinUI", "App.CapabilityHandlers.cs");

        Assert.Contains("await _connectionManager.ConnectNodeOnlyAsync();", appSource);
        Assert.Contains("WaitForAppStateUpdateAsync(nameof(AppState.Nodes), client.RequestNodesAsync)", appSource);
    }

    [Fact]
    public void NodeTrustPendingToast_CopiesNodeApprovalCommand()
    {
        var appSource = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("args.ApprovalKind switch", appSource);
        Assert.Contains(
            "OpenClaw.Shared.PairingApprovalKind.DevicePair => BuildPairingApprovalCommand(args.DeviceId)",
            appSource);
        Assert.Contains(
            "OpenClaw.Shared.PairingApprovalKind.NodePair => CommandCenterDiagnostics.BuildNodeApprovalRepairCommand(args.RequestId)",
            appSource);
        Assert.Contains("_ => \"openclaw nodes pending\"", appSource);
        Assert.Contains("ShowPairingPendingNotification(args.DeviceId, approvalCommand)", appSource);
    }

    [Fact]
    public void LocalNodeTrustPairListUpdate_RefreshesVisibleNodeList()
    {
        var managerSource = ReadSource("src", "OpenClaw.Connection", "GatewayConnectionManager.cs");

        Assert.Contains("operatorClient.RequestNodesAsync()", managerSource);
        Assert.Contains("Node list refresh failed after local node trust request", managerSource);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
