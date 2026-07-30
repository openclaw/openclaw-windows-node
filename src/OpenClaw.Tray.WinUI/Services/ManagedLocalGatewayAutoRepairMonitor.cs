using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Watches the operator connection and, when an app-owned setup-managed local WSL gateway looks
/// <em>transport-unreachable</em> for a sustained period, invokes
/// <see cref="ManagedLocalGatewayRepairCoordinator"/> to (probe and, if down) restart the WSL
/// distro and reconnect — so a crashed/stopped local gateway self-heals without user action.
/// </summary>
/// <remarks>
/// Safety design (addresses the multi-model review findings):
/// <list type="bullet">
///   <item>Only acts on <see cref="WslKeepAlivePolicy.IsSetupManagedLocalRecord"/> gateways.</item>
///   <item>Repairs only a <em>transport</em> failure. Auth/pairing/rate-limit/scope/TLS/tunnel and
///     token-drift are excluded via <see cref="GatewayErrorClassifier"/> — a distro restart cannot
///     fix credentials, and an intentional user disconnect (Idle) is never repaired.</item>
///   <item>Honors a startup grace period so a slow WSL cold start is not interrupted, and an
///     unhealthy threshold + cooldown so it cannot restart-storm.</item>
///   <item>Kill switch: <c>isEnabled</c> (settings) can disable automatic repair entirely.</item>
///   <item>Delegates to the coordinator (single-flight, budgeted, probe-before-restart); owns no
///     reconnect loop of its own.</item>
/// </list>
/// </remarks>
public sealed class ManagedLocalGatewayAutoRepairMonitor : IAsyncDisposable
{
    private readonly Func<GatewayConnectionSnapshot> _getSnapshot;
    private readonly GatewayRegistry _registry;
    private readonly Func<CancellationToken, Task<ManagedLocalGatewayRepairResult>> _repairAsync;
    private readonly Action<string> _resetRepairBudget;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, bool> _isAutomaticRepairAllowed;
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly IOpenClawLogger _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _unhealthyThreshold;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _startupGrace;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;
    private DateTime? _startedAt;

    private DateTime? _operatorUnhealthySince;
    private string? _unhealthyGatewayId;
    // Last repair attempt time keyed by gateway id, so switching away from and back to a gateway does
    // not drop its cooldown (a single-slot timestamp would let alternating gateways restart-storm).
    private readonly Dictionary<string, DateTime> _lastRepairByGateway = new(StringComparer.Ordinal);

    public ManagedLocalGatewayAutoRepairMonitor(
        Func<GatewayConnectionSnapshot> getSnapshot,
        GatewayRegistry registry,
        Func<CancellationToken, Task<ManagedLocalGatewayRepairResult>> repairAsync,
        Action<string> resetRepairBudget,
        Func<bool> isEnabled,
        ConnectionDiagnostics diagnostics,
        IOpenClawLogger logger,
        IClock? clock = null,
        TimeSpan? unhealthyThreshold = null,
        TimeSpan? pollInterval = null,
        TimeSpan? cooldown = null,
        TimeSpan? startupGrace = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, bool>? isAutomaticRepairAllowed = null)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repairAsync = repairAsync ?? throw new ArgumentNullException(nameof(repairAsync));
        _resetRepairBudget = resetRepairBudget ?? (static _ => { });
        _isEnabled = isEnabled ?? (static () => true);
        _isAutomaticRepairAllowed = isAutomaticRepairAllowed ?? (static _ => true);
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? SystemClock.Instance;
        _unhealthyThreshold = unhealthyThreshold ?? TimeSpan.FromSeconds(15);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _cooldown = cooldown ?? TimeSpan.FromSeconds(90);
        _startupGrace = startupGrace ?? TimeSpan.FromSeconds(45);
        _delay = delay ?? Task.Delay;
        _startedAt = _clock.UtcNow;
    }

    /// <summary>Starts the background monitor loop. Idempotent.</summary>
    public void Start()
    {
        if (_disposed || _loop != null)
            return;

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[AutoRepair] Monitor evaluation failed: {ex.Message}");
            }

            try
            {
                await _delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One evaluation tick. Internal for unit tests.</summary>
    internal async Task EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var record = _registry.GetActive();

        // Only auto-repair app-owned setup-managed local WSL gateways.
        if (record is null || !WslKeepAlivePolicy.IsSetupManagedLocalRecord(record))
        {
            ClearUnhealthyTracking();
            return;
        }

        if (!_isAutomaticRepairAllowed(record.Id))
        {
            ClearUnhealthyTracking();
            return;
        }

        // Kill switch.
        if (!_isEnabled())
        {
            ClearUnhealthyTracking();
            return;
        }

        var snapshot = _getSnapshot();
        var now = _clock.UtcNow;

        // The snapshot can briefly describe a DIFFERENT gateway than the active record during a switch:
        // SwitchGatewayAsync sets the active id BEFORE the connection state catches up, so a tick here
        // could read gateway B as active while the snapshot still reports gateway A's Connected state.
        // Attributing A's health to B would wrongly reset B's restart budget (letting alternating
        // gateways bypass the bound); attributing A's outage to B could repair the wrong gateway. Skip
        // until the snapshot's gateway matches the active record.
        if (!string.IsNullOrEmpty(snapshot.GatewayId) &&
            !string.Equals(snapshot.GatewayId, record.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (!IsRepairCandidate(snapshot))
        {
            ClearUnhealthyTracking();
            // Genuine health (any route) frees the coordinator's restart budget FOR THIS GATEWAY so a
            // later outage starts fresh instead of inheriting a count from an earlier repair.
            if (snapshot.OperatorState == RoleConnectionState.Connected)
                _resetRepairBudget(record.Id);
            return;
        }

        // Startup grace: never restart during the initial window, so a slow WSL cold start is not
        // interrupted (a fresh Connecting/Error with no classified failure is normal at startup).
        if (_startedAt is { } startedAt && now - startedAt < _startupGrace)
            return;

        // Per-gateway unhealthy timer: a freshly-activated gateway accrues its own threshold.
        if (!string.Equals(_unhealthyGatewayId, record.Id, StringComparison.Ordinal))
        {
            _operatorUnhealthySince = now;
            _unhealthyGatewayId = record.Id;
        }
        else
        {
            _operatorUnhealthySince ??= now;
        }

        if (now - _operatorUnhealthySince.Value < _unhealthyThreshold)
            return;

        if (_lastRepairByGateway.TryGetValue(record.Id, out var lastAttempt) && now - lastAttempt < _cooldown)
            return;

        // Prune cooldown entries that have fully elapsed (they no longer gate anything) so the map
        // stays bounded even across many create/delete gateway churns in a long-running session.
        PruneExpiredCooldowns(now);
        _lastRepairByGateway[record.Id] = now;

        var unhealthyMs = (now - _operatorUnhealthySince.Value).TotalMilliseconds;
        _diagnostics.Record("setup", "Local gateway unreachable — attempting automatic repair", $"unhealthyForMs={unhealthyMs:0}");

        var result = await _repairAsync(cancellationToken).ConfigureAwait(false);
        _diagnostics.Record("setup", $"Automatic local gateway repair: {result.Outcome}", result.Detail);

        if (result.Succeeded)
            ClearUnhealthyTracking();
    }

    private void ClearUnhealthyTracking()
    {
        _operatorUnhealthySince = null;
        _unhealthyGatewayId = null;
    }

    private void PruneExpiredCooldowns(DateTime now)
    {
        if (_lastRepairByGateway.Count == 0)
            return;

        List<string>? expired = null;
        foreach (var kvp in _lastRepairByGateway)
        {
            if (now - kvp.Value >= _cooldown)
                (expired ??= []).Add(kvp.Key);
        }

        if (expired is null)
            return;

        foreach (var id in expired)
            _lastRepairByGateway.Remove(id);
    }

    // Repair applies only to a genuine transport outage: the operator is Connecting/Error AND the
    // classified failure (if any) is not an auth/pairing/credential/rate-limit/TLS/tunnel/scope or
    // token-drift problem, none of which a distro restart can fix. A null error (Connecting with no
    // classified reason) is treated as transport-ish; the coordinator's probe is the real gate for
    // whether a restart actually happens. Public so the coordinator can re-check this same condition
    // immediately before a restart (the failure may have become terminal-auth during the verify wait).
    public static bool IsTransportUnreachable(GatewayConnectionSnapshot snapshot)
    {
        if (snapshot.OperatorState is not (RoleConnectionState.Connecting or RoleConnectionState.Error))
            return false;

        if (snapshot.OperatorPairingRequired || snapshot.OperatorCredentialBootstrapRequired)
            return false;

        var kind = snapshot.OperatorErrorKind ??
            GatewayErrorClassifier.Classify(snapshot.OperatorError);

        // A cold start can sit in Connecting before any classified failure exists. That remains
        // repairable after grace/threshold + the coordinator's real reachability probe.
        if (snapshot.OperatorState == RoleConnectionState.Connecting &&
            string.IsNullOrWhiteSpace(snapshot.OperatorError) &&
            snapshot.OperatorErrorKind is null)
        {
            return true;
        }

        return kind switch
        {
            GatewayErrorKind.Network or GatewayErrorKind.Server => true,
            _ => false, // Unknown, Auth, DeviceTokenMismatch, TokenDrift, ScopeMismatch, Pairing*, RateLimited, Tls, Tunnel
        };
    }

    public static bool IsRepairCandidate(GatewayConnectionSnapshot snapshot) =>
        IsTransportUnreachable(snapshot) ||
        (snapshot.OperatorState == RoleConnectionState.Error &&
         snapshot.OperatorErrorKind == GatewayErrorKind.LocalPortConflict);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _cts.Cancel(); }
        // slopwatch-ignore: SW003 Shutdown cancellation is expected; callers already preserve safe state.
        catch (ObjectDisposedException) { }

        // Await the loop so an in-flight evaluation cannot drive a reconnect into a disposing
        // manager, bounding the wait so shutdown cannot hang on a stuck repair.
        if (_loop is { } loop)
        {
            try { await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn($"[AutoRepair] Dispose await failed: {ex.Message}"); }
        }

        _cts.Dispose();
    }
}
