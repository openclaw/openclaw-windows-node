namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPageNodeApprovalSourceTests
{
    [Fact]
    public void CommandCenterFallback_ProjectsLocalDeclarationsAsUnverified()
    {
        var builderSource = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "CommandCenterStateBuilder.cs");

        Assert.Contains("NodeCapabilityHealthInfo.FromLocalDeclarations(localNode)", builderSource);
        Assert.DoesNotContain("NodeCapabilityHealthInfo.FromNode(localNode)", builderSource);
        Assert.Contains("var hasAuthoritativePendingLocalNodeTrust =", builderSource);
        Assert.Contains("string.Equals(node.NodeId, localNodeId, StringComparison.OrdinalIgnoreCase)", builderSource);
        Assert.Contains("if (!hasAuthoritativePendingLocalNodeTrust &&", builderSource);
    }

    [Fact]
    public void ConnectionPagePlan_ProjectsLocalNodeApprovalWithoutMergingPendingDeclarations()
    {
        var planSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");

        Assert.Contains("GatewayNodeInfo? localNode", planSource);
        Assert.Contains("NodeCardState.OnNodeApprovalRequired", planSource);
        Assert.Contains("NodeCardState.OnNodeReapprovalRequired", planSource);
        Assert.Contains("CommandCenterDiagnostics.TryBuildNodeApprovalCommand(", planSource);
        Assert.Contains("var nodeCardAllowsTrustOverride = plan.NodeCard is", planSource);
        Assert.Contains("NodeCardState.OnNodePairingRequired", planSource);
        Assert.Contains("pairingApprovalKind != PairingApprovalKind.DevicePair", planSource);
        Assert.Contains("var snapshotTrustOwnsApprovalUx =", planSource);
        Assert.Contains("pairingApprovalKind == PairingApprovalKind.NodePair", planSource);
        Assert.Contains("var nodeTrustOwnsApprovalUx =", planSource);
        Assert.Contains("NodeApproveCommand = nodeTrustOwnsApprovalUx ? null : plan.NodeApproveCommand", planSource);
        Assert.Contains("NodeTrustCommandApprovesRequest = hasApprovalCommand", planSource);
        Assert.Contains("\"openclaw nodes pending\"", planSource);
        Assert.DoesNotContain("NodeEffectiveCapabilities = localNode.PendingDeclaredCapabilities", planSource);
        Assert.DoesNotContain("NodeEffectiveCommands = localNode.PendingDeclaredCommands", planSource);
    }

    [Fact]
    public void ConnectionPage_PassesLocalNodeAndRendersTrustApprovalAsCopyOnly()
    {
        var pageSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        var pageMarkup = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml");

        Assert.Contains("var localNode = NodeCapabilityGating.GetLocalNodeInfo(", pageSource);
        Assert.Contains("userIntent: _userIntent", pageSource);
        Assert.Contains("localNode: localNode", pageSource);
        Assert.Contains("case nameof(AppState.Nodes):", pageSource);
        Assert.Contains("NodeCardState.OnNodeApprovalRequired", pageSource);
        Assert.Contains("NodeCardState.OnNodeReapprovalRequired", pageSource);
        Assert.Contains("plan.NodeEffectiveCapabilities", pageSource);
        Assert.Contains("plan.NodeEffectiveCommands", pageSource);
        Assert.Contains("plan.NodeEffectivePermissions", pageSource);
        Assert.Contains("plan.NodePendingDeclaredCapabilities", pageSource);
        Assert.Contains("plan.NodePendingDeclaredCommands", pageSource);
        Assert.Contains("plan.NodePendingDeclaredPermissions", pageSource);
        Assert.Contains("NodeTrustApproveCmdText.Text = plan.NodeTrustApproveCommand", pageSource);
        Assert.Contains("ConnectionPage_NodeTrustDiscoveryHelp", pageSource);
        Assert.Contains("plan.NodeTrustCommandApprovesRequest", pageSource);
        Assert.Contains("canReconnectAfterNodeTrustApproval", pageSource);
        Assert.Contains("ConnectionPage_NodeReconnectAfterApproval", pageSource);
        Assert.Contains("NodeReconnectButton.Visibility = Visibility.Visible", pageSource);

        var copyHandler = ExtractMethodBody(pageSource, "OnCopyNodeTrustApproveCommand");
        Assert.Contains("ClipboardHelper.CopyText(NodeTrustApproveCmdText.Text)", copyHandler);
        Assert.DoesNotContain("NodePairApproveAsync", copyHandler);
        Assert.DoesNotContain("NodePairRejectAsync", copyHandler);

        Assert.Contains("x:Name=\"NodePendingDeclarationsPanel\"", pageMarkup);
        Assert.Contains("x:Name=\"NodeTrustApproveCmdBox\"", pageMarkup);
        Assert.Contains("x:Name=\"NodeTrustApproveCmdText\"", pageMarkup);
        Assert.Contains("Click=\"OnCopyNodeTrustApproveCommand\"", pageMarkup);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var signature = $"private void {methodName}(";
        var methodStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find {methodName}.");
        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Could not find body for {methodName}.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        return source[bodyStart..];
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
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

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
