using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>The result of reconciling a fresh pair-list snapshot against prior state.</summary>
public sealed class PairingApprovalDelta
{
    /// <summary>Requests that are newly surfaced and prompt-worthy (not previously known, not already decided).</summary>
    public IReadOnlyList<PendingApproval> Added { get; init; } = Array.Empty<PendingApproval>();

    /// <summary>Keys (<see cref="PendingApproval.Key"/>) that were present last time but have now left the list.</summary>
    public IReadOnlyList<string> ResolvedKeys { get; init; } = Array.Empty<string>();

    /// <summary>All currently actionable pending approvals (excludes ones the local user already decided).</summary>
    public IReadOnlyList<PendingApproval> Current { get; init; } = Array.Empty<PendingApproval>();

    public bool HasChanges => Added.Count > 0 || ResolvedKeys.Count > 0;
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
///   <item>Suppress re-prompting for a request the local user already approved/rejected until the
///         gateway confirms by dropping it from the list.</item>
/// </list>
/// Not thread-safe; the coordinator marshals all calls onto a single dispatcher thread.
/// </summary>
public sealed class PairingApprovalQueue
{
    private readonly Dictionary<string, PendingApproval> _current = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decided = new(StringComparer.Ordinal);

    /// <summary>Snapshot of currently actionable approvals, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Current =>
        _current.Values
            .Where(a => !_decided.Contains(a.Key))
            .OrderBy(a => a.Ts)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>True when there are no actionable approvals.</summary>
    public bool IsEmpty => Current.Count == 0;

    /// <summary>
    /// Reconcile a fresh snapshot. <paramref name="ownNodeDeviceId"/>, when provided, filters out the
    /// local Windows node's own pending node request so we don't prompt the user to approve themselves.
    /// </summary>
    public PairingApprovalDelta Reconcile(
        DevicePairingListInfo? devices,
        PairingListInfo? nodes,
        string? ownNodeDeviceId = null)
    {
        var incoming = BuildIncoming(devices, nodes, ownNodeDeviceId);
        var incomingByKey = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        foreach (var item in incoming)
            incomingByKey[item.Key] = item; // last wins on duplicate ids

        var added = new List<PendingApproval>();
        foreach (var item in incoming)
        {
            if (_current.ContainsKey(item.Key)) continue; // already known
            if (_decided.Contains(item.Key)) continue;    // user already acted; awaiting gateway drop
            if (added.Any(a => a.Key == item.Key)) continue;
            added.Add(item);
        }

        var resolvedKeys = _current.Keys
            .Where(key => !incomingByKey.ContainsKey(key))
            .ToArray();

        // Swap in the new snapshot.
        _current.Clear();
        foreach (var kvp in incomingByKey)
            _current[kvp.Key] = kvp.Value;

        // Forget decisions for requests that have now left the list — a future request that
        // happens to reuse the id is a genuinely new prompt.
        _decided.RemoveWhere(key => !incomingByKey.ContainsKey(key));

        return new PairingApprovalDelta
        {
            Added = added,
            ResolvedKeys = resolvedKeys,
            Current = Current,
        };
    }

    /// <summary>
    /// Mark a request as locally decided (approved/rejected) so it is not re-surfaced while the
    /// gateway catches up and the same id is still echoed in the pending list.
    /// </summary>
    public void MarkDecided(string key)
    {
        if (!string.IsNullOrEmpty(key))
            _decided.Add(key);
    }

    /// <summary>Look up a currently-tracked approval by its key.</summary>
    public PendingApproval? Find(string key) =>
        _current.TryGetValue(key, out var value) && !_decided.Contains(key) ? value : null;

    /// <summary>Clears all state (e.g. on disconnect / gateway switch).</summary>
    public void Reset()
    {
        _current.Clear();
        _decided.Clear();
    }

    private static List<PendingApproval> BuildIncoming(
        DevicePairingListInfo? devices,
        PairingListInfo? nodes,
        string? ownNodeDeviceId)
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

        if (nodes?.Pending is { Count: > 0 })
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
