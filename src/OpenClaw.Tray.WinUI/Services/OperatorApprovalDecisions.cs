// The one owner of operator decisions on Gateway approvals. Inbox rows, actionable
// approval toasts and the chat approval banner all send through it, and it sends through
// the one kind-aware RPC (OpenClawGatewayClient.ResolveApprovalAsync: exec.approval.resolve,
// plugin.approval.resolve or approval.resolve with kind system-agent). It never uses the
// "/approve" chat slash command: that is queued behind the agent turn the approval blocks,
// so it would only run after the approval had expired.
//
// Inbox and toast decisions are validated immediately before sending against the live
// pending approval held by InboxGatewaySource: same id, still unexpired, observed on the
// current authenticated connection epoch, decision offered by GatewayApprovalDecisions and
// the connection holds operator.approvals. A stale reply (decided, expired or unknown
// elsewhere) removes the row. A post-admission failure quarantines the original request;
// a pending snapshot does not prove the earlier resolve has finished, so it never authorizes a retry.
//
// Toast buttons carry only the approval id, the decision and a random one-shot token issued
// here. The token binds the toast to the client and connection epoch that showed it, so a
// stale, replayed or forged activation, or one from an earlier connection, decides nothing.
//
// Local node exec approvals (ExecApprovalV2DialogPromptHandler / ExecApprovalDialog) are a
// separate, in-process flow guarding commands this PC runs; they do not pass through here.
//
// Usage:
//   var decisions = new OperatorApprovalDecisions(inboxGatewaySource);
//   var outcome = await decisions.DecideAsync(approvalId, GatewayApprovalDecisions.Deny, row.ApprovalObservation);   // UI thread
//   var token = decisions.IssueToastToken(approval, ApprovalToastPlan.Decisions(approval, local)); // UI thread
//   await decisions.DecideFromToastAsync(approvalId, decision, token);
//   await decisions.ResolveChatExecApprovalAsync(client, requestId, alternateId, decision); // throws to keep the banner

using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Security.Cryptography;

namespace OpenClawTray.Services;

internal enum ApprovalDecisionOutcome
{
    /// <summary>The Gateway accepted the decision; the row is removed.</summary>
    Sent,
    /// <summary>The approval was already decided, expired or unknown at the Gateway; the row is removed.</summary>
    AlreadyResolved,
    /// <summary>No live pending approval matched (unknown, expired, stale toast or other connection); nothing was sent.</summary>
    NotPending,
    /// <summary>The decision is not offered for this approval or the connection lacks approval access; nothing was sent.</summary>
    NotAllowed,
    /// <summary>A decision for this approval is already being sent.</summary>
    InFlight,
    /// <summary>Refused before transport admission; the row may be tried again explicitly.</summary>
    Failed,
    /// <summary>A decision may have been transmitted. Further decisions are quarantined.</summary>
    Uncertain,
}

/// <summary>A chat decision refusal or unconfirmed result; <see cref="Outcome"/> distinguishes them.</summary>
internal sealed class ApprovalDecisionRefusedException(ApprovalDecisionOutcome outcome)
    : InvalidOperationException($"Approval decision refused: {outcome}.")
{
    public ApprovalDecisionOutcome Outcome { get; } = outcome;
}

internal sealed class OperatorApprovalDecisions
{
    private const int ToastTokenBytes = 16;
    private const int MaxToastGrants = 64;
    private const int MaxUncertainApprovals = 4096;

    private readonly InboxGatewaySource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToastGrant> _toastGrants = new(StringComparer.Ordinal);
    // Never evict a possibly transmitted decision into retry eligibility, including after reconnect.
    private readonly Dictionary<string, DateTimeOffset> _uncertain = new(StringComparer.Ordinal);
    private bool _uncertaintyCapacityReached;
    private sealed record UncertainDecision(IOperatorGatewayClient Client,
        InboxGatewaySource.PendingApproval Observation, long Epoch);

    private sealed record ToastGrant(
        string ApprovalId, IOperatorGatewayClient Client, long ConnectionEpoch, DateTimeOffset Deadline,
        InboxGatewaySource.PendingApproval Observation, IReadOnlyList<string> Decisions)
    {
        // Accessed under _gate. Consumed grants remain revocable while a send is queued.
        public bool Consumed { get; set; }
        public bool Revoked { get; set; }
    }

    public OperatorApprovalDecisions(InboxGatewaySource source, Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _source.ObserveDecisionUncertainty(UncertainDeadline);
    }

    /// <summary>True while an earlier decision for this identity has no proven outcome.</summary>
    public bool IsUncertain(string approvalId) => UncertainDeadline(approvalId) is not null;

    private DateTimeOffset? UncertainDeadline(string approvalId)
    {
        lock (_gate)
            return _uncertain.TryGetValue(approvalId, out var deadline) ? deadline
                : _uncertaintyCapacityReached ? DateTimeOffset.MaxValue : null;
    }

    /// <summary>Validates <paramref name="decision"/> against the live pending approval and sends it.</summary>
    public Task<ApprovalDecisionOutcome> DecideAsync(string approvalId, string decision, object? observation) =>
        DecideCoreAsync(approvalId, decision, observation, null);

    private Task<ApprovalDecisionOutcome> DecideCoreAsync(string approvalId, string decision, object? observation, ToastGrant? toastGrant)
    {
        if (!GatewayApprovalDecisions.IsKnown(decision))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        var state = _source.Approvals;
        if (state.Client is not { } client ||
            !state.Pending.TryGetValue(approvalId ?? "", out var pending) ||
            !ReferenceEquals(pending, observation) ||
            !IsLive(client, pending, out var epoch))
            return Task.FromResult(ApprovalDecisionOutcome.NotPending);
        if (IsUncertain(pending.Approval.Id))
            return Task.FromResult(ApprovalDecisionOutcome.Uncertain);
        // Inbox rows (and toasts, which offer a subset) never send Allow for a command the row could not show in full.
        if (!state.CanDecide || !InboxGatewayItems.InboxDecisions(pending.Approval).Contains(decision))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        return SendAsync(client, pending, decision, epoch, toastGrant);
    }

    // Before its local deadline (fixed at arrival), and observed on the authenticated connection epoch that is still current.
    private bool IsLive(IOperatorGatewayClient client, InboxGatewaySource.PendingApproval pending, out long epoch)
    {
        epoch = pending.ConnectionEpoch ?? 0;
        return pending.Deadline > _clock() &&
            pending.ConnectionEpoch is { } observed &&
            client.SessionMutationConnectionEpoch == observed;
    }

    /// <summary>
    /// Issues the one-shot token an actionable toast carries for <paramref name="approval"/>, bound
    /// to the decisions the toast shows (<paramref name="offeredDecisions"/>), or null when the
    /// approval is no longer live on the current connection.
    /// </summary>
    public string? IssueToastToken(GatewayPendingApproval approval, IReadOnlyList<string> offeredDecisions)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(offeredDecisions);
        var state = _source.Approvals;
        if (state.Client is not { } client ||
            !state.Pending.TryGetValue(approval.Id, out var pending) ||
            !ReferenceEquals(approval, pending.Approval) || IsUncertain(approval.Id) ||
            !IsLive(client, pending, out var epoch))
            return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(ToastTokenBytes));
        lock (_gate)
        {
            PruneToastGrantsLocked();
            _toastGrants[token] = new(approval.Id, client, epoch, pending.Deadline, pending,
                offeredDecisions.Where(decision => decision != GatewayApprovalDecisions.AllowAlways).ToArray());
        }
        return token;
    }

    /// <summary>
    /// Applies a toast button. The token is consumed on first use; it must have been issued for
    /// <paramref name="approvalId"/> on the client and connection epoch that are still current,
    /// and the decision must be one the toast showed.
    /// </summary>
    public Task<ApprovalDecisionOutcome> DecideFromToastAsync(string? approvalId, string? decision, string? token)
    {
        ToastGrant? grant = null;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(token) && _toastGrants.TryGetValue(token, out var issued) &&
                !issued.Consumed && !issued.Revoked)
            {
                issued.Consumed = true;
                grant = issued;
            }
        }
        var state = _source.Approvals;
        if (grant is null ||
            !string.Equals(grant.ApprovalId, approvalId, StringComparison.Ordinal) ||
            !ReferenceEquals(grant.Client, state.Client) ||
            !state.Pending.TryGetValue(grant.ApprovalId, out var pending) ||
            pending.ConnectionEpoch != grant.ConnectionEpoch ||
            !ReferenceEquals(pending, grant.Observation))
        {
            Logger.Warn("[Approval] ignored a toast decision that no longer matches a pending approval");
            return Task.FromResult(ApprovalDecisionOutcome.NotPending);
        }
        // A toast can only send what its buttons offered (never Always allow).
        if (!grant.Decisions.Contains(decision ?? ""))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        return DecideCoreAsync(grant.ApprovalId, decision!, grant.Observation, grant);
    }

    /// <summary>Withdraws every toast token for <paramref name="approvalId"/> (decided, expired or gone).</summary>
    public void RevokeToastTokens(string approvalId)
    {
        lock (_gate)
        {
            foreach (var token in _toastGrants.Where(pair => pair.Value.ApprovalId == approvalId).Select(pair => pair.Key).ToList())
                RevokeGrantLocked(token);
        }
    }

    /// <summary>
    /// Sends the chat banner's decision for an exec approval. The banner's id, or the id correlated
    /// with it (agent-stream banners show a slug; the canonical id arrives through the same Gateway's
    /// top-level approval event), must name an approval this owner tracks. Then the same rules as
    /// Inbox rows apply: current client, explicit grant, exec kind, unexpired, observed on this
    /// connection epoch and offered decision. An untracked id is refused, because absence can mean
    /// expired or already resolved. Throws on every refusal or failure so the banner stays up, as
    /// the chat contract requires; the decision is sent with the canonical approval id.
    /// </summary>
    public async Task ResolveChatExecApprovalAsync(IOperatorGatewayClient client, string requestId, string? alternateId, string decision)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        if (!GatewayApprovalDecisions.IsKnown(decision))
            throw new ArgumentException("Unknown approval decision.", nameof(decision));
        var state = _source.Approvals;
        if (!ReferenceEquals(client, state.Client) || !state.CanDecide)
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotAllowed);
        if (!state.Pending.TryGetValue(requestId, out var pending) &&
            (string.IsNullOrEmpty(alternateId) || !state.Pending.TryGetValue(alternateId, out pending)))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotPending);
        if (pending.Approval.Kind != GatewayApprovalKind.Exec || !IsLive(client, pending, out var epoch))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotPending);
        if (!GatewayApprovalDecisions.EffectiveForChat(pending.Approval).Contains(decision))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotAllowed);

        var approvalId = pending.Approval.Id;
        if (IsUncertain(approvalId))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.Uncertain);
        if (!TryBegin(approvalId))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.InFlight);
        var admitted = false;
        try
        {
            await client.ResolveApprovalAsync(GatewayApprovalKind.Exec, approvalId, decision, epoch,
                () => { ValidateBeforeSend(client, pending, decision, chat: true); admitted = true; }).ConfigureAwait(false);
            Logger.Info($"[Approval] chat decision sent decision={decision}");
            Retire(client, approvalId, epoch, pending);
        }
        catch (GatewayApprovalStaleException)
        {
            Retire(client, approvalId, epoch, pending);
            throw;
        }
        catch (Exception) when (admitted)
        {
            await QuarantineAndReconcileAsync(client, pending, epoch).ConfigureAwait(false);
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.Uncertain);
        }
        finally
        {
            End(approvalId);
        }
    }

    private async Task<ApprovalDecisionOutcome> SendAsync(
        IOperatorGatewayClient client, InboxGatewaySource.PendingApproval pending, string decision, long epoch, ToastGrant? toastGrant)
    {
        var kind = pending.Approval.Kind;
        var approvalId = pending.Approval.Id;
        if (!TryBegin(approvalId))
            return ApprovalDecisionOutcome.InFlight;
        var admitted = false;
        try
        {
            await client.ResolveApprovalAsync(kind, approvalId, decision, epoch,
                () =>
                {
                    ValidateBeforeSend(client, pending, decision);
                    lock (_gate)
                    {
                        if (toastGrant is { Revoked: true })
                            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotPending);
                    }
                    admitted = true;
                }).ConfigureAwait(true);
            Logger.Info($"[Approval] decision sent kind={kind} decision={decision}");
            Retire(client, approvalId, epoch, pending);
            return ApprovalDecisionOutcome.Sent;
        }
        catch (GatewayApprovalStaleException)
        {
            Logger.Info($"[Approval] approval was already resolved kind={kind}");
            Retire(client, approvalId, epoch, pending);
            return ApprovalDecisionOutcome.AlreadyResolved;
        }
        catch (ApprovalDecisionRefusedException ex)
        {
            return ex.Outcome;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Approval] decision send failed kind={kind}: {ex.GetType().Name}");
            if (!admitted) return ApprovalDecisionOutcome.Failed;
            await QuarantineAndReconcileAsync(client, pending, epoch).ConfigureAwait(true);
            return ApprovalDecisionOutcome.Uncertain;
        }
        finally
        {
            End(approvalId);
        }
    }

    // This callback reads immutable source snapshots; it can run on the transport thread.
    private void ValidateBeforeSend(IOperatorGatewayClient client, InboxGatewaySource.PendingApproval observation, string decision, bool chat = false)
    {
        var state = _source.Approvals;
        if (IsUncertain(observation.Approval.Id))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.Uncertain);
        if (!ReferenceEquals(client, state.Client) ||
            !state.Pending.TryGetValue(observation.Approval.Id, out var current) ||
            !ReferenceEquals(current, observation) || !IsLive(client, observation, out _))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotPending);
        if (!state.CanDecide || !GatewayApprovalDecisions.HasDecisionGrant(client.GrantedOperatorScopes) ||
            !(chat ? GatewayApprovalDecisions.EffectiveForChat(observation.Approval) : GatewayApprovalDecisions.Effective(observation.Approval)).Contains(decision))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotAllowed);
    }

    private async Task QuarantineAndReconcileAsync(IOperatorGatewayClient client,
        InboxGatewaySource.PendingApproval observation, long epoch)
    {
        var id = observation.Approval.Id;
        var attempt = new UncertainDecision(client, observation, epoch);
        lock (_gate)
        {
            if (_uncertain.TryGetValue(id, out _)) return;
            if (_uncertain.Count >= MaxUncertainApprovals)
            {
                _uncertaintyCapacityReached = true;
            }
            else
                _uncertain.Add(id, observation.Deadline);
        }
        RevokeToastTokens(id);
        _source.RefreshDecisionState();
        var method = GatewayPendingApprovalParser.ListMethod(observation.Approval.Kind);
        if (!MatchesOriginalAttempt(attempt) ||
            (client.AdvertisedServerMethods.Count > 0 && !client.AdvertisedServerMethods.Contains(method, StringComparer.Ordinal)))
            return;
        try
        {
            var reply = await client.SendWizardRequestOnConnectionAsync(method, new { }, epoch,
                InboxGatewaySource.RequestTimeoutMs()).ConfigureAwait(false);
            if (!GatewayPendingApprovalParser.TryParseCompleteList(observation.Approval.Kind, reply, out var pending) ||
                !MatchesOriginalAttempt(attempt))
                return;
            // Absence settles only that this exact observation is no longer pending, not which
            // decision ran. Presence cannot settle a lost resolve: RPC handlers may overlap and
            // the list carries no resolve-attempt receipt or completion barrier. Keep quarantined.
            if (!pending.Any(approval => approval.Id == id))
                Retire(client, id, epoch, observation);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Approval] reconciliation failed kind={observation.Approval.Kind}: {ex.GetType().Name}");
        }
    }

    private bool MatchesOriginalAttempt(UncertainDecision attempt)
    {
        var state = _source.Approvals;
        return ReferenceEquals(state.Client, attempt.Client) &&
            attempt.Client.SessionMutationConnectionEpoch == attempt.Epoch &&
            GatewayApprovalDecisions.HasDecisionGrant(attempt.Client.GrantedOperatorScopes) &&
            state.Pending.TryGetValue(attempt.Observation.Approval.Id, out var current) &&
            ReferenceEquals(current, attempt.Observation) && current.Deadline > _clock();
    }

    private void Retire(IOperatorGatewayClient client, string approvalId, long epoch, InboxGatewaySource.PendingApproval observation)
    {
        // Only this connection's grants: the same id may already be live again on a later epoch.
        lock (_gate)
        {
            foreach (var token in _toastGrants
                .Where(pair => pair.Value.ApprovalId == approvalId &&
                    ReferenceEquals(pair.Value.Client, client) &&
                    pair.Value.ConnectionEpoch == epoch && ReferenceEquals(pair.Value.Observation, observation))
                .Select(pair => pair.Key).ToList())
                RevokeGrantLocked(token);
        }
        _source.MarkApprovalResolved(client, approvalId, epoch, observation);
    }

    private bool TryBegin(string approvalId)
    {
        lock (_gate)
            return !_uncertaintyCapacityReached && !_uncertain.ContainsKey(approvalId) && _inFlight.Add(approvalId);
    }

    private void End(string approvalId)
    {
        lock (_gate)
            _inFlight.Remove(approvalId);
    }

    private void RevokeGrantLocked(string token)
    {
        if (_toastGrants.Remove(token, out var grant))
            grant.Revoked = true;
    }

    private void PruneToastGrantsLocked()
    {
        var now = _clock();
        foreach (var token in _toastGrants.Where(pair => pair.Value.Deadline <= now).Select(pair => pair.Key).ToList())
            RevokeGrantLocked(token);
        // Bounded: the oldest grants go first when many approvals arrive at once.
        while (_toastGrants.Count >= MaxToastGrants)
            RevokeGrantLocked(_toastGrants.MinBy(pair => pair.Value.Deadline).Key);
    }
}
