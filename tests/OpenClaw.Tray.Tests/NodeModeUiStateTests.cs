using System.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins source-level UI contracts because OpenClaw.Tray.Tests cannot reference
/// the WinUI assembly directly.
/// </summary>
public sealed class NodeModeUiStateTests
{
    [Fact]
    public void NodeCardState_DeclaresMcpOnlyAndConnecting()
    {
        var plan = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");

        Assert.Contains("OffMcpOnly", plan);
        Assert.Contains("OnNodeConnecting", plan);
    }

    [Fact]
    public void BuildNodeCardState_MapsMcpOnlyWhenNodeModeOffButMcpEnabled()
    {
        var plan = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");

        Assert.Contains(
            "settings.EnableMcpServer ? NodeCardState.OffMcpOnly : NodeCardState.Off",
            plan);
    }

    [Fact]
    public void BuildNodeCardState_MapsConnectingToStartingState()
    {
        var plan = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");

        Assert.Contains(
            "RoleConnectionState.Connecting => NodeCardState.OnNodeConnecting",
            plan);
    }

    [Fact]
    public void ConnectionPage_PresentsMcpOnlyAndStartingStates()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");

        Assert.Contains("NodeCardState.OffMcpOnly", page);
        Assert.Contains("NodeCardState.OnNodeConnecting", page);
        Assert.Contains("ConnectionPage_NodeMcpOnly", page);
        Assert.Contains("ConnectionPage_NodeMcpOnlyReachable", page);
        Assert.Contains("ConnectionPage_NodeStarting", page);
        Assert.Contains("NodeService.McpServerUrl", page);
        Assert.Contains("ConnectionPage_NodeMcpError", page);
        Assert.Contains("ActiveNodeService", page);
    }

    [Fact]
    public void App_GatewayNodeConnection_GatedOnNodeModeOnly_NotMcp()
    {
        var app = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var connectMethod = ExtractMethodBody(app, "bool TryConnectGatewayIfCredentialAvailable");

        Assert.Contains("isNodeEnabled: IsGatewayNodeEnabled", app);

        var gate = ExtractMethodBody(app, "bool IsGatewayNodeEnabled");
        Assert.Contains("EnableNodeMode == true", gate);
        Assert.DoesNotContain("EnableMcpServer", gate);

        Assert.Contains("nodeCredential != null && IsGatewayNodeEnabled()", app);
        Assert.Contains("TryStartLocalMcpOnlyNode()", connectMethod);
    }

    [Fact]
    public void PermissionsPage_PresentsMcpOnlyNodeStatus()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

        Assert.Contains("EnableMcpServer", page);
        Assert.Contains("PermissionsPage_NodeStatus_McpOnly", page);
        Assert.Contains("PermissionsPage_NodeStatus_McpOnlyDetailsFormat", page);
        Assert.Contains("NodeService.McpServerUrl", page);
        Assert.Contains("CountMcpServedCapabilities", page);
        Assert.Contains("PermissionsPage_NodeStatus_McpError", page);
        Assert.Contains("ActiveNodeService", page);
        Assert.Contains("mcpEnabled && !string.IsNullOrEmpty(mcpStartupError)", page);
        Assert.Contains("McpStatusText.Text =", page);
    }

    [Fact]
    public void PermissionsPage_DrivesNodeStatusFromRoleState_AndSubscribesToChanges()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

        Assert.Contains("RoleConnectionState", page);
        Assert.Contains("CurrentSnapshot", page);
        Assert.Contains("PermissionsPage_NodeStatus_Starting", page);
        Assert.Contains("StateChanged", page);
        Assert.Contains("OnConnectionStateChanged", page);
    }

    [Fact]
    public void PermissionsPage_CapabilityToggles_StayActionableInMcpOnly()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

        Assert.Contains(
            "var canServe = (s?.EnableNodeMode ?? false) || (s?.EnableMcpServer ?? false);",
            page);
        Assert.Contains("BrowserProxyToggleIndex", page);
        Assert.Contains("!isBrowserProxyToggle || s?.EnableNodeMode == true", page);
    }

    [Fact]
    public void PermissionsPage_McpToggleRefreshesNodeStatus()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

        var toggle = ExtractMethodBody(page, "OnMcpToggled");
        Assert.Contains("UpdateNodeStatus()", toggle);
    }

    [Fact]
    public void NewNodeStateStrings_ExistInEnUsResources()
    {
        var resw = ReadSource(
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

        foreach (var key in new[]
        {
            "ConnectionPage_NodeStarting",
            "ConnectionPage_NodeMcpOnly",
            "ConnectionPage_NodeMcpOnlyReachable",
            "ConnectionPage_NodeMcpError",
            "PermissionsPage_NodeStatus_McpOnly",
            "PermissionsPage_NodeStatus_McpOnlyDetailsFormat",
            "PermissionsPage_NodeStatus_Starting",
            "PermissionsPage_NodeStatus_McpError",
        })
        {
            Assert.Contains($"name=\"{key}\"", resw);
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var sigIndex = source.IndexOf(methodName + "(", System.StringComparison.Ordinal);
        if (sigIndex < 0) return string.Empty;
        var bodyStart = source.IndexOf('{', sigIndex);
        if (bodyStart < 0) return string.Empty;
        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(bodyStart, i - bodyStart + 1);
            }
        }
        return source.Substring(bodyStart);
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
