using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class McpRuntimeStatePolicyTests
{
    [Fact]
    public void PlanStartupNotification_WhenDisabled_Dismisses()
    {
        var plan = McpRuntimeStatePolicy.PlanStartupNotification(
            enableMcpServer: false,
            isMcpRunning: false,
            startupError: "port busy");

        Assert.False(plan.ShouldShow);
        Assert.True(plan.ShouldDismiss);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void PlanStartupNotification_WhenEnabledAndHealthy_Dismisses()
    {
        var plan = McpRuntimeStatePolicy.PlanStartupNotification(
            enableMcpServer: true,
            isMcpRunning: true,
            startupError: null);

        Assert.False(plan.ShouldShow);
        Assert.True(plan.ShouldDismiss);
    }

    [Fact]
    public void PlanStartupNotification_WhenEnabledWithStartupError_ShowsError()
    {
        var plan = McpRuntimeStatePolicy.PlanStartupNotification(
            enableMcpServer: true,
            isMcpRunning: false,
            startupError: "Port 8765 is already in use.");

        Assert.True(plan.ShouldShow);
        Assert.False(plan.ShouldDismiss);
        Assert.Equal("Port 8765 is already in use.", plan.Message);
    }

    [Fact]
    public void GetSettingsSetError_WhenEnablingMcpFails_ReturnsStartupError()
    {
        var error = McpRuntimeStatePolicy.GetSettingsSetError(
            nameof(SettingsManager.EnableMcpServer),
            true,
            isMcpRunning: false,
            startupError: "Access denied.");

        Assert.Equal("Access denied.", error);
    }

    [Fact]
    public void GetSettingsSetError_WhenEnablingMcpDoesNotStart_ReturnsDefaultError()
    {
        var error = McpRuntimeStatePolicy.GetSettingsSetError(
            nameof(SettingsManager.EnableMcpServer),
            true,
            isMcpRunning: false,
            startupError: null);

        Assert.Equal(McpRuntimeStatePolicy.DefaultStartupError, error);
    }

    [Fact]
    public void GetSettingsSetError_WhenDisablingMcp_ReturnsNull()
    {
        var error = McpRuntimeStatePolicy.GetSettingsSetError(
            nameof(SettingsManager.EnableMcpServer),
            false,
            isMcpRunning: false,
            startupError: "old error");

        Assert.Null(error);
    }
}
