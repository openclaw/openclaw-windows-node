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
    public SshTunnelService? SshTunnelService   { get; init; }
    public bool HasGatewayClient               { get; init; }
}
