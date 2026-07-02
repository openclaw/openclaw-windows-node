using System;

namespace OpenClawTray.Services;

internal readonly record struct McpStartupNotificationPlan(bool ShouldShow, string? Message)
{
    public bool ShouldDismiss => !ShouldShow;
}

internal static class McpRuntimeStatePolicy
{
    public const string DefaultStartupError = "Local MCP server did not start.";

    public static McpStartupNotificationPlan PlanStartupNotification(
        bool enableMcpServer,
        bool isMcpRunning,
        string? startupError)
    {
        if (!enableMcpServer)
            return new McpStartupNotificationPlan(false, null);

        if (!string.IsNullOrWhiteSpace(startupError))
            return new McpStartupNotificationPlan(true, startupError);

        return isMcpRunning
            ? new McpStartupNotificationPlan(false, null)
            : new McpStartupNotificationPlan(true, DefaultStartupError);
    }

    public static string? GetSettingsSetError(
        string settingName,
        object? convertedValue,
        bool isMcpRunning,
        string? startupError)
    {
        if (!string.Equals(settingName, nameof(SettingsManager.EnableMcpServer), StringComparison.OrdinalIgnoreCase) ||
            convertedValue is not bool enableMcpServer ||
            !enableMcpServer)
        {
            return null;
        }

        var plan = PlanStartupNotification(enableMcpServer, isMcpRunning, startupError);
        return plan.ShouldShow ? plan.Message : null;
    }
}
