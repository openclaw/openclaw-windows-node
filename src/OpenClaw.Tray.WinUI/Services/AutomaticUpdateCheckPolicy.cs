using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal enum StartupGatewayConnectKind
{
    None,
    Operator,
    NodeOnly,
}

internal enum AutomaticUpdateCheckStartupPlan
{
    Immediate,
    AwaitOperatorHandshakeWithDeadline,
}

internal static class AutomaticUpdateCheckPolicy
{
    public static readonly TimeSpan GatewayResolutionDeadline =
        TimeSpan.FromSeconds(45);

    public static AutomaticUpdateCheckStartupPlan PlanStartup(
        StartupGatewayConnectKind connectKind) =>
        connectKind == StartupGatewayConnectKind.Operator
            ? AutomaticUpdateCheckStartupPlan.AwaitOperatorHandshakeWithDeadline
            : AutomaticUpdateCheckStartupPlan.Immediate;

    public static bool IsGatewayStatusTerminallyUnavailable(
        RoleConnectionState operatorState) =>
        operatorState == RoleConnectionState.PairingRequired;
}
