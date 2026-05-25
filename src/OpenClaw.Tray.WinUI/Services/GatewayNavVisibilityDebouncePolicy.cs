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
}
