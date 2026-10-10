using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Collections.Immutable;

namespace OpenClawTray.Services;

internal sealed record TrayMenuSettingsSnapshot(
    bool EnableNodeMode,
    bool EnableMcpServer,
    bool NodeSystemRunEnabled,
    bool NodeBrowserProxyEnabled,
    bool NodeCameraEnabled,
    bool NodeCanvasEnabled,
    bool NodeScreenEnabled,
    bool NodeLocationEnabled,
    bool NodeTtsEnabled,
    bool NodeSttEnabled,
    bool NodeOllamaInferenceEnabled);

internal sealed record TrayGatewaySelfSnapshot(
    string? ServerVersion,
    string? ConnectionId,
    int? Protocol,
    long? UptimeMs,
    string? AuthMode)
{
    internal bool HasAnyDetails =>
        !string.IsNullOrWhiteSpace(ServerVersion) ||
        !string.IsNullOrWhiteSpace(ConnectionId) ||
        Protocol.HasValue ||
        UptimeMs.HasValue ||
        !string.IsNullOrWhiteSpace(AuthMode);

    internal static TrayGatewaySelfSnapshot? From(GatewaySelfInfo? value) =>
        value is null
            ? null
            : new(value.ServerVersion, value.ConnectionId, value.Protocol, value.UptimeMs, value.AuthMode);
}

internal sealed record TrayPresenceSnapshot(
    string? Host,
    string? Platform,
    string? Version,
    string? Mode)
{
    internal static TrayPresenceSnapshot From(PresenceEntry value) =>
        new(value.Host, value.Platform, value.Version, value.Mode);
}

internal sealed record TrayNodeSnapshot(
    string NodeId,
    string DisplayName,
    string Mode,
    string? Platform,
    DateTime? LastSeen,
    bool IsOnline,
    int CapabilityCount,
    int CommandCount,
    ImmutableArray<string> Capabilities,
    ImmutableArray<string> Commands,
    string? Version,
    string? DeviceFamily)
{
    internal string ShortId => NodeId.Length <= 12 ? NodeId : NodeId[..12] + "…";

    internal static TrayNodeSnapshot From(GatewayNodeInfo value) => new(
        value.NodeId,
        value.DisplayName,
        value.Mode,
        value.Platform,
        value.LastSeen,
        value.IsOnline,
        value.CapabilityCount,
        value.CommandCount,
        [.. value.Capabilities],
        [.. value.Commands],
        value.Version,
        value.DeviceFamily);
}

internal sealed record TraySessionWorktreeSnapshot(string? Id, string? Branch, string? RepoRoot)
{
    internal static TraySessionWorktreeSnapshot? From(SessionWorktreeInfo? value) =>
        value is null ? null : new(value.Id, value.Branch, value.RepoRoot);
}

internal sealed record TraySessionSnapshot(
    string Key,
    bool IsMain,
    string? Label,
    string Status,
    bool AbortedLastRun,
    string? Model,
    string? Channel,
    string? DisplayName,
    string? DerivedTitle,
    string? ExecNode,
    TraySessionWorktreeSnapshot? Worktree,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long ContextTokens,
    DateTime? UpdatedAt,
    DateTime LastSeen)
{
    internal string? Classification { get; init; }
    internal string? AgentId { get; init; }
    internal string? AccountId { get; init; }
    internal string? PeerKind { get; init; }
    internal bool? IsBackground { get; init; }
    internal bool? HasActiveRun { get; init; }

    internal static TraySessionSnapshot From(SessionInfo value) => new(
        value.Key,
        value.IsMain,
        value.Label,
        value.Status,
        value.AbortedLastRun,
        value.Model,
        value.Channel,
        value.DisplayName,
        value.DerivedTitle,
        value.ExecNode,
        TraySessionWorktreeSnapshot.From(value.Worktree),
        value.InputTokens,
        value.OutputTokens,
        value.TotalTokens,
        value.ContextTokens,
        value.UpdatedAt,
        value.LastSeen)
    {
        Classification = value.Classification,
        AgentId = value.AgentId,
        AccountId = value.AccountId,
        PeerKind = value.PeerKind,
        IsBackground = value.IsBackground,
        HasActiveRun = value.HasActiveRun,
    };

    internal SessionInfo ToSessionInfo() => new()
    {
        Key = Key,
        IsMain = IsMain,
        Label = Label,
        Status = Status,
        AbortedLastRun = AbortedLastRun,
        Model = Model,
        Channel = Channel,
        DisplayName = DisplayName,
        DerivedTitle = DerivedTitle,
        ExecNode = ExecNode,
        Classification = Classification,
        AgentId = AgentId,
        AccountId = AccountId,
        PeerKind = PeerKind,
        IsBackground = IsBackground,
        HasActiveRun = HasActiveRun,
        Worktree = Worktree is null
            ? null
            : new SessionWorktreeInfo
            {
                Id = Worktree.Id,
                Branch = Worktree.Branch,
                RepoRoot = Worktree.RepoRoot,
            },
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        TotalTokens = TotalTokens,
        ContextTokens = ContextTokens,
        UpdatedAt = UpdatedAt,
        LastSeen = LastSeen,
    };
}

internal sealed record TrayUsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double CostUsd,
    int RequestCount)
{
    internal static TrayUsageSnapshot? From(GatewayUsageInfo? value) =>
        value is null
            ? null
            : new(value.InputTokens, value.OutputTokens, value.TotalTokens, value.CostUsd, value.RequestCount);
}

internal sealed record TrayUsageWindowSnapshot(string Label, double UsedPercent)
{
    internal static TrayUsageWindowSnapshot From(GatewayUsageWindowInfo value) =>
        new(value.Label, value.UsedPercent);
}

internal sealed record TrayUsageProviderSnapshot(
    string Provider,
    string DisplayName,
    string? Plan,
    string? Error,
    ImmutableArray<TrayUsageWindowSnapshot> Windows)
{
    internal static TrayUsageProviderSnapshot From(GatewayUsageProviderInfo value) => new(
        value.Provider,
        value.DisplayName,
        value.Plan,
        value.Error,
        [.. value.Windows.Select(TrayUsageWindowSnapshot.From)]);
}

internal sealed record TrayUsageStatusSnapshot(ImmutableArray<TrayUsageProviderSnapshot> Providers)
{
    internal static TrayUsageStatusSnapshot? From(GatewayUsageStatusInfo? value) =>
        value is null
            ? null
            : new([.. value.Providers.Select(TrayUsageProviderSnapshot.From)]);
}

internal sealed record TrayUsageCostSnapshot(long TotalTokens, double TotalCost)
{
    internal static TrayUsageCostSnapshot? From(GatewayCostUsageInfo? value) =>
        value is null ? null : new(value.Totals.TotalTokens, value.Totals.TotalCost);
}

internal sealed record TrayMenuSnapshot
{
    internal required ConnectionStatus CurrentStatus { get; init; }
    internal OverallConnectionState? OverallState { get; init; }
    internal string? AuthFailureMessage { get; init; }
    internal string? GatewayUrl { get; init; }
    internal TrayGatewaySelfSnapshot? GatewaySelf { get; init; }
    internal ImmutableArray<TrayPresenceSnapshot> Presence { get; init; } = [];
    internal required bool EnableNodeMode { get; init; }
    internal required bool NodeIsPaired { get; init; }
    internal required bool NodeIsPendingApproval { get; init; }
    internal required bool NodeIsConnected { get; init; }
    internal required int NodePendingPairCount { get; init; }
    internal required int DevicePendingPairCount { get; init; }
    internal ImmutableArray<TrayNodeSnapshot> Nodes { get; init; } = [];
    internal ImmutableArray<TraySessionSnapshot> Sessions { get; init; } = [];
    internal TrayUsageSnapshot? Usage { get; init; }
    internal TrayUsageStatusSnapshot? UsageStatus { get; init; }
    internal TrayUsageCostSnapshot? UsageCost { get; init; }
    internal TrayMenuSettingsSnapshot? Settings { get; init; }
    internal required string SetupMenuLabel { get; init; }
    internal required bool ShowSetupMenuEntry { get; init; }
    internal DateTime? LastUpdated { get; init; }
    internal bool IsMcpRunning { get; init; }
    internal string? McpStartupError { get; init; }
}
