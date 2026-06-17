using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Outcome of a single approve/reject decision, surfaced for confirmation toasts/activity.</summary>
public sealed class PairingDecisionResult
{
    public required PendingApproval Approval { get; init; }

    /// <summary>True when the user approved, false when rejected.</summary>
    public required bool Approved { get; init; }

    /// <summary>True when the gateway RPC succeeded.</summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Orchestrates inbound pairing approvals (devices and nodes requesting to join the gateway).
/// Bridges the pure <see cref="PairingApprovalQueue"/> to the live operator client and the tray
/// presentation layer:
/// <list type="bullet">
///   <item>Consumes pair-list snapshots from <see cref="GatewayService"/>.</item>
///   <item>Raises <see cref="ApprovalRequested"/> for genuinely new requests so the app can present
///         the approval window + an awareness toast (gated by scope and the user setting).</item>
///   <item>Raises <see cref="ApprovalsChanged"/> whenever the actionable set changes so an open
///         window refreshes.</item>
///   <item>Executes approve/reject via the operator client and reports <see cref="DecisionCompleted"/>.</item>
/// </list>
/// All members are expected to be invoked on the UI dispatcher thread (the gateway service marshals
/// list updates there), so no internal locking is required.
/// </summary>
public sealed class PairingApprovalCoordinator
{
    private readonly PairingApprovalQueue _queue = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private bool _pollInFlight;
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly Func<string?> _getOwnNodeDeviceId;
    private readonly Func<bool> _isPromptEnabled;
    private readonly IOpenClawLogger _logger;

    public PairingApprovalCoordinator(
        Func<IOperatorGatewayClient?> getClient,
        Func<string?> getOwnNodeDeviceId,
        Func<bool> isPromptEnabled,
        IOpenClawLogger logger)
    {
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getOwnNodeDeviceId = getOwnNodeDeviceId ?? throw new ArgumentNullException(nameof(getOwnNodeDeviceId));
        _isPromptEnabled = isPromptEnabled ?? throw new ArgumentNullException(nameof(isPromptEnabled));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised for each newly-arrived, actionable request the user should be prompted to decide.</summary>
    public event EventHandler<PendingApproval>? ApprovalRequested;

    /// <summary>Raised whenever the actionable approval set changes (add, resolve, or decision).</summary>
    public event EventHandler? ApprovalsChanged;

    /// <summary>Raised after an approve/reject RPC completes (success or failure).</summary>
    public event EventHandler<PairingDecisionResult>? DecisionCompleted;

    /// <summary>The current actionable approvals, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Current => _queue.Current;

    /// <summary>True when the connected operator can approve pairings (has admin/pairing scope).</summary>
    public bool CanApprove => CanApproveWith(_getClient());

    /// <summary>
    /// Feed a fresh device/node pair-list snapshot. Diffs against prior state and raises the
    /// appropriate presentation events. Safe to call with nulls (treated as empty lists).
    /// </summary>
    public void OnPairListsUpdated(DevicePairingListInfo? devices, PairingListInfo? nodes)
    {
        PairingApprovalDelta delta;
        try
        {
            delta = _queue.Reconcile(devices, nodes, _getOwnNodeDeviceId());
        }
        catch (Exception ex)
        {
            _logger.Warn($"[PairApproval] Reconcile failed: {ex.Message}");
            return;
        }

        if (!delta.HasChanges)
            return;

        ApprovalsChanged?.Invoke(this, EventArgs.Empty);

        if (delta.Added.Count == 0)
            return;

        // Only prompt when the user hasn't opted out and we actually have the scope to act.
        if (!_isPromptEnabled() || !CanApprove)
            return;

        foreach (var added in delta.Added)
        {
            try
            {
                ApprovalRequested?.Invoke(this, added);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[PairApproval] ApprovalRequested handler threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Safety-net refresh: asks the gateway to re-send the pending pair lists. The gateway
    /// broadcasts pair requests with <c>dropIfSlow=true</c>, so a congested socket can silently
    /// drop a "device wants to connect" event; a periodic refresh ensures the request is still
    /// picked up (and the in-page banner stays current). No-op unless the operator is connected
    /// with approval scope. <see cref="OpenClawGatewayClient"/> already short-circuits these
    /// requests on gateways that don't support pair lists, so this is cheap on older gateways.
    /// </summary>
    public async Task RefreshFromGatewayAsync()
    {
        // Non-reentrant: if a wedged-but-"connected" transport makes a refresh hang, don't let the
        // 20s timer stack up more tracked requests on top of it.
        if (_pollInFlight)
            return;

        var client = _getClient();
        if (!CanApproveWith(client))
            return;

        _pollInFlight = true;
        try
        {
            try { await client!.RequestDevicePairListAsync(); }
            catch (Exception ex) { _logger.Info($"[PairApproval] Device pair-list poll failed: {ex.Message}"); }

            try { await client!.RequestNodePairListAsync(); }
            catch (Exception ex) { _logger.Info($"[PairApproval] Node pair-list poll failed: {ex.Message}"); }
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// <summary>Approve the request identified by <paramref name="key"/> (<see cref="PendingApproval.Key"/>).</summary>
    public Task<bool> ApproveAsync(string key) => DecideAsync(key, approve: true);

    /// <summary>Reject the request identified by <paramref name="key"/>.</summary>
    public Task<bool> RejectAsync(string key) => DecideAsync(key, approve: false);

    /// <summary>Look up a tracked approval by key (for the window to render details).</summary>
    public PendingApproval? Find(string key) => _queue.Find(key);

    /// <summary>Clear all tracked state (e.g. on disconnect or gateway switch).</summary>
    public void Reset()
    {
        _queue.Reset();
        ApprovalsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> DecideAsync(string key, bool approve)
    {
        var approval = _queue.Find(key);
        if (approval == null)
        {
            _logger.Info($"[PairApproval] Decision for unknown/decided key '{key}' ignored");
            return false;
        }

        // Guard against a double decision for the same request — e.g. a stray
        // second click that slips through while the approve/reject RPC is still
        // in flight. Single-threaded (UI dispatcher), so a HashSet is sufficient.
        if (!_inFlight.Add(key))
        {
            _logger.Info($"[PairApproval] Decision already in flight for '{key}'");
            return false;
        }

        try
        {
            var client = _getClient();
            // Re-check connection AND approval scope at decision time — the dialog may have been
            // open while scope was revoked or the operator reconnected with fewer scopes. The
            // approve/reject frame is "send-acknowledged" (not gateway-acked), so guarding here
            // avoids attempting an out-of-scope decision the gateway would silently reject.
            if (client is not { IsConnectedToGateway: true }
                || !OperatorScopeHelper.CanApproveDevices(client.GrantedOperatorScopes))
            {
                _logger.Warn("[PairApproval] Operator can no longer approve pairings (disconnected or scope lost); decision aborted");
                return false;
            }

            bool ok = false;
            try
            {
                ok = (approval.Kind, approve) switch
                {
                    (PairingApprovalKind.Device, true) => await client.DevicePairApproveAsync(approval.DecisionId),
                    (PairingApprovalKind.Device, false) => await client.DevicePairRejectAsync(approval.DecisionId),
                    (PairingApprovalKind.Node, true) => await client.NodePairApproveAsync(approval.DecisionId),
                    (PairingApprovalKind.Node, false) => await client.NodePairRejectAsync(approval.DecisionId),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                _logger.Warn($"[PairApproval] {(approve ? "Approve" : "Reject")} RPC failed: {ex.Message}");
                ok = false;
            }

            if (ok)
            {
                _queue.MarkDecided(key);
                // Nudge the gateway to re-send the list so the resolved entry drops promptly.
                try
                {
                    if (approval.Kind == PairingApprovalKind.Device)
                        await client.RequestDevicePairListAsync();
                    else
                        await client.RequestNodePairListAsync();
                }
                catch (Exception ex)
                {
                    _logger.Info($"[PairApproval] Post-decision list refresh failed: {ex.Message}");
                }

                ApprovalsChanged?.Invoke(this, EventArgs.Empty);
            }

            DecisionCompleted?.Invoke(this, new PairingDecisionResult
            {
                Approval = approval,
                Approved = approve,
                Success = ok,
            });

            return ok;
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    private static bool CanApproveWith(IOperatorGatewayClient? client) =>
        client is { IsConnectedToGateway: true }
        && OperatorScopeHelper.CanApproveDevices(client.GrantedOperatorScopes);
}
