using OpenClaw.Shared;
using System;

namespace OpenClawTray.Services;

internal enum GatewayNavVisibilityDecision
{
    ShowNow,
    HideNow,
    ScheduleHide
}

internal static class GatewayNavVisibilityDebouncePolicy
{
    public static readonly TimeSpan DisconnectHideDelay = TimeSpan.FromSeconds(2);

    public static GatewayNavVisibilityDecision GetDecision(ConnectionStatus status, bool debounceDisconnected)
    {
        if (status == ConnectionStatus.Connected)
            return GatewayNavVisibilityDecision.ShowNow;

        return debounceDisconnected
            ? GatewayNavVisibilityDecision.ScheduleHide
            : GatewayNavVisibilityDecision.HideNow;
    }

    public static bool ShouldHideAfterDelay(ConnectionStatus status) =>
        status != ConnectionStatus.Connected;

    public static bool ShouldKeepCurrentPageVisibleDuringDisconnect(string? currentTag) =>
        string.Equals(currentTag, "config", StringComparison.OrdinalIgnoreCase);

    public static bool IsGatewayPageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return tag.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("skills", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("channels", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("instances", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("agentevents", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("bindings", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("cron", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("workspace", StringComparison.OrdinalIgnoreCase);
    }
}
