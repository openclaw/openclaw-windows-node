using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal static class ConnectionTogglePresenter
{
    internal static ConnectionTogglePresentation Present(
        ConnectionStatus status,
        OverallConnectionState? overallState)
    {
        var isOn = ConnectionStatusPresenter.IsLiveOrPending(overallState, status);
        var isEnabled = overallState switch
        {
            OverallConnectionState.Connecting or OverallConnectionState.Disconnecting => false,
            null => status is ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error,
            _ => true,
        };
        var statusText = ConnectionStatusPresenter.PlainText(overallState, status);
        var toolTip = isOn
            ? $"{statusText} - toggle off to disconnect"
            : status == ConnectionStatus.Connecting
                ? "Connecting..."
                : $"{statusText} - toggle on to connect";

        return new ConnectionTogglePresentation(
            IsOn: isOn,
            IsEnabled: isEnabled,
            ToolTip: toolTip,
            AutomationName: "Gateway connection");
    }
}
