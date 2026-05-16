using OpenClaw.Shared;
using System;
using System.Diagnostics;

namespace OpenClaw.Connection;

/// <summary>
/// Manages an SSH local port-forward process for gateway access.
/// </summary>
public sealed class SshTunnelService : ISshTunnelManager
{
    private readonly IOpenClawLogger _logger;
    private Process? _process;
    private string? _lastSpec;
    private bool _stopping;

    /// <summary>Raised when the SSH tunnel exits unexpectedly (not during shutdown).</summary>
    public event EventHandler<int>? TunnelExited;

    public SshTunnelService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };
    public bool IsActive => IsRunning;
    public string? LocalTunnelUrl => IsActive ? $"ws://localhost:{CurrentLocalPort}" : null;
    public string? CurrentUser { get; private set; }
    public string? CurrentHost { get; private set; }
    public int CurrentRemotePort { get; private set; }
    public int CurrentLocalPort { get; private set; }
    public int CurrentBrowserProxyRemotePort { get; private set; }
    public int CurrentBrowserProxyLocalPort { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public TunnelStatus Status { get; private set; } = TunnelStatus.NotConfigured;

    public SshTunnelSnapshot CreateSnapshot() => new(
        IsRunning,
        CurrentUser,
        CurrentHost,
        CurrentRemotePort,
        CurrentLocalPort,
        CurrentBrowserProxyRemotePort,
        CurrentBrowserProxyLocalPort,
        StartedAtUtc,
        LastError,
        Status);

    public void MarkRestarting(int exitCode)
    {
        Status = TunnelStatus.Restarting;
        LastError = $"SSH tunnel exited unexpectedly with code {exitCode}; restart is scheduled.";
    }

    public void EnsureStarted(string user, string host, int remotePort, int localPort)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward: false);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
    {
        user = user.Trim();
        host = host.Trim();

        var spec = BuildSpec(user, host, remotePort, localPort, includeBrowserProxyForward);

        if (IsRunning && string.Equals(_lastSpec, spec, StringComparison.Ordinal))
        {
            Status = TunnelStatus.Up;
            return;
        }

        Stop();
        Status = TunnelStatus.Starting;
        StartProcess(user, host, remotePort, localPort, includeBrowserProxyForward);
        _lastSpec = spec;
    }

    public void Stop()
    {
        if (_process == null)
        {
            CurrentBrowserProxyLocalPort = 0;
            CurrentBrowserProxyRemotePort = 0;
            if (Status != TunnelStatus.NotConfigured)
                Status = TunnelStatus.Stopped;
            return;
        }

        _stopping = true;
        _logger.Info("Stopping SSH tunnel process");

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH tunnel stop failed: {ex.Message}");
        }
        finally
        {
            try { _process.Dispose(); } catch { }
            _process = null;
            _lastSpec = null;
            CurrentBrowserProxyLocalPort = 0;
            CurrentBrowserProxyRemotePort = 0;
            StartedAtUtc = null;
            if (Status != TunnelStatus.NotConfigured)
                Status = TunnelStatus.Stopped;
            _stopping = false;
        }
    }

    public void ResetNotConfigured()
    {
        Stop();
        LastError = null;
        Status = TunnelStatus.NotConfigured;
    }

    private void StartProcess(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = SshTunnelCommandLine.BuildArguments(user, host, remotePort, localPort, includeBrowserProxyForward),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Info($"[SSH] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Warn($"[SSH] {e.Data}");
            }
        };

        process.Exited += (_, _) =>
        {
            var exitCode = process.ExitCode;
            if (_stopping)
            {
                _logger.Info($"SSH tunnel exited during shutdown (code {exitCode})");
            }
            else
            {
                _logger.Warn($"SSH tunnel exited unexpectedly (code {exitCode})");
                LastError = $"SSH tunnel exited unexpectedly with code {exitCode}.";
                StartedAtUtc = null;
                Status = TunnelStatus.Failed;
                try { process.Dispose(); } catch { }
                _process = null;
                _lastSpec = null;
                CurrentBrowserProxyLocalPort = 0;
                CurrentBrowserProxyRemotePort = 0;
                TunnelExited?.Invoke(this, exitCode);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ssh process");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = TunnelStatus.Failed;
            process.Dispose();
            throw new InvalidOperationException("Unable to start SSH tunnel process. Ensure OpenSSH client is installed and available in PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        CurrentUser = user;
        CurrentHost = host;
        CurrentRemotePort = remotePort;
        CurrentLocalPort = localPort;
        CurrentBrowserProxyRemotePort = includeBrowserProxyForward ? remotePort + 2 : 0;
        CurrentBrowserProxyLocalPort = includeBrowserProxyForward ? localPort + 2 : 0;
        StartedAtUtc = DateTime.UtcNow;
        LastError = null;
        Status = TunnelStatus.Up;

        _logger.Info($"SSH tunnel started: 127.0.0.1:{localPort} -> 127.0.0.1:{remotePort} via {user}@{host}");
        if (includeBrowserProxyForward)
        {
            _logger.Info($"SSH tunnel browser proxy forward started: 127.0.0.1:{localPort + 2} -> 127.0.0.1:{remotePort + 2} via {user}@{host}");
        }
    }

    private static string BuildSpec(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
        => $"{user}@{host}:{localPort}:{remotePort}:browserProxy={includeBrowserProxyForward}";

    public void Dispose()
    {
        Stop();
    }

    public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
    {
        EnsureStarted(config.User, config.Host, config.RemotePort, config.LocalPort, config.IncludeBrowserProxyForward);
        var localUrl = $"ws://localhost:{config.LocalPort}";
        return Task.FromResult(localUrl);
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }
}
