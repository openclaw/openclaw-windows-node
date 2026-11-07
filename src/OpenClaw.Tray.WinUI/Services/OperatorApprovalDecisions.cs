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
// elsewhere) removes the row like a success; any other failure keeps it for retry.
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
//   var outcome = await decisions.DecideAsync(approvalId, GatewayApprovalDecisions.Deny);   // UI thread
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
    /// <summary>Sending failed; the row stays so the user can retry.</summary>
    Failed,
}

/// <summary>A chat banner decision the owner refused before sending; <see cref="Outcome"/> says why.</summary>
internal sealed class ApprovalDecisionRefusedException(ApprovalDecisionOutcome outcome)
    : InvalidOperationException($"Approval decision refused: {outcome}.")
{
    public ApprovalDecisionOutcome Outcome { get; } = outcome;
}

internal sealed class OperatorApprovalDecisions
{
    private const int ToastTokenBytes = 16;
    private const int MaxToastGrants = 64;

    private readonly InboxGatewaySource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToastGrant> _toastGrants = new(StringComparer.Ordinal);

    private sealed record ToastGrant(
        string ApprovalId, IOperatorGatewayClient Client, long ConnectionEpoch, DateTimeOffset Deadline, IReadOnlyList<string> Decisions);

    public OperatorApprovalDecisions(InboxGatewaySource source, Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Validates <paramref name="decision"/> against the live pending approval and sends it.</summary>
    public Task<ApprovalDecisionOutcome> DecideAsync(string approvalId, string decision)
    {
        if (!GatewayApprovalDecisions.IsKnown(decision))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        var state = _source.Approvals;
        if (state.Client is not { } client ||
            !state.Pending.TryGetValue(approvalId ?? "", out var pending) ||
            !IsLive(client, pending, out var epoch))
            return Task.FromResult(ApprovalDecisionOutcome.NotPending);
        // Inbox rows (and toasts, which offer a subset) never send Allow for a command the row could not show in full.
        if (!state.CanDecide || !InboxGatewayItems.InboxDecisions(pending.Approval).Contains(decision))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        return SendAsync(client, pending.Approval.Kind, pending.Approval.Id, decision, epoch);
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
            !IsLive(client, pending, out var epoch))
            return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(ToastTokenBytes));
        lock (_gate)
        {
            PruneToastGrantsLocked();
            _toastGrants[token] = new(approval.Id, client, epoch, pending.Deadline,
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
            if (!string.IsNullOrEmpty(token) && _toastGrants.Remove(token, out var issued))
                grant = issued;
        }
        var state = _source.Approvals;
        if (grant is null ||
            !string.Equals(grant.ApprovalId, approvalId, StringComparison.Ordinal) ||
            !ReferenceEquals(grant.Client, state.Client) ||
            !state.Pending.TryGetValue(grant.ApprovalId, out var pending) ||
            pending.ConnectionEpoch != grant.ConnectionEpoch)
        {
            Logger.Warn("[Approval] ignored a toast decision that no longer matches a pending approval");
            return Task.FromResult(ApprovalDecisionOutcome.NotPending);
        }
        // A toast can only send what its buttons offered (never Always allow).
        if (!grant.Decisions.Contains(decision ?? ""))
            return Task.FromResult(ApprovalDecisionOutcome.NotAllowed);
        return DecideAsync(grant.ApprovalId, decision!);
    }

    /// <summary>Withdraws every toast token for <paramref name="approvalId"/> (decided, expired or gone).</summary>
    public void RevokeToastTokens(string approvalId)
    {
        lock (_gate)
        {
            foreach (var token in _toastGrants.Where(pair => pair.Value.ApprovalId == approvalId).Select(pair => pair.Key).ToList())
                _toastGrants.Remove(token);
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
        if (!GatewayApprovalDecisions.Effective(pending.Approval).Contains(decision))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.NotAllowed);

        var approvalId = pending.Approval.Id;
        if (!TryBegin(approvalId))
            throw new ApprovalDecisionRefusedException(ApprovalDecisionOutcome.InFlight);
        try
        {
            await client.ResolveApprovalAsync(GatewayApprovalKind.Exec, approvalId, decision, epoch).ConfigureAwait(false);
            Logger.Info($"[Approval] chat decision sent decision={decision}");
            Retire(client, approvalId, epoch);
        }
        catch (GatewayApprovalStaleException)
        {
            Retire(client, approvalId, epoch);
            throw;
        }
        finally
        {
            End(approvalId);
        }
    }

    private async Task<ApprovalDecisionOutcome> SendAsync(
        IOperatorGatewayClient client, GatewayApprovalKind kind, string approvalId, string decision, long epoch)
    {
        if (!TryBegin(approvalId))
            return ApprovalDecisionOutcome.InFlight;
        try
        {
            await client.ResolveApprovalAsync(kind, approvalId, decision, epoch).ConfigureAwait(true);
            Logger.Info($"[Approval] decision sent kind={kind} decision={decision}");
            Retire(client, approvalId, epoch);
            return ApprovalDecisionOutcome.Sent;
        }
        catch (GatewayApprovalStaleException)
        {
            Logger.Info($"[Approval] approval was already resolved kind={kind}");
            Retire(client, approvalId, epoch);
            return ApprovalDecisionOutcome.AlreadyResolved;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Approval] decision send failed kind={kind}: {ex.GetType().Name}");
            return ApprovalDecisionOutcome.Failed;
        }
        finally
        {
            End(approvalId);
        }
    }

    private void Retire(IOperatorGatewayClient client, string approvalId, long epoch)
    {
        // Only this connection's grants: the same id may already be live again on a later epoch.
        lock (_gate)
        {
            foreach (var token in _toastGrants
                .Where(pair => pair.Value.ApprovalId == approvalId &&
                    ReferenceEquals(pair.Value.Client, client) &&
                    pair.Value.ConnectionEpoch == epoch)
                .Select(pair => pair.Key).ToList())
                _toastGrants.Remove(token);
        }
        _source.MarkApprovalResolved(client, approvalId, epoch);
    }

    private bool TryBegin(string approvalId)
    {
        lock (_gate)
            return _inFlight.Add(approvalId);
    }

    private void End(string approvalId)
    {
        lock (_gate)
            _inFlight.Remove(approvalId);
    }

    private void PruneToastGrantsLocked()
    {
        var now = _clock();
        foreach (var token in _toastGrants.Where(pair => pair.Value.Deadline <= now).Select(pair => pair.Key).ToList())
            _toastGrants.Remove(token);
        // Bounded: the oldest grants go first when many approvals arrive at once.
        while (_toastGrants.Count >= MaxToastGrants)
            _toastGrants.Remove(_toastGrants.MinBy(pair => pair.Value.Deadline).Key);
    }
}
