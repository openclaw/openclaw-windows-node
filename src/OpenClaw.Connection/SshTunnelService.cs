using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Connection;

/// <summary>
/// SSH process that passed listener ownership for one dashboard launch.
/// </summary>
public readonly record struct SettingsOwnedForwardBinding(
    bool Owned,
    long Generation,
    int LocalPort,
    int ProcessId);

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
    private SshTunnelOwner _currentOwner;
    private string? _lastSpec;
    private long _lifecycleGeneration;
    private int _browserHandoffLeases;
    private readonly HashSet<int> _dashboardProtectedPorts = new();
    private readonly List<BrowserHandoffLease> _browserHandoffs = new();
    private long _nextBrowserHandoffId;
    private DeferredTunnelStop _deferredStop;

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
    public long OwnershipGeneration
    {
        get
        {
            lock (_stateLock)
            {
                return _lifecycleGeneration;
            }
        }
    }

    public bool HasDeferredStop
    {
        get
        {
            lock (_stateLock)
            {
                return _deferredStop != DeferredTunnelStop.None;
            }
        }
    }

    public int BrowserHandoffLeaseCount
    {
        get
        {
            lock (_stateLock)
            {
                return _browserHandoffLeases;
            }
        }
    }

    public SshTunnelConfig? ActiveConfig
    {
        get
        {
            lock (_stateLock)
            {
                return IsRunningLocked() ? _currentConfig : null;
            }
        }
    }
    public string? LocalTunnelUrl => IsActive ? $"ws://{LoopbackWebSocketHost(CurrentLocalPort)}:{CurrentLocalPort}" : null;
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
                   tunnelExit.Owner == _currentOwner &&
                   !IsRunningLocked() &&
                   Status == TunnelStatus.Restarting;
        }
    }

    public bool TryMarkRecoveryFailed(SshTunnelExit tunnelExit, string reason)
    {
        lock (_stateLock)
        {
            if (!IsRestartPendingLocked(tunnelExit))
                return false;

            Status = TunnelStatus.Failed;
            LastError = reason;
            return true;
        }
    }

    public bool TryRestart(SshTunnelExit tunnelExit)
    {
        lock (_operationLock)
        {
            lock (_stateLock)
            {
                if (!IsRestartPendingLocked(tunnelExit))
                    return false;
            }

            EnsureStartedCore(tunnelExit.Tunnel, tunnelExit.Owner);
            return true;
        }
    }

    public void EnsureStarted(string user, string host, int remotePort, int localPort)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward: false);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort: 22);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
        => EnsureStartedCore(
            new SshTunnelConfig(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort),
            SshTunnelOwner.Settings);

    private void EnsureStartedCore(
        SshTunnelConfig tunnel,
        SshTunnelOwner owner,
        Action<SshTunnelConfig>? beforeStart = null)
    {
        lock (_operationLock)
        {
            var user = tunnel.User.Trim();
            var host = tunnel.Host.Trim();
            tunnel = tunnel with { User = user, Host = host };

            var spec = BuildSpec(
                user,
                host,
                tunnel.RemotePort,
                tunnel.LocalPort,
                tunnel.IncludeBrowserProxyForward,
                tunnel.SshPort);

            Process? claimedProcess;
            lock (_stateLock)
            {
                if (IsRunningLocked() && string.Equals(_lastSpec, spec, StringComparison.Ordinal))
                {
                    _currentOwner = ResolveOwnerForReuse(_currentOwner, owner);
                    Status = TunnelStatus.Up;
                    return;
                }

                if (_browserHandoffLeases > 0)
                    throw new InvalidOperationException("SSH tunnel is held for a dashboard launch.");

                // Lease check and claim share this hold. A later handoff cannot
                // make the stop defer after this caller has decided to replace.
                claimedProcess = ClaimProcessForStopLocked();
            }

            StopClaimedProcess(claimedProcess);
            beforeStart?.Invoke(tunnel);
            lock (_stateLock)
            {
                Status = TunnelStatus.Starting;
            }
            StartProcess(tunnel, owner, spec);
        }
    }

    public void Stop()
    {
        lock (_operationLock)
        {
            StopLocked();
        }
    }

    /// <summary>
    /// Stops SSH. A port already handed to the dashboard stays bound here.
    /// An unsubmitted handoff still defers the stop.
    /// </summary>
    private bool StopLocked()
    {
        CancelOpenBrowserHandoffs();
        int[] portsToKeep;
        SshTunnelConfig? config;
        lock (_stateLock)
        {
            portsToKeep = _dashboardProtectedPorts.ToArray();
            config = _currentConfig;
            if (_browserHandoffLeases > 0 && portsToKeep.Length == 0)
            {
                RememberDeferredStopLocked(DeferredTunnelStop.Stop);
                return false;
            }
        }

        BindDashboardGuards(portsToKeep, config);
        Process? process;
        int[] portsToRelease;
        lock (_stateLock)
        {
            if (portsToKeep.Length > 0)
            {
                _browserHandoffs.Clear();
                _browserHandoffLeases = 0;
            }

            portsToRelease = PortsToReleaseLocked();
            process = ClaimProcessForStopLocked();
            _deferredStop = DeferredTunnelStop.None;
        }

        StopClaimedProcess(process);
        ReleaseForwardPorts(portsToRelease);
        return true;
    }

    /// <summary>
    /// Caller holds <see cref="_stateLock"/>. Claim clears the current config,
    /// so the ports have to be captured first.
    /// </summary>
    private int[] PortsToReleaseLocked()
    {
        if (_browserHandoffs.Any(handoff => !handoff.Settled && (handoff.Submitted || handoff.Opening)))
            return [];

        return _browserHandoffs
            .Select(handoff => handoff.LocalPort)
            .Append(_currentConfig?.LocalPort ?? 0)
            .Where(port => port > 0 && !_dashboardProtectedPorts.Contains(port))
            .Distinct()
            .ToArray();
    }

    private static void BindDashboardGuards(int[] ports, SshTunnelConfig? config)
    {
        foreach (var port in ports)
        {
            if (!DashboardForwardPortGuard.IsHolding(port))
                DashboardForwardPortGuard.Hold(port);
            if (config is not null && config.LocalPort == port)
            {
                DashboardForwardPortGuard.AllowsDestination(
                    port,
                    config.User,
                    config.Host,
                    config.RemotePort,
                    config.SshPort);
            }
        }
    }

    private static void ReleaseForwardPorts(int[] ports)
    {
        foreach (var port in ports)
            DashboardForwardPortGuard.Release(port);
    }

    /// <summary>
    /// Caller holds <see cref="_stateLock"/> and has already decided the lease
    /// does not defer this stop. Does not consult the lease again.
    /// </summary>
    private Process? ClaimProcessForStopLocked()
    {
        // Claim and clear the current process before stopping it. Exit callbacks can
        // then only observe stale ownership and cannot overwrite a replacement.
        _lifecycleGeneration++;
        var process = _process;
        _process = null;
        _processStarted = false;
        _currentConfig = null;
        _currentOwner = SshTunnelOwner.Unspecified;
        _lastSpec = null;
        CurrentBrowserProxyLocalPort = 0;
        CurrentBrowserProxyRemotePort = 0;
        StartedAtUtc = null;
        if (Status != TunnelStatus.NotConfigured)
            Status = TunnelStatus.Stopped;
        return process;
    }

    private void StopClaimedProcess(Process? process)
    {
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
            CancelOpenBrowserHandoffs();
            int[] portsToKeep;
            SshTunnelConfig? config;
            lock (_stateLock)
            {
                portsToKeep = _dashboardProtectedPorts.ToArray();
                config = _currentConfig;
                if (_browserHandoffLeases > 0 && portsToKeep.Length == 0)
                {
                    RememberDeferredStopLocked(DeferredTunnelStop.ResetNotConfigured);
                    return;
                }
            }

            BindDashboardGuards(portsToKeep, config);
            Process? process;
            int[] portsToRelease;
            lock (_stateLock)
            {
                if (portsToKeep.Length > 0)
                {
                    _browserHandoffs.Clear();
                    _browserHandoffLeases = 0;
                }

                portsToRelease = PortsToReleaseLocked();
                process = ClaimProcessForStopLocked();
                _deferredStop = DeferredTunnelStop.None;
                LastError = null;
                Status = TunnelStatus.NotConfigured;
            }

            StopClaimedProcess(process);
            ReleaseForwardPorts(portsToRelease);
        }
    }

    private void StartProcess(SshTunnelConfig tunnel, SshTunnelOwner owner, string spec)
    {
        var user = tunnel.User;
        var host = tunnel.Host;
        var remotePort = tunnel.RemotePort;
        var localPort = tunnel.LocalPort;
        var includeBrowserProxyForward = tunnel.IncludeBrowserProxyForward;
        var sshPort = tunnel.SshPort;
        var reuseGuard = DashboardForwardPortGuard.IsHolding(localPort);
        if (reuseGuard &&
            !DashboardForwardPortGuard.AllowsDestination(localPort, user, host, remotePort, sshPort))
        {
            throw new InvalidOperationException("SSH destination does not match the dashboard forward.");
        }

        var sshLocalPort = reuseGuard ? AllocateLoopbackPort() : localPort;
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = SshTunnelCommandLine.BuildArguments(
                user,
                host,
                remotePort,
                sshLocalPort,
                includeBrowserProxyForward,
                sshPort,
                reuseGuard && includeBrowserProxyForward ? localPort + 2 : null),
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
            SshTunnelExit? tunnelExit = null;
            lock (_stateLock)
            {
                if (generation == _lifecycleGeneration &&
                    ReferenceEquals(_process, process))
                {
                    int exitCode;
                    try
                    {
                        exitCode = process.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Ignoring SSH tunnel exit after process disposal: {ex.Message}");
                        return;
                    }

                    LastError = $"SSH tunnel exited unexpectedly with code {exitCode}.";
                    StartedAtUtc = null;
                    Status = TunnelStatus.Failed;
                    _process = null;
                    _processStarted = false;
                    _lastSpec = null;
                    CurrentBrowserProxyLocalPort = 0;
                    CurrentBrowserProxyRemotePort = 0;
                    tunnelExit = new SshTunnelExit(exitCode, tunnel, generation, _currentOwner);
                }
            }

            if (tunnelExit == null)
            {
                _logger.Debug("Ignoring stale SSH tunnel exit");
                return;
            }

            RetainPublicPortAfterUnexpectedExit(tunnel.LocalPort);
            _logger.Warn($"SSH tunnel exited unexpectedly (code {tunnelExit.ExitCode})");
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
            _currentOwner = owner;
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
            if (reuseGuard)
                DashboardForwardPortGuard.SetBackend(localPort, sshLocalPort, process.Id, process.StartTime.ToUniversalTime());
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
                    _currentOwner = SshTunnelOwner.Unspecified;
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

    private bool IsRestartPendingLocked(SshTunnelExit tunnelExit) =>
        tunnelExit.Generation == _lifecycleGeneration &&
        Equals(_currentConfig, tunnelExit.Tunnel) &&
        tunnelExit.Owner == _currentOwner &&
        !IsRunningLocked() &&
        Status == TunnelStatus.Restarting;

    internal static SshTunnelOwner ResolveOwnerForReuse(
        SshTunnelOwner currentOwner,
        SshTunnelOwner requestedOwner) =>
        currentOwner == SshTunnelOwner.GatewayConnectionManager
            ? currentOwner
            : requestedOwner;

    private void MarkRestartingLocked(int exitCode)
    {
        Status = TunnelStatus.Restarting;
        LastError = $"SSH tunnel exited unexpectedly with code {exitCode}; restart is scheduled.";
    }

    private static string BuildSpec(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
        => $"{user}@{host}:{sshPort}:{localPort}:{remotePort}:browserProxy={includeBrowserProxyForward}";

    public void Dispose()
    {
        lock (_operationLock)
            TeardownLocked();
    }

    public Task<bool> IsOwnedListenerReadyAsync(
        SshTunnelConfig config,
        int destinationPort,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isConfiguredForward =
            destinationPort == config.LocalPort ||
            (config.IncludeBrowserProxyForward && destinationPort == config.LocalPort + 2);
        if (!isConfiguredForward)
            return Task.FromResult(false);

        var normalizedConfig = config with
        {
            User = config.User.Trim(),
            Host = config.Host.Trim(),
        };

        Process process;
        long generation;
        int processId;
        DateTime processStartTimeUtc;
        lock (_stateLock)
        {
            if (!IsRunningLocked() ||
                _process is null ||
                !Equals(_currentConfig, normalizedConfig) ||
                _currentOwner != SshTunnelOwner.GatewayConnectionManager)
            {
                return Task.FromResult(false);
            }

            process = _process;
            generation = _lifecycleGeneration;
            processId = process.Id;
            try
            {
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                _logger.Debug($"SSH listener ownership process inspection failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        try
        {
            if (DashboardForwardPortGuard.IsHolding(destinationPort) &&
                !TryConfirmRetainedPublicRoute(destinationPort))
            {
                return Task.FromResult(false);
            }

            var proofPort = ListenerPortToProve(destinationPort);
            if (proofPort < 1 ||
                !ValidateListenerOwnership(
                    WindowsTcpListenerSnapshot.Capture(),
                    proofPort,
                    processId,
                    processStartTimeUtc))
            {
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH listener ownership verification failed: {ex.Message}");
            return Task.FromResult(false);
        }

        lock (_stateLock)
        {
            return Task.FromResult(
                generation == _lifecycleGeneration &&
                ReferenceEquals(_process, process) &&
                IsRunningLocked() &&
                Equals(_currentConfig, normalizedConfig) &&
                _currentOwner == SshTunnelOwner.GatewayConnectionManager);
        }
    }

    public async Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct) =>
        (await StartOwnedAsync(config, ct).ConfigureAwait(false)).Url;

    public async Task<SettingsOwnedForwardBinding> EnsureSettingsOwnedForwardReadyAsync(
        SshTunnelConfig config,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        long generation = 0;
        var acquiredGuard = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SshTunnelOwner restartOwner;
            lock (_stateLock)
            {
                restartOwner = DashboardRestartOwner(
                    _currentOwner,
                    IsRunningLocked() && _currentConfig?.LocalPort == config.LocalPort);
            }

            acquiredGuard = ClaimDashboardPublicPort(config);
            EnsureStartedCore(
                config,
                restartOwner,
                tunnel => RejectOccupiedForwardPorts(tunnel));

            var normalizedConfig = config with
            {
                User = config.User.Trim(),
                Host = config.Host.Trim(),
            };
            DateTime processStartTimeUtc;
            lock (_stateLock)
            {
                if (!IsRunningLocked() ||
                    _process is null ||
                    !Equals(_currentConfig, normalizedConfig))
                {
                    throw new InvalidOperationException(
                        "SSH tunnel changed before listener ownership could be verified.");
                }

                process = _process;
                generation = _lifecycleGeneration;
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }

            var processId = process.Id;
            await WaitForOwnedLocalListenerAsync(
                config.LocalPort,
                process,
                generation,
                processId,
                processStartTimeUtc,
                cancellationToken).ConfigureAwait(false);
            if (config.IncludeBrowserProxyForward)
            {
                await WaitForOwnedLocalListenerAsync(
                    config.LocalPort + 2,
                    process,
                    generation,
                    processId,
                    processStartTimeUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    !IsRunningLocked() ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner is not (SshTunnelOwner.Settings or SshTunnelOwner.GatewayConnectionManager))
                {
                    return default;
                }

                return new SettingsOwnedForwardBinding(
                    true,
                    generation,
                    normalizedConfig.LocalPort,
                    processId);
            }
        }
        catch (Exception ex)
        {
            var stillCurrent = false;
            lock (_stateLock)
            {
                stillCurrent = process is null
                    ? _process is null
                    : generation == _lifecycleGeneration && ReferenceEquals(_process, process);
                if (!stillCurrent)
                {
                    _logger.Warn($"SSH dashboard forward wait lost ownership: {ex.Message}");
                    return default;
                }

                LastError = ex.Message;
                Status = TunnelStatus.Failed;
            }

            if (acquiredGuard)
                ReleaseUnsubmittedGuard(config.LocalPort);

            if (process is not null)
                StopIfCurrent(process, generation);

            _logger.Warn($"SSH dashboard forward is not owned: {ex.Message}");
            return default;
        }
    }

    public bool IsSettingsOwnedForwardCurrent(long generation, int localPort)
    {
        lock (_stateLock)
        {
            return IsForwardCurrentLocked(generation, localPort);
        }
    }

    public bool TryEnterBrowserHandoff(long generation, int localPort, int processId = 0) =>
        TryEnterBrowserHandoff(generation, localPort, processId, out _);

    public bool TryEnterBrowserHandoff(long generation, int localPort, int processId, out long handoffId)
    {
        lock (_stateLock)
        {
            handoffId = 0;
            if (!IsForwardCurrentLocked(generation, localPort))
                return false;
            if (processId > 0 && _process?.Id != processId)
                return false;

            handoffId = ++_nextBrowserHandoffId;
            _browserHandoffLeases++;
            _browserHandoffs.Add(new BrowserHandoffLease
            {
                Id = handoffId,
                LocalPort = localPort,
            });
            return true;
        }
    }

    public void NoteBrowserHandoffClient(int processId) =>
        NoteBrowserHandoffClient(NewestOpenHandoffId(), processId);

    public void NoteBrowserHandoffClient(long handoffId, int processId)
    {
        if (handoffId <= 0 || processId <= 0)
            return;

        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is not null)
                handoff.BrowserProcessId = processId;
        }
    }

    internal int? HandoffBrowserProcessId(long handoffId)
    {
        lock (_stateLock)
        {
            return FindOpenHandoffLocked(handoffId)?.BrowserProcessId;
        }
    }

    public bool IsBrowserHandoffOpen(long handoffId)
    {
        lock (_stateLock)
        {
            return FindOpenHandoffLocked(handoffId) is not null;
        }
    }

    /// <summary>
    /// Keeps one browser-handoff lease after launch and asks the caller to watch it.
    /// A stop that arrives before attributable browser use stays deferred.
    /// Returns false when <paramref name="localPort"/> is not leased.
    /// </summary>
    public bool TryReleaseBrowserHandoffUnlessDeferred(int localPort, out bool watchDeferredStop)
    {
        lock (_stateLock)
        {
            watchDeferredStop = false;
            if (_browserHandoffLeases <= 0 || _currentConfig?.LocalPort != localPort)
                return false;

            // Process.Start returns before the browser connects. Keep the lease
            // until attributable use, including a stop that arrives after launch.
            watchDeferredStop = true;
            return true;
        }
    }

    public bool TryBeginDashboardNavigation(long handoffId)
    {
        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is null || handoff.CancelRequested || handoff.Submitted)
                return false;

            handoff.Opening = true;
            _dashboardProtectedPorts.Add(handoff.LocalPort);
            return true;
        }
    }

    public bool CompleteDashboardNavigation(long handoffId, bool opened, int? processId)
    {
        var exit = false;
        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is null)
                return false;

            handoff.Opening = false;
            if (!opened)
            {
                exit = true;
                if (!_browserHandoffs.Any(other =>
                        !other.Settled &&
                        other.Id != handoff.Id &&
                        other.LocalPort == handoff.LocalPort &&
                        (other.Submitted || other.Opening)))
                {
                    _dashboardProtectedPorts.Remove(handoff.LocalPort);
                }
            }
            else
            {
                handoff.Submitted = true;
                _dashboardProtectedPorts.Add(handoff.LocalPort);
                if (processId is int id && id > 0)
                    handoff.BrowserProcessId = id;
            }
        }

        if (exit)
            ExitBrowserHandoff(handoffId);
        return opened && IsBrowserHandoffOpen(handoffId);
    }

    public void CancelOpenBrowserHandoffs()
    {
        long[] handoffIds;
        lock (_stateLock)
        {
            foreach (var handoff in _browserHandoffs)
            {
                if (!handoff.Settled)
                    handoff.CancelRequested = true;
            }

            handoffIds = _browserHandoffs
                .Where(handoff => !handoff.Settled && !handoff.Submitted && !handoff.Opening)
                .Select(handoff => handoff.Id)
                .ToArray();
        }

        foreach (var handoffId in handoffIds)
            ExitBrowserHandoff(handoffId);
    }

    public void ExitBrowserHandoff()
    {
        long handoffId;
        lock (_stateLock)
        {
            var oldest = FindOldestOpenHandoffLocked();
            if (oldest is null)
                return;
            handoffId = oldest.Id;
        }

        ExitBrowserHandoff(handoffId);
    }

    public void ExitBrowserHandoff(long handoffId)
    {
        // Same order as Stop: operation lock, then state lock. Claim under the
        // state lock and kill only after releasing it. Do not take the operation
        // lock again when the caller already holds it.
        var enteredOperationLock = false;
        if (!Monitor.IsEntered(_operationLock))
        {
            Monitor.Enter(_operationLock);
            enteredOperationLock = true;
        }

        try
        {
            Process? process = null;
            var completeStop = false;
            lock (_stateLock)
            {
                var handoff = _browserHandoffs.FirstOrDefault(item => item.Id == handoffId);
                if (handoff is null || handoff.Settled)
                    return;

                handoff.Settled = true;
                _browserHandoffs.Remove(handoff);
                if (_browserHandoffLeases > 0)
                    _browserHandoffLeases--;
                if (_browserHandoffLeases != 0 || _deferredStop == DeferredTunnelStop.None)
                    return;

                var reset = _deferredStop == DeferredTunnelStop.ResetNotConfigured;
                process = ClaimProcessForStopLocked();
                _deferredStop = DeferredTunnelStop.None;
                if (reset)
                {
                    LastError = null;
                    Status = TunnelStatus.NotConfigured;
                }

                completeStop = true;
            }

            if (completeStop)
                StopClaimedProcess(process);
        }
        finally
        {
            if (enteredOperationLock)
                Monitor.Exit(_operationLock);
        }
    }

    /// <summary>
    /// Releases one browser-handoff lease for <paramref name="localPort"/>.
    /// A deferred stop or reset then runs. Returns false when that port is not leased.
    /// </summary>
    public bool TryCompleteDeferredBrowserHandoff(int localPort)
    {
        var enteredOperationLock = false;
        if (!Monitor.IsEntered(_operationLock))
        {
            Monitor.Enter(_operationLock);
            enteredOperationLock = true;
        }

        try
        {
            lock (_stateLock)
            {
                if (_browserHandoffLeases <= 0 || _currentConfig?.LocalPort != localPort)
                    return false;
            }

            ExitBrowserHandoff();
            return true;
        }
        finally
        {
            if (enteredOperationLock)
                Monitor.Exit(_operationLock);
        }
    }

    /// <summary>
    /// Waits until the browser uses <paramref name="localPort"/>, or the SSH process is gone.
    /// A timeout leaves the lease and listener in place and returns false.
    /// </summary>
    public Task<bool> WatchBrowserHandoffConsumptionAsync(
        int localPort,
        TimeSpan timeout,
        Func<int, bool>? consumptionProbe,
        CancellationToken cancellationToken = default) =>
        WatchBrowserHandoffConsumptionAsync(0, localPort, timeout, consumptionProbe, cancellationToken);

    public async Task<bool> WatchBrowserHandoffConsumptionAsync(
        long handoffId,
        int localPort,
        TimeSpan timeout,
        Func<int, bool>? consumptionProbe,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handoffId > 0)
            {
                if (!IsBrowserHandoffOpen(handoffId))
                    return true;
            }
            else if (BrowserHandoffLeaseCount <= 0)
            {
                return true;
            }

            // A shell activation process can exit after handing the URL to an
            // existing browser, so that exit is not navigation completion.
            var consumed = consumptionProbe?.Invoke(localPort) == true;
            if (!consumed && !IsRunning && handoffId > 0 && HandoffNeedsRetainedPort(handoffId))
            {
                RetainPublicPortAfterUnexpectedExit(localPort);
                return true;
            }
            else if (consumed || !IsRunning)
            {
                var completed = handoffId > 0
                    ? TryFinishHandoff(handoffId, localPort)
                    : TryCompleteDeferredBrowserHandoff(localPort);
                if (completed || (handoffId > 0 ? !IsBrowserHandoffOpen(handoffId) : BrowserHandoffLeaseCount <= 0))
                    return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.Warn(
                    "Dashboard SSH forward stayed owned because the browser has not connected; the listener was not stopped.");
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryFinishHandoff(long handoffId, int localPort)
    {
        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is null || handoff.LocalPort != localPort)
                return false;
        }

        ExitBrowserHandoff(handoffId);
        return true;
    }

    private long NewestOpenHandoffId()
    {
        lock (_stateLock)
        {
            return FindNewestOpenHandoffLocked()?.Id ?? 0;
        }
    }

    private BrowserHandoffLease? FindOpenHandoffLocked(long handoffId) =>
        _browserHandoffs.FirstOrDefault(handoff => handoff.Id == handoffId && !handoff.Settled);

    private BrowserHandoffLease? FindOldestOpenHandoffLocked()
    {
        BrowserHandoffLease? oldest = null;
        foreach (var handoff in _browserHandoffs)
        {
            if (handoff.Settled)
                continue;
            if (oldest is null || handoff.Id < oldest.Id)
                oldest = handoff;
        }

        return oldest;
    }

    private BrowserHandoffLease? FindNewestOpenHandoffLocked()
    {
        BrowserHandoffLease? newest = null;
        foreach (var handoff in _browserHandoffs)
        {
            if (handoff.Settled)
                continue;
            if (newest is null || handoff.Id > newest.Id)
                newest = handoff;
        }

        return newest;
    }

    private sealed class BrowserHandoffLease
    {
        public long Id { get; init; }
        public int LocalPort { get; init; }
        public int? BrowserProcessId { get; set; }
        public bool Opening { get; set; }
        public bool Submitted { get; set; }
        public bool CancelRequested { get; set; }
        public bool Settled { get; set; }
    }

    /// <summary>
    /// Caller holds <see cref="_stateLock"/>. A reset replaces a plain stop.
    /// A later stop does not downgrade a reset.
    /// </summary>
    private void RememberDeferredStopLocked(DeferredTunnelStop kind)
    {
        if (_deferredStop != DeferredTunnelStop.ResetNotConfigured)
            _deferredStop = kind;
    }

    private enum DeferredTunnelStop
    {
        None,
        Stop,
        ResetNotConfigured,
    }

    private bool IsForwardCurrentLocked(long generation, int localPort)
    {
        return generation == _lifecycleGeneration &&
            IsRunningLocked() &&
            _currentOwner is SshTunnelOwner.Settings or SshTunnelOwner.GatewayConnectionManager &&
            _currentConfig?.LocalPort == localPort;
    }

    /// <summary>
    /// Keeps a manager-owned tunnel on that owner when Dashboard moves SSH behind the public port.
    /// </summary>
    internal static SshTunnelOwner DashboardRestartOwner(SshTunnelOwner currentOwner, bool runningOnRequestedPort) =>
        runningOnRequestedPort && currentOwner == SshTunnelOwner.GatewayConnectionManager
            ? SshTunnelOwner.GatewayConnectionManager
            : SshTunnelOwner.Settings;

    internal static string LoopbackWebSocketHost(int port) =>
        DashboardForwardPortGuard.IsHolding(port) ? "127.0.0.1" : "localhost";

    /// <summary>
    /// Binds the dashboard's public port before SSH listens there.
    /// Returns true when this call created the guard.
    /// The browser URL is not issued until this returns.
    /// </summary>
    private bool ClaimDashboardPublicPort(SshTunnelConfig config)
    {
        var localPort = config.LocalPort;
        var user = config.User.Trim();
        var host = config.Host.Trim();
        if (DashboardForwardPortGuard.IsHolding(localPort))
        {
            DashboardForwardPortGuard.AllowsDestination(
                localPort,
                user,
                host,
                config.RemotePort,
                config.SshPort);
            return false;
        }

        var ownsPublicPort = false;
        lock (_stateLock)
        {
            ownsPublicPort = IsRunningLocked() && _currentConfig?.LocalPort == localPort;
        }

        if (ownsPublicPort)
            Stop();

        RejectForeignForwardPort(localPort);
        if (config.IncludeBrowserProxyForward)
            RejectForeignForwardPort(localPort + 2);

        DashboardForwardPortGuard.Hold(localPort);
        DashboardForwardPortGuard.AllowsDestination(
            localPort,
            user,
            host,
            config.RemotePort,
            config.SshPort);
        return true;
    }

    private void ReleaseUnsubmittedGuard(int port)
    {
        var protect = false;
        lock (_stateLock)
            protect = _dashboardProtectedPorts.Contains(port);
        if (!protect)
            DashboardForwardPortGuard.Release(port);
    }

    private static void RejectOccupiedForwardPorts(SshTunnelConfig tunnel)
    {
        RejectForeignForwardPort(tunnel.LocalPort);
        if (tunnel.IncludeBrowserProxyForward)
            RejectForeignForwardPort(tunnel.LocalPort + 2);
    }

    internal static void RejectForeignForwardPort(int port)
    {
        if (DashboardForwardPortGuard.IsHolding(port))
            return;

        EnsurePortIsUnoccupied(WindowsTcpListenerSnapshot.Capture(), port);
    }

    private static int ListenerPortToProve(int publicPort)
    {
        if (!DashboardForwardPortGuard.IsHolding(publicPort))
            return publicPort;

        return DashboardForwardPortGuard.BackendPort(publicPort) ?? -1;
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task<SshTunnelStartResult> StartOwnedAsync(
        SshTunnelConfig config,
        CancellationToken ct)
    {
        Process? process = null;
        long generation = 0;
        try
        {
            EnsureStartedCore(
                config,
                SshTunnelOwner.GatewayConnectionManager,
                RejectOccupiedForwardPorts);

            var normalizedConfig = config with
            {
                User = config.User.Trim(),
                Host = config.Host.Trim(),
            };
            DateTime processStartTimeUtc;
            lock (_stateLock)
            {
                if (!IsRunningLocked() ||
                    _process is null ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner != SshTunnelOwner.GatewayConnectionManager)
                {
                    throw new InvalidOperationException("SSH tunnel changed before listener ownership could be verified.");
                }

                process = _process;
                generation = _lifecycleGeneration;
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }

            var processId = process.Id;
            await WaitForOwnedLocalListenerAsync(
                config.LocalPort,
                process,
                generation,
                processId,
                processStartTimeUtc,
                ct).ConfigureAwait(false);
            if (config.IncludeBrowserProxyForward)
            {
                await WaitForOwnedLocalListenerAsync(
                    config.LocalPort + 2,
                    process,
                    generation,
                    processId,
                    processStartTimeUtc,
                    ct).ConfigureAwait(false);
            }
            return new SshTunnelStartResult(
                $"ws://{LoopbackWebSocketHost(config.LocalPort)}:{config.LocalPort}",
                normalizedConfig,
                generation);
        }
        catch
        {
            if (process is not null)
                StopIfCurrent(process, generation);
            throw;
        }
    }

    public Task StopAsync()
    {
        lock (_operationLock)
            TeardownLocked();
        return Task.CompletedTask;
    }

    private void TeardownLocked()
    {
        int[] protectedPorts;
        int stoppedPort;
        Process? process;
        SshTunnelConfig? stoppedConfig;
        lock (_stateLock)
        {
            stoppedConfig = _currentConfig;
            stoppedPort = stoppedConfig?.LocalPort ?? 0;
            protectedPorts = _browserHandoffs
                .Where(handoff => !handoff.Settled && (handoff.Submitted || handoff.Opening))
                .Select(handoff => handoff.LocalPort)
                .Concat(_dashboardProtectedPorts)
                .Where(port => port > 0)
                .Distinct()
                .ToArray();
        }

        BindDashboardGuards(protectedPorts, stoppedConfig);
        lock (_stateLock)
        {
            foreach (var handoff in _browserHandoffs)
                handoff.CancelRequested = true;
            _browserHandoffs.Clear();
            _browserHandoffLeases = 0;
            _deferredStop = DeferredTunnelStop.None;
            foreach (var port in protectedPorts)
                _dashboardProtectedPorts.Remove(port);
            process = ClaimProcessForStopLocked();
        }

        foreach (var port in protectedPorts)
            DashboardForwardPortGuard.ClearBackend(port);
        StopClaimedProcess(process);

        if (protectedPorts.Length == 0 && stoppedPort > 0)
            DashboardForwardPortGuard.Release(stoppedPort);
    }

    private bool HandoffNeedsRetainedPort(long handoffId)
    {
        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            return handoff is { Submitted: true } or { Opening: true };
        }
    }

    /// <summary>
    /// Keeps the public dashboard port after SSH exits and drops the lease.
    /// Recovery can then start a backend listener. A deferred stop is not run.
    /// </summary>
    private void RetainPublicPortAfterUnexpectedExit(int localPort)
    {
        SshTunnelConfig? config;
        long[] retainIds;
        lock (_stateLock)
        {
            config = _currentConfig;
            retainIds = _browserHandoffs
                .Where(handoff => !handoff.Settled &&
                    handoff.LocalPort == localPort &&
                    (handoff.Submitted || handoff.Opening))
                .Select(handoff => handoff.Id)
                .ToArray();
            if (retainIds.Length > 0)
                _dashboardProtectedPorts.Add(localPort);
        }

        if (retainIds.Length == 0)
            return;

        if (!DashboardForwardPortGuard.IsHolding(localPort))
            DashboardForwardPortGuard.Hold(localPort);
        if (config is not null && config.LocalPort == localPort)
        {
            DashboardForwardPortGuard.AllowsDestination(
                localPort,
                config.User,
                config.Host,
                config.RemotePort,
                config.SshPort);
        }

        DashboardForwardPortGuard.ClearBackend(localPort);
        foreach (var handoffId in retainIds)
            DropHandoffForRecovery(handoffId);
    }

    private void DropHandoffForRecovery(long handoffId)
    {
        lock (_stateLock)
        {
            var handoff = _browserHandoffs.FirstOrDefault(item => item.Id == handoffId);
            if (handoff is null || handoff.Settled)
                return;

            handoff.Settled = true;
            _browserHandoffs.Remove(handoff);
            if (_browserHandoffLeases > 0)
                _browserHandoffLeases--;
            if (_browserHandoffLeases == 0)
                _deferredStop = DeferredTunnelStop.None;
        }
    }

    public Task<bool> StopIfOwnedAsync(
        SshTunnelConfig config,
        long ownershipGeneration,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(_operationLock, millisecondsTimeout: 50))
            ct.ThrowIfCancellationRequested();
        try
        {
            ct.ThrowIfCancellationRequested();
            var normalizedConfig = config with
            {
                User = config.User.Trim(),
                Host = config.Host.Trim(),
            };
            lock (_stateLock)
            {
                ct.ThrowIfCancellationRequested();
                if (_lifecycleGeneration != ownershipGeneration ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner != SshTunnelOwner.GatewayConnectionManager)
                {
                    return Task.FromResult(false);
                }
            }

            TeardownLocked();
            return Task.FromResult(true);
        }
        finally
        {
            Monitor.Exit(_operationLock);
        }
    }

    private async Task WaitForOwnedLocalListenerAsync(
        int localPort,
        Process process,
        long generation,
        int processId,
        DateTime processStartTimeUtc,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromSeconds(20).TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    !IsRunningLocked())
                {
                    throw new InvalidOperationException(
                        LastError ?? "SSH tunnel changed before its local listener became ready.");
                }
            }

            if (DashboardForwardPortGuard.IsHolding(localPort) &&
                !TryConfirmRetainedPublicRoute(localPort))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var proofPort = ListenerPortToProve(localPort);
            if (proofPort > 0 &&
                ValidateListenerOwnership(
                    WindowsTcpListenerSnapshot.Capture(),
                    proofPort,
                    processId,
                    processStartTimeUtc))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"SSH tunnel did not establish ownership of local listener port {localPort}.");
    }

    private void StopIfCurrent(Process process, long generation)
    {
        lock (_operationLock)
        {
            Process? claimed;
            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    _browserHandoffLeases > 0)
                {
                    return;
                }

                claimed = ClaimProcessForStopLocked();
            }

            StopClaimedProcess(claimed);
        }
    }

    internal static bool TryConfirmRetainedPublicRoute(int publicPort)
    {
        var snapshot = WindowsTcpListenerSnapshot.Capture();
        if (!snapshot.Ipv4Complete || !snapshot.Ipv6Complete)
            return false;

        var listeners = snapshot.Listeners
            .Where(listener => listener.Port == publicPort && CanServeLoopback(listener.Address))
            .ToArray();
        var ownerPid = Environment.ProcessId;
        if (listeners.Any(listener => listener.ProcessId != ownerPid))
        {
            throw new InvalidOperationException(
                $"Local port {publicPort} is not owned exclusively by the dashboard forward.");
        }

        if (listeners.Length == 0)
            return false;

        return !Socket.OSSupportsIPv6 ||
            listeners.Any(listener => listener.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
    }

    internal static void EnsurePortIsUnoccupied(
        WindowsTcpListenerSnapshotResult snapshot,
        int localPort)
    {
        EnsureCompleteListenerSnapshot(snapshot);
        if (snapshot.Listeners.Any(listener =>
            listener.Port == localPort && CanServeLoopback(listener.Address)))
            throw new InvalidOperationException($"Local port {localPort} is already owned by another process.");
    }

    internal static bool ValidateListenerOwnership(
        WindowsTcpListenerSnapshotResult snapshot,
        int localPort,
        int processId,
        DateTime processStartTimeUtc)
    {
        EnsureCompleteListenerSnapshot(snapshot);
        var listeners = snapshot.Listeners
            .Where(listener =>
                listener.Port == localPort && CanServeLoopback(listener.Address))
            .ToArray();
        if (listeners.Length == 0)
            return false;

        if (listeners.Any(listener =>
            listener.ProcessId != processId ||
            listener.ProcessStartTimeUtc != processStartTimeUtc))
        {
            throw new InvalidOperationException(
                $"Local port {localPort} is not owned exclusively by the launched SSH process.");
        }

        return true;
    }

    private static bool CanServeLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any);

    private static void EnsureCompleteListenerSnapshot(WindowsTcpListenerSnapshotResult snapshot)
    {
        if (!snapshot.Ipv4Complete || !snapshot.Ipv6Complete)
            throw new InvalidOperationException("TCP listener ownership could not be verified.");
    }
}

public sealed record SshTunnelExit(
    int ExitCode,
    SshTunnelConfig Tunnel,
    long Generation,
    SshTunnelOwner Owner = SshTunnelOwner.Unspecified);

public enum SshTunnelOwner
{
    Unspecified = 0,
    Settings,
    GatewayConnectionManager
}
