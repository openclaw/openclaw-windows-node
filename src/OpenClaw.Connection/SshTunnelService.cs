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
    private readonly object _operationLock = new();
    private readonly object _stateLock = new();
    private Process? _process;
    private bool _processStarted;
    private SshTunnelConfig? _currentConfig;
    private string? _lastSpec;
    private long _lifecycleGeneration;

    /// <summary>Raised when the SSH tunnel exits unexpectedly (not during shutdown).</summary>
    public event EventHandler<SshTunnelExit>? TunnelExited;

    public SshTunnelService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return IsRunningLocked();
            }
        }
    }

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

    public SshTunnelSnapshot CreateSnapshot()
    {
        lock (_stateLock)
        {
            return new SshTunnelSnapshot(
                IsRunningLocked(),
                CurrentUser,
                CurrentHost,
                CurrentRemotePort,
                CurrentLocalPort,
                CurrentBrowserProxyRemotePort,
                CurrentBrowserProxyLocalPort,
                StartedAtUtc,
                LastError,
                Status);
        }
    }

    public bool TryMarkRestarting(SshTunnelExit tunnelExit)
    {
        lock (_stateLock)
        {
            if (tunnelExit.Generation != _lifecycleGeneration ||
                !Equals(_currentConfig, tunnelExit.Tunnel) ||
                IsRunningLocked() ||
                Status != TunnelStatus.Failed)
            {
                return false;
            }

            MarkRestartingLocked(tunnelExit.ExitCode);
            return true;
        }
    }

    public void MarkRestarting(int exitCode)
    {
        lock (_stateLock)
        {
            MarkRestartingLocked(exitCode);
        }
    }

    public bool IsRestartPending(SshTunnelExit tunnelExit)
    {
        lock (_stateLock)
        {
            return tunnelExit.Generation == _lifecycleGeneration &&
                   Equals(_currentConfig, tunnelExit.Tunnel) &&
                   !IsRunningLocked() &&
                   Status == TunnelStatus.Restarting;
        }
    }

    public void EnsureStarted(string user, string host, int remotePort, int localPort)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward: false);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort: 22);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
    {
        lock (_operationLock)
        {
            user = user.Trim();
            host = host.Trim();

            var spec = BuildSpec(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort);

            lock (_stateLock)
            {
                if (IsRunningLocked() && string.Equals(_lastSpec, spec, StringComparison.Ordinal))
                {
                    Status = TunnelStatus.Up;
                    return;
                }
            }

            StopLocked();
            lock (_stateLock)
            {
                Status = TunnelStatus.Starting;
            }
            StartProcess(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort, spec);
        }
    }

    public void Stop()
    {
        lock (_operationLock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        Process? process;
        lock (_stateLock)
        {
            // Claim and clear the current process before stopping it. Exit callbacks can
            // then only observe stale ownership and cannot overwrite a replacement.
            _lifecycleGeneration++;
            process = _process;
            _process = null;
            _processStarted = false;
            _currentConfig = null;
            _lastSpec = null;
            CurrentBrowserProxyLocalPort = 0;
            CurrentBrowserProxyRemotePort = 0;
            StartedAtUtc = null;
            if (Status != TunnelStatus.NotConfigured)
                Status = TunnelStatus.Stopped;
        }

        if (process == null)
            return;

        _logger.Info("Stopping SSH tunnel process");
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH tunnel stop failed: {ex.Message}");
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception disposeEx) { _logger.Debug($"SshTunnelService.Stop: process dispose failed: {disposeEx.Message}"); }
        }
    }

    public void ResetNotConfigured()
    {
        lock (_operationLock)
        {
            StopLocked();
            lock (_stateLock)
            {
                LastError = null;
                Status = TunnelStatus.NotConfigured;
            }
        }
    }

    private void StartProcess(
        string user,
        string host,
        int remotePort,
        int localPort,
        bool includeBrowserProxyForward,
        int sshPort,
        string spec)
    {
        var tunnel = new SshTunnelConfig(
            user,
            host,
            remotePort,
            localPort,
            includeBrowserProxyForward,
            sshPort);
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = SshTunnelCommandLine.BuildArguments(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process
        {
            StartInfo = psi,
        };
        long generation = 0;

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
            SshTunnelExit? tunnelExit = null;
            lock (_stateLock)
            {
                if (generation == _lifecycleGeneration &&
                    ReferenceEquals(_process, process))
                {
                    LastError = $"SSH tunnel exited unexpectedly with code {exitCode}.";
                    StartedAtUtc = null;
                    Status = TunnelStatus.Failed;
                    _process = null;
                    _processStarted = false;
                    _lastSpec = null;
                    CurrentBrowserProxyLocalPort = 0;
                    CurrentBrowserProxyRemotePort = 0;
                    tunnelExit = new SshTunnelExit(exitCode, tunnel, generation);
                }
            }

            if (tunnelExit == null)
            {
                _logger.Debug($"Ignoring stale SSH tunnel exit (code {exitCode})");
                return;
            }

            _logger.Warn($"SSH tunnel exited unexpectedly (code {exitCode})");
            try { process.Dispose(); }
            catch (Exception disposeEx) { _logger.Debug($"SshTunnelService: process dispose after unexpected exit failed: {disposeEx.Message}"); }
            TunnelExited?.Invoke(this, tunnelExit);
        };

        lock (_stateLock)
        {
            generation = ++_lifecycleGeneration;
            _process = process;
            _processStarted = false;
            _currentConfig = tunnel;
            _lastSpec = spec;
        }

        var processStarted = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ssh process");
            }
            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process))
                {
                    return;
                }

                _processStarted = true;
                CurrentUser = user;
                CurrentHost = host;
                CurrentRemotePort = remotePort;
                CurrentLocalPort = localPort;
                CurrentBrowserProxyRemotePort = includeBrowserProxyForward ? remotePort + 2 : 0;
                CurrentBrowserProxyLocalPort = includeBrowserProxyForward ? localPort + 2 : 0;
                StartedAtUtc = DateTime.UtcNow;
                LastError = null;
                Status = TunnelStatus.Up;
            }

            // Enable exit delivery only after the process is fully published. If it already
            // exited, Process raises the event now and the callback atomically claims it.
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (generation == _lifecycleGeneration &&
                    ReferenceEquals(_process, process))
                {
                    LastError = ex.Message;
                    Status = TunnelStatus.Failed;
                    _process = null;
                    _processStarted = false;
                    _currentConfig = null;
                    _lastSpec = null;
                }
            }
            if (processStarted)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger.Debug($"SshTunnelService: process cleanup after start failure failed: {killEx.Message}");
                }
            }
            process.Dispose();
            throw new InvalidOperationException("Unable to start SSH tunnel process. Ensure OpenSSH client is installed and available in PATH.", ex);
        }

        lock (_stateLock)
        {
            if (generation != _lifecycleGeneration ||
                !ReferenceEquals(_process, process))
            {
                return;
            }
        }

        _logger.Info($"SSH tunnel started: 127.0.0.1:{localPort} -> 127.0.0.1:{remotePort} via {user}@{host}:{sshPort}");
        if (includeBrowserProxyForward)
        {
            _logger.Info($"SSH tunnel browser proxy forward started: 127.0.0.1:{localPort + 2} -> 127.0.0.1:{remotePort + 2} via {user}@{host}:{sshPort}");
        }
    }

    private bool IsRunningLocked() => _processStarted && _process is { HasExited: false };

    private void MarkRestartingLocked(int exitCode)
    {
        Status = TunnelStatus.Restarting;
        LastError = $"SSH tunnel exited unexpectedly with code {exitCode}; restart is scheduled.";
    }

    private static string BuildSpec(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
        => $"{user}@{host}:{sshPort}:{localPort}:{remotePort}:browserProxy={includeBrowserProxyForward}";

    public void Dispose()
    {
        Stop();
    }

    public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
    {
        EnsureStarted(config.User, config.Host, config.RemotePort, config.LocalPort, config.IncludeBrowserProxyForward, config.SshPort);
        var localUrl = $"ws://localhost:{config.LocalPort}";
        return Task.FromResult(localUrl);
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }
}

public sealed record SshTunnelExit(
    int ExitCode,
    SshTunnelConfig Tunnel,
    long Generation);
