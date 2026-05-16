using OpenClaw.Shared;
using System;

namespace OpenClawTray.Services;

internal sealed record TrayStateSnapshot
{
    public ConnectionStatus Status             { get; init; }
    public AgentActivity? CurrentActivity      { get; init; }
    public ChannelHealth[] Channels            { get; init; } = [];
    public GatewayNodeInfo[] Nodes             { get; init; } = [];
    public GatewayNodeInfo? LocalNodeFallback  { get; init; }
    public string? AuthFailureMessage          { get; init; }
    public DateTime LastCheckTime              { get; init; }
    public SettingsManager? Settings           { get; init; }
}
