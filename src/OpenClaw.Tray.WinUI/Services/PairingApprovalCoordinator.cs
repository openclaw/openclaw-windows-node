using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Outcome of a confirmed approve/reject decision, surfaced for confirmation toasts/activity.</summary>
public sealed class PairingDecisionResult
{
    public required PendingApproval Approval { get; init; }

    /// <summary>True when the user approved, false when rejected.</summary>
    public required bool Approved { get; init; }

    /// <summary>True when the gateway confirmed the decision (the request left the pending list).</summary>
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
///   <item>Executes approve/reject via the operator client and, once the gateway confirms the
///         decision (the request leaves the pending list), reports <see cref="DecisionCompleted"/>.</item>
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
    private readonly Func<IReadOnlyCollection<string>?> _getOwnNodeIds;
    private readonly Func<bool> _isPromptEnabled;
    private readonly IOpenClawLogger _logger;

    public PairingApprovalCoordinator(
        Func<IOperatorGatewayClient?> getClient,
        Func<IReadOnlyCollection<string>?> getOwnNodeIds,
        Func<bool> isPromptEnabled,
        IOpenClawLogger logger)
    {
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getOwnNodeIds = getOwnNodeIds ?? throw new ArgumentNullException(nameof(getOwnNodeIds));
        _isPromptEnabled = isPromptEnabled ?? throw new ArgumentNullException(nameof(isPromptEnabled));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised for each newly-arrived, actionable request the user should be prompted to decide.</summary>
    public event EventHandler<PendingApproval>? ApprovalRequested;

    /// <summary>Raised whenever the actionable approval set changes (add, resolve, or decision).</summary>
    public event EventHandler? ApprovalsChanged;

    /// <summary>
    /// Raised when a submitted approve/reject is CONFIRMED by the gateway (the request left the
    /// pending list). This is the authoritative success signal — it does not fire on send-ack alone.
    /// </summary>
    public event EventHandler<PairingDecisionResult>? DecisionCompleted;

    /// <summary>The current actionable approvals, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Current => _queue.Current;

    /// <summary>
    /// Feed a fresh device/node pair-list snapshot. Diffs against prior state and raises the
    /// appropriate presentation events. A null list for a kind means "no fresh snapshot for that
    /// kind" — its prior entries are carried forward rather than treated as resolved.
    /// </summary>
    public void OnPairListsUpdated(DevicePairingListInfo? devices, PairingListInfo? nodes)
    {
        // If we're not connected, an empty/missing list reflects lost visibility — NOT that
        // requests resolved. Clearing silently here prevents a still-pending submitted decision
        // from being mis-reported as a confirmed success on disconnect, and drops stale approvals.
        var client = _getClient();
        if (client is not { IsConnectedToGateway: true })
        {
            Reset();
            return;
        }

        PairingApprovalDelta delta;
        try
        {
            // A null list for a kind = no fresh snapshot (carried forward), not "now empty".
            delta = _queue.Reconcile(devices, nodes, _getOwnNodeIds(), Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[PairApproval] Reconcile failed: {ex.Message}");
            return;
        }

        if (!delta.HasChanges)
            return;

        // A submitted decision the gateway has now confirmed (request left the pending list) is the
        // authoritative success signal — report it so the app shows the "approved/rejected" toast.
        foreach (var resolved in delta.ConfirmedDecisions)
        {
            try
            {
                DecisionCompleted?.Invoke(this, new PairingDecisionResult
                {
                    Approval = resolved.Approval,
                    Approved = resolved.Approved,
                    Success = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Warn($"[PairApproval] DecisionCompleted handler threw: {ex.Message}");
            }
        }

        ApprovalsChanged?.Invoke(this, EventArgs.Empty);

        if (delta.Added.Count == 0)
            return;

        // Only prompt when the user hasn't opted out and we actually have the scope to act.
        if (!_isPromptEnabled() || !CanApproveWith(client))
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

    /// <summary>Clear all tracked state (e.g. on disconnect or gateway switch).</summary>
    public void Reset()
    {
        _queue.Reset();
        _inFlight.Clear();
        _pollInFlight = false;
        ApprovalsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> DecideAsync(string key, bool approve)
    {
        var approval = _queue.Find(key);
        if (approval == null)
        {
            _logger.Info($"[PairApproval] Decision for unknown or already-submitted key '{key}' ignored");
            return false;
        }

        // Legacy gateways may omit a request id; DecisionId then falls back to the device/node id.
        // The gateway may ignore that, but the confirmed-resolution model handles it gracefully: the
        // request simply isn't confirmed, expires, and re-surfaces — no permanent hide, no false toast.
        if (string.IsNullOrEmpty(approval.RequestId))
            _logger.Info($"[PairApproval] No request id; submitting decision with device/node id fallback for '{key}'");

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
                // Send-ack only means the frame left the socket, NOT that the gateway accepted it.
                // But first re-validate the exact connection: a disconnect/reconnect may have raced
                // the await (and already Reset() the queue). Recording a submission against a new
                // client would strand a likely-dropped frame and risk a later false confirmation, so
                // treat it as not completed instead — the request re-surfaces on reconnect for a
                // clean retry.
                var liveClient = _getClient();
                if (!ReferenceEquals(liveClient, client) || !CanApproveWith(liveClient))
                {
                    _logger.Info("[PairApproval] Connection changed during decision; not recording optimistic submission");
                    return false;
                }

                // Record the decision as optimistic-pending so the UI advances, but defer the
                // success confirmation until the gateway drops the request from the pending list
                // (surfaced as a ConfirmedDecision on a later reconcile). If it never does, the
                // submission expires and the request re-surfaces for retry.
                _queue.MarkSubmitted(approval, approve, Environment.TickCount64);

                // Nudge the gateway to re-send the list so the resolved entry drops (and confirms) promptly.
                try
                {
                    if (approval.Kind == PairingApprovalKind.Device)
                        await liveClient.RequestDevicePairListAsync();
                    else
                        await liveClient.RequestNodePairListAsync();
                }
                catch (Exception ex)
                {
                    _logger.Info($"[PairApproval] Post-decision list refresh failed: {ex.Message}");
                }

                ApprovalsChanged?.Invoke(this, EventArgs.Empty);
            }

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
