namespace OpenClaw.Connection;

/// <summary>
/// Manages an SSH tunnel lifecycle for a gateway connection.
/// Wraps the existing SshTunnelService behind a clean interface.
/// </summary>
public interface ISshTunnelManager : IDisposable
{
    bool IsActive { get; }
    long OwnershipGeneration => 0;
    bool IsRestartPending(SshTunnelExit tunnelExit);
    SshTunnelConfig? ActiveConfig { get; }
    Task<bool> IsOwnedListenerReadyAsync(
        SshTunnelConfig config,
        int destinationPort,
        CancellationToken ct);
    Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct);
    Task StopAsync();
    string? LocalTunnelUrl { get; }
}
