using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum ConnectionStatusAccent
{
    Neutral,
    Success,
    Caution,
    Critical,
}

internal static class ConnectionStatusPresenter
{
    public static ConnectionStatus ToLegacyStatus(OverallConnectionState overall) => overall switch
    {
        OverallConnectionState.Connected or OverallConnectionState.Ready or OverallConnectionState.Degraded => ConnectionStatus.Connected,
        OverallConnectionState.Connecting => ConnectionStatus.Connecting,
        OverallConnectionState.Idle or OverallConnectionState.Disconnecting => ConnectionStatus.Disconnected,
        OverallConnectionState.PairingRequired or
        OverallConnectionState.Error => ConnectionStatus.Error,
        _ => ConnectionStatus.Disconnected,
    };

    public static ConnectionStatus ToLegacyStatus(GatewayConnectionSnapshot snapshot)
    {
        if (snapshot.OperatorState == RoleConnectionState.Connected)
            return ConnectionStatus.Connected;

        return ToLegacyStatus(snapshot.OverallState);
    }

    public static bool IsHealthy(OverallConnectionState? overall, ConnectionStatus fallback) =>
        overall is OverallConnectionState.Connected or OverallConnectionState.Ready ||
        (overall is null && fallback == ConnectionStatus.Connected);

    public static bool IsLiveOrPending(OverallConnectionState? overall, ConnectionStatus fallback) =>
        overall is OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.PairingRequired
            or OverallConnectionState.Connecting ||
        (overall is null && fallback is ConnectionStatus.Connected or ConnectionStatus.Connecting);

    public static bool IsOperatorChannelLive(GatewayConnectionSnapshot snapshot) =>
        snapshot.OperatorState == RoleConnectionState.Connected;

    public static string PlainText(OverallConnectionState? overall, ConnectionStatus fallback) => overall switch
    {
        OverallConnectionState.Connected or OverallConnectionState.Ready => "Connected",
        OverallConnectionState.Connecting => "Connecting",
        OverallConnectionState.Degraded => "Degraded",
        OverallConnectionState.PairingRequired => "Pairing required",
        OverallConnectionState.Error => "Connection error",
        OverallConnectionState.Disconnecting => "Disconnecting",
        OverallConnectionState.Idle => "Disconnected",
        _ => fallback switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting",
            ConnectionStatus.Error => "Connection error",
            _ => "Disconnected",
        },
    };

    public static (string LabelKey, ConnectionStatusAccent Accent) Pill(OverallConnectionState overall) => overall switch
    {
        OverallConnectionState.Connected or OverallConnectionState.Ready =>
            ("StatusDisplay_Connected", ConnectionStatusAccent.Success),
        OverallConnectionState.Connecting =>
            ("StatusDisplay_Connecting", ConnectionStatusAccent.Caution),
        OverallConnectionState.Degraded =>
            ("HubWindow_Pill_Degraded", ConnectionStatusAccent.Caution),
        OverallConnectionState.PairingRequired =>
            ("HubWindow_Pill_PairingRequired", ConnectionStatusAccent.Caution),
        OverallConnectionState.Error =>
            ("StatusDisplay_Error", ConnectionStatusAccent.Critical),
        _ => ("StatusDisplay_Disconnected", ConnectionStatusAccent.Neutral),
    };

    public static ConnectionStatusAccent RoleAccent(RoleConnectionState state) => state switch
    {
        RoleConnectionState.Connected => ConnectionStatusAccent.Success,
        RoleConnectionState.Connecting => ConnectionStatusAccent.Caution,
        RoleConnectionState.PairingRequired => ConnectionStatusAccent.Caution,
        RoleConnectionState.Error or
        RoleConnectionState.PairingRejected or
        RoleConnectionState.RateLimited => ConnectionStatusAccent.Critical,
        _ => ConnectionStatusAccent.Neutral,
    };

    public static string RoleStateLabelKey(RoleConnectionState state) => state switch
    {
        RoleConnectionState.Connected => "StatusDisplay_Connected",
        RoleConnectionState.Connecting => "StatusDisplay_Connecting",
        RoleConnectionState.PairingRequired => "HubWindow_Role_PairingRequired",
        RoleConnectionState.PairingRejected => "HubWindow_Role_PairingRejected",
        RoleConnectionState.RateLimited => "HubWindow_Role_RateLimited",
        RoleConnectionState.Error => "StatusDisplay_Error",
        RoleConnectionState.Disabled => "HubWindow_Role_Disabled",
        _ => "HubWindow_Role_Off",
    };

    public static (string LabelKey, ConnectionStatusAccent Accent) NodeRow(
        GatewayConnectionSnapshot snapshot, bool nodeModeEnabled, int enabledCapabilityCount)
    {
        if (!nodeModeEnabled || snapshot.OperatorState != RoleConnectionState.Connected)
            return ("HubWindow_Role_Disabled", ConnectionStatusAccent.Neutral);

        return snapshot.NodeState switch
        {
            RoleConnectionState.PairingRequired => (RoleStateLabelKey(snapshot.NodeState), ConnectionStatusAccent.Caution),
            RoleConnectionState.PairingRejected or
            RoleConnectionState.RateLimited or
            RoleConnectionState.Error => (RoleStateLabelKey(snapshot.NodeState), ConnectionStatusAccent.Critical),
            _ when enabledCapabilityCount == 0 => ("HubWindow_Role_PermissionsIncomplete", ConnectionStatusAccent.Caution),
            _ => (RoleStateLabelKey(snapshot.NodeState), RoleAccent(snapshot.NodeState)),
        };
    }

    public static bool NodeNeedsApproval(GatewayConnectionSnapshot snapshot, bool nodeModeEnabled) =>
        nodeModeEnabled
        && snapshot.OperatorState == RoleConnectionState.Connected
        && snapshot.NodeState == RoleConnectionState.PairingRequired;
}
