using System.Linq;
using OpenClaw.Connection;
using OpenClawTray.Pages;
using OpenClawTray.Services;

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

    [Theory]
    [InlineData(0, (int)ConnectionPageMode.Welcome)]
    [InlineData(1, (int)ConnectionPageMode.Cockpit)]
    public void IdlePlan_SurfacesMcpOnlyNodeCardWithoutGatewaySession(
        int savedGatewayCount,
        int expectedMode)
    {
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "openclaw-node-mode-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsManager(settingsDirectory)
            {
                EnableMcpServer = true,
                EnableNodeMode = false
            };

            var plan = ConnectionPagePlan.Build(
                GatewayConnectionSnapshot.Idle,
                activeRecord: null,
                self: null,
                settings: settings,
                savedGatewayCount: savedGatewayCount);

            Assert.Equal((ConnectionPageMode)expectedMode, plan.Mode);
            Assert.Equal(NodeCardState.OffMcpOnly, plan.NodeCard);
            Assert.Equal(OperatorCardState.Hidden, plan.OperatorCard);
        }
        finally
        {
            if (Directory.Exists(settingsDirectory))
                Directory.Delete(settingsDirectory, recursive: true);
        }
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
        Assert.Contains("var hasStandaloneNodeCard = plan.NodeCard != NodeCardState.Hidden && !hasOperatorSession;", page);
        Assert.Contains("showRoles = (hasOperatorSession || hasStandaloneNodeCard)", page);
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

        var localNodeConnect = ExtractMethodBody(app, "Task TryConnectLocalNodeServiceAsync");
        Assert.Contains("!IsGatewayNodeEnabled()", localNodeConnect);
        Assert.Contains("EnsureNodeConnectedAsync()", localNodeConnect);
    }

    [Fact]
    public void PermissionsPage_PresentsMcpOnlyNodeStatus()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");
        var viewModel = ReadSource("src", "OpenClaw.Tray.WinUI", "Presentation", "PermissionsPageViewModel.cs");
        var app = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("_viewModel.McpEnabled = McpToggle.IsOn;", page);
        Assert.Contains("PermissionsPage_NodeStatus_McpOnly", page);
        Assert.Contains("PermissionsPage_NodeStatus_McpOnlyDetailsFormat", page);
        Assert.Contains("\"PermissionsPage_NodeStatus_McpOnly\"", viewModel);
        Assert.Contains("NodeService.McpServerUrl", app);
        Assert.Contains("CountMcpServedCapabilities", app);
        Assert.Contains("PermissionsPage_NodeStatus_McpError", page);
        Assert.Contains("PermissionsNodeStatusKind.McpError", viewModel);
        Assert.Contains("McpStatusText.Text =", page);
    }

    [Fact]
    public void PermissionsPage_DrivesNodeStatusFromRoleState_AndSubscribesToChanges()
    {
        var viewModel = ReadSource("src", "OpenClaw.Tray.WinUI", "Presentation", "PermissionsPageViewModel.cs");
        var app = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("RoleConnectionState", viewModel);
        Assert.Contains("PermissionsPage_NodeStatus_Starting", viewModel);
        Assert.Contains("event EventHandler? IPermissionsPageRuntimeHost.Changed", app);
        Assert.Contains("PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);", app);
        Assert.Contains("GatewayConnectionSnapshot IPermissionsPageRuntimeHost.ConnectionSnapshot", app);
    }

    [Fact]
    public void PermissionsPage_CapabilityToggles_StayActionableInMcpOnly()
    {
        var viewModel = ReadSource("src", "OpenClaw.Tray.WinUI", "Presentation", "PermissionsPageViewModel.cs");

        Assert.Contains("var featuresEnabled = _nodeModeEnabled || _mcpEnabled;", viewModel);
        Assert.Contains("PermissionsCapabilityKey.BrowserProxy, _nodeBrowserProxyEnabled, featuresEnabled && _nodeModeEnabled", viewModel);
        Assert.Contains("new PermissionsCapabilityState(PermissionsCapabilityKey.SystemRun, _nodeSystemRunEnabled, featuresEnabled)", viewModel);
    }

    [Fact]
    public void PermissionsPage_McpToggleRefreshesNodeStatus()
    {
        var page = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

        var toggle = ExtractMethodBody(page, "OnMcpToggled");
        Assert.Contains("_viewModel.McpEnabled = McpToggle.IsOn;", toggle);
    }

    [Fact]
    public void NodeService_ExposesMcpStartupFailures()
    {
        var service = ReadSource("src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs");

        Assert.Contains("public string? McpStartupError", service);
        Assert.Contains("public void SetMcpStartupError", service);
        Assert.Contains("SetMcpStartupFailure(ex, \"capability registration\")", service);
        Assert.Contains("return false;", ExtractMethodBody(service, "bool StartMcpServer"));
        Assert.Contains("MCP server startup failed: listener did not start.", service);
    }

    [Fact]
    public void NodeService_MxcSettingsSnapshotIncludesWindowsUiAccess()
    {
        var service = ReadSource("src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs");
        var snapshot = ExtractMethodBody(service, "SnapshotSettings");

        Assert.Contains(
            "SystemRunAllowWindowsUi = _settings.SystemRunAllowWindowsUi",
            snapshot);
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
