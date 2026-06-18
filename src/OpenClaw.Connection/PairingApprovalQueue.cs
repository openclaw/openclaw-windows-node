using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>A pairing decision the gateway has now confirmed by removing it from the pending list.</summary>
public sealed record PairingApprovalResolution(PendingApproval Approval, bool Approved);

/// <summary>The result of reconciling a fresh pair-list snapshot against prior state.</summary>
public sealed class PairingApprovalDelta
{
    /// <summary>Requests that are newly surfaced and prompt-worthy (not previously known, not awaiting resolution).</summary>
    public IReadOnlyList<PendingApproval> Added { get; init; } = Array.Empty<PendingApproval>();

    /// <summary>Keys (<see cref="PendingApproval.Key"/>) that were present last time but have now left the list.</summary>
    public IReadOnlyList<string> ResolvedKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Decisions the local user submitted that the gateway has now confirmed (the request left the
    /// pending list). These — not the send-ack — are the source of truth for "approved/rejected".
    /// </summary>
    public IReadOnlyList<PairingApprovalResolution> ConfirmedDecisions { get; init; } = Array.Empty<PairingApprovalResolution>();

    /// <summary>All currently actionable pending approvals (excludes ones awaiting resolution).</summary>
    public IReadOnlyList<PendingApproval> Current { get; init; } = Array.Empty<PendingApproval>();

    public bool HasChanges => Added.Count > 0 || ResolvedKeys.Count > 0 || ConfirmedDecisions.Count > 0;
}

/// <summary>
/// Pure, UI-agnostic diff engine for inbound pairing approvals. Translates successive
/// device/node pair-list snapshots (the gateway re-sends the full list on every
/// <c>*.pair.requested</c>/<c>*.pair.resolved</c> event) into add/resolve deltas the
/// presentation layer can act on without re-prompting for requests it has already shown
/// or already decided.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Merge device + node pending lists into a single ordered queue (oldest first).</item>
///   <item>Filter out the local node's own pairing request (handled by the auto-approve path)
///         so the operator is never prompted to approve their own machine.</item>
///   <item>Drop entries with no usable id and de-duplicate by <see cref="PendingApproval.Key"/>.</item>
///   <item>Treat a submitted approve/reject as <em>optimistic-pending</em> — suppress it from the
///         actionable set, but only report it <see cref="PairingApprovalDelta.ConfirmedDecisions">
///         confirmed</see> once the gateway actually drops it from the pending list. If the gateway
///         never acts within <see cref="SubmissionResolveTimeoutMs"/>, re-surface the request so a
///         lost decision is retried rather than hidden forever (and no false success is reported).</item>
/// </list>
/// Not thread-safe; the coordinator marshals all calls onto a single dispatcher thread.
/// </summary>
public sealed class PairingApprovalQueue
{
    /// <summary>How long a submitted-but-unresolved decision is suppressed before it is re-surfaced.</summary>
    public const long SubmissionResolveTimeoutMs = 10_000;

    private readonly Dictionary<string, PendingApproval> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Submission> _submitted = new(StringComparer.Ordinal);

    private readonly record struct Submission(PendingApproval Approval, bool Approved, long SubmittedAtMs);

    /// <summary>Snapshot of currently actionable approvals, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Current =>
        _current.Values
            .Where(a => !_submitted.ContainsKey(a.Key))
            .OrderBy(a => a.Ts)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>True when there are no actionable approvals.</summary>
    public bool IsEmpty => Current.Count == 0;

    /// <summary>
    /// Reconcile a fresh snapshot. <paramref name="ownNodeDeviceId"/>, when provided, filters out the
    /// local Windows node's own pending node request. <paramref name="nowMs"/> is a monotonic timestamp
    /// (e.g. <see cref="Environment.TickCount64"/>) used to expire stale submissions. When
    /// <paramref name="deferNodeRequests"/> is true, node requests are excluded entirely (used while
    /// the local node's own device id is not yet known, so we never prompt for our own machine).
    /// </summary>
    public PairingApprovalDelta Reconcile(
        DevicePairingListInfo? devices,
        PairingListInfo? nodes,
        string? ownNodeDeviceId = null,
        long nowMs = 0,
        bool deferNodeRequests = false)
    {
        var incoming = BuildIncoming(devices, nodes, ownNodeDeviceId, deferNodeRequests);
        var incomingByKey = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        foreach (var item in incoming)
            incomingByKey[item.Key] = item; // last wins on duplicate ids

        // Confirmed resolutions: decisions we submitted whose request has now left the pending list.
        // This — not the send-ack — is the authoritative "the gateway accepted it" signal.
        var confirmed = new List<PairingApprovalResolution>();
        foreach (var sub in _submitted.Values)
            if (!incomingByKey.ContainsKey(sub.Approval.Key))
                confirmed.Add(new PairingApprovalResolution(sub.Approval, sub.Approved));
        foreach (var c in confirmed)
            _submitted.Remove(c.Approval.Key);

        // Expire submissions the gateway hasn't acted on in time → drop suppression so they re-surface
        // (a lost/rejected decision is retried instead of being hidden indefinitely).
        var expired = _submitted
            .Where(kv => incomingByKey.ContainsKey(kv.Key) && nowMs - kv.Value.SubmittedAtMs >= SubmissionResolveTimeoutMs)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in expired)
            _submitted.Remove(k);
        var expiredSet = new HashSet<string>(expired, StringComparer.Ordinal);

        // Added: genuinely new requests, plus expired-and-re-surfaced ones. Skip anything still
        // awaiting resolution.
        var added = new List<PendingApproval>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in incoming)
        {
            if (_submitted.ContainsKey(item.Key)) continue;                                   // awaiting resolution
            if (_current.ContainsKey(item.Key) && !expiredSet.Contains(item.Key)) continue;   // already known
            if (!seen.Add(item.Key)) continue;
            added.Add(item);
        }

        var resolvedKeys = _current.Keys
            .Where(key => !incomingByKey.ContainsKey(key))
            .ToArray();

        // Swap in the new snapshot.
        _current.Clear();
        foreach (var kvp in incomingByKey)
            _current[kvp.Key] = kvp.Value;

        return new PairingApprovalDelta
        {
            Added = added,
            ResolvedKeys = resolvedKeys,
            ConfirmedDecisions = confirmed,
            Current = Current,
        };
    }

    /// <summary>
    /// Record that the local user submitted an approve/reject for a request. The request is suppressed
    /// from the actionable set (so the UI advances) but remains tracked until the gateway confirms by
    /// dropping it from the pending list, or until <see cref="SubmissionResolveTimeoutMs"/> elapses.
    /// </summary>
    public void MarkSubmitted(PendingApproval approval, bool approved, long nowMs)
    {
        if (approval is null || string.IsNullOrEmpty(approval.Key))
            return;
        _submitted[approval.Key] = new Submission(approval, approved, nowMs);
    }

    /// <summary>Look up a currently-actionable approval by its key (excludes ones awaiting resolution).</summary>
    public PendingApproval? Find(string key) =>
        !_submitted.ContainsKey(key) && _current.TryGetValue(key, out var value) ? value : null;

    /// <summary>Clears all state (e.g. on disconnect / gateway switch).</summary>
    public void Reset()
    {
        _current.Clear();
        _submitted.Clear();
    }

    private static List<PendingApproval> BuildIncoming(
        DevicePairingListInfo? devices,
        PairingListInfo? nodes,
        string? ownNodeDeviceId,
        bool deferNodeRequests)
    {
        var list = new List<PendingApproval>();

        if (devices?.Pending is { Count: > 0 })
        {
            foreach (var req in devices.Pending)
            {
                var approval = PendingApproval.FromDevice(req);
                if (approval.IsActionable)
                    list.Add(approval);
            }
        }

        if (!deferNodeRequests && nodes?.Pending is { Count: > 0 })
        {
            foreach (var req in nodes.Pending)
            {
                var approval = PendingApproval.FromNode(req);
                if (!approval.IsActionable) continue;
                if (IsOwnNode(approval, ownNodeDeviceId)) continue;
                list.Add(approval);
            }
        }

        return list;
    }

    private static bool IsOwnNode(PendingApproval approval, string? ownNodeDeviceId) =>
        !string.IsNullOrWhiteSpace(ownNodeDeviceId)
        && !string.IsNullOrEmpty(approval.DeviceId)
        && approval.DeviceId.Equals(ownNodeDeviceId, StringComparison.OrdinalIgnoreCase);
}
