using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public class TrayMenuWindowMarkupTests
{
    [Fact]
    public void TrayMenuWindow_UsesAutoVerticalScrollbar()
    {
        var xamlPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "TrayMenuWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.Matches(
            new Regex(@"<ScrollViewer[^>]*VerticalScrollBarVisibility=""Auto""", RegexOptions.Singleline),
            xaml);
    }

    [Fact]
    public void OnboardingHostAndGatewayWizard_UseThemeAwareBackgroundResources()
    {
        var onboardingPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Onboarding");

        var sources = Directory.GetFiles(onboardingPath, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToList();

        foreach (var source in sources.Select(file => file.Source))
        {
            Assert.DoesNotContain(".Background(\"#", source);
        }

        Assert.Contains(sources, file => file.Source.Contains("CardBackgroundFillColorDefaultBrush"));
        Assert.Contains(sources, file => file.Source.Contains("SystemFillColorAttentionBackgroundBrush"));

        var onboardingWindowSource = File.ReadAllText(Path.Combine(
            onboardingPath,
            "OnboardingWindow.cs"));
        Assert.Contains("SolidBackgroundFillColorBaseBrush", onboardingWindowSource);
        Assert.DoesNotContain("Microsoft.UI.Colors.White", onboardingWindowSource);

        var functionalUiSource = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClawTray.FunctionalUI",
            "FunctionalUI.cs"));
        Assert.Contains("BackgroundResource", functionalUiSource);
        Assert.Contains("SolidBackgroundFillColorBaseBrush", functionalUiSource);
        Assert.DoesNotContain("Colors.White", functionalUiSource);
    }

    [Fact]
    public void CanvasWindow_BridgeValidatesOriginAndPostsOnDispatcher()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "CanvasWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("BridgeMessageReceived", source);
        Assert.Contains("IsTrustedBridgeSource(e.Source)", source);
        Assert.Contains("openclaw-canvas.local", source);
        Assert.Contains("DispatcherQueue", source);
        Assert.Contains("TryEnqueue(() => PostBridgeMessageOnUiThread", source);
        Assert.Contains("PostWebMessageAsJson(json)", source);
        Assert.Contains("SanitizeBridgeLogValue", source);
        Assert.Contains("WebMessageReceived -= _webMessageReceivedHandler", source);
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
        // ActivityPage was removed; the activity deep link now redirects by filter
        // to the appropriate page (channels by default, sessions/usage/instances
        // when a matching filter is present). The case label is still present.
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""activity"":", source);
        Assert.Contains(@"OpenHub?.Invoke(", source);
    }

    [Fact]
    public void DeepLinkHandler_HasNotificationHistoryEntryPoint()
    {
        // Notification/history deep links now redirect to Channels.
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "DeepLinkHandler.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains(@"case ""history"":", source);
        Assert.Contains(@"case ""notification-history"":", source);
        Assert.Contains(@"OpenHub?.Invoke(""channels"")", source);
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
    public void CommandCenterTextHelper_SupportContextIncludesRedactedTopology()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Helpers",
            "CommandCenterTextHelper.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("Gateway URL: {RedactSupportValue", source);
        Assert.Contains("Topology detail: {RedactSupportValue", source);
        Assert.Contains("Gateway runtime: {RedactSupportValue", source);
        Assert.Contains("Tunnel remote endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel browser proxy local endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel browser proxy remote endpoint: {RedactSupportValue", source);
        Assert.Contains("Tunnel last error: {RedactSupportValue", source);
        Assert.Contains("RedactSupportValue", source);
        Assert.Contains("BuildPortDiagnosticsSummary", source);
        Assert.Contains("OpenClaw port diagnostics", source);
        Assert.Contains("OpenClaw Windows Tray Debug Bundle", source);
        Assert.Contains("BuildDebugBundle", source);
        Assert.Contains("AppendSection", source);
        Assert.Contains("OwningProcessId", source);
        Assert.Contains("OwningProcessName", source);
        Assert.Contains("Stop-Process -Id", source);
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
    public void CommandCenterTextHelper_NodeInventoryIncludesDiagnostics()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Helpers",
            "CommandCenterTextHelper.cs");

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

    [Fact]
    public void ChatWindow_RequestsChatInputFocusWhenShownAndLoaded()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ChatWindow.xaml.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("RequestChatInputFocus();", source);
        Assert.Contains("WebView.Focus(FocusState.Programmatic)", source);
        Assert.Contains("ExecuteScriptAsync", source);
        Assert.Contains("textarea:not([disabled])", source);
        Assert.Contains("[contenteditable=\"true\"]", source);

        var showMethod = Regex.Match(
            source,
            @"public void ShowNearTray\(\).*?SetForegroundWindow\(hwnd\);\s*RequestChatInputFocus\(\);",
            RegexOptions.Singleline);
        Assert.True(showMethod.Success);

        var navigationCompleted = Regex.Match(
            source,
            @"NavigationCompleted \+= .*?WebView\.Visibility = Visibility\.Visible;\s*RequestChatInputFocus\(\);",
            RegexOptions.Singleline);
        Assert.True(navigationCompleted.Success);
    }

    private static string GetRepositoryRoot()
    {
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

        throw new InvalidOperationException("Could not find repository root.");
    }
}
