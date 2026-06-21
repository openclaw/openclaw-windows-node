using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using System;

namespace OpenClawTray.Services;

internal sealed record AppStateSnapshot
{
    public ConnectionStatus Status              { get; init; }
    public DateTime LastCheckTime               { get; init; }
    public ChannelHealth[] Channels             { get; init; } = [];
    public SessionInfo[] Sessions               { get; init; } = [];
    public GatewayNodeInfo[] Nodes              { get; init; } = [];
    public GatewayUsageInfo? Usage              { get; init; }
    public GatewayUsageStatusInfo? UsageStatus  { get; init; }
    public GatewayCostUsageInfo? UsageCost      { get; init; }
    public GatewaySelfInfo? GatewaySelf         { get; init; }
    public string? AuthFailureMessage           { get; init; }
    public UpdateCommandCenterInfo LastUpdateInfo { get; init; } = new();
    public SettingsManager? Settings            { get; init; }
    public NodeService? NodeService             { get; init; }
    public PairingApprovalKind NodePairingApprovalKind { get; init; }
    public string? NodePairingRequestId         { get; init; }
    public SshTunnelSnapshot? SshTunnelSnapshot   { get; init; }
    public bool HasGatewayClient               { get; init; }

    /// <summary>Browser-control override for the active gateway record (scoped per-gateway,
    /// resolved the same way the node-side browser.proxy capability resolves it).</summary>
    public int? EffectiveBrowserControlPort     { get; init; }

    /// <summary>True when a GatewayRecord is currently active. Distinguishes "active gateway
    /// has no tunnel" from "no active gateway (fall back to global settings)".</summary>
    public bool HasActiveGatewayRecord          { get; init; }

    /// <summary>SSH tunnel config from the active GatewayRecord. Null means this gateway is
    /// direct (no tunnel), NOT "unknown". Only meaningful when HasActiveGatewayRecord is true.</summary>
    public SshTunnelConfig? ActiveGatewaySshTunnel { get; init; }
}
