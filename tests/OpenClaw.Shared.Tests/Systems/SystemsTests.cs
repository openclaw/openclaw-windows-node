using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;

namespace OpenClaw.Shared.Tests.Systems;

public class SystemsParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("available", SystemStatus.Online)]
    [InlineData("unavailable", SystemStatus.Offline)]
    [InlineData("starting", SystemStatus.Starting)]
    [InlineData("stopping", SystemStatus.Stopping)]
    [InlineData("error", SystemStatus.Error)]
    [InlineData("future-state", SystemStatus.Unknown)]
    [InlineData(null, SystemStatus.Unknown)]
    public void ParseStatus_MapsEveryGatewayRuntimeState(string? raw, SystemStatus expected) =>
        Assert.Equal(expected, SystemsParser.ParseStatus(raw));

    [Fact]
    public void ParseEnvironments_ReadsMachineMetadataAndSkipsMalformedRows()
    {
        var environments = SystemsParser.ParseEnvironments(Json("""
        {"environments":[
          {"id":"gateway","type":"local","status":"available","desktopSetup":{"state":"needs-server","detail":"no vnc"}},
          {"id":"node:studio","type":"node","label":"Studio","status":"unavailable","platform":"windows",
           "sessionHost":true,"workerSlots":{"total":4,"available":1},"lastSeenAtMs":1700000000000,"lastSeenReason":"periodic",
           "capabilities":["system","",3],"invocableCommands":["system.run"],"desktop":true,"desktopAvailability":{"state":"locked"},
           "issues":[{"code":"update-required"},{"message":"no code"}]},
          {"id":"worker:p:abc","type":"worker","status":"starting",
           "worker":{"providerId":"p","profileId":"gpu","state":"provisioning","attachedSessionIds":["s1"],"tunnelStatus":"connecting","ageMs":5}},
          {"type":"node","status":"available"},
          "not-an-object"
        ]}
        """));

        // Setup probes, worker slots and attached session ids are web-only details and are ignored.
        Assert.Equal(["gateway", "node:studio", "worker:p:abc"], environments.Select(e => e.Id));
        var host = environments[0];
        Assert.Null(host.NodeId);
        Assert.Null(host.Desktop);

        var studio = environments[1];
        Assert.Equal("studio", studio.NodeId);
        Assert.Equal(SystemStatus.Offline, studio.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), studio.LastSeenAt);
        Assert.Equal(["system"], studio.Capabilities);
        Assert.True(studio.Desktop);
        Assert.Equal(DesktopLockState.Locked, studio.DesktopAvailability);
        Assert.Equal("update-required", Assert.Single(studio.Issues).Code);

        var worker = environments[2].Worker!;
        Assert.Equal(("p", "gpu", "provisioning", "connecting"), (worker.ProviderId, worker.ProfileId, worker.State, worker.TunnelStatus));
    }

    [Fact]
    public void ParseEnvironments_MissingListIsAProtocolError() =>
        Assert.Throws<InvalidOperationException>(() => SystemsParser.ParseEnvironments(Json("""{"profiles":[]}""")));

    [Fact]
    public void ParseSystemInfo_ReadsIdentityAndMeasurements()
    {
        var info = SystemsParser.ParseSystemInfo(Json("""
        {"machineName":"Gateway","hostname":"gw","platform":"linux","release":"6.8","arch":"x64","osLabel":"Ubuntu 24.04",
         "lanAddress":"10.0.0.2","port":18789,"nodeVersion":"v22","pid":1,"uptimeMs":3600000,"cpuCount":8,"cpuModel":"Xeon",
         "loadAverage":[1.25,1.0,0.5],"memoryTotalBytes":100,"memoryFreeBytes":30,
         "disks":[{"path":"/","totalBytes":50,"availableBytes":20},{"path":"","totalBytes":1,"availableBytes":1}]}
        """));

        Assert.NotNull(info);
        Assert.Equal(("Gateway", "Ubuntu 24.04", 18789), (info!.MachineName, info.OsLabel, info.Port!.Value));
        Assert.Equal(1.25, info.Measurements.Load1);
        Assert.Equal(70, info.Measurements.MemoryUsedBytes);
        Assert.Equal(new DiskStats("/", 50, 20), Assert.Single(info.Measurements.Disks!));
    }

    [Fact]
    public void ParseNodeHostStats_RequiresReceiptTimeAndCoreMeasurements()
    {
        Assert.Null(SystemsParser.ParseNodeHostStats(Json("""{"cpuCount":2,"memoryTotalBytes":1,"memoryFreeBytes":1}""")));
        Assert.Null(SystemsParser.ParseNodeHostStats(Json("""{"cpuCount":2,"memoryTotalBytes":1,"updatedAtMs":5}""")));
        var stats = SystemsParser.ParseNodeHostStats(Json("""{"cpuCount":2,"memoryTotalBytes":10,"memoryFreeBytes":4,"updatedAtMs":5}"""));
        Assert.Equal(6, stats!.Measurements.MemoryUsedBytes);
        Assert.Null(stats.Measurements.Load1);
    }

    [Theory]
    [InlineData(0, 100, 50)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 100, -1)]
    [InlineData(2, 100, 101)]
    public void ParseMeasurements_InvalidCoreValuesAreUnavailable(int cores, long total, long free)
    {
        var payload = JsonSerializer.SerializeToElement(new { cpuCount = cores, memoryTotalBytes = total, memoryFreeBytes = free });
        Assert.Null(SystemsParser.ParseMeasurements(payload));
    }

    [Fact]
    public void ParseMeasurements_InvalidOptionalValuesDoNotBecomeZeroUsage()
    {
        var stats = SystemsParser.ParseMeasurements(Json("""
            {"cpuCount":2,"memoryTotalBytes":100,"memoryFreeBytes":20,"loadAverage":[-1,0,0],
             "diskTotalBytes":10,"diskAvailableBytes":11,
             "disks":[{"path":"invalid","totalBytes":10,"availableBytes":-1},{"path":"valid","totalBytes":10,"availableBytes":0}]}
            """))!;
        Assert.Equal(80, stats.MemoryUsedBytes);
        Assert.Null(stats.Load1);
        Assert.Null(stats.DiskAvailableBytes);
        Assert.Null(stats.DiskTotalBytes);
        Assert.Equal(new DiskStats("valid", 10, 0), Assert.Single(stats.Disks!));
    }

    [Theory]
    [InlineData("""{"placement":{"state":"active","environmentId":"worker:a","runner":{"deviceId":"d"}}}""", SessionPlacementState.Active, "worker:a", "d")]
    [InlineData("""{"placement":{"state":"reclaimed","environmentId":"node:x"}}""", SessionPlacementState.Reclaimed, "node:x", null)]
    [InlineData("""{"placement":{"state":"new-state"}}""", SessionPlacementState.Unknown, null, null)]
    public void ParseSessionPlacement_ReadsStateEnvironmentAndRunner(string row, SessionPlacementState state, string? environment, string? runner) =>
        Assert.Equal(new SessionPlacementInfo(state, environment, runner), SystemsParser.ParseSessionPlacement(Json(row)));

    [Fact]
    public void ParseSessionPlacement_AbsentPlacementIsNull() =>
        Assert.Null(SystemsParser.ParseSessionPlacement(Json("""{"key":"k"}""")));
}

public class SystemsProjectionTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static SystemEnvironment Env(string id, string type = "node", SystemStatus status = SystemStatus.Online,
        string? label = null, WorkerMetadata? worker = null) =>
        new() { Id = id, Type = type, Status = status, Label = label, Worker = worker };

    private static GatewayNodeInfo Node(string id, string? name = null) => new()
    {
        NodeId = id,
        DisplayName = name ?? id,
        HasExplicitDisplayName = name is not null,
        IsOnline = true,
    };

    private static SystemsSessionRef Session(string key, SessionPlacementInfo? placement = null, string? execNode = null) =>
        new(key, "Title " + key, placement, execNode);

    private static SystemsProjectionOptions Options => new() { NowUtc = Now };

    [Fact]
    public void BuildRelations_RecordsBindingsByPlacementStateAndExecTarget()
    {
        var relations = SystemsProjection.BuildRelations(
        [
            Session("placed", new(SessionPlacementState.Active, "worker:w", "studio")),
            Session("draining", new(SessionPlacementState.Draining, "worker:w", "studio")),
            Session("retained", new(SessionPlacementState.Reclaimed, "node:studio", null)),
            Session("failed", new(SessionPlacementState.Failed, "node:studio", null)),
            Session("requested", new(SessionPlacementState.Requested, "worker:w", "studio")),
            Session("exec", execNode: " mac "),
            Session("local", new(SessionPlacementState.Local, null, null)),
            Session("plain"),
        ]);

        Assert.Equal(
            [("placed", SessionRelationKind.Placement), ("draining", SessionRelationKind.Placement)],
            relations["worker:w"].Select(r => (r.Session.Key, r.Kind)));
        // Only an active placement makes its runner device a related machine.
        Assert.Equal([("placed", SessionRelationKind.Runner), ("retained", SessionRelationKind.RetainedPlacement),
            ("failed", SessionRelationKind.RetainedPlacement)], relations["node:studio"].Select(r => (r.Session.Key, r.Kind)));
        Assert.Equal(("exec", SessionRelationKind.ExecBinding), relations["node:mac"].Select(r => (r.Session.Key, r.Kind)).Single());
        Assert.Equal(["local", "plain"], relations["gateway"].Select(r => r.Session.Key));
        Assert.DoesNotContain(relations.Values.SelectMany(r => r), r => r.Session.Key == "requested");
    }

    [Fact]
    public void Project_JoinsExactIdentitiesAndClassifiesKinds()
    {
        var snapshot = new SystemsInventorySnapshot
        {
            Environments =
            [
                Env("gateway", "local"),
                Env("node:studio", label: null),
                Env("worker:p:abc123def", "worker", worker: new WorkerMetadata("p", "gpu", "ready", "connected", null, null)),
                Env("worker:p:gone", "worker", SystemStatus.Offline, worker: new WorkerMetadata("p", "gpu", "destroyed", "stopped", null, null)),
                Env("worker:p:failed", "worker", SystemStatus.Error, worker: new WorkerMetadata("p", "gpu", "failed", "stopped", null, null)),
            ],
            Nodes = [Node("studio", "Studio PC"), Node("suppressed", "Hidden")],
            SystemInfo = new GatewaySystemInfo { MachineName = "Gateway Box", OsLabel = "Ubuntu", Measurements = new HostMeasurements { CpuCount = 8 } },
            SystemInfoSampledAt = new DateTimeOffset(Now),
        };

        var rows = SystemsProjection.Project(snapshot, [Session("s1", new(SessionPlacementState.Active, "worker:p:abc123def", null))], Options);

        Assert.Equal(["gateway", "node:studio", "worker:p:abc123def"], rows.Select(r => r.Id));
        Assert.Equal([SystemKind.Host, SystemKind.Node, SystemKind.Worker], rows.Select(r => r.Kind));
        Assert.Equal("Gateway Box", rows[0].Name);
        Assert.Equal("Ubuntu", rows[0].Platform);
        Assert.Equal(8, rows[0].Measurements!.CpuCount);
        Assert.Equal("Studio PC", rows[1].Name);
        Assert.Same(snapshot.Nodes[0], rows[1].Node);
        // A worker is named after the session placed on it.
        Assert.Equal("Title s1", rows[2].Name);
        // node.list entries absent from environments.list are never resurrected.
        Assert.DoesNotContain(rows, r => r.Node?.NodeId == "suppressed");
    }

    [Fact]
    public void Project_NamesFallBackThroughLabelProfileAndIdentifier()
    {
        var snapshot = new SystemsInventorySnapshot
        {
            Environments =
            [
                Env("gateway", "local"),
                Env("worker:p:abc", "worker", worker: new WorkerMetadata("p", "gpu", "ready", "connected", null, null)),
                Env("worker:q:def", "worker", worker: new WorkerMetadata("q", null, "ready", "connected", null, null)),
                Env("node:labelled", label: "Label"),
            ],
        };

        var rows = SystemsProjection.Project(snapshot, [], Options);

        Assert.Null(rows[0].Name);
        Assert.Equal("p · gpu", rows[1].Name);
        Assert.Equal("worker:q:def", rows[2].Name);
        Assert.Equal("Label", rows[3].Name);
        Assert.Equal("abc", SystemsProjection.ShortId("worker:p:abc"));
        Assert.Equal("abcdef", SystemsProjection.ShortId("worker:p:abcdefghij"));
    }

    [Fact]
    public void Project_KeepsPresenceOnlyClientsAndAttachesPresenceToNodes()
    {
        var nowMs = new DateTimeOffset(Now).ToUnixTimeMilliseconds();
        var snapshot = new SystemsInventorySnapshot
        {
            Environments = [Env("gateway", "local"), Env("node:studio")],
            Nodes = [Node("studio", "Studio")],
            Presence =
            [
                new PresenceEntry { Host = "gw", Mode = "gateway", Ts = nowMs },
                new PresenceEntry { Host = "Studio", DeviceId = "studio", Mode = "node", Ts = nowMs - 10_000, Reason = "periodic" },
                new PresenceEntry { Host = "Phone", InstanceId = "phone-1", Mode = "ios", Platform = "ios 18", Ts = nowMs - 30_000 },
                new PresenceEntry { Host = "Old tab", InstanceId = "tab-1", Mode = "webchat", Ts = nowMs - 3_600_000 },
            ],
        };

        var rows = SystemsProjection.Project(snapshot, [], new SystemsProjectionOptions { NowUtc = Now, LocalNodeId = "studio" });

        Assert.Equal("gw", rows.Single(r => r.Id == "gateway").Instance!.DisplayName);
        var studio = rows.Single(r => r.Id == "node:studio");
        Assert.Equal("periodic", studio.Instance!.Reason);
        Assert.True(studio.IsThisDevice);
        Assert.True(studio.CanManageNode);
        var clients = rows.Where(r => r.Kind == SystemKind.Client).ToArray();
        Assert.Equal(["Old tab", "Phone"], clients.Select(c => c.Name).Order());
        Assert.Equal(SystemStatus.Online, clients.Single(c => c.Name == "Phone").Status);
        Assert.Equal("iOS 18", clients.Single(c => c.Name == "Phone").Platform);
        // A beacon past the idle window is shown as offline, not hidden.
        Assert.Equal(SystemStatus.Offline, clients.Single(c => c.Name == "Old tab").Status);
    }

    [Fact]
    public void Project_JudgesPresenceFreshnessOnTheGatewayClock()
    {
        // The Gateway clock runs an hour behind; its beacon was stamped seconds ago on that clock.
        var skew = TimeSpan.FromHours(1);
        var beacon = new DateTimeOffset(Now - skew).ToUnixTimeMilliseconds() - 5_000;
        var snapshot = new SystemsInventorySnapshot
        {
            Environments = [Env("gateway", "local")],
            Presence = [new PresenceEntry { Host = "Phone", InstanceId = "phone-1", Mode = "ios", Ts = beacon }],
        };

        var rows = SystemsProjection.Project(snapshot, [], new SystemsProjectionOptions { NowUtc = Now, GatewayClockOffset = skew });

        Assert.Equal(SystemStatus.Online, rows.Single(r => r.Kind == SystemKind.Client).Status);
    }

    [Fact]
    public void Project_LegacyGatewaySynthesizesInventoryFromNodeList()
    {
        var offline = Node("mac", "MacBook");
        offline.IsOnline = false;
        offline.ConnectedAt = new DateTime(2026, 10, 27, 8, 30, 0, DateTimeKind.Utc);
        var snapshot = new SystemsInventorySnapshot { Environments = null, Nodes = [Node("studio", "Studio"), offline] };

        var rows = SystemsProjection.Project(snapshot, [], Options with { GatewayConnected = false });

        Assert.Equal(["gateway", "node:studio", "node:mac"], rows.Select(r => r.Id));
        Assert.Equal([SystemStatus.Offline, SystemStatus.Online, SystemStatus.Offline], rows.Select(r => r.Status));
        Assert.Equal("MacBook", rows[2].Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 8, 30, 0, TimeSpan.Zero), rows[2].Environment!.LastConnectedAt);
        Assert.Null(rows[1].Environment!.LastConnectedAt);
    }

    [Theory]
    [InlineData(true, new[] { "alpha", "beta 2", "beta 10", "aardvark", "building", "client", "error", "stopping", "unknown" })]
    [InlineData(false, new[] { "aardvark", "alpha", "beta 2", "beta 10", "building", "client", "error", "stopping", "unknown" })]
    public void OrderForInventory_KeepsAllKindsAndStatusesUsingEffectiveOnlineFirstOrder(bool connected, string[] expected)
    {
        SystemRow Row(string name, SystemStatus status, SystemKind kind = SystemKind.Node) =>
            new() { Id = name, Kind = kind, Status = status, Name = name };
        var rows = new[]
        {
            Row("unknown", SystemStatus.Unknown),
            Row("aardvark", SystemStatus.Offline),
            Row("beta 10", SystemStatus.Online),
            Row("alpha", SystemStatus.Online, SystemKind.Host),
            Row("error", SystemStatus.Error),
            Row("client", SystemStatus.Offline, SystemKind.Client),
            Row("building", SystemStatus.Starting, SystemKind.Worker),
            Row("beta 2", SystemStatus.Online),
            Row("stopping", SystemStatus.Stopping),
        };

        var result = SystemsProjection.OrderForInventory(rows, row => row.Name!, gatewayConnected: connected);

        Assert.Equal(expected, result.Select(row => row.Id));
    }

    [Fact]
    public void NeedsAttention_UsesReportedMachineIssuesNotPresenceOrDisabledPermissions()
    {
        SystemRow[] rows =
        [
            new() { Id = "node:offline", Kind = SystemKind.Node, Status = SystemStatus.Offline },
            new() { Id = "node:pending", Kind = SystemKind.Node, Status = SystemStatus.Online,
                Node = new GatewayNodeInfo { NodeId = "pending", ApprovalState = GatewayNodeApprovalState.PendingReapproval } },
            new() { Id = "node:update", Kind = SystemKind.Node, Status = SystemStatus.Online,
                Environment = new SystemEnvironment { Id = "node:update", Type = "node", Issues = [new("update-required", null)] } },
            new() { Id = "node:allowed-choice", Kind = SystemKind.Node, Status = SystemStatus.Online,
                Node = new GatewayNodeInfo { NodeId = "allowed-choice", Permissions = new() { ["screen"] = false } } },
            new() { Id = "client:offline", Kind = SystemKind.Client, Status = SystemStatus.Offline },
        ];

        Assert.Equal([true, true, true, false, false], rows.Select(row => row.NeedsAttention));
    }

    [Fact]
    public void Project_GroupsOnlyTheGatewayEnvironmentAsHostAndOtherTypesAsWorkers()
    {
        var snapshot = new SystemsInventorySnapshot
        {
            Environments =
            [
                new SystemEnvironment { Id = "gateway", Type = "local", Status = SystemStatus.Online },
                new SystemEnvironment { Id = "sandbox:1", Type = "sandbox", Status = SystemStatus.Online },
            ],
        };

        var rows = SystemsProjection.Project(snapshot, [], Options);

        Assert.Equal([SystemKind.Host, SystemKind.Worker], rows.Select(r => r.Kind));
    }

    [Fact]
    public void DefaultSelection_PrefersGatewayThenFirstRow()
    {
        SystemRow[] rows =
        [
            new() { Id = "node:a", Kind = SystemKind.Node, Status = SystemStatus.Online },
            new() { Id = "gateway", Kind = SystemKind.Host, Status = SystemStatus.Online },
        ];

        Assert.Equal("gateway", SystemsProjection.DefaultSelection(rows));
        Assert.Equal("node:a", SystemsProjection.DefaultSelection([rows[0]]));
        Assert.Null(SystemsProjection.DefaultSelection([]));
    }

    [Theory]
    [InlineData("windows 11", null, "Windows 11")]
    [InlineData("macos 15.1", null, "macOS 15.1")]
    [InlineData("darwin", null, "macOS")]
    [InlineData("ipados 18", null, "iPadOS 18")]
    [InlineData(null, "iPhone", "iPhone")]
    [InlineData(null, null, null)]
    public void PlatformLabels_ProducesReadableNames(string? raw, string? family, string? expected) =>
        Assert.Equal(expected, PlatformLabels.Pretty(raw, family));
}

public class SystemsFreshnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static SystemRow Row(string id, DateTimeOffset? at, SystemStatus status = SystemStatus.Online) => new()
    {
        Id = id,
        Kind = id == "gateway" ? SystemKind.Host : SystemKind.Node,
        Status = status,
        MeasuredAt = at,
        Measurements = at is null ? null : new HostMeasurements { CpuCount = 1, MemoryTotalBytes = 100, MemoryFreeBytes = 90 },
    };

    [Fact]
    public void Meters_ClampOverloadAndRejectMissingTotals()
    {
        var stats = new HostMeasurements { CpuCount = 8, Load1 = 12, MemoryTotalBytes = 64, MemoryFreeBytes = 16 };
        Assert.Equal(100, SystemsMeters.LoadPercent(stats));
        Assert.Null(SystemsMeters.LoadPercent(stats with { CpuCount = 0 }));
        Assert.Null(SystemsMeters.MemoryUsedPercent(new HostMeasurements()));
        Assert.Null(SystemsMeters.DiskUsedPercent(400, null));
    }

    [Fact]
    public void IsLastKnown_CoversDisconnectOfflineFailedRefreshAndAge()
    {
        var fresh = Row("node:a", T0);
        Assert.False(SystemsFreshness.IsLastKnown(fresh, connected: true, refreshFailed: false, T0.AddSeconds(30)));
        Assert.True(SystemsFreshness.IsLastKnown(fresh, connected: false, refreshFailed: false, T0));
        Assert.True(SystemsFreshness.IsLastKnown(fresh, connected: true, refreshFailed: true, T0));
        Assert.True(SystemsFreshness.IsLastKnown(Row("node:a", T0, status: SystemStatus.Offline), true, false, T0));
        Assert.True(SystemsFreshness.IsLastKnown(fresh, true, false, T0.AddMinutes(3)));
        // The Gateway host reports more often, so its readings go stale sooner than a node's.
        Assert.True(SystemsFreshness.IsLastKnown(Row("gateway", T0), true, false, T0.AddSeconds(45)));
        Assert.False(SystemsFreshness.IsLastKnown(fresh, true, false, T0.AddSeconds(45)));
    }
}

public class DesktopPresentationTests
{
    [Fact]
    public void Resolve_UnreportedDesktopDoesNotClaimSetupIsMissing()
    {
        var row = Row("gateway", desktop: false);
        Assert.Null(DesktopPresentation.Resolve(row with { Environment = row.Environment! with { Desktop = null } }, connected: true));
    }

    private static SystemRow Row(string id, bool desktop, SystemStatus status = SystemStatus.Online, bool workerDesktop = false) => new()
    {
        Id = id,
        Kind = id == "gateway" ? SystemKind.Host : SystemKind.Node,
        Status = status,
        Environment = new SystemEnvironment
        {
            Id = id,
            Type = id == "gateway" ? "local" : "node",
            Status = status,
            Desktop = desktop,
            Worker = workerDesktop ? new WorkerMetadata("p", null, "attached", "connected", null, true) : null,
        },
    };

    [Fact]
    public void Resolve_ReportsAvailabilityOnlyForAdvertisedDesktops()
    {
        Assert.Equal(DesktopSectionKind.Available, DesktopPresentation.Resolve(Row("node:a", desktop: true), connected: true));
        Assert.Equal(DesktopSectionKind.Available,
            DesktopPresentation.Resolve(Row("worker:p:a", desktop: false, workerDesktop: true), connected: true));
        Assert.Equal(DesktopSectionKind.NotReady,
            DesktopPresentation.Resolve(Row("node:a", desktop: true, SystemStatus.Starting), connected: true));
        Assert.Equal(DesktopSectionKind.Offline,
            DesktopPresentation.Resolve(Row("node:a", desktop: true, SystemStatus.Offline), connected: true));
        // A disconnected Gateway cannot vouch for a desktop being reachable.
        Assert.Equal(DesktopSectionKind.Offline, DesktopPresentation.Resolve(Row("node:a", desktop: true), connected: false));
        // Machines without a desktop show no desktop section at all.
        Assert.Null(DesktopPresentation.Resolve(Row("node:a", desktop: false), connected: true));
    }

    [Fact]
    public void Resolve_GatewayHostWithoutDesktopPointsToWebSetup()
    {
        Assert.Equal(DesktopSectionKind.HostNotSetUp, DesktopPresentation.Resolve(Row("gateway", desktop: false), connected: true));
        Assert.Equal(DesktopSectionKind.Available, DesktopPresentation.Resolve(Row("gateway", desktop: true), connected: true));
        Assert.Null(DesktopPresentation.Resolve(Row("gateway", desktop: false), connected: false));
    }
}

public class SystemsGatewayClientTests
{
    private sealed class FakeTransport
    {
        public List<(string Method, string Params)> Calls { get; } = new();
        public Dictionary<string, Func<JsonElement>> Responses { get; } = new();

        public Task<JsonElement> Request(string method, object? parameters, int timeoutMs)
        {
            lock (Calls)
                Calls.Add((method, JsonSerializer.Serialize(parameters)));
            if (!Responses.TryGetValue(method, out var respond))
                return Task.FromException<JsonElement>(new InvalidOperationException("unknown method: " + method));
            try
            {
                return Task.FromResult(respond());
            }
            catch (Exception ex)
            {
                return Task.FromException<JsonElement>(ex);
            }
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task LoadInventory_ReportsAuxiliaryFailuresWithoutFailingTheInventory()
    {
        var transport = new FakeTransport();
        transport.Responses["environments.list"] = () => Json("""{"environments":[{"id":"gateway","type":"local","status":"available"}]}""");
        transport.Responses["node.list"] = () => throw new InvalidOperationException("node.list timed out");
        var client = new SystemsGatewayClient(transport.Request);

        var snapshot = await client.LoadInventoryAsync(CancellationToken.None);

        Assert.Single(snapshot.Environments!);
        Assert.Empty(snapshot.Nodes);
        Assert.Equal("node.list timed out", snapshot.Errors.Nodes);
        // An unknown system.info method is an older Gateway, not an error.
        Assert.Null(snapshot.Errors.SystemInfo);
        // The native page never asks the Gateway to probe host desktop setup.
        Assert.Contains(transport.Calls, call => call.Method == "environments.list" && call.Params == "{}");
    }

    [Fact]
    public async Task LoadInventory_MissingEnvironmentsMethodUsesLegacyInventory()
    {
        var transport = new FakeTransport();
        transport.Responses["node.list"] = () => Json("""{"nodes":[{"nodeId":"studio","connected":true}]}""");
        var snapshot = await new SystemsGatewayClient(transport.Request).LoadInventoryAsync(CancellationToken.None);

        Assert.Null(snapshot.Environments);
        Assert.Equal("studio", Assert.Single(snapshot.Nodes).NodeId);
    }

    [Fact]
    public async Task LoadInventory_InventoryFailureThrows()
    {
        var transport = new FakeTransport();
        transport.Responses["environments.list"] = () => throw new InvalidOperationException("permission denied");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SystemsGatewayClient(transport.Request).LoadInventoryAsync(CancellationToken.None));
        Assert.Equal("permission denied", error.Message);
    }

    [Fact]
    public async Task SystemInfo_OlderGatewayIsUnsupportedNotAnError()
    {
        var transport = new FakeTransport();
        var client = new SystemsGatewayClient(transport.Request);
        Assert.Null(await client.ReadSystemInfoAsync(CancellationToken.None));

        transport.Responses["system.info"] = () => throw new InvalidOperationException("missing scope: operator.read");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadSystemInfoAsync(CancellationToken.None));
        Assert.Equal("missing scope: operator.read", error.Message);
    }
}

public class HostStatsSamplerTests
{
    [Fact]
    public void Sample_MatchesTheGatewayHostStatsSchema()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var payload = HostStatsSampler.Sample();
        Assert.NotNull(payload);
        var allowed = new[] { "cpuCount", "memoryTotalBytes", "memoryFreeBytes", "diskTotalBytes", "diskAvailableBytes" };
        Assert.All(payload!.Select(p => p.Key), key => Assert.Contains(key, allowed));
        Assert.InRange(payload["cpuCount"]!.GetValue<int>(), 1, 4096);
        Assert.True(payload["memoryFreeBytes"]!.GetValue<long>() <= payload["memoryTotalBytes"]!.GetValue<long>());
        // Disk values are sent as a pair or not at all.
        Assert.Equal(payload.ContainsKey("diskTotalBytes"), payload.ContainsKey("diskAvailableBytes"));
    }
}
