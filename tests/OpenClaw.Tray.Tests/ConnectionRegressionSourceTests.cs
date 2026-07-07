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
        Assert.Contains("_ => CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands()", appSource);
        Assert.Contains("ShowPairingPendingNotification(args.DeviceId, approvalCommand)", appSource);
    }

    [Fact]
    public void LocalNodeTrustPairListUpdate_RefreshesVisibleNodeList()
    {
        var managerSource = ReadSource("src", "OpenClaw.Connection", "GatewayConnectionManager.cs");

        Assert.Contains("operatorClient.RequestNodesAsync()", managerSource);
        Assert.Contains("Node list refresh failed after local node trust request", managerSource);
    }

    [Fact]
    public void SetupCodeEntry_ClearsStaleSshTunnelFields()
    {
        var pageSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");

        Assert.Contains("private void ClearAddGatewaySshFields()", pageSource);
        Assert.Contains("ClearAddGatewaySshFields();\r\n        ShowAddPane(\"setup\");", pageSource);
        Assert.Contains("AddSshExpander.IsExpanded = false;", pageSource);
        Assert.Contains("AddSshUserBox.Text = \"\";", pageSource);
        Assert.Contains("AddSshHostBox.Text = \"\";", pageSource);
    }

    [Fact]
    public void NodeCapabilityPills_ExposeStateThroughReadableTextPeer()
    {
        var pageSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");

        Assert.Contains("AutomationProperties.SetName(labelText", pageSource);
        Assert.DoesNotContain(
            "AutomationProperties.SetAccessibilityView(labelText, AccessibilityView.Raw);",
            pageSource);
        Assert.DoesNotContain("AutomationProperties.SetName(pill", pageSource);
    }

    [Fact]
    public void PairingRequiredDisconnectGuard_RunsInsideTransitionSemaphore()
    {
        var managerSource = ReadSource("src", "OpenClaw.Connection", "GatewayConnectionManager.cs");
        var methodStart = managerSource.IndexOf("private async Task HandleOperatorStatusChangedAsync", StringComparison.Ordinal);
        var waitIndex = managerSource.IndexOf("await _transitionSemaphore.WaitAsync();", methodStart, StringComparison.Ordinal);
        var pairingIndex = managerSource.IndexOf("var isPairingPending", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(waitIndex > methodStart);
        Assert.True(pairingIndex > waitIndex);
    }

    [Fact]
    public void GatewayClient_PairingRequiredFlag_UsesVolatileAccess()
    {
        var source = ReadSource("src", "OpenClaw.Shared", "OpenClawGatewayClient.cs");

        Assert.Contains("public bool IsPairingRequired => Volatile.Read(ref _pairingRequiredAwaitingApproval);", source);
        Assert.Contains("public string? PairingRequiredRequestId => Volatile.Read(ref _pairingRequiredRequestId);", source);
        AssertInOrder(
            source,
            "Volatile.Write(ref _pairingRequiredRequestId, pairingDetails.RequestId);",
            "Volatile.Write(ref _pairingRequiredAwaitingApproval, true);");
        Assert.Contains("Volatile.Write(ref _pairingRequiredAwaitingApproval, false);", source);
        Assert.Contains("Volatile.Write(ref _pairingRequiredRequestId, null);", source);
        Assert.Contains("Volatile.Write(ref _pairingRequiredAwaitingApproval, true);", source);
    }

    [Fact]
    public void OperatorCli_DefersIdentityDefaultsUntilAfterArgumentParsing()
    {
        var source = ReadSource("src", "OpenClaw.Cli", "Program.cs");

        Assert.Contains("public string SettingsPath { get; set; } = \"\";", source);
        Assert.Contains("public string IdentityDataPath { get; set; } = \"\";", source);
        AssertInOrder(
            source,
            "options = ParseArgs(args);",
            "ApplyIdentityDefaults(options, Environment.GetEnvironmentVariable);");
        AssertInOrder(
            source,
            "options.Identity = OpenClawAppIdentity.ResolveIdentity(envLookup, options.Identity);",
            "options.IdentityDataPath = OpenClawAppIdentity.ResolveRoamingDataDirectory(envLookup, options.Identity);");
    }

    [Fact]
    public void SetupKeepalive_DisposesProcessWrapperAfterWritingMarker()
    {
        var source = ReadSource("src", "OpenClaw.SetupEngine", "SetupSteps.cs");

        Assert.Contains("using var proc = System.Diagnostics.Process.Start(psi);", source);
        Assert.Contains("WriteKeepaliveMarker(ctx, markerPath, proc.Id);", source);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }

    private static void AssertInOrder(string source, params string[] snippets)
    {
        var previous = -1;
        foreach (var snippet in snippets)
        {
            var current = source.IndexOf(snippet, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected to find '{snippet}' after index {previous}.");
            previous = current;
        }
    }
}
