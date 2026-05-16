namespace OpenClaw.Connection;

/// <summary>
/// Manages an SSH tunnel lifecycle for a gateway connection.
/// Wraps the existing SshTunnelService behind a clean interface.
/// </summary>
public interface ISshTunnelManager : IDisposable
{
    bool IsActive { get; }
    Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct);
    Task StopAsync();
    string? LocalTunnelUrl { get; }
}
