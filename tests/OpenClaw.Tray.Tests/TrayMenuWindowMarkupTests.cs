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
    public void WebChatWindow_BridgeValidatesOriginAndPostsOnDispatcher()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "WebChatWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("IsTrustedBridgeSource(e.Source)", source);
        Assert.Contains("rejected bridge message from untrusted source", source);
        Assert.Contains("DispatcherQueue", source);
        Assert.Contains("TryEnqueue(() => PostBridgeMessageOnUiThread", source);
        Assert.Contains("PostWebMessageAsJson(json)", source);
        Assert.Contains("SanitizeBridgeLogValue", source);
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
    public void CommandPalette_HasCommandCenterEntryPoint()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://commandcenter", source);
        Assert.Contains("Command Center", source);
        Assert.Contains("gateway, tunnel, node, and browser diagnostics", source);
    }

    [Fact]
    public void CommandPalette_HasActivityStreamEntryPoint()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://activity", source);
        Assert.Contains("Activity Stream", source);
        Assert.Contains("recent tray activity", source);
    }

    [Fact]
    public void CommandPalette_HasNotificationHistoryEntryPoint()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://history", source);
        Assert.Contains("Notification History", source);
        Assert.Contains("recent OpenClaw tray notifications", source);
    }

    [Fact]
    public void CommandPalette_HasTrayUtilityEntryPoints()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://setup", source);
        Assert.Contains("Setup Wizard", source);
        Assert.Contains(@"openclaw://healthcheck", source);
        Assert.Contains("Run Health Check", source);
        Assert.Contains(@"openclaw://check-updates", source);
        Assert.Contains("Check for Updates", source);
        Assert.Contains(@"openclaw://logs", source);
        Assert.Contains("Open Log File", source);
    }

    [Fact]
    public void CommandPalette_HasDashboardSubpathEntryPoints()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://dashboard/sessions", source);
        Assert.Contains("Dashboard: Sessions", source);
        Assert.Contains(@"openclaw://dashboard/channels", source);
        Assert.Contains("Dashboard: Channels", source);
        Assert.Contains(@"openclaw://dashboard/skills", source);
        Assert.Contains("Dashboard: Skills", source);
        Assert.Contains(@"openclaw://dashboard/cron", source);
        Assert.Contains("Dashboard: Cron", source);
    }

    [Fact]
    public void CommandPalette_HasSupportDebugEntryPoints()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.CommandPalette",
            "Pages",
            "OpenClawPage.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"openclaw://log-folder", source);
        Assert.Contains("Open Logs Folder", source);
        Assert.Contains(@"openclaw://config", source);
        Assert.Contains("Open Config Folder", source);
        Assert.Contains(@"openclaw://diagnostics", source);
        Assert.Contains("Open Diagnostics Folder", source);
        Assert.Contains(@"openclaw://check-updates", source);
        Assert.Contains("Check for Updates", source);
        Assert.Contains(@"openclaw://support-context", source);
        Assert.Contains("Copy Support Context", source);
        Assert.Contains(@"openclaw://debug-bundle", source);
        Assert.Contains("Copy Debug Bundle", source);
        Assert.Contains(@"openclaw://browser-setup", source);
        Assert.Contains("Copy Browser Setup", source);
        Assert.Contains(@"openclaw://port-diagnostics", source);
        Assert.Contains("Copy Port Diagnostics", source);
        Assert.Contains(@"openclaw://capability-diagnostics", source);
        Assert.Contains("Copy Capability Diagnostics", source);
        Assert.Contains(@"openclaw://node-inventory", source);
        Assert.Contains("Copy Node Inventory", source);
        Assert.Contains(@"openclaw://channel-summary", source);
        Assert.Contains("Copy Channel Summary", source);
        Assert.Contains(@"openclaw://activity-summary", source);
        Assert.Contains("Copy Activity Summary", source);
        Assert.Contains(@"openclaw://extensibility-summary", source);
        Assert.Contains("Copy Extensibility Summary", source);
        Assert.Contains(@"openclaw://restart-ssh-tunnel", source);
        Assert.Contains("Restart SSH Tunnel", source);
    }

    [Fact]
    public void DeepLinkHandler_HasActivityStreamEntryPoint()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""activity"":", source);
        Assert.Contains("OpenActivityStream?.Invoke", source);
        Assert.Contains(@"GetValueOrDefault(""filter"")", source);
    }

    [Fact]
    public void DeepLinkHandler_HasNotificationHistoryEntryPoint()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""history"":", source);
        Assert.Contains(@"case ""notification-history"":", source);
        Assert.Contains("OpenNotificationHistory?.Invoke", source);
    }

    [Fact]
    public void DeepLinkHandler_HasTrayUtilityEntryPoints()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""healthcheck"":", source);
        Assert.Contains("RunHealthCheck", source);
        Assert.Contains(@"case ""check-updates"":", source);
        Assert.Contains("CheckForUpdates", source);
        Assert.Contains(@"case ""logs"":", source);
        Assert.Contains("OpenLogFile?.Invoke", source);
    }

    [Fact]
    public void DeepLinkHandler_HasSupportDebugEntryPoints()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""log-folder"":", source);
        Assert.Contains("OpenLogFolder?.Invoke", source);
        Assert.Contains(@"case ""config"":", source);
        Assert.Contains("OpenConfigFolder?.Invoke", source);
        Assert.Contains(@"case ""diagnostics"":", source);
        Assert.Contains("OpenDiagnosticsFolder?.Invoke", source);
        Assert.Contains(@"case ""support-context"":", source);
        Assert.Contains("CopySupportContext?.Invoke", source);
        Assert.Contains(@"case ""debug-bundle"":", source);
        Assert.Contains("CopyDebugBundle?.Invoke", source);
        Assert.Contains(@"case ""browser-setup"":", source);
        Assert.Contains("CopyBrowserSetupGuidance?.Invoke", source);
        Assert.Contains(@"case ""port-diagnostics"":", source);
        Assert.Contains("CopyPortDiagnostics?.Invoke", source);
        Assert.Contains(@"case ""capability-diagnostics"":", source);
        Assert.Contains("CopyCapabilityDiagnostics?.Invoke", source);
        Assert.Contains(@"case ""node-inventory"":", source);
        Assert.Contains("CopyNodeInventory?.Invoke", source);
        Assert.Contains(@"case ""channel-summary"":", source);
        Assert.Contains("CopyChannelSummary?.Invoke", source);
        Assert.Contains(@"case ""activity-summary"":", source);
        Assert.Contains("CopyActivitySummary?.Invoke", source);
        Assert.Contains(@"case ""extensibility-summary"":", source);
        Assert.Contains("CopyExtensibilitySummary?.Invoke", source);
        Assert.Contains(@"case ""restart-ssh-tunnel"":", source);
        Assert.Contains("RestartSshTunnel?.Invoke", source);
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
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyBrowserSetupButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopyDebugBundleButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCheckUpdatesButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterRestartSshTunnelButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterUpdateStatusText""", xaml);
        Assert.Matches(
            new Regex(@"<Grid\.RowDefinitions>\s*<RowDefinition/>\s*<RowDefinition/>\s*<RowDefinition/>\s*<RowDefinition/>\s*</Grid\.RowDefinitions>\s*<StackPanel Grid\.Row=""0""", RegexOptions.Singleline),
            xaml);
    }

    [Fact]
    public void TrayMenu_HasSupportDebugActions()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""logfolder"": OpenLogFolder(); break;", source);
        Assert.Contains(@"case ""configfolder"": OpenConfigFolder(); break;", source);
        Assert.Contains(@"case ""diagnosticsfolder"": OpenDiagnosticsFolder(); break;", source);
        Assert.Contains(@"case ""supportcontext"": CopySupportContext(); break;", source);
        Assert.Contains(@"case ""debugbundle"": CopyDebugBundle(); break;", source);
        Assert.Contains(@"case ""browsersetup"": CopyBrowserSetupGuidance(); break;", source);
        Assert.Contains(@"case ""portdiagnostics"": CopyPortDiagnostics(); break;", source);
        Assert.Contains(@"case ""capabilitydiagnostics"": CopyCapabilityDiagnostics(); break;", source);
        Assert.Contains(@"case ""nodeinventory"": CopyNodeInventory(); break;", source);
        Assert.Contains(@"case ""channelsummary"": CopyChannelSummary(); break;", source);
        Assert.Contains(@"case ""activitysummary"": CopyActivitySummary(); break;", source);
        Assert.Contains(@"case ""extensibilitysummary"": CopyExtensibilitySummary(); break;", source);
        Assert.Contains(@"case ""restartsshtunnel"": RestartSshTunnel(); break;", source);
        Assert.Contains(@"menu.AddHeader(LocalizationHelper.GetString(""Menu_SupportDebugHeader""))", source);
        Assert.Contains(@"menu.AddFlyoutMenuItem(LocalizationHelper.GetString(""Menu_OpenSupportFiles""), ""📁"", new[]", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_OpenLogFile""), ""📄"", ""log"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_LogsFolder""), ""📁"", ""logfolder"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_ConfigFolder""), ""🗂️"", ""configfolder"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_DiagnosticsFolder""), ""🧪"", ""diagnosticsfolder"")", source);
        Assert.Contains(@"menu.AddFlyoutMenuItem(LocalizationHelper.GetString(""Menu_CopyDiagnostics""), ""📋"", new[]", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_SupportContext""), ""📋"", ""supportcontext"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_DebugBundle""), ""🧰"", ""debugbundle"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_BrowserSetup""), ""🌐"", ""browsersetup"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_PortDiagnostics""), ""🔌"", ""portdiagnostics"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_CapabilityDiagnostics""), ""🛡️"", ""capabilitydiagnostics"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_NodeInventory""), ""🖥️"", ""nodeinventory"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_ChannelSummary""), ""📡"", ""channelsummary"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_ActivitySummary""), ""⚡"", ""activitysummary"")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_ExtensibilitySummary""), ""🧩"", ""extensibilitysummary"")", source);
        Assert.Contains(@"menu.AddMenuItem(LocalizationHelper.GetString(""Menu_RestartSshTunnel""), ""🔁"", ""restartsshtunnel"", indent: true)", source);
    }

    [Fact]
    public void TrayMenu_UsesFlyoutForRecentActivityPreview()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("recentActivityFlyoutItems", source);
        Assert.Contains("recentActivity", source);
        Assert.Contains("new TrayMenuFlyoutItem(TruncateMenuText(line, 94), \"\", \"activity\")", source);
        Assert.Contains(@"new TrayMenuFlyoutItem(LocalizationHelper.GetString(""Menu_ActivityStream""), ""⚡"", ""activity"")", source);
        Assert.Contains("Menu_RecentActivityFormat", source);
    }

    [Fact]
    public void TrayMenuWindow_SupportsFlyoutMenuItems()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "TrayMenuWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("AddFlyoutMenuItem", source);
        Assert.Contains("Button", source);
        Assert.Contains("ShowCascadingFlyout", source);
        Assert.Contains("ShowAdjacentTo", source);
        Assert.Contains("MonitorFromPoint", source);
        Assert.Contains("CreateRoundRectRgn", source);
        Assert.Contains("SetWindowRgn(hwnd, region, false)", source);
        Assert.Contains("WS_EX_NOACTIVATE", source);
        Assert.Contains("_activeFlyoutOwner", source);
        Assert.Contains("TrayMenuFlyoutItem", source);
    }

    [Fact]
    public void StatusDetailWindow_WiresRestartSshTunnelRequest()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "StatusDetailWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("RestartSshTunnelRequested", source);
        Assert.Contains("OnRestartSshTunnel", source);
        Assert.Contains("CheckUpdatesRequested", source);
        Assert.Contains("OnCheckUpdates", source);
        Assert.Contains("state.Tunnel != null", source);

        var appSourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs");
        var appSource = File.ReadAllText(appSourcePath);

        Assert.Contains(@"case ""checkupdates"":", appSource);
        Assert.Contains("CheckForUpdatesUserInitiatedAsync", appSource);
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
        Assert.Contains("OpenClaw Windows Tray Debug Bundle", source);
        Assert.Contains("BuildDebugBundle", source);
        Assert.Contains("AppendSection", source);
        Assert.Contains("OwningProcessId", source);
        Assert.Contains("OwningProcessName", source);
        Assert.Contains("Stop-Process -Id", source);
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
        Assert.Contains("StatusDetailWindow.BuildBrowserSetupGuidance(port.Port, topology, tunnel)", appSource);
        Assert.Contains("Copy browser setup guidance", appSource);
        Assert.Contains("openclaw node run --host", source);
        Assert.Contains("openclaw browser --browser-profile openclaw doctor", source);
        Assert.Contains(@"topology.Host", source);
        Assert.DoesNotContain("RedactSupportValue(topology.Host)", source);
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
