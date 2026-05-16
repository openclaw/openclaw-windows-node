using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Immutable snapshot of SSH tunnel state for UI consumers.
/// </summary>
public sealed record SshTunnelSnapshot(
    bool IsRunning,
    string? CurrentUser,
    string? CurrentHost,
    int CurrentRemotePort,
    int CurrentLocalPort,
    int CurrentBrowserProxyRemotePort,
    int CurrentBrowserProxyLocalPort,
    DateTime? StartedAtUtc,
    string? LastError,
    TunnelStatus Status);
