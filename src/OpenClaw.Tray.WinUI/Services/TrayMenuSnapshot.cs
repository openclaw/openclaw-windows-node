using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Services;

internal sealed record TrayMenuSnapshot
{
    // ── Conexión ──
    internal required ConnectionStatus CurrentStatus { get; init; }
    internal required string? AuthFailureMessage { get; init; }
    internal required string? GatewayUrl { get; init; }
    internal required GatewaySelfInfo? GatewaySelf { get; init; }
    internal required PresenceEntry[]? Presence { get; init; }

    // ── Node ──
    internal required bool EnableNodeMode { get; init; }
    internal required bool NodeIsPaired { get; init; }
    internal required bool NodeIsPendingApproval { get; init; }
    internal required bool NodeIsConnected { get; init; }
    internal required PairingListInfo? NodePairList { get; init; }
    internal required DevicePairingListInfo? DevicePairList { get; init; }
    internal required GatewayNodeInfo[] Nodes { get; init; }

    // ── Sesiones ──
    internal required SessionInfo[] Sessions { get; init; }

    // ── Usage ──
    internal required GatewayUsageInfo? Usage { get; init; }
    internal required GatewayUsageStatusInfo? UsageStatus { get; init; }
    internal required GatewayCostUsageInfo? UsageCost { get; init; }

    // ── Permisos y setup ──
    internal required SettingsManager? Settings { get; init; }
    internal required string SetupMenuLabel { get; init; }
}
