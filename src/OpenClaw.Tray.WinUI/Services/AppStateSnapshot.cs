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
}
