namespace OpenClaw.Connection;

public sealed record SshTunnelStartResult(
    string Url,
    SshTunnelConfig Config,
    long OwnershipGeneration);

/// <summary>
/// Manages an SSH tunnel lifecycle for a gateway connection.
/// Wraps the existing SshTunnelService behind a clean interface.
/// </summary>
public interface ISshTunnelManager : IDisposable
{
    bool IsActive { get; }
    long OwnershipGeneration { get; }
    bool IsRestartPending(SshTunnelExit tunnelExit);
    SshTunnelConfig? ActiveConfig { get; }
    Task<bool> IsOwnedListenerReadyAsync(
        SshTunnelConfig config,
        int destinationPort,
        CancellationToken ct);
    Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct);
    Task<SshTunnelStartResult> StartOwnedAsync(
        SshTunnelConfig config,
        CancellationToken ct);
    Task StopAsync();
    Task<bool> StopIfOwnedAsync(
        SshTunnelConfig config,
        long ownershipGeneration,
        CancellationToken ct);
    string? LocalTunnelUrl { get; }
}
