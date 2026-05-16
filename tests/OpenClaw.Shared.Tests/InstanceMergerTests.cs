using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class InstanceMergerTests
{
    private const long FixedNowMs = 1_700_000_000_000;
    private static readonly DateTime FixedNowUtc =
        DateTimeOffset.FromUnixTimeMilliseconds(FixedNowMs).UtcDateTime;

    private static InstanceMergeOptions Options(
        string? localNodeId = null,
        string? localHost = null,
        Action<string>? onUnmatched = null) =>
        new()
        {
            LocalNodeId = localNodeId,
            LocalHost = localHost,
            OnUnmatchedNode = onUnmatched,
            NowUtc = () => FixedNowUtc,
        };

    private static PresenceEntry Presence(
        string? host = null,
        string? deviceId = null,
        string? instanceId = null,
        string? mode = null,
        string? platform = "Windows",
        long ageSeconds = 10,
        string? ip = null,
        string? version = null,
        string? text = null)
        => new()
        {
            Host = host,
            DeviceId = deviceId,
            InstanceId = instanceId,
            Mode = mode,
            Platform = platform,
            Ip = ip,
            Version = version,
            Text = text,
            Ts = FixedNowMs - (ageSeconds * 1000),
        };

    private static GatewayNodeInfo Node(
        string nodeId,
        string? displayName = null,
        string? clientId = null,
        bool isOnline = true,
        DateTime? lastSeen = null,
        string? platform = "Windows")
        => new()
        {
            NodeId = nodeId,
            DisplayName = displayName ?? "",
            ClientId = clientId,
            IsOnline = isOnline,
            IsPaired = true,
            LastSeen = lastSeen,
            Platform = platform,
        };

    // ── Match strategies ──────────────────────────────────────────────────

    [Fact]
    public void Matches_PresenceDeviceId_To_NodeId()
    {
        var p = Presence(deviceId: "node-abc-123", host: "BRENO-PC");
        var n = Node("node-abc-123", displayName: "Breno's PC");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Single(result);
        Assert.Same(p, result[0].Presence);
        Assert.Same(n, result[0].Node);
        Assert.True(result[0].IsManaged);
        Assert.Equal("Breno's PC", result[0].DisplayName);
    }

    [Fact]
    public void Matches_PresenceDeviceId_To_ClientId_When_NodeId_Differs()
    {
        var p = Presence(deviceId: "winnode-xyz", host: "PC1");
        var n = Node("node-internal-id", clientId: "winnode-xyz");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Single(result);
        Assert.Same(n, result[0].Node);
        Assert.True(result[0].IsManaged);
    }

    [Fact]
    public void Matches_PresenceInstanceId_To_NodeId()
    {
        var p = Presence(deviceId: null, instanceId: "node-instance-1", host: "PC1");
        var n = Node("node-instance-1", displayName: "PC1");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Same(n, result[0].Node);
    }

    [Fact]
    public void Match_Is_CaseInsensitive_And_TrimsWhitespace()
    {
        var p = Presence(deviceId: "  Node-ABC  ", host: "PC");
        var n = Node("node-abc");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Same(n, result[0].Node);
    }

    [Fact]
    public void HostFallback_Matches_When_BothSides_Unique()
    {
        var p = Presence(host: "BRENO-PC");
        var n = Node("opaque-uuid", displayName: "BRENO-PC");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Same(n, result[0].Node);
        Assert.False(result[0].IsManaged);
    }

    [Fact]
    public void HostFallback_Does_Not_Match_When_Presence_Host_Is_Ambiguous()
    {
        // Two presence rows on the same host (e.g. multi-user) — host fallback
        // must NOT attach Rename/Forget to either row.
        var p1 = Presence(host: "PC1", deviceId: "device-a");
        var p2 = Presence(host: "PC1", deviceId: "device-b");
        var n = Node("opaque-uuid", displayName: "PC1");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p1, p2 }, Options());

        Assert.Equal(3, result.Count); // 2 presence + 1 orphan node
        Assert.All(result.Where(r => r.Presence != null), r => Assert.Null(r.Node));
        Assert.Contains(result, r => r.Presence == null && r.Node == n); // node as orphan/offline
    }

    [Fact]
    public void HostFallback_Does_Not_Match_When_Node_DisplayName_Is_Ambiguous()
    {
        var p = Presence(host: "PC1");
        var n1 = Node("uuid-1", displayName: "PC1");
        var n2 = Node("uuid-2", displayName: "PC1");

        var result = InstanceMerger.Merge(new[] { n1, n2 }, new[] { p }, Options());

        var presenceRow = result.First(r => r.Presence == p);
        Assert.Null(presenceRow.Node);
    }

    [Fact]
    public void Presence_Without_Matching_Node_Renders_Without_Manage()
    {
        var p = Presence(host: "iPhone", platform: "iOS");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Single(result);
        Assert.Null(result[0].Node);
        Assert.False(result[0].IsManaged);
    }

    [Fact]
    public void Node_Without_Matching_Presence_Renders_As_Offline()
    {
        var lastSeen = FixedNowUtc - TimeSpan.FromMinutes(20);
        var n = Node("offline-node", displayName: "Old PC", isOnline: false, lastSeen: lastSeen);

        var result = InstanceMerger.Merge(new[] { n }, presence: null, Options());

        Assert.Single(result);
        Assert.Equal(PresenceStatus.Offline, result[0].Status);
        Assert.Same(n, result[0].Node);
        Assert.Null(result[0].Presence);
        Assert.True(result[0].IsManaged);
        Assert.Equal(lastSeen, result[0].Timestamp);
    }

    [Fact]
    public void Unmatched_Node_Triggers_Debug_Callback()
    {
        var captured = new List<string>();
        var n = Node("lonely-node");

        InstanceMerger.Merge(new[] { n }, presence: null, Options(onUnmatched: s => captured.Add(s)));

        Assert.Single(captured);
        Assert.Contains("lonely-node", captured[0]);
    }

    // ── Presence status thresholds ────────────────────────────────────────

    [Theory]
    [InlineData(10, PresenceStatus.Active)]
    [InlineData(120, PresenceStatus.Active)]
    [InlineData(121, PresenceStatus.Idle)]
    [InlineData(300, PresenceStatus.Idle)]
    [InlineData(301, PresenceStatus.Stale)]
    [InlineData(10000, PresenceStatus.Stale)]
    public void PresenceStatus_ThresholdBoundaries(long ageSeconds, PresenceStatus expected)
    {
        var p = Presence(host: "X", ageSeconds: ageSeconds);

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal(expected, result[0].Status);
    }

    [Fact]
    public void GatewayMode_Overrides_Status()
    {
        var p = Presence(host: "gateway", mode: "gateway", ageSeconds: 5);

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal(PresenceStatus.Gateway, result[0].Status);
        Assert.True(result[0].IsGateway);
    }

    [Fact]
    public void Ts_Of_Zero_Is_Treated_As_Stale_Not_Crash()
    {
        var p = new PresenceEntry { Host = "X", Ts = 0 };

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal(PresenceStatus.Stale, result[0].Status);
    }

    // ── Sort order ────────────────────────────────────────────────────────

    [Fact]
    public void SortOrder_Gateway_First_ThisInstance_Second_Then_By_Status()
    {
        var gateway = Presence(host: "gateway", mode: "gateway", ageSeconds: 5);
        var stale   = Presence(host: "stale-pc", deviceId: "d-stale", ageSeconds: 500);
        var active  = Presence(host: "active-pc", deviceId: "d-active", ageSeconds: 5);
        var idle    = Presence(host: "idle-pc", deviceId: "d-idle", ageSeconds: 200);
        var thisOne = Presence(host: "this-pc", deviceId: "d-this", ageSeconds: 5);
        var offlineNode = Node("d-offline", displayName: "offline-pc", isOnline: false);

        var result = InstanceMerger.Merge(
            new[] { offlineNode },
            new[] { stale, idle, active, thisOne, gateway },
            Options(localNodeId: "d-this"));

        var hosts = result.Select(r => r.Presence?.Host ?? r.Node?.DisplayName).ToArray();
        Assert.Equal(new[] { "gateway", "this-pc", "active-pc", "idle-pc", "stale-pc", "offline-pc" }, hosts);
    }

    [Fact]
    public void SortOrder_Within_Bucket_Is_By_DisplayName()
    {
        var a = Presence(host: "alpha", deviceId: "d-a", ageSeconds: 5);
        var b = Presence(host: "bravo", deviceId: "d-b", ageSeconds: 5);
        var c = Presence(host: "charlie", deviceId: "d-c", ageSeconds: 5);

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { c, a, b }, Options());

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, result.Select(r => r.DisplayName));
    }

    // ── This instance ──────────────────────────────────────────────────────

    [Fact]
    public void ThisInstance_Set_From_LocalNodeId_Match_On_Node()
    {
        var p = Presence(host: "PC", deviceId: "node-X");
        var n = Node("node-X", displayName: "PC");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options(localNodeId: "node-X"));

        Assert.True(result[0].IsThisInstance);
    }

    [Fact]
    public void ThisInstance_Set_From_LocalNodeId_Match_On_Presence_DeviceId()
    {
        var p = Presence(host: "PC", deviceId: "node-X");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options(localNodeId: "node-X"));

        Assert.True(result[0].IsThisInstance);
    }

    [Fact]
    public void ThisInstance_Falls_Back_To_LocalHost_When_No_NodeId()
    {
        var p = Presence(host: "BRENO-PC");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options(localHost: "BRENO-PC"));

        Assert.True(result[0].IsThisInstance);
    }

    [Fact]
    public void Gateway_Row_Is_Never_Marked_ThisInstance()
    {
        var p = Presence(host: "this-host", mode: "gateway", deviceId: "node-X");

        var result = InstanceMerger.Merge(
            nodes: null,
            presence: new[] { p },
            Options(localNodeId: "node-X", localHost: "this-host"));

        Assert.True(result[0].IsGateway);
        Assert.False(result[0].IsThisInstance);
    }

    // ── Stable key ────────────────────────────────────────────────────────

    [Fact]
    public void StableKey_Prefers_NodeId_When_Matched()
    {
        var p = Presence(host: "PC", deviceId: "device-1");
        var n = Node("real-node-id", clientId: "device-1");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Equal("n:real-node-id", result[0].Key);
    }

    [Fact]
    public void StableKey_Uses_DeviceId_When_No_Node()
    {
        var p = Presence(host: "PC", deviceId: "device-1");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal("d:device-1", result[0].Key);
    }

    [Fact]
    public void StableKey_Falls_Back_To_Host_When_All_Ids_Empty()
    {
        var p = Presence(host: "lone-host");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal("h:lone-host", result[0].Key);
    }

    // ── Null/empty safety ─────────────────────────────────────────────────

    [Fact]
    public void Merge_Handles_Both_Inputs_Null()
    {
        var result = InstanceMerger.Merge(nodes: null, presence: null);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_Handles_Empty_Inputs()
    {
        var result = InstanceMerger.Merge(Array.Empty<GatewayNodeInfo>(), Array.Empty<PresenceEntry>());
        Assert.Empty(result);
    }

    // ── Restored fields (cap/cmd counts, roles, identity, raw status) ─────

    [Fact]
    public void MatchedNode_Surfaces_CapabilityAndCommand_Counts()
    {
        var p = Presence(deviceId: "node-x", host: "PC");
        var n = Node("node-x");
        n.CapabilityCount = 9;
        n.CommandCount = 38;

        var result = InstanceMerger.Merge(new[] { n }, new[] { p }, Options());

        Assert.Equal(9, result[0].CapabilityCount);
        Assert.Equal(38, result[0].CommandCount);
    }

    [Fact]
    public void OrphanNode_Also_Surfaces_Counts()
    {
        var n = Node("offline-node");
        n.CapabilityCount = 5;
        n.CommandCount = 12;

        var result = InstanceMerger.Merge(new[] { n }, presence: null, Options());

        Assert.Equal(5, result[0].CapabilityCount);
        Assert.Equal(12, result[0].CommandCount);
    }

    [Fact]
    public void Presence_Only_Row_Has_Zero_Counts()
    {
        var p = Presence(host: "iPhone", platform: "iOS");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal(0, result[0].CapabilityCount);
        Assert.Equal(0, result[0].CommandCount);
    }

    [Fact]
    public void Roles_From_Presence_Are_Surfaced()
    {
        var p = new PresenceEntry
        {
            Host = "PC", Ts = FixedNowMs - 5000,
            Roles = new[] { "operator", "node" },
        };

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Equal(new[] { "operator", "node" }, result[0].Roles);
    }

    [Fact]
    public void Roles_Default_Empty_When_Missing()
    {
        var p = Presence(host: "PC");

        var result = InstanceMerger.Merge(nodes: null, presence: new[] { p }, Options());

        Assert.Empty(result[0].Roles);
    }

    [Fact]
    public void IdentityCaption_Prefers_InstanceId_Then_DeviceId_Then_NodeId()
    {
        var p1 = new PresenceEntry { Host = "A", InstanceId = "inst-1", DeviceId = "dev-1", Ts = FixedNowMs - 5000 };
        var p2 = new PresenceEntry { Host = "B", DeviceId = "dev-2", Ts = FixedNowMs - 5000 };
        var n = Node("node-only", displayName: "NodeOnly");

        var r1 = InstanceMerger.Merge(null, new[] { p1 }, Options())[0];
        var r2 = InstanceMerger.Merge(null, new[] { p2 }, Options())[0];
        var r3 = InstanceMerger.Merge(new[] { n }, null, Options())[0];

        Assert.Equal("inst-1", r1.IdentityCaption);
        Assert.Equal("dev-2", r2.IdentityCaption);
        Assert.Equal("node-only", r3.IdentityCaption);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("unknown", null)]
    [InlineData("Online", null)]      // redundant with PresenceStatus
    [InlineData("online", null)]
    [InlineData("pairing", "pairing")]
    [InlineData("error", "error")]
    public void NodeStatusRaw_Filters_Redundant_Values(string? input, string? expected)
    {
        var n = Node("x");
        n.Status = input ?? "";
        var result = InstanceMerger.Merge(new[] { n }, presence: null, Options());
        Assert.Equal(expected, result[0].NodeStatusRaw);
    }

    // ── Duplicate node-list entries collapse ──────────────────────────────

    [Fact]
    public void Duplicate_NodeId_Entries_Collapse_Into_One_Row()
    {
        var dup1 = Node("dup-id", displayName: "First");
        var dup2 = Node("dup-id", displayName: "Second");

        var result = InstanceMerger.Merge(new[] { dup1, dup2 }, presence: null, Options());

        Assert.Single(result);
        Assert.Same(dup1, result[0].Node);
    }

    [Fact]
    public void Duplicate_ClientId_Entries_Collapse_Into_One_Row()
    {
        // Same ClientId on two distinct node-list rows — gateway sometimes
        // echoes a node twice across reconnect races. Both should not surface
        // as separate offline cards.
        var dup1 = Node("alpha", clientId: "shared-cid", displayName: "Alpha");
        var dup2 = Node("beta", clientId: "shared-cid", displayName: "Beta");

        var result = InstanceMerger.Merge(new[] { dup1, dup2 }, presence: null, Options());

        Assert.Single(result);
        Assert.Same(dup1, result[0].Node);
    }

    // ── Single-match node consumption ─────────────────────────────────────

    [Fact]
    public void Same_Node_Does_Not_Attach_To_Multiple_Presence_Rows()
    {
        // Two presence beacons resolving to the same node (e.g. one matches
        // by DeviceId, another by InstanceId, or a reconnect race produced
        // two beacons with the same DeviceId). Only one card should expose
        // the destructive Rename/Forget surface; the other should render
        // presence-only without IsManaged.
        var p1 = Presence(deviceId: "shared-device", host: "PC", ageSeconds: 5);
        var p2 = Presence(deviceId: "shared-device", host: "PC-alt", ageSeconds: 10);
        var n = Node("shared-device", displayName: "Workstation");

        var result = InstanceMerger.Merge(new[] { n }, new[] { p1, p2 }, Options());

        Assert.Equal(2, result.Count);
        var managed = result.Count(r => r.IsManaged);
        Assert.Equal(1, managed);
    }

    // ── Cross-namespace dedup safety ──────────────────────────────────────

    [Fact]
    public void Dedup_Does_Not_Drop_Node_When_Its_ClientId_Equals_Another_NodeId()
    {
        // Pathological gateway echo: Node B's ClientId happens to equal
        // Node A's NodeId. Both rows must survive — the NodeId namespace
        // and the ClientId namespace are independent.
        var a = Node("shared-token", displayName: "Alpha");
        var b = Node("beta", clientId: "shared-token", displayName: "Beta");

        var result = InstanceMerger.Merge(new[] { a, b }, presence: null, Options());

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Node is { } n && n.NodeId == "shared-token");
        Assert.Contains(result, r => r.Node is { } n && n.NodeId == "beta");
    }

    // ── Two-pass match priority ───────────────────────────────────────────

    [Fact]
    public void Strong_DeviceId_Match_Wins_Over_Earlier_Weak_Host_Match()
    {
        // P1 arrives first and only has a host. It would weak-match node A
        // via host↔displayName. P2 arrives second with a strong DeviceId
        // that points directly at A. The strong match must win even though
        // P1 is iterated first — the two-pass merger does all strong matches
        // before any weak fallback runs.
        var p1 = Presence(host: "laptop", deviceId: null, ageSeconds: 5);
        var p2 = Presence(host: "laptop-other", deviceId: "alpha", ageSeconds: 10);
        var nodeA = Node("alpha", displayName: "laptop");

        var result = InstanceMerger.Merge(new[] { nodeA }, new[] { p1, p2 }, Options());

        Assert.Equal(2, result.Count);
        var managedRow = Assert.Single(result, r => r.IsManaged);
        // The managed row must be the one with the strong DeviceId match (p2),
        // not the one that only matched by host (p1).
        Assert.Equal("alpha", managedRow.Presence?.DeviceId);
    }
}
