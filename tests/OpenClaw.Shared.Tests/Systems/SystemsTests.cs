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

        Assert.Equal(["gateway", "node:studio", "worker:p:abc"], environments.Select(e => e.Id));
        var host = environments[0];
        Assert.Equal(new DesktopSetup(DesktopSetupState.NeedsServer, "no vnc"), host.DesktopSetup);
        Assert.Null(host.NodeId);

        var studio = environments[1];
        Assert.Equal("studio", studio.NodeId);
        Assert.Equal(SystemStatus.Offline, studio.Status);
        Assert.True(studio.SessionHost);
        Assert.Equal(new WorkerSlots(4, 1), studio.WorkerSlots);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), studio.LastSeenAt);
        Assert.Equal(["system"], studio.Capabilities);
        Assert.True(studio.Desktop);
        Assert.Equal(DesktopLockState.Locked, studio.DesktopAvailability);
        Assert.Equal("update-required", Assert.Single(studio.Issues).Code);

        var worker = environments[2].Worker!;
        Assert.Equal(("p", "gpu", "provisioning", "connecting"), (worker.ProviderId, worker.ProfileId, worker.State, worker.TunnelStatus));
        Assert.Equal(["s1"], worker.AttachedSessionIds);
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
    [InlineData("""{"placement":{"state":"active","environmentId":"worker:a","runner":{"deviceId":"d"}}}""", SessionPlacementState.Active, "worker:a", "d")]
    [InlineData("""{"placement":{"state":"reclaimed","environmentId":"node:x"}}""", SessionPlacementState.Reclaimed, "node:x", null)]
    [InlineData("""{"placement":{"state":"new-state"}}""", SessionPlacementState.Unknown, null, null)]
    public void ParseSessionPlacement_ReadsStateEnvironmentAndRunner(string row, SessionPlacementState state, string? environment, string? runner) =>
        Assert.Equal(new SessionPlacementInfo(state, environment, runner), SystemsParser.ParseSessionPlacement(Json(row)));

    [Fact]
    public void ParseSessionPlacement_AbsentPlacementIsNull() =>
        Assert.Null(SystemsParser.ParseSessionPlacement(Json("""{"key":"k"}""")));

    [Fact]
    public void ParseBackupStatus_ReadsTargetsSchedulesAndLocations()
    {
        var status = SystemsParser.ParseBackupStatus(Json("""
        {"targets":[
           {"kind":"git","target":"repo","latest":{"id":"1","createdAt":10,"archivePath":"a","status":"ok","kind":"git","pushFailed":true},
            "latestOk":{"id":"0","createdAt":5,"archivePath":"a","status":"ok","kind":"git","bytes":42}},
           {"kind":"archive","target":"broken"}
         ],
         "schedules":[{"id":"s","mode":"git","target":"repo","enabled":true,"everyMs":1,"nextRunAtMs":99}],
         "locations":[{"name":"b2","provider":"b2","displayTarget":"b2://x","encrypted":true}]}
        """));

        var target = Assert.Single(status.Targets);
        Assert.Equal(BackupKind.Git, target.Kind);
        Assert.True(target.Latest.Ok);
        Assert.True(target.Latest.PushFailed);
        Assert.Equal(42, target.LatestOk!.Bytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(99), Assert.Single(status.Schedules).NextRunAt);
        Assert.Equal(new StorageLocation("b2", "b2", "b2://x", true), Assert.Single(status.Locations));
    }

    [Theory]
    [InlineData("ok", StorageProbeState.Ok)]
    [InlineData("unavailable", StorageProbeState.Unavailable)]
    [InlineData("uninitialized", StorageProbeState.Uninitialized)]
    [InlineData("wrong-key", StorageProbeState.WrongKey)]
    [InlineData("error", StorageProbeState.Error)]
    [InlineData("unexpected", StorageProbeState.Error)]
    public void ParseStorageProbe_MapsEveryState(string state, StorageProbeState expected)
    {
        var result = SystemsParser.ParseStorageProbe(Json($$"""{"state":"{{state}}","freeBytes":7,"message":"m"}"""));
        Assert.Equal(new StorageProbeResult(expected, 7, null, "m"), result);
    }

    [Theory]
    [InlineData("""{"config":{"desktop":{"host":{"enabled":true}}}}""", true)]
    [InlineData("""{"desktop":{"host":{"enabled":true}}}""", true)]
    [InlineData("""{"config":{"desktop":{"host":{"enabled":false}}}}""", false)]
    [InlineData("""{"config":{"desktop":{}}}""", false)]
    public void IsHostDesktopEnabled_ReadsConfigSnapshot(string config, bool expected) =>
        Assert.Equal(expected, SystemsParser.IsHostDesktopEnabled(Json(config)));
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
                Env("worker:p:abc123def", "worker", worker: new WorkerMetadata("p", "gpu", "ready", [], "connected", null, null)),
                Env("worker:p:gone", "worker", SystemStatus.Offline, worker: new WorkerMetadata("p", "gpu", "destroyed", [], "stopped", null, null)),
                Env("worker:p:failed", "worker", SystemStatus.Error, worker: new WorkerMetadata("p", "gpu", "failed", [], "stopped", null, null)),
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
                Env("worker:p:abc", "worker", worker: new WorkerMetadata("p", "gpu", "ready", [], "connected", null, null)),
                Env("worker:q:def", "worker", worker: new WorkerMetadata("q", null, "ready", [], "connected", null, null)),
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
        var snapshot = new SystemsInventorySnapshot { Environments = null, Nodes = [Node("studio", "Studio"), offline] };

        var rows = SystemsProjection.Project(snapshot, [], Options with { GatewayConnected = false });

        Assert.Equal(["gateway", "node:studio", "node:mac"], rows.Select(r => r.Id));
        Assert.Equal([SystemStatus.Offline, SystemStatus.Online, SystemStatus.Offline], rows.Select(r => r.Status));
        Assert.Equal("MacBook", rows[2].Name);
    }

    [Theory]
    [InlineData(SystemsSortMode.Alphabetical, SystemsStatusFilter.All, "", new[] { "Alpha", "beta 2", "beta 10", "Gamma" })]
    [InlineData(SystemsSortMode.OnlineFirst, SystemsStatusFilter.All, "", new[] { "Alpha", "beta 10", "beta 2", "Gamma" })]
    [InlineData(SystemsSortMode.OfflineFirst, SystemsStatusFilter.All, "", new[] { "beta 2", "Gamma", "Alpha", "beta 10" })]
    [InlineData(SystemsSortMode.Alphabetical, SystemsStatusFilter.Online, "", new[] { "Alpha", "beta 10" })]
    [InlineData(SystemsSortMode.Alphabetical, SystemsStatusFilter.Offline, "", new[] { "beta 2", "Gamma" })]
    [InlineData(SystemsSortMode.Alphabetical, SystemsStatusFilter.All, "BETA", new[] { "beta 2", "beta 10" })]
    [InlineData(SystemsSortMode.Alphabetical, SystemsStatusFilter.All, "linux", new[] { "Gamma" })]
    public void Query_FiltersSearchesAndSortsWithNumericNames(SystemsSortMode sort, SystemsStatusFilter filter, string query, string[] expected)
    {
        SystemRow Row(string name, SystemStatus status, string? platform = null) =>
            new() { Id = "node:" + name, Kind = SystemKind.Node, Status = status, Name = name, Platform = platform };
        var rows = new[]
        {
            Row("Gamma", SystemStatus.Offline, "Linux"),
            Row("beta 10", SystemStatus.Online),
            Row("Alpha", SystemStatus.Online),
            Row("beta 2", SystemStatus.Offline),
        };

        var result = SystemsProjection.Query(rows, query, filter, sort, row => row.Name!);

        Assert.Equal(expected, result.Select(r => r.Name));
    }

    [Fact]
    public void Query_WhileDisconnectedTreatsEveryRowAsOffline()
    {
        SystemRow[] rows =
        [
            new() { Id = "node:a", Kind = SystemKind.Node, Status = SystemStatus.Online, Name = "A" },
            new() { Id = "node:b", Kind = SystemKind.Node, Status = SystemStatus.Offline, Name = "B" },
        ];

        Assert.Empty(SystemsProjection.Query(rows, "", SystemsStatusFilter.Online, SystemsSortMode.Alphabetical, r => r.Name!, gatewayConnected: false));
        Assert.Equal(2, SystemsProjection.Query(rows, "", SystemsStatusFilter.Offline, SystemsSortMode.Alphabetical, r => r.Name!, gatewayConnected: false).Count);
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

public class SystemsTelemetryHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static double[] Values(IReadOnlyList<TelemetrySample> history, Func<HostMeasurements, double?> read) =>
        SystemsTelemetryHistory.TimedSeries(history, read).Select(point => point.Value).ToArray();

    private static SystemRow Row(string id, DateTimeOffset? at, long used = 10, SystemStatus status = SystemStatus.Online) => new()
    {
        Id = id,
        Kind = id == "gateway" ? SystemKind.Host : SystemKind.Node,
        Status = status,
        MeasuredAt = at,
        Measurements = at is null ? null : new HostMeasurements { CpuCount = 1, MemoryTotalBytes = 100, MemoryFreeBytes = 100 - used },
    };

    [Fact]
    public void Record_DeduplicatesRepeatedReportsAndRestartsAfterStaleGaps()
    {
        var history = new SystemsTelemetryHistory();
        history.Record([Row("node:a", T0, 10)]);
        history.Record([Row("node:a", T0, 99)]);
        history.Record([Row("node:a", T0.AddSeconds(60), 20)]);
        Assert.Equal([10d, 20d], Values(history.For("node:a"), s => s.MemoryUsedBytes));

        // Longer than the node stale window: the chart must not bridge the gap.
        history.Record([Row("node:a", T0.AddMinutes(10), 30)]);
        Assert.Equal([30d], Values(history.For("node:a"), s => s.MemoryUsedBytes));

        // The Gateway host goes stale faster.
        history.Record([Row("gateway", T0, 1)]);
        history.Record([Row("gateway", T0.AddSeconds(45), 2)]);
        Assert.Single(history.For("gateway"));
    }

    [Fact]
    public void Record_CapsSamplesAndForgetsRowsThatLeaveTheInventory()
    {
        var history = new SystemsTelemetryHistory();
        for (var i = 0; i < SystemsTelemetryHistory.SampleLimit + 5; i++)
            history.Record([Row("node:a", T0.AddSeconds(i * 10), i), Row("node:b", T0.AddSeconds(i * 10))]);
        Assert.Equal(SystemsTelemetryHistory.SampleLimit, history.For("node:a").Count);
        Assert.Equal(5d, Values(history.For("node:a"), s => s.MemoryUsedBytes)[0]);

        history.Record([Row("node:a", T0.AddHours(1)), Row("node:b", null)]);
        Assert.Empty(history.For("node:b"));
        history.Record([]);
        Assert.Empty(history.For("node:a"));
    }

    [Fact]
    public void TimedSeries_RestartsWhenAMetricIsMissing()
    {
        var samples = new[]
        {
            new TelemetrySample(T0, new HostMeasurements { Load1 = 1 }),
            new TelemetrySample(T0.AddSeconds(1), new HostMeasurements { Load1 = null }),
            new TelemetrySample(T0.AddSeconds(2), new HostMeasurements { Load1 = 3 }),
            new TelemetrySample(T0.AddSeconds(3), new HostMeasurements { Load1 = double.NaN }),
            new TelemetrySample(T0.AddSeconds(4), new HostMeasurements { Load1 = 5 }),
        };
        Assert.Equal([new TelemetryPoint(T0.AddSeconds(4), 5)], SystemsTelemetryHistory.TimedSeries(samples, s => s.Load1));
    }

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
        Assert.False(SystemsTelemetryHistory.IsLastKnown(fresh, connected: true, refreshFailed: false, T0.AddSeconds(30)));
        Assert.True(SystemsTelemetryHistory.IsLastKnown(fresh, connected: false, refreshFailed: false, T0));
        Assert.True(SystemsTelemetryHistory.IsLastKnown(fresh, connected: true, refreshFailed: true, T0));
        Assert.True(SystemsTelemetryHistory.IsLastKnown(Row("node:a", T0, status: SystemStatus.Offline), true, false, T0));
        Assert.True(SystemsTelemetryHistory.IsLastKnown(fresh, true, false, T0.AddMinutes(3)));
    }
}

public class BackupsPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static BackupRunRecord Run(bool ok, bool pushFailed = false, string? error = null, BackupRunLocation? location = null, long? bytes = null) =>
        new(T0, ok, error, pushFailed, bytes, location);

    [Fact]
    public void Project_ReportsHealthLocationsAndNextRun()
    {
        var b2 = new BackupRunLocation("b2", "b2", 900);
        var status = new BackupStatus(
            [
                new BackupTarget(BackupKind.Archive, "b2", "home", Run(true, location: b2), Run(true, location: b2, bytes: 1000)),
                new BackupTarget(BackupKind.Git, "repo", null, Run(true, pushFailed: true), Run(true, bytes: 5)),
                new BackupTarget(BackupKind.Archive, "nas", "home", Run(false, error: "disk full"), null),
                // Only Git targets can have push failures.
                new BackupTarget(BackupKind.Archive, "odd", null, Run(true, pushFailed: true), null),
            ],
            [
                new BackupSchedule("1", "offsite", "b2", "home", true, T0.AddHours(5)),
                new BackupSchedule("2", "offsite", "b2", "home", true, T0.AddHours(2)),
                new BackupSchedule("3", "offsite", "b2", "work", true, T0.AddHours(1)),
                new BackupSchedule("4", "git", "repo", null, false, T0.AddMinutes(1)),
                new BackupSchedule("5", "offsite", "s3", null, true, T0.AddDays(1)),
            ],
            [new StorageLocation("b2", "b2", "b2://bucket", true)]);

        var view = BackupsPresentation.Project(status);

        Assert.Equal([BackupHealth.Ok, BackupHealth.PushFailed, BackupHealth.Failed, BackupHealth.Ok], view.Targets.Select(t => t.Health));
        var archive = view.Targets[0];
        Assert.Equal(("b2", "b2://bucket", 900L, T0.AddHours(2)), (archive.Name, archive.Destination, archive.SizeBytes!.Value, archive.NextRunAt!.Value));
        Assert.Null(view.Targets[1].NextRunAt);
        Assert.Equal(5, view.Targets[1].SizeBytes);
        Assert.Equal("disk full", view.Targets[2].Error);
        Assert.Null(view.Targets[2].LastSuccessAt);
        // Schedules for other namespaces or targets are listed separately; disabled ones are not.
        Assert.Equal(["3", "5"], view.UnmatchedSchedules.Select(s => s.Id));
    }
}

public class DesktopPresentationTests
{
    private static SystemRow Host(DesktopSetupState? setup, string platform = "Linux", bool desktop = false,
        SystemStatus status = SystemStatus.Online) => new()
    {
        Id = "gateway",
        Kind = SystemKind.Host,
        Status = status,
        Platform = platform,
        Environment = new SystemEnvironment
        {
            Id = "gateway",
            Type = "local",
            Status = status,
            Desktop = desktop,
            DesktopSetup = setup is { } state ? new DesktopSetup(state, state == DesktopSetupState.Unsupported ? "why" : null) : null,
        },
    };

    [Theory]
    [InlineData(DesktopSetupState.Ready, "macOS 15", DesktopTitle.ScreenSharingDetected, DesktopHint.EnableAccess, true, false)]
    [InlineData(DesktopSetupState.Ready, "Linux", DesktopTitle.DesktopDetected, DesktopHint.EnableAccess, true, false)]
    [InlineData(DesktopSetupState.Managed, "Linux", DesktopTitle.ManagedConfigured, DesktopHint.EnableAccess, true, false)]
    [InlineData(DesktopSetupState.NeedsServer, "macOS 15", DesktopTitle.ScreenSharingNeeded, DesktopHint.ScreenSharingSetup, false, true)]
    [InlineData(DesktopSetupState.NeedsServer, "Linux", DesktopTitle.DesktopServerNeeded, DesktopHint.DesktopServerSetup, false, true)]
    [InlineData(DesktopSetupState.Unsupported, "Linux", DesktopTitle.Attention, DesktopHint.DesktopServerSetup, false, true)]
    public void Resolve_HostSetupGuidance(DesktopSetupState state, string platform, DesktopTitle title, DesktopHint hint, bool showEnable, bool checkAgain)
    {
        var section = DesktopPresentation.Resolve(Host(state, platform), connected: true, hostDesktopEnabled: false, canAdmin: false);

        Assert.Equal(DesktopSectionKind.HostSetup, section.Kind);
        Assert.Equal((title, hint, showEnable, checkAgain), (section.Title, section.Hint, section.ShowEnable, section.ShowCheckAgain));
        Assert.False(section.CanEnable);
        Assert.Equal(state == DesktopSetupState.Unsupported ? "why" : null, section.Detail);
    }

    [Fact]
    public void Resolve_EnabledHostWaitsAndAdministratorsCanEnable()
    {
        var enabled = DesktopPresentation.Resolve(Host(DesktopSetupState.Ready), true, hostDesktopEnabled: true, canAdmin: true);
        Assert.Equal((DesktopTitle.Enabled, DesktopHint.Connecting, false), (enabled.Title, enabled.Hint, enabled.ShowEnable));

        Assert.True(DesktopPresentation.Resolve(Host(DesktopSetupState.Ready), true, false, canAdmin: true).CanEnable);
        Assert.Equal(DesktopSectionKind.Available,
            DesktopPresentation.Resolve(Host(null, desktop: true), true, hostDesktopEnabled: true, canAdmin: true).Kind);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(DesktopSetupState.Ready, false)]
    [InlineData(DesktopSetupState.Ready, true)]
    [InlineData(DesktopSetupState.Managed, true)]
    public void Resolve_AvailableHostDesktopTakesPrecedenceOverSetup(DesktopSetupState? setup, bool canAdmin)
    {
        var host = Host(setup, desktop: true);
        var section = DesktopPresentation.Resolve(host, true, hostDesktopEnabled: true, canAdmin);

        Assert.Equal((DesktopSectionKind.Available, DesktopTitle.Available, DesktopHint.ViewingDeferred),
            (section.Kind, section.Title, section.Hint));
        Assert.False(section.ShowEnable);
        Assert.False(section.ShowCheckAgain);
        Assert.Equal(DesktopSectionKind.Offline,
            DesktopPresentation.Resolve(host, false, hostDesktopEnabled: true, canAdmin).Kind);
    }

    [Fact]
    public void Resolve_NonSetupStates()
    {
        Assert.Equal(DesktopSectionKind.Offline, DesktopPresentation.Resolve(Host(DesktopSetupState.Ready), false, false, true).Kind);
        Assert.Equal(DesktopSectionKind.NoHostDesktop, DesktopPresentation.Resolve(Host(null), true, false, true).Kind);
        Assert.Equal(DesktopSectionKind.NotReady,
            DesktopPresentation.Resolve(Host(null, status: SystemStatus.Starting), true, false, true).Kind);

        var node = new SystemRow
        {
            Id = "node:a", Kind = SystemKind.Node, Status = SystemStatus.Online,
            Environment = new SystemEnvironment { Id = "node:a", Type = "node", Status = SystemStatus.Online, Desktop = true },
        };
        var available = DesktopPresentation.Resolve(node, true, false, true);
        Assert.Equal((DesktopSectionKind.Available, DesktopHint.ViewingDeferred), (available.Kind, available.Hint));
        Assert.Equal(DesktopSectionKind.NoDesktop,
            DesktopPresentation.Resolve(node with { Environment = node.Environment with { Desktop = false } }, true, false, true).Kind);
        Assert.Equal(DesktopSectionKind.Offline,
            DesktopPresentation.Resolve(node with { Status = SystemStatus.Offline }, true, false, true).Kind);
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
        Assert.Contains(transport.Calls, call => call.Method == "environments.list" && call.Params.Contains("\"includeDesktopSetup\":true"));
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
    public async Task EnableHostDesktop_PatchesAgainstTheCurrentConfigHash()
    {
        var transport = new FakeTransport();
        transport.Responses["config.get"] = () => Json("""{"hash":"abc123","config":{}}""");
        transport.Responses["config.patch"] = () => Json("""{"ok":true}""");

        await new SystemsGatewayClient(transport.Request).EnableHostDesktopAsync(CancellationToken.None);

        Assert.Equal(["config.get", "config.patch"], transport.Calls.Select(c => c.Method));
        var patch = JsonDocument.Parse(transport.Calls[1].Params).RootElement;
        Assert.Equal("abc123", patch.GetProperty("baseHash").GetString());
        Assert.True(JsonDocument.Parse(patch.GetProperty("raw").GetString()!).RootElement
            .GetProperty("desktop").GetProperty("host").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task BackupsAndProbe_ReadAndHandleOlderGateways()
    {
        var transport = new FakeTransport();
        var client = new SystemsGatewayClient(transport.Request);
        Assert.Null(await client.ReadBackupStatusAsync(CancellationToken.None));

        transport.Responses["storage.locations.probe"] = () => Json("""{"state":"wrong-key"}""");
        var probe = await client.ProbeStorageAsync("b2", CancellationToken.None);
        Assert.Equal(StorageProbeState.WrongKey, probe.State);
        Assert.Contains(transport.Calls, call => call.Method == "storage.locations.probe" && call.Params == """{"name":"b2"}""");
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
