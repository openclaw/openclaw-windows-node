namespace OpenClaw.Shared.Systems;

public enum SystemsStatusFilter
{
    All,
    Online,
    Offline,
}

public enum SystemsSortMode
{
    Alphabetical,
    OnlineFirst,
    OfflineFirst,
}

/// <summary>Errors from auxiliary reads. Inventory failures are reported separately because they replace the list.</summary>
public sealed record SystemsPartialErrors(string? Nodes = null, string? SystemInfo = null)
{
    public bool Any => Nodes is not null || SystemInfo is not null;

    public IEnumerable<string> Messages =>
        new[] { Nodes, SystemInfo }.Where(message => message is not null)!;
}

/// <summary>Everything the Gateway reported for one Systems refresh.</summary>
public sealed record SystemsInventorySnapshot
{
    /// <summary><c>environments.list</c> rows; null when the Gateway predates the method.</summary>
    public IReadOnlyList<SystemEnvironment>? Environments { get; init; }
    public IReadOnlyList<GatewayNodeInfo> Nodes { get; init; } = Array.Empty<GatewayNodeInfo>();
    public IReadOnlyList<PresenceEntry> Presence { get; init; } = Array.Empty<PresenceEntry>();
    public GatewaySystemInfo? SystemInfo { get; init; }
    public DateTimeOffset? SystemInfoSampledAt { get; init; }
    public SystemsPartialErrors Errors { get; init; } = new();
}

/// <summary>One machine or connected client in the Systems inventory.</summary>
public sealed record SystemRow
{
    public required string Id { get; init; }
    public required SystemKind Kind { get; init; }
    public required SystemStatus Status { get; init; }
    public SystemEnvironment? Environment { get; init; }
    public GatewayNodeInfo? Node { get; init; }
    /// <summary>Presence/merged identity carried over from the Instances page.</summary>
    public MergedInstance? Instance { get; init; }
    public GatewaySystemInfo? SystemInfo { get; init; }
    public IReadOnlyList<SessionRelation> Sessions { get; init; } = Array.Empty<SessionRelation>();
    /// <summary>Reported name; null means the UI supplies a localized fallback (for example "Gateway host").</summary>
    public string? Name { get; init; }
    public string? Platform { get; init; }
    public bool IsThisDevice { get; init; }
    public HostMeasurements? Measurements { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }

    public bool IsOnline => Status == SystemStatus.Online;

    /// <summary>Rename/forget are offered only for strong node identity matches.</summary>
    public bool CanManageNode => Node is not null && Kind == SystemKind.Node &&
        (Instance is null || Instance.CanManageNode || Instance.Node is null);
}

public sealed record SystemsProjectionOptions
{
    public string? LocalNodeId { get; init; }
    public string? LocalHost { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    /// <summary>When false (disconnected), the synthesized legacy Gateway row reports Offline.</summary>
    public bool GatewayConnected { get; init; } = true;
    /// <summary>Host stats delivered by <c>node.hostStats</c> events; the newer of these and node.list wins.</summary>
    public IReadOnlyDictionary<string, NodeHostStats>? HostStats { get; init; }
    /// <summary>Local clock minus Gateway clock, applied to Gateway receipt times so ages use the local clock.</summary>
    public TimeSpan GatewayClockOffset { get; init; }
}

/// <summary>
/// Pure projection behind the Systems page. Joins only exact Gateway identities;
/// desktop capability never determines row membership.
/// </summary>
public static class SystemsProjection
{
    public const string NodeIdPrefix = "node:";
    public const string GatewayId = "gateway";
    public const string ClientIdPrefix = "client:";

    public static IReadOnlyList<SystemRow> Project(
        SystemsInventorySnapshot snapshot,
        IReadOnlyList<SystemsSessionRef> sessions,
        SystemsProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new SystemsProjectionOptions();

        var relations = BuildRelations(sessions);
        var nodesById = new Dictionary<string, GatewayNodeInfo>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes)
            nodesById.TryAdd(node.NodeId, node);

        var instances = InstanceMerger.Merge(snapshot.Nodes, snapshot.Presence, new InstanceMergeOptions
        {
            LocalNodeId = options.LocalNodeId,
            LocalHost = options.LocalHost,
            // Presence beacons carry Gateway time, so their freshness is judged on the Gateway's clock.
            NowUtc = () => options.NowUtc - options.GatewayClockOffset,
        });

        var environments = snapshot.Environments ?? SynthesizeLegacyEnvironments(snapshot, options);
        var claimed = new HashSet<MergedInstance>(ReferenceEqualityComparer.Instance);
        var rows = new List<SystemRow>(environments.Count + instances.Count);

        foreach (var environment in environments)
        {
            // Terminal worker records remain in Gateway history after their machine is gone.
            if (environment.Worker is { State: "destroyed" or "failed" })
                continue;

            // Only the Gateway's own environment is the host; other non-node types are workers, as on the web.
            var kind = environment.Id == GatewayId ? SystemKind.Host
                : environment.Type == "node" ? SystemKind.Node
                : SystemKind.Worker;
            var nodeId = environment.NodeId;
            var node = kind == SystemKind.Node && nodeId is not null ? nodesById.GetValueOrDefault(nodeId) : null;
            var instance = kind == SystemKind.Host
                ? instances.FirstOrDefault(i => i.IsGateway && !claimed.Contains(i))
                : nodeId is null ? null : BestInstanceForNode(instances, nodeId);
            foreach (var matched in instances.Where(i =>
                         ReferenceEquals(i, instance) ||
                         (nodeId is not null && string.Equals(i.Node?.NodeId, nodeId, StringComparison.Ordinal))))
            {
                claimed.Add(matched);
            }

            var systemInfo = environment.Id == GatewayId ? snapshot.SystemInfo : null;
            var rowSessions = relations.GetValueOrDefault(environment.Id) ?? (IReadOnlyList<SessionRelation>)Array.Empty<SessionRelation>();
            HostMeasurements? measurements = null;
            DateTimeOffset? measuredAt = null;
            if (systemInfo is not null)
                (measurements, measuredAt) = (systemInfo.Measurements, snapshot.SystemInfoSampledAt);
            else if (Newest(node?.HostStats, nodeId is null ? null : options.HostStats?.GetValueOrDefault(nodeId)) is { } stats)
                (measurements, measuredAt) = (stats.Measurements, stats.UpdatedAt + options.GatewayClockOffset);

            rows.Add(new SystemRow
            {
                Id = environment.Id,
                Kind = kind,
                Status = environment.Status,
                Environment = environment,
                Node = node,
                Instance = instance,
                SystemInfo = systemInfo,
                Sessions = rowSessions,
                Name = ResolveName(environment, systemInfo, node, rowSessions),
                Platform = PlatformLabels.Pretty(
                    NonEmpty(systemInfo?.OsLabel) ?? environment.Platform ?? node?.Platform ?? instance?.Platform,
                    node?.DeviceFamily ?? instance?.DeviceFamily),
                IsThisDevice = instance?.IsThisInstance == true ||
                    (nodeId is not null && string.Equals(nodeId, options.LocalNodeId, StringComparison.OrdinalIgnoreCase)),
                Measurements = measurements,
                MeasuredAt = measuredAt,
            });
        }

        foreach (var instance in instances)
        {
            if (claimed.Contains(instance) || instance.IsGateway)
                continue;
            // A node suppressed by environments.list stays hidden; never resurrect it from node.list.
            if (instance.Node is not null && snapshot.Environments is not null)
                continue;
            rows.Add(new SystemRow
            {
                Id = ClientIdPrefix + instance.Key,
                Kind = SystemKind.Client,
                Status = instance.Status is PresenceStatus.Active or PresenceStatus.Idle
                    ? SystemStatus.Online
                    : SystemStatus.Offline,
                Instance = instance,
                Name = instance.DisplayName,
                Platform = PlatformLabels.Pretty(instance.Platform, instance.DeviceFamily),
                IsThisDevice = instance.IsThisInstance,
            });
        }

        return rows;
    }

    private static NodeHostStats? Newest(NodeHostStats? listed, NodeHostStats? pushed) =>
        listed is null ? pushed : pushed is null || listed.UpdatedAt >= pushed.UpdatedAt ? listed : pushed;

    /// <summary>A disconnected Gateway cannot vouch for anything being online, so every row reads Offline.</summary>
    public static SystemStatus EffectiveStatus(SystemRow row, bool gatewayConnected) =>
        gatewayConnected ? row.Status : SystemStatus.Offline;

    /// <summary>Older Gateways have no environments.list; rebuild the minimum inventory from node.list.</summary>
    private static IReadOnlyList<SystemEnvironment> SynthesizeLegacyEnvironments(
        SystemsInventorySnapshot snapshot, SystemsProjectionOptions options)
    {
        var result = new List<SystemEnvironment>
        {
            new()
            {
                Id = GatewayId,
                Type = "local",
                Status = options.GatewayConnected ? SystemStatus.Online : SystemStatus.Offline,
            },
        };
        foreach (var node in snapshot.Nodes)
        {
            result.Add(new SystemEnvironment
            {
                Id = NodeIdPrefix + node.NodeId,
                Type = "node",
                Label = node.HasExplicitDisplayName ? node.DisplayName : null,
                Status = node.IsOnline ? SystemStatus.Online : SystemStatus.Offline,
                Platform = node.Platform,
                Capabilities = node.Capabilities,
                InvocableCommands = node.Commands,
                LastSeenAt = node.LastSeen is { } seen ? new DateTimeOffset(DateTime.SpecifyKind(seen, DateTimeKind.Utc)) : null,
                LastSeenReason = node.LastSeenReason,
            });
        }
        return result;
    }

    private static MergedInstance? BestInstanceForNode(IReadOnlyList<MergedInstance> instances, string nodeId) =>
        instances
            .Where(i => string.Equals(i.Node?.NodeId, nodeId, StringComparison.Ordinal))
            .OrderByDescending(i => i.CanManageNode)
            .ThenByDescending(i => i.Presence is not null)
            .ThenByDescending(i => i.Timestamp ?? DateTime.MinValue)
            .FirstOrDefault();

    /// <summary>Session-to-machine bindings. Requested placements are not yet bound anywhere.</summary>
    public static Dictionary<string, IReadOnlyList<SessionRelation>> BuildRelations(IReadOnlyList<SystemsSessionRef> sessions)
    {
        var map = new Dictionary<string, List<SessionRelation>>(StringComparer.Ordinal);
        void Add(string id, SessionRelationKind kind, SystemsSessionRef session)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = new List<SessionRelation>();
            list.Add(new SessionRelation(kind, session));
        }

        foreach (var session in sessions)
        {
            if (session.Placement is { } placement && placement.State != SessionPlacementState.Local)
            {
                if (placement.State == SessionPlacementState.Requested)
                    continue;
                if (placement.EnvironmentId is { } environmentId)
                {
                    Add(environmentId,
                        placement.State is SessionPlacementState.Reclaimed or SessionPlacementState.Failed
                            ? SessionRelationKind.RetainedPlacement
                            : SessionRelationKind.Placement,
                        session);
                }
                if (placement.State == SessionPlacementState.Active && placement.RunnerDeviceId is { } runner)
                    Add(NodeIdPrefix + runner, SessionRelationKind.Runner, session);
                continue;
            }

            var execNode = session.ExecNode?.Trim();
            if (!string.IsNullOrEmpty(execNode))
                Add(NodeIdPrefix + execNode, SessionRelationKind.ExecBinding, session);
            else
                Add(GatewayId, SessionRelationKind.Gateway, session);
        }

        return map.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SessionRelation>)pair.Value, StringComparer.Ordinal);
    }

    private static string? ResolveName(
        SystemEnvironment environment,
        GatewaySystemInfo? systemInfo,
        GatewayNodeInfo? node,
        IReadOnlyList<SessionRelation> sessions)
    {
        var named = NonEmpty(systemInfo?.MachineName) ?? environment.Label ??
            (node?.HasExplicitDisplayName == true ? node.DisplayName : null);
        if (named is not null)
            return named;
        if (environment.Id == GatewayId)
            return null;
        if (environment.Worker is { } worker)
        {
            // A worker is best known by the session placed on it, then by its provider profile.
            var placed = sessions.FirstOrDefault(r => r.Kind == SessionRelationKind.Placement)?.Session;
            if (placed is not null)
                return placed.Title;
            if (worker.ProfileId is { } profile)
                return $"{worker.ProviderId} · {profile}";
        }
        return node?.DisplayName is { Length: > 0 } fallback ? fallback : environment.Id;
    }

    /// <summary>Short, stable fragment of a Gateway-owned id for telling same-profile workers apart.</summary>
    public static string ShortId(string id)
    {
        var start = id.LastIndexOf(':') + 1;
        return id.Substring(start, Math.Min(6, id.Length - start));
    }

    /// <summary>
    /// Applies search, status filter and sort on the status the page shows (see <see cref="EffectiveStatus"/>).
    /// <paramref name="displayName"/> supplies localized fallbacks.
    /// </summary>
    public static IReadOnlyList<SystemRow> Query(
        IEnumerable<SystemRow> rows,
        string? query,
        SystemsStatusFilter filter,
        SystemsSortMode sort,
        Func<SystemRow, string> displayName,
        bool gatewayConnected = true)
    {
        var needle = query?.Trim() ?? "";
        SystemStatus Status(SystemRow row) => EffectiveStatus(row, gatewayConnected);
        var filtered = rows.Where(row => filter switch
            {
                SystemsStatusFilter.Online => Status(row) == SystemStatus.Online,
                SystemsStatusFilter.Offline => Status(row) == SystemStatus.Offline,
                _ => true,
            })
            .Where(row => needle.Length == 0 || new[]
            {
                displayName(row), row.Id, row.Platform ?? "", row.Environment?.Platform ?? row.Node?.Platform ?? "",
            }.Any(value => value.Contains(needle, StringComparison.CurrentCultureIgnoreCase)));

        var comparer = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
        var first = sort switch
        {
            SystemsSortMode.OnlineFirst => SystemStatus.Online,
            SystemsSortMode.OfflineFirst => SystemStatus.Offline,
            _ => (SystemStatus?)null,
        };
        return filtered
            .OrderBy(row => first is { } status && Status(row) == status ? 0 : 1)
            .ThenBy(displayName, comparer)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Initial selection: the Gateway host, then the first row.</summary>
    public static string? DefaultSelection(IReadOnlyList<SystemRow> rows) =>
        rows.FirstOrDefault(row => row.Id == GatewayId)?.Id
        ?? rows.FirstOrDefault()?.Id;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Human-readable platform names shared by Systems rows.</summary>
public static class PlatformLabels
{
    public static string? Pretty(string? raw, string? deviceFamily = null)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return string.IsNullOrWhiteSpace(deviceFamily) ? null : deviceFamily.Trim();
        foreach (var (prefix, label) in Known)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return label + trimmed[prefix.Length..];
        }
        return trimmed switch
        {
            "win32" => "Windows",
            "darwin" => "macOS",
            _ => trimmed,
        };
    }

    public static bool IsMac(string? platform) =>
        platform?.StartsWith("macOS", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(platform, "darwin", StringComparison.OrdinalIgnoreCase);

    private static readonly (string Prefix, string Label)[] Known =
    [
        ("macos", "macOS"),
        ("ipados", "iPadOS"),
        ("ios", "iOS"),
        ("tvos", "tvOS"),
        ("watchos", "watchOS"),
        ("windows", "Windows"),
        ("linux", "Linux"),
        ("android", "Android"),
    ];
}
