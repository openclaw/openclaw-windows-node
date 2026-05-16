using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared;

/// <summary>
/// Pure merge of presence beacons (broad, all platforms) with gateway node-list entries
/// (rich, Windows-paired only) into one ordered list for the Instances UI.
/// </summary>
/// <remarks>
/// Match strategy is intentionally conservative — destructive actions (Rename/Forget)
/// must never be attached to the wrong row. The fallback to host/displayName matching
/// only applies when both sides are unambiguous on those values.
/// </remarks>
public static class InstanceMerger
{
    public static IReadOnlyList<MergedInstance> Merge(
        IReadOnlyList<GatewayNodeInfo>? nodes,
        IReadOnlyList<PresenceEntry>? presence,
        InstanceMergeOptions? options = null)
    {
        options ??= new InstanceMergeOptions();
        var nowUtc = options.NowUtc?.Invoke() ?? DateTime.UtcNow;

        var nodesList = DedupeNodes(nodes);
        var presenceList = presence is null
            ? new List<PresenceEntry>()
            : presence.Where(p => p is not null).ToList();

        var unmatchedNodes = new HashSet<GatewayNodeInfo>(nodesList);
        var nodeByIdKey = BuildNodeIdIndex(nodesList);
        var nodeByDisplayName = BuildNodeDisplayNameIndex(nodesList);

        var hostCounts = CountNormalized(presenceList.Select(p => p.Host));
        var displayCounts = CountNormalized(nodesList.Select(n => n.DisplayName));

        var rows = new List<MergedInstance>(presenceList.Count + unmatchedNodes.Count);

        // Two-pass match: do all strong (DeviceId/InstanceId → NodeId/ClientId)
        // matches FIRST, then weak (host → displayName) fallbacks on what's
        // left. Without this split, an earlier presence that matches a node by
        // host would consume that node from the indexes — and a later presence
        // entry with a strong DeviceId match for the same node would miss it
        // and render presence-only, losing the Rename/Forget surface.
        var matchByPresenceIndex = new GatewayNodeInfo?[presenceList.Count];
        var strongMatchByPresenceIndex = new bool[presenceList.Count];

        for (int i = 0; i < presenceList.Count; i++)
        {
            var matched = TryStrongMatch(presenceList[i], nodeByIdKey);
            if (matched is not null)
            {
                unmatchedNodes.Remove(matched);
                RemoveNodeFromIndexes(matched, nodeByIdKey, nodeByDisplayName);
                matchByPresenceIndex[i] = matched;
                strongMatchByPresenceIndex[i] = true;
            }
        }

        for (int i = 0; i < presenceList.Count; i++)
        {
            if (matchByPresenceIndex[i] is not null) continue;
            var matched = TryWeakMatch(presenceList[i], nodeByDisplayName, hostCounts, displayCounts);
            if (matched is not null)
            {
                unmatchedNodes.Remove(matched);
                RemoveNodeFromIndexes(matched, nodeByIdKey, nodeByDisplayName);
                matchByPresenceIndex[i] = matched;
            }
        }

        for (int i = 0; i < presenceList.Count; i++)
        {
            rows.Add(BuildFromPresence(
                presenceList[i],
                matchByPresenceIndex[i],
                strongMatchByPresenceIndex[i],
                nowUtc,
                options));
        }

        foreach (var orphan in unmatchedNodes)
        {
            options.OnUnmatchedNode?.Invoke(
                $"node.list entry without matching presence: " +
                $"nodeId={orphan.NodeId} clientId={orphan.ClientId} " +
                $"displayName={orphan.DisplayName} platform={orphan.Platform}");
            rows.Add(BuildFromOrphanNode(orphan, nowUtc, options));
        }

        return SortStable(rows, nowUtc);
    }

    /// <summary>
    /// Drops null entries and collapses duplicate node-list rows that point at
    /// the same identity (same normalised NodeId or ClientId) so the gateway
    /// echoing a node twice does not become two offline cards. Tracks NodeId
    /// and ClientId in separate sets so a NodeId value cannot accidentally
    /// suppress a different node that happens to share it as ClientId.
    /// </summary>
    private static List<GatewayNodeInfo> DedupeNodes(IReadOnlyList<GatewayNodeInfo>? nodes)
    {
        if (nodes is null) return new List<GatewayNodeInfo>();
        var result = new List<GatewayNodeInfo>(nodes.Count);
        var seenNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            if (n is null) continue;
            var nodeKey = Normalize(n.NodeId);
            var clientKey = Normalize(n.ClientId);
            if (nodeKey.Length > 0 && seenNodeIds.Contains(nodeKey)) continue;
            if (clientKey.Length > 0 && seenClientIds.Contains(clientKey)) continue;
            if (nodeKey.Length > 0) seenNodeIds.Add(nodeKey);
            if (clientKey.Length > 0) seenClientIds.Add(clientKey);
            result.Add(n);
        }
        return result;
    }

    private static void RemoveNodeFromIndexes(
        GatewayNodeInfo node,
        Dictionary<string, GatewayNodeInfo> nodeByIdKey,
        Dictionary<string, GatewayNodeInfo> nodeByDisplayName)
    {
        RemoveIfMatches(nodeByIdKey, node.NodeId, node);
        RemoveIfMatches(nodeByIdKey, node.ClientId, node);
        RemoveIfMatches(nodeByDisplayName, node.DisplayName, node);
    }

    private static void RemoveIfMatches(
        Dictionary<string, GatewayNodeInfo> map,
        string? key,
        GatewayNodeInfo node)
    {
        var k = Normalize(key);
        if (k.Length == 0) return;
        // Only remove the entry if it still points at the same node — a later
        // node with a colliding key (which AddIfMissing would have skipped)
        // never reached the index in the first place.
        if (map.TryGetValue(k, out var existing) && ReferenceEquals(existing, node))
        {
            map.Remove(k);
        }
    }

    private static Dictionary<string, GatewayNodeInfo> BuildNodeIdIndex(IReadOnlyList<GatewayNodeInfo> nodes)
    {
        var map = new Dictionary<string, GatewayNodeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            AddIfMissing(map, n.NodeId, n);
            AddIfMissing(map, n.ClientId, n);
        }
        return map;
    }

    private static Dictionary<string, GatewayNodeInfo> BuildNodeDisplayNameIndex(IReadOnlyList<GatewayNodeInfo> nodes)
    {
        // Only the first node per (case-insensitive) display name is kept; uniqueness
        // is gated separately at match time via displayCounts.
        var map = new Dictionary<string, GatewayNodeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            AddIfMissing(map, n.DisplayName, n);
        }
        return map;
    }

    private static void AddIfMissing(Dictionary<string, GatewayNodeInfo> map, string? key, GatewayNodeInfo node)
    {
        var k = Normalize(key);
        if (k.Length == 0) return;
        if (!map.ContainsKey(k)) map[k] = node;
    }

    /// <summary>
    /// Strong match: exact DeviceId / InstanceId against NodeId / ClientId
    /// (both folded into <paramref name="nodeByIdKey"/>). Only this kind of
    /// match should attach Rename/Forget to a presence row.
    /// </summary>
    private static GatewayNodeInfo? TryStrongMatch(
        PresenceEntry p,
        Dictionary<string, GatewayNodeInfo> nodeByIdKey)
    {
        if (TryGet(nodeByIdKey, p.DeviceId, out var n1)) return n1;
        if (TryGet(nodeByIdKey, p.InstanceId, out var n2)) return n2;
        return null;
    }

    /// <summary>
    /// Weak fallback: host ↔ display-name fuzzy match, gated by uniqueness on
    /// BOTH sides. Runs only after all strong matches have consumed their
    /// nodes so a host-collision can never steal a node from its true owner.
    /// </summary>
    private static GatewayNodeInfo? TryWeakMatch(
        PresenceEntry p,
        Dictionary<string, GatewayNodeInfo> nodeByDisplayName,
        Dictionary<string, int> hostCounts,
        Dictionary<string, int> displayCounts)
    {
        var hostKey = Normalize(p.Host);
        if (hostKey.Length > 0 &&
            hostCounts.TryGetValue(hostKey, out var hc) && hc == 1 &&
            displayCounts.TryGetValue(hostKey, out var dc) && dc == 1 &&
            nodeByDisplayName.TryGetValue(hostKey, out var nh))
        {
            return nh;
        }
        return null;
    }

    private static bool TryGet(Dictionary<string, GatewayNodeInfo> map, string? key, out GatewayNodeInfo node)
    {
        var k = Normalize(key);
        if (k.Length > 0 && map.TryGetValue(k, out var found))
        {
            node = found;
            return true;
        }
        node = null!;
        return false;
    }

    private static MergedInstance BuildFromPresence(
        PresenceEntry p,
        GatewayNodeInfo? node,
        bool isStrongNodeMatch,
        DateTime nowUtc,
        InstanceMergeOptions options)
    {
        var status = ClassifyPresence(p, nowUtc, options);
        var isGateway = string.Equals(p.Mode?.Trim(), "gateway", StringComparison.OrdinalIgnoreCase);
        if (isGateway) status = PresenceStatus.Gateway;

        var key = StableKey(node?.NodeId, p.DeviceId, p.InstanceId, p.Host, p.Ip);
        return new MergedInstance
        {
            Key = key,
            Presence = p,
            Node = node,
            CanManageNode = node is not null && isStrongNodeMatch,
            Status = status,
            IsGateway = isGateway,
            IsThisInstance = !isGateway && IsLocalIdentity(node, p, options),
            DisplayName = node?.DisplayName is { Length: > 0 } dn ? dn : p.DisplayName,
            Ip = p.Ip ?? node?.RemoteIp,
            Version = p.Version ?? node?.Version,
            Platform = p.Platform ?? node?.Platform,
            DeviceFamily = p.DeviceFamily ?? node?.DeviceFamily,
            ModelIdentifier = p.ModelIdentifier ?? node?.ModelIdentifier,
            Mode = p.Mode ?? node?.Mode,
            LastInputSeconds = p.LastInputSeconds,
            Reason = p.Reason ?? node?.LastSeenReason,
            Timestamp = p.Ts > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.Ts).UtcDateTime : node?.LastSeen,
            CapabilityCount = node?.CapabilityCount ?? 0,
            CommandCount = node?.CommandCount ?? 0,
            Roles = p.Roles is { Length: > 0 } r ? r : Array.Empty<string>(),
            IdentityCaption = p.InstanceId ?? p.DeviceId ?? node?.NodeId,
            NodeStatusRaw = NormalizeStatus(node?.Status),
            DebugText = p.Text,
        };
    }

    private static MergedInstance BuildFromOrphanNode(
        GatewayNodeInfo node,
        DateTime nowUtc,
        InstanceMergeOptions options)
    {
        var key = StableKey(node.NodeId, node.ClientId, displayName: node.DisplayName);
        return new MergedInstance
        {
            Key = key,
            Presence = null,
            Node = node,
            CanManageNode = true,
            Status = PresenceStatus.Offline,
            IsGateway = false,
            IsThisInstance = IsLocalIdentity(node, presence: null, options),
            DisplayName = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName,
            Ip = node.RemoteIp,
            Version = node.Version,
            Platform = node.Platform,
            DeviceFamily = node.DeviceFamily,
            ModelIdentifier = node.ModelIdentifier,
            Mode = node.Mode,
            LastInputSeconds = null,
            Reason = node.LastSeenReason,
            Timestamp = node.LastSeen,
            CapabilityCount = node.CapabilityCount,
            CommandCount = node.CommandCount,
            Roles = Array.Empty<string>(),
            IdentityCaption = node.NodeId,
            NodeStatusRaw = NormalizeStatus(node.Status),
            DebugText = null,
        };
    }

    /// <summary>
    /// Normalize the raw node status — return null when the value carries no extra
    /// signal beyond what PresenceStatus already conveys (e.g. blank, "unknown",
    /// "online" for an active row).
    /// </summary>
    private static string? NormalizeStatus(string? status)
    {
        var n = Normalize(status);
        if (n.Length == 0) return null;
        if (string.Equals(n, "unknown", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(n, "online", StringComparison.OrdinalIgnoreCase)) return null;
        return n;
    }

    private static PresenceStatus ClassifyPresence(
        PresenceEntry p,
        DateTime nowUtc,
        InstanceMergeOptions options)
    {
        if (p.Ts <= 0) return PresenceStatus.Stale;
        var beaconUtc = DateTimeOffset.FromUnixTimeMilliseconds(p.Ts).UtcDateTime;
        var age = nowUtc - beaconUtc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age <= options.ActiveThreshold) return PresenceStatus.Active;
        if (age <= options.IdleThreshold) return PresenceStatus.Idle;
        return PresenceStatus.Stale;
    }

    private static bool IsLocalIdentity(GatewayNodeInfo? node, PresenceEntry? presence, InstanceMergeOptions options)
    {
        var localId = Normalize(options.LocalNodeId);
        var localHost = Normalize(options.LocalHost);

        if (localId.Length > 0)
        {
            if (node is not null &&
                (string.Equals(localId, Normalize(node.NodeId), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(localId, Normalize(node.ClientId), StringComparison.OrdinalIgnoreCase)))
                return true;
            if (presence is not null &&
                (string.Equals(localId, Normalize(presence.DeviceId), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(localId, Normalize(presence.InstanceId), StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        if (localHost.Length > 0 && presence is not null &&
            string.Equals(localHost, Normalize(presence.Host), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static List<MergedInstance> SortStable(List<MergedInstance> rows, DateTime nowUtc)
    {
        return rows
            .Select((r, i) => (r, i))
            .OrderBy(t => SortBucket(t.r))
            .ThenBy(t => t.r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.i)
            .Select(t => t.r)
            .ToList();
    }

    private static int SortBucket(MergedInstance r) => r switch
    {
        { IsGateway: true } => 0,
        { IsThisInstance: true } => 1,
        { Status: PresenceStatus.Active } => 2,
        { Status: PresenceStatus.Idle } => 3,
        { Status: PresenceStatus.Stale } => 4,
        { Status: PresenceStatus.Offline } => 5,
        _ => 6,
    };

    private static Dictionary<string, int> CountNormalized(IEnumerable<string?> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            var k = Normalize(v);
            if (k.Length == 0) continue;
            counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    /// <summary>
    /// Builds a deterministic identity string for a merged row. Used as the
    /// row's <see cref="MergedInstance.Key"/> for diffing / animation hooks
    /// callers may add later. The key composes available identifiers in
    /// priority order so two rows that share only a host (e.g. behind NAT) or
    /// only an IP do not collide.
    /// </summary>
    internal static string StableKey(
        string? nodeId = null,
        string? deviceId = null,
        string? instanceId = null,
        string? host = null,
        string? ip = null,
        string? displayName = null)
    {
        // Strongest identifiers first — any one of these is sufficient.
        var strong = Normalize(nodeId);
        if (strong.Length > 0) return "n:" + strong;
        strong = Normalize(deviceId);
        if (strong.Length > 0) return "d:" + strong;
        strong = Normalize(instanceId);
        if (strong.Length > 0) return "i:" + strong;

        // Weak identifiers — combine all that are present so two rows that
        // share, say, a host name but live on different IPs remain distinct.
        var parts = new List<string>(3);
        var h = Normalize(host);
        var dn = Normalize(displayName);
        var i = Normalize(ip);
        if (h.Length > 0) parts.Add("h:" + h);
        if (dn.Length > 0) parts.Add("dn:" + dn);
        if (i.Length > 0) parts.Add("ip:" + i);
        if (parts.Count > 0) return string.Join("|", parts);

        return "instance";
    }
}
