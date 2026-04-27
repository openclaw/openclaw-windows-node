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
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseLocalGatewayButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseWslGatewayButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseSshTunnelButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SettingsUseRemoteGatewayButton""", xaml);
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
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenLogsButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenConfigButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterOpenDiagnosticsButton""", xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""CommandCenterCopySupportContextButton""", xaml);
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
