using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public class TrayMenuWindowMarkupTests
{
    [Fact]
    public void TrayMenuWindow_UsesVisibleVerticalScrollbar()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "TrayMenuWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Matches(
            new Regex(@"<ScrollViewer[^>]*VerticalScrollBarVisibility=""Visible""", RegexOptions.Singleline),
            xaml);
    }

    [Fact]
    public void SettingsWindow_HasCommandCenterEntryPoint()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SettingsWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsOpenCommandCenterButton""", xaml);
        Assert.Contains(@"Content=""Open Command Center""", xaml);
        Assert.Contains(@"Click=""OnOpenCommandCenter""", xaml);
    }

    [Fact]
    public void SettingsWindow_HasTopologyChoiceGuide()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SettingsWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsTopologyGuide""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsDetectedTopologyText""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseLocalGatewayButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseWslGatewayButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseSshTunnelButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseRemoteGatewayButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsSshBrowserForwardHint""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsSshTunnelPreviewText""", xaml);
        Assert.Contains("local-port+2 to remote-port+2", xaml);
        Assert.Contains("IsTextSelectionEnabled=\"True\"", xaml);
        Assert.Contains(@"TextChanged=""OnTopologyInputChanged""", xaml);
        Assert.Contains(@"Toggled=""OnNodeBrowserProxyToggled""", xaml);

        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SettingsWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);
        Assert.Contains("browserProxyForward", source);
        Assert.Contains("NodeBrowserProxyToggle.IsOn", source);
        Assert.Contains("CanForwardBrowserProxyPort", source);
        Assert.Contains("Managed tunnel preview: ssh", source);
        Assert.Contains("SshTunnelCommandLine.BuildArguments(user, host, remotePort, localPort, includeBrowserProxyForward)", source);
    }

    [Fact]
    public void SettingsWindow_HasNodeCapabilityToggles()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SettingsWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsNodeCapabilityToggles""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""NodeCanvasToggle""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""NodeScreenToggle""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""NodeCameraToggle""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""NodeLocationToggle""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""NodeBrowserProxyToggle""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasSupportDebugActions()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterSupportActionsSection""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterGatewayRuntimeText""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenLogsButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenConfigButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenDiagnosticsButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopySupportContextButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterUpdateStatusText""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasCopyablePortDiagnostics()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyPortDiagnosticsButton""", xaml);
        Assert.Contains(@"Click=""OnCopyPortDiagnostics""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_SupportContextIncludesRedactedTopology()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("Gateway URL: {RedactSupportValue", source);
        Assert.Contains("Topology detail: {RedactSupportValue", source);
        Assert.Contains("Gateway runtime: {RedactSupportValue", source);
        Assert.Contains("Tunnel remote endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel browser proxy local endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel browser proxy remote endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel last error: {RedactSupportValue", source);
        Assert.Contains("RedactSupportValue", source);
        Assert.Contains("<host>", source);
        Assert.Contains("<ip>", source);
        Assert.Contains("<user>@<host>", source);
        Assert.Contains("BuildPortDiagnosticsSummary", source);
        Assert.Contains("OpenClaw port diagnostics", source);
        Assert.Contains("OwningProcessId", source);
        Assert.Contains("OwningProcessName", source);
        var appSourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs");
        var appSource = File.ReadAllText(appSourcePath);

        Assert.Contains("ApplyDetectedSshForwardTopology", appSource);
        Assert.Contains("SSH tunnel (detected)", appSource);
        Assert.Contains("Browser proxy SSH forward is not listening", appSource);
        Assert.Contains("BuildBrowserProxySshForwardHint(port.Port, tunnel)", appSource);
        Assert.Contains("ResolveLocalBrowserProxyPort", appSource);
        Assert.Contains("ResolveRemoteBrowserProxyPort", appSource);
        Assert.Contains("<remote-gateway-port+2>", appSource);
        Assert.Contains("BuildBrowserProxyAuthWarnings(nodes)", appSource);
        Assert.Contains("Do not paste QR bootstrap tokens into the normal gateway token field.", appSource);
        var portDiagnosticsSourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "PortDiagnosticsService.cs");
        var portDiagnosticsSource = File.ReadAllText(portDiagnosticsSourcePath);
        Assert.Contains("TryGetBrowserProxyPort(topology, tunnel", portDiagnosticsSource);
        Assert.Contains("tunnel?.LocalEndpoint", portDiagnosticsSource);
    }

    [Fact]
    public void StatusDetailWindow_HasCopyableChannelAndNodeSummaries()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterChannelSummaryText""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyChannelSummaryButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyNodeSummaryButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyNodeInventoryButton""", xaml);
        Assert.Contains(@"Click=""OnCopyNodeInventory""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_NodeInventoryIncludesDiagnostics()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("BuildNodeInventorySummary", source);
        Assert.Contains("OpenClaw node inventory", source);
        Assert.Contains("Safe companion commands", source);
        Assert.Contains("Privacy-sensitive commands", source);
        Assert.Contains("Browser proxy commands", source);
        Assert.Contains("Missing browser proxy allowlist", source);
        Assert.Contains("Disabled in Settings", source);
        Assert.Contains("Missing Mac parity", source);
    }

    [Fact]
    public void StatusDetailWindow_HasUsageCostTrendBars()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCostTrendSection""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCostTrendList""", xaml);
        Assert.Contains("30-DAY COST TREND", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasRecentActivitySummaryActions()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterRecentActivitySection""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterRecentActivityList""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenActivityStreamButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyActivitySummaryButton""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasChannelToggleActions()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterToggleChannelButton""", xaml);
        Assert.Contains(@"Click=""OnToggleChannel""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasChannelDashboardAndExtensibilityActions()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenChannelDashboardButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterExtensibilitySection""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenChannelsDashboardButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenSkillsDashboardButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenCronDashboardButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyExtensibilitySummaryButton""", xaml);
    }

    [Fact]
    public void StatusDetailWindow_HasCapabilityDiagnosticsCopyAction()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyCapabilityDiagnosticsButton""", xaml);
        Assert.Contains(@"Click=""OnCopyCapabilityDiagnostics""", xaml);
    }

    [Fact]
    public void SetupWizard_HasPairingExpectationGuidance()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SetupWizardWindow.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"""SetupPairingStatusText""", source);
        Assert.Contains("Auto-pairing expected", source);
        Assert.Contains("Manual approval expected", source);
        Assert.Contains("Already paired", source);
    }

    [Fact]
    public void SetupWizard_DetectsExpiredSetupCodes()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SetupWizardWindow.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("TryGetSetupCodeExpiry", source);
        Assert.Contains("Setup code expired", source);
        Assert.Contains("expiresAt", source);
        Assert.Contains("expires_at", source);
        Assert.Contains("exp", source);
    }

    [Fact]
    public void SetupWizard_HasNodeModeSecurityWarning()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "SetupWizardWindow.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"""SetupNodeModeSecurityWarning""", source);
        Assert.Contains("Setup_NodeModeSecurityTitle", source);
        Assert.Contains("Setup_NodeModeSecurityMessage", source);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "moltbot-windows-hub.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
