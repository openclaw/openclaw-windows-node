using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// Implementation of <see cref="ISshTunnelManager"/> wrapping the existing
/// <see cref="SshTunnelService"/>. Lives outside the Connection directory
/// because it depends on the WinUI-specific SshTunnelService.
/// </summary>
public sealed class SshTunnelManager : ISshTunnelManager
{
    private readonly SshTunnelService _service;
    private readonly IOpenClawLogger _logger;

    public SshTunnelManager(SshTunnelService service, IOpenClawLogger logger)
    {
        _service = service;
        _logger = logger;
    }

    public bool IsActive => _service.IsRunning;
    public string? LocalTunnelUrl => IsActive ? $"ws://localhost:{_service.CurrentLocalPort}" : null;

    public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
    {
        _logger.Info($"[SshTunnelMgr] Starting tunnel {config.User}@{config.Host}:{config.RemotePort} → localhost:{config.LocalPort}");
        _service.EnsureStarted(config.User, config.Host, config.RemotePort, config.LocalPort, config.IncludeBrowserProxyForward);
        var localUrl = $"ws://localhost:{config.LocalPort}";
        _logger.Info($"[SshTunnelMgr] Tunnel started, local URL: {localUrl}");
        return Task.FromResult(localUrl);
    }

    public Task StopAsync()
    {
        _service.Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // SshTunnelService process lifecycle is managed by App — don't dispose here.
        // The connection manager calls StopAsync when needed.
    }
}
