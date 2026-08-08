using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Outcome of a managed-local gateway repair attempt.</summary>
public enum ManagedLocalGatewayRepairOutcome
{
    /// <summary>The active gateway is not an app-owned setup-managed local WSL gateway; nothing touched.</summary>
    NotEligible,

    /// <summary>A repair is already running; this call coalesced.</summary>
    AlreadyInProgress,

    /// <summary>The active gateway changed (user switch/disconnect) during the repair; aborted without
    /// restarting or reconnecting a gateway we are no longer on.</summary>
    AbortedGatewayChanged,

    /// <summary>The operator explicitly disconnected or stopped this gateway; automatic repair must
    /// not override that desired state.</summary>
    AbortedUserIntent,

    /// <summary>By the time a restart was due, the operator's failure was no longer a transport outage
    /// (e.g. it became an auth/pairing/credential error). A distro restart cannot fix those, so the
    /// repair aborts rather than pointlessly restarting and burning the restart budget.</summary>
    AbortedNonTransportFailure,

    /// <summary>The per-gateway restart budget is exhausted; manual action needed.</summary>
    AttemptsExhausted,

    /// <summary>The gateway process was already reachable; reconnected without restarting the distro.</summary>
    ReconnectedWithoutRestart,

    /// <summary>Restarting the managed WSL distro failed.</summary>
    RestartFailed,

    /// <summary>An unverified process owns the managed gateway port; no process was stopped.</summary>
    PortConflictBlocked,

    /// <summary>A proven obsolete native OpenClaw gateway could not be removed safely.</summary>
    PortConflictRepairFailed,

    /// <summary>Restart/reconnect ran but the operator connection was not verified before the timeout.</summary>
    ReconnectPendingVerification,

    /// <summary>Restart + reconnect succeeded and the operator connection is verified live.</summary>
    Repaired
}

/// <summary>Structured result of a managed-local gateway repair.</summary>
public sealed record ManagedLocalGatewayRepairResult(
    ManagedLocalGatewayRepairOutcome Outcome,
    string? DistroName = null,
    string? Detail = null)
{
    public bool Succeeded =>
        Outcome is ManagedLocalGatewayRepairOutcome.Repaired
            or ManagedLocalGatewayRepairOutcome.ReconnectedWithoutRestart;
}

/// <summary>Result of restarting a managed-local WSL gateway distro.</summary>
public sealed record ManagedLocalGatewayRestartResult(bool Success, string? Detail = null);

/// <summary>Restarts an app-owned local WSL gateway distro. Abstracted for testability.</summary>
public interface IManagedLocalGatewayRestarter
{
    Task<ManagedLocalGatewayRestartResult> RestartAsync(
        string distroName,
        CancellationToken cancellationToken,
        Func<bool>? canContinue = null);
}

/// <summary>
/// Bounded self-repair for app-owned setup-managed local WSL gateways. It <em>probes before it
/// restarts</em>: if the gateway process is already reachable, it reconnects without touching the
/// distro (the Mac "attach" path); only a genuinely-down gateway triggers a distro restart, a
/// keepalive re-arm, and a reconnect. Success is verified by a real operator connection to the
/// same gateway rather than trusting the restart command.
/// </summary>
/// <remarks>
/// Safety invariants (this is the single owner of dangerous local process supervision, kept out
/// of the connection layer):
/// <list type="bullet">
///   <item>Only acts on <see cref="WslKeepAlivePolicy.IsSetupManagedLocalRecord"/> gateways —
///     never SSH, remote, or ambiguous-localhost gateways.</item>
///   <item>Probe-before-restart so a transient blip or a live-gateway/dead-socket case does not
///     trigger the heaviest remedy.</item>
///   <item>Reconnect is gateway-pinned and cancellable
///     (<see cref="GatewayConnectionManager.ReconnectIfCurrentAsync"/>) so a gateway switch during
///     repair cannot disrupt the newly selected gateway.</item>
///   <item>Single-flight; per-gateway restart budget (reset on verified success or gateway change,
///     and by the monitor when the gateway is healthy again).</item>
///   <item>Never reads or logs credential material.</item>
/// </list>
/// </remarks>
public sealed class ManagedLocalGatewayRepairCoordinator
{
    private readonly GatewayRegistry _registry;
    private readonly IManagedLocalGatewayRestarter _restarter;
    private readonly Func<string, CancellationToken, Task<bool>> _probeReachableAsync;
    private readonly Func<string, CancellationToken, Task<bool>> _reconnectIfCurrentAsync;
    private readonly Func<bool> _isOperatorConnected;
    private readonly Func<CancellationToken, Task> _reArmKeepAliveAsync;
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly IOpenClawLogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxRestartsPerGateway;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeSpan _verifyPollInterval;
    // Non-blocking acquire of the shared gateway-lifecycle lease; null => a manual op holds it, so the
    // destructive restart must be deferred. Held only around the restart (mutual exclusion with a
    // manual WSL start/stop/restart).
    private readonly Func<IDisposable?>? _tryAcquireLifecycleLease;
    // Re-checks, immediately before restarting, that the operator failure is STILL a transport outage
    // (not an auth/pairing/credential error that surfaced during the verify wait — a restart can't fix
    // those). Null => skip the re-check.
    private readonly Func<bool>? _isRestartStillWarranted;
    private readonly Func<string, bool> _isAutomaticRepairAllowed;
    private readonly Func<GatewayRecord, CancellationToken, Task<ManagedLocalPortConflictRepairResult>>?
        _repairPortConflictAsync;
    private readonly Func<bool> _isPortConflictCandidate;

    private readonly SemaphoreSlim _repairLock = new(1, 1);
    private readonly object _attemptLock = new();
    // Restart budget keyed by gateway id so alternating between gateways cannot bypass the per-gateway
    // bound (a single-slot counter would reset every time the target gateway changed).
    private readonly Dictionary<string, int> _restartCounts = new(StringComparer.Ordinal);

    public ManagedLocalGatewayRepairCoordinator(
        GatewayRegistry registry,
        IManagedLocalGatewayRestarter restarter,
        Func<string, CancellationToken, Task<bool>> probeReachableAsync,
        Func<string, CancellationToken, Task<bool>> reconnectIfCurrentAsync,
        Func<bool> isOperatorConnected,
        Func<CancellationToken, Task> reArmKeepAliveAsync,
        ConnectionDiagnostics diagnostics,
        IOpenClawLogger logger,
        int maxRestartsPerGateway = 3,
        TimeSpan? verifyTimeout = null,
        TimeSpan? verifyPollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<IDisposable?>? tryAcquireLifecycleLease = null,
        Func<bool>? isRestartStillWarranted = null,
        Func<string, bool>? isAutomaticRepairAllowed = null,
        Func<GatewayRecord, CancellationToken, Task<ManagedLocalPortConflictRepairResult>>?
            repairPortConflictAsync = null,
        Func<bool>? isPortConflictCandidate = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _restarter = restarter ?? throw new ArgumentNullException(nameof(restarter));
        _probeReachableAsync = probeReachableAsync ?? throw new ArgumentNullException(nameof(probeReachableAsync));
        _reconnectIfCurrentAsync = reconnectIfCurrentAsync ?? throw new ArgumentNullException(nameof(reconnectIfCurrentAsync));
        _isOperatorConnected = isOperatorConnected ?? throw new ArgumentNullException(nameof(isOperatorConnected));
        _reArmKeepAliveAsync = reArmKeepAliveAsync ?? throw new ArgumentNullException(nameof(reArmKeepAliveAsync));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRestartsPerGateway = maxRestartsPerGateway > 0 ? maxRestartsPerGateway : 1;
        _verifyTimeout = verifyTimeout ?? TimeSpan.FromSeconds(15);
        _verifyPollInterval = verifyPollInterval ?? TimeSpan.FromMilliseconds(250);
        _delay = delay ?? Task.Delay;
        _tryAcquireLifecycleLease = tryAcquireLifecycleLease;
        _isRestartStillWarranted = isRestartStillWarranted;
        _isAutomaticRepairAllowed = isAutomaticRepairAllowed ?? (static _ => true);
        _repairPortConflictAsync = repairPortConflictAsync;
        _isPortConflictCandidate = isPortConflictCandidate ?? (static () => false);
    }

    /// <summary>
    /// Repairs the active gateway if — and only if — it is an app-owned setup-managed local WSL
    /// gateway. Safe to call when healthy or ineligible; returns a descriptive outcome without
    /// side effects in those cases.
    /// </summary>
    public async Task<ManagedLocalGatewayRepairResult> TryRepairActiveGatewayAsync(CancellationToken cancellationToken = default)
    {
        if (!WslKeepAlivePolicy.IsSetupManagedLocalRecord(_registry.GetActive() ?? EmptyRecord))
            return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.NotEligible);

        if (!await _repairLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AlreadyInProgress);

        IDisposable? lifecycleLease = null;
        try
        {
            // Re-read the active record under the lock and bind everything below to THIS record so a
            // gateway switch mid-repair cannot restart the wrong distro or report a false success.
            var record = _registry.GetActive();
            if (record is null || !WslKeepAlivePolicy.IsSetupManagedLocalRecord(record))
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.NotEligible);

            var distro = WslKeepAlivePolicy.ResolveDistroName(record, setupStateDistroName: null, environmentOverride: null);
            var gatewayId = record.Id;
            if (!_isAutomaticRepairAllowed(gatewayId))
                return new ManagedLocalGatewayRepairResult(
                    ManagedLocalGatewayRepairOutcome.AbortedUserIntent,
                    distro,
                    "Automatic repair is disabled because the gateway was explicitly disconnected or stopped.");

            var collisionRepaired = false;
            if (_repairPortConflictAsync is not null && _isPortConflictCandidate())
            {
                lifecycleLease = _tryAcquireLifecycleLease?.Invoke();
                if (_tryAcquireLifecycleLease is not null && lifecycleLease is null)
                {
                    return new ManagedLocalGatewayRepairResult(
                        ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged,
                        distro,
                        "Manual gateway operation in progress.");
                }

                var currentBeforeRepair = _registry.GetActive();
                if (!WslKeepAlivePolicy.IsSameSetupManagedGateway(record, currentBeforeRepair) ||
                    !_isAutomaticRepairAllowed(gatewayId) ||
                    !_isPortConflictCandidate())
                {
                    return new ManagedLocalGatewayRepairResult(
                        ManagedLocalGatewayRepairOutcome.AbortedUserIntent,
                        distro,
                        "Gateway changed or was explicitly stopped before port-conflict repair.");
                }

                var portRepair = await _repairPortConflictAsync(record, cancellationToken).ConfigureAwait(false);
                switch (portRepair.Outcome)
                {
                    case ManagedLocalPortConflictRepairOutcome.Repaired:
                        collisionRepaired = true;
                        _diagnostics.Record("setup", "Removed proven obsolete native gateway port conflict", portRepair.Detail);
                        break;
                    case ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner:
                        _diagnostics.Record("setup", "Managed gateway port conflict has an unverified owner", portRepair.Detail);
                        return new ManagedLocalGatewayRepairResult(
                            ManagedLocalGatewayRepairOutcome.PortConflictBlocked,
                            distro,
                            portRepair.Detail);
                    case ManagedLocalPortConflictRepairOutcome.Failed:
                        _diagnostics.Record("setup", "Could not remove proven native gateway port conflict", portRepair.Detail);
                        return new ManagedLocalGatewayRepairResult(
                            ManagedLocalGatewayRepairOutcome.PortConflictRepairFailed,
                            distro,
                            portRepair.Detail);
                }
            }

            // Probe-before-restart: if the gateway process is already reachable, try a plain reconnect
            // first (cheapest remedy, no restart budget consumed). If the gateway answers TCP but the
            // connection still can't be verified — e.g. a wedged gateway that accepts sockets yet never
            // completes the WebSocket handshake — fall through to a budgeted distro restart instead of
            // looping on the TCP-positive reachable path forever.
            if (!collisionRepaired &&
                await SafeProbeAsync(record.Url, cancellationToken).ConfigureAwait(false))
            {
                if (!_isAutomaticRepairAllowed(gatewayId))
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedUserIntent, distro);

                _diagnostics.Record("setup", "Local gateway reachable; reconnecting without restart", $"distro={distro}");
                var gatewayStillCurrent = true;
                var verified = false;
                try
                {
                    // A false return means the gateway is no longer the active one (user switched or
                    // disconnected during the repair); true means we reconnected the pinned gateway.
                    gatewayStillCurrent = await _reconnectIfCurrentAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                    if (gatewayStillCurrent)
                        verified = await VerifyAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A reconnect that ERRORS (vs cleanly no-oping) means the pinned gateway is still
                    // current but the connect failed — leave gatewayStillCurrent=true so we escalate.
                    _logger.Warn($"[GatewayRepair] Reconnect (reachable path) threw: {ex.Message}");
                    _diagnostics.Record("setup", "Local gateway reconnect (reachable) failed", ex.Message);
                }

                if (!gatewayStillCurrent)
                {
                    // Do NOT escalate to a distro restart of a gateway we are no longer on.
                    _diagnostics.Record("setup", "Active gateway changed during reconnect; aborting repair", $"distro={distro}");
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, distro,
                        "Active gateway changed during repair.");
                }

                if (verified)
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.ReconnectedWithoutRestart, distro);

                // Reachable + still the pinned gateway + not verified: the gateway is answering TCP yet
                // the operator connection is still down. Escalate to a budgeted distro restart rather
                // than retrying the reachable path.
                _diagnostics.Record("setup", "Local gateway reachable but not verified; escalating to restart", $"distro={distro}");
            }

            // The gateway process is down (or wedged) — this needs a distro restart, which is budgeted
            // per gateway id.
            //
            // Re-validate the FULL record — not just the id — immediately before the destructive
            // restart. During the up-to-15s verify the user may have (a) switched gateways, (b)
            // repointed THIS record to a remote host or SSH tunnel (same id, no longer a managed WSL
            // gateway), or (c) started a manual gateway lifecycle action. Any of these means we must not
            // restart the cached distro (a manual restart + our --terminate could kill its fresh VM).
            var currentActive = _registry.GetActive();
            if (!WslKeepAlivePolicy.IsSameSetupManagedGateway(record, currentActive))
            {
                _diagnostics.Record("setup", "Gateway no longer eligible for restart (switched/edited); aborting", $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, distro,
                    "Gateway changed during repair.");
            }
            if (!_isAutomaticRepairAllowed(gatewayId))
            {
                _diagnostics.Record("setup", "Gateway was explicitly disconnected/stopped; skipping restart", $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedUserIntent, distro);
            }

            // The operator failure may have become a non-transport terminal error (auth/pairing/creds)
            // during the up-to-15s verify wait — a distro restart cannot fix those, so abort instead of
            // burning the restart budget on a pointless restart.
            if (_isRestartStillWarranted is not null && !_isRestartStillWarranted())
            {
                _diagnostics.Record("setup", "Operator failure is no longer a transport outage; skipping restart", $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedNonTransportFailure, distro,
                    "Failure is not a transport outage; a restart cannot fix it.");
            }

            // Acquire the shared gateway-lifecycle lease so this destructive restart is mutually
            // exclusive with a manual WSL start/stop/restart. Non-blocking: if a manual op holds it,
            // defer — running a concurrent restart (whose --terminate could kill the manual op's fresh
            // VM) is exactly what this lease prevents.
            lifecycleLease ??= _tryAcquireLifecycleLease?.Invoke();
            if (_tryAcquireLifecycleLease is not null && lifecycleLease is null)
            {
                _diagnostics.Record("setup", "Manual gateway operation in progress; deferring auto-repair restart", $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, distro,
                    "Manual gateway operation in progress.");
            }

            ManagedLocalGatewayRepairResult? GetRestartAbortResult(bool requireTransportFailure = true)
            {
                var active = _registry.GetActive();
                if (!WslKeepAlivePolicy.IsSameSetupManagedGateway(record, active))
                {
                    return new ManagedLocalGatewayRepairResult(
                        ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged,
                        distro,
                        "Gateway changed before restart.");
                }
                if (!_isAutomaticRepairAllowed(gatewayId))
                {
                    return new ManagedLocalGatewayRepairResult(
                        ManagedLocalGatewayRepairOutcome.AbortedUserIntent,
                        distro,
                        "Gateway was explicitly disconnected or stopped.");
                }
                if (requireTransportFailure &&
                    _isRestartStillWarranted is not null &&
                    !_isRestartStillWarranted())
                {
                    return new ManagedLocalGatewayRepairResult(
                        ManagedLocalGatewayRepairOutcome.AbortedNonTransportFailure,
                        distro,
                        "Failure is no longer a transport outage.");
                }
                return null;
            }

            bool CanContinueRestart() => GetRestartAbortResult() is null;

            if (GetRestartAbortResult() is { } beforeRestartAbort)
                return beforeRestartAbort;

            ManagedLocalGatewayRestartResult restart;
            try
            {
                lock (_attemptLock)
                {
                    _restartCounts.TryGetValue(gatewayId, out var count);
                    if (count >= _maxRestartsPerGateway)
                    {
                        _diagnostics.Record("setup", "Local gateway restart budget exhausted; manual action needed", $"distro={distro}; restarts={count}");
                        return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, distro,
                            "Repair restart budget exhausted. Restart the gateway from Connection settings or re-run setup.");
                    }

                    _restartCounts[gatewayId] = count + 1;
                }

                _diagnostics.Record("setup", "Local gateway unreachable; restarting managed WSL distro", $"distro={distro}");

                try
                {
                    restart = await _restarter
                        .RestartAsync(distro!, cancellationToken, CanContinueRestart)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[GatewayRepair] WSL restart threw: {ex.Message}");
                    _diagnostics.Record("setup", "Local gateway restart failed", ex.Message);
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.RestartFailed, distro, ex.Message);
                }

                if (GetRestartAbortResult(requireTransportFailure: !restart.Success) is { } duringRestartAbort)
                    return duringRestartAbort;

                if (!restart.Success)
                {
                    _diagnostics.Record("setup", "Local gateway restart failed", restart.Detail);
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.RestartFailed, distro, restart.Detail);
                }

                // Re-arm the WSL keepalive so an externally-terminated distro does not idle back out
                // immediately after we bring it back.
                try { await _reArmKeepAliveAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.Warn($"[GatewayRepair] Keepalive re-arm threw: {ex.Message}"); }
            }
            finally
            {
                // Release the lease before the (non-destructive) reconnect + verify so a manual op does
                // not wait out the verify window.
                lifecycleLease?.Dispose();
                lifecycleLease = null;
            }

            _diagnostics.Record("setup", "Local gateway restarted; reconnecting", $"distro={distro}");
            cancellationToken.ThrowIfCancellationRequested();
            if (!WslKeepAlivePolicy.IsSameSetupManagedGateway(record, _registry.GetActive()))
                return new ManagedLocalGatewayRepairResult(
                    ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged,
                    distro,
                    "Gateway changed after restart.");
            if (!_isAutomaticRepairAllowed(gatewayId))
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedUserIntent, distro);

            if (_isOperatorConnected() &&
                WslKeepAlivePolicy.IsSameSetupManagedGateway(record, _registry.GetActive()))
            {
                lock (_attemptLock) { _restartCounts.Remove(gatewayId); }
                _diagnostics.Record(
                    "setup",
                    "Local gateway repair verified during restart (operator connected)",
                    $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.Repaired, distro);
            }

            try
            {
                if (!await _reconnectIfCurrentAsync(gatewayId, cancellationToken).ConfigureAwait(false))
                {
                    // Active gateway changed after the restart — do not keep driving a gateway we are
                    // no longer on. The distro restart already happened (budget consumed); that's fine.
                    _diagnostics.Record("setup", "Active gateway changed after restart; aborting repair", $"distro={distro}");
                    return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, distro,
                        "Active gateway changed during repair.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[GatewayRepair] Reconnect after restart threw: {ex.Message}");
                _diagnostics.Record("setup", "Local gateway reconnect after restart failed", ex.Message);
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, distro,
                    "Gateway restarted; reconnect could not be completed.");
            }

            if (await VerifyAsync(gatewayId, cancellationToken).ConfigureAwait(false))
            {
                lock (_attemptLock) { _restartCounts.Remove(gatewayId); }
                _diagnostics.Record("setup", "Local gateway repair verified (operator connected)", $"distro={distro}");
                return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.Repaired, distro);
            }

            _diagnostics.Record("setup", "Local gateway restarted but operator connection not yet verified", $"distro={distro}");
            return new ManagedLocalGatewayRepairResult(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, distro,
                "Gateway restarted; still waiting for the connection to come back.");
        }
        finally
        {
            lifecycleLease?.Dispose();
            _repairLock.Release();
        }
    }

    /// <summary>Resets the restart budget for a specific gateway. Called by the monitor when that
    /// gateway is healthy again so a later outage starts fresh — without clearing other gateways'
    /// budgets (which would let alternating gateways evade the per-gateway bound).</summary>
    public void ResetAttemptBudget(string? gatewayId)
    {
        if (string.IsNullOrEmpty(gatewayId))
            return;
        lock (_attemptLock) { _restartCounts.Remove(gatewayId); }
    }

    /// <summary>Resets the restart budget for all gateways.</summary>
    public void ResetAttemptBudget()
    {
        lock (_attemptLock) { _restartCounts.Clear(); }
    }

    private async Task<bool> SafeProbeAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            return await _probeReachableAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[GatewayRepair] Reachability probe threw: {ex.Message}");
            return false; // treat probe failure as unreachable
        }
    }

    // Verified success requires BOTH that the operator is connected AND that the still-active gateway
    // is the one we repaired — otherwise a gateway switch mid-repair could report a false success.
    private async Task<bool> VerifyAsync(string gatewayId, CancellationToken cancellationToken)
    {
        bool Verified() =>
            string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal) &&
            _isOperatorConnected();

        if (Verified())
            return true;

        var deadline = DateTimeOffset.UtcNow + _verifyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(_verifyPollInterval, cancellationToken).ConfigureAwait(false);
            if (Verified())
                return true;
        }

        return false;
    }

    private static readonly GatewayRecord EmptyRecord = new() { Id = string.Empty, Url = string.Empty };
}
