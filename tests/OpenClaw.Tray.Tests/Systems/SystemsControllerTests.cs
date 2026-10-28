using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClawTray.Systems;

namespace OpenClaw.Tray.Tests.Systems;

public class SystemsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSystemsGateway : ISystemsGateway
    {
        public int InventoryReads;
        public int BackupReads;
        public TaskCompletionSource<SystemsInventorySnapshot>? HeldInventory;
        public Func<SystemsInventorySnapshot> Inventory = () => new SystemsInventorySnapshot();
        public Func<BackupStatus?> Backups = () => new BackupStatus([], [], []);
        public Exception? InventoryError;
        public Exception? EnableError;
        public int EnableCalls;
        public IReadOnlyList<GatewayNodeInfo> Nodes = Array.Empty<GatewayNodeInfo>();
        public int NodeReads;
        public TaskCompletionSource<IReadOnlyList<GatewayNodeInfo>>? HeldNodes;

        public Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
        {
            InventoryReads++;
            if (InventoryError is { } error)
                return Task.FromException<SystemsInventorySnapshot>(error);
            return HeldInventory?.Task ?? Task.FromResult(Inventory());
        }

        public Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(GatewaySystemInfo, DateTimeOffset)?>(null);

        public Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken)
        {
            NodeReads++;
            return HeldNodes?.Task ?? Task.FromResult(Nodes);
        }

        public Task<BackupStatus?> ReadBackupStatusAsync(CancellationToken cancellationToken)
        {
            BackupReads++;
            return Task.FromResult(Backups());
        }

        public TaskCompletionSource<StorageProbeResult>? HeldProbe;

        public Task<StorageProbeResult> ProbeStorageAsync(string name, CancellationToken cancellationToken) =>
            HeldProbe?.Task ?? (name == "broken"
                ? Task.FromException<StorageProbeResult>(new InvalidOperationException("probe failed"))
                : Task.FromResult(new StorageProbeResult(StorageProbeState.Ok, 10, 20, null)));

        public Task EnableHostDesktopAsync(CancellationToken cancellationToken)
        {
            EnableCalls++;
            return EnableError is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
    }

    private static SystemsInventorySnapshot Inventory(params SystemEnvironment[] environments) => new()
    {
        Environments = environments,
        Nodes = [new GatewayNodeInfo { NodeId = "studio", DisplayName = "Studio", HasExplicitDisplayName = true, IsOnline = true }],
    };

    private static SystemEnvironment Env(string id, string type = "node", SystemStatus status = SystemStatus.Online) =>
        new() { Id = id, Type = type, Status = status };

    private static SystemsInputs Connected(params string[] scopes) => new()
    {
        Connected = true,
        Scopes = scopes.Length == 0 ? ["operator.read"] : scopes,
    };

    private static (SystemsController Controller, FakeSystemsGateway Gateway) Create(
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var gateway = new FakeSystemsGateway { Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")) };
        return (new SystemsController(() => gateway, clock ?? (() => Now), delay ?? ((_, _) => Task.CompletedTask)), gateway);
    }

    private static NodeHostStatsEvent Stats(long usedBytes, DateTimeOffset at) =>
        new("studio", new NodeHostStats(new HostMeasurements { CpuCount = 4, MemoryTotalBytes = 100, MemoryFreeBytes = 100 - usedBytes }, at));

    [Fact]
    public async Task Connecting_LoadsInventoryAndSelectsTheGatewayHost()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);

        var state = controller.State;
        Assert.True(state.HasLoaded);
        Assert.False(state.Loading);
        Assert.Equal(["gateway", "node:studio"], state.Rows.Select(row => row.Id));
        Assert.Equal("gateway", state.SelectedId);
        Assert.Equal(Now, state.LastRefreshedAt);
        Assert.True(gateway.BackupReads > 0);
    }

    [Fact]
    public async Task Disconnected_DoesNotReadAndKeepsLastKnownRows()
    {
        var (controller, gateway) = Create();
        await controller.RefreshAsync(manual: true);
        Assert.Equal(0, gateway.InventoryReads);

        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        var reads = gateway.InventoryReads;
        controller.UpdateInputs(new SystemsInputs { Connected = false });
        await controller.TickAsync();

        Assert.Equal(reads, gateway.InventoryReads);
        Assert.False(controller.State.Connected);
        Assert.Equal(2, controller.State.Rows.Count);
    }

    [Fact]
    public async Task SelectingANode_RefreshesItsMeasurementsImmediately()
    {
        var (controller, gateway) = Create();
        gateway.Nodes = Inventory().Nodes;
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        Assert.Equal(0, gateway.NodeReads);

        controller.Select("node:studio");
        Assert.Equal(1, gateway.NodeReads);

        controller.SetShowStatistics(false);
        controller.Select("gateway");
        controller.Select("node:studio");
        Assert.Equal(1, gateway.NodeReads);
    }

    [Fact]
    public async Task Disconnect_KeepsLastKnownNodesPresenceAndSessionsUntilReconnect()
    {
        var (controller, _) = Create();
        var placed = new SessionInfo { Key = "agent:main:placed", DisplayName = "Placed", Placement = new(SessionPlacementState.Active, "node:studio", "studio") };
        controller.UpdateInputs(Connected() with
        {
            Sessions = [placed],
            Presence = [new PresenceEntry { DeviceId = "control-ui", Host = "Control UI", Mode = "webchat", Ts = Now.ToUnixTimeMilliseconds() }],
        });
        await controller.RefreshAsync(manual: false);
        var rowsBefore = controller.State.Rows.Count;

        // The app clears its node list, presence and sessions when the connection drops.
        controller.UpdateInputs(new SystemsInputs { Connected = false });
        controller.UpdateNodes(Array.Empty<GatewayNodeInfo>());

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal("Studio", studio.Node?.DisplayName);
        Assert.Equal("Placed", studio.Sessions.First().Session.Title);
        Assert.Equal(rowsBefore, controller.State.Rows.Count);

        controller.UpdateInputs(Connected());
        Assert.Empty(controller.State.Rows.Single(row => row.Id == "node:studio").Sessions);
    }

    [Fact]
    public async Task EventBurstsDuringALoadCollapseIntoOneFollowUpRefresh()
    {
        var (controller, gateway) = Create();
        gateway.HeldInventory = new TaskCompletionSource<SystemsInventorySnapshot>();
        controller.UpdateInputs(Connected());
        var first = controller.RefreshAsync(manual: false);
        await controller.RefreshAsync(manual: false);
        await controller.RefreshAsync(manual: false);
        var reads = gateway.InventoryReads;

        var held = gateway.HeldInventory;
        gateway.HeldInventory = null;
        held.SetResult(Inventory(Env("gateway", "local")));
        await first;

        Assert.Equal(reads + 1, gateway.InventoryReads);
    }

    [Fact]
    public async Task InventoryFailure_KeepsPreviousRowsAndReportsTheError()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        gateway.InventoryError = new InvalidOperationException("permission denied");

        await controller.RefreshAsync(manual: true);

        Assert.Equal("permission denied", controller.State.InventoryError);
        Assert.Equal(2, controller.State.Rows.Count);
    }

    [Fact]
    public async Task WithoutReadScope_DoesNotLoadInventoryOrBackups()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected("operator.pairing"));
        await controller.RefreshAsync(manual: true);
        await controller.RefreshBackupsAsync();

        Assert.False(controller.State.CanRead);
        Assert.Equal(0, gateway.InventoryReads);
        Assert.Equal(0, gateway.BackupReads);
    }

    [Theory]
    [InlineData(new string[0], true, false, false)]
    [InlineData(new[] { "operator.read" }, true, false, false)]
    [InlineData(new[] { "operator.pairing" }, false, false, true)]
    [InlineData(new[] { "operator.admin" }, true, true, true)]
    [InlineData(new[] { " operator.write " }, true, false, false)]
    public void Scopes_MapToReadAdminAndPairingAccess(string[] scopes, bool read, bool admin, bool pair)
    {
        var (controller, _) = Create();
        controller.UpdateInputs(new SystemsInputs { Connected = false, Scopes = scopes });
        Assert.Equal((read, admin, pair), (controller.State.CanRead, controller.State.CanAdmin, controller.State.CanPair));
    }

    [Fact]
    public async Task HostStatsEvent_UpdatesTheNodeAndBuildsHistory()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);

        for (var i = 0; i < 3; i++)
        {
            clock = Now.AddSeconds(i * 30);
            controller.ApplyHostStats(Stats(50 + i, clock));
        }
        controller.ApplyHostStats(new NodeHostStatsEvent("unknown", new NodeHostStats(new HostMeasurements(), Now)));

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(52, studio.Measurements!.MemoryUsedBytes);
        Assert.Equal([50d, 51d, 52d], SystemsTelemetryHistory.TimedSeries(controller.Telemetry.For("node:studio"), s => s.MemoryUsedBytes).Select(p => p.Value));
    }

    [Fact]
    public async Task Reconnecting_ClearsProbes()
    {
        var (controller, _) = Create();
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        await controller.ProbeStorageAsync("b2");
        Assert.NotEmpty(controller.State.Probes);

        controller.UpdateInputs(new SystemsInputs { Connected = false });
        controller.UpdateInputs(Connected());

        Assert.Empty(controller.State.Probes);
    }

    [Fact]
    public async Task Selection_IsPreservedWhenTheMachineDisappears()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        controller.Select("node:studio");

        gateway.Inventory = () => Inventory(Env("gateway", "local"));
        await controller.RefreshAsync(manual: true);

        Assert.Equal("node:studio", controller.State.SelectedId);
        Assert.True(controller.State.SelectionMissing);
    }

    [Fact]
    public async Task Probe_ReportsCheckingThenResultOrError()
    {
        var (controller, _) = Create();
        controller.UpdateInputs(Connected());
        var states = new List<ProbeStatus?>();
        controller.Changed += (_, _) => states.Add(controller.State.Probes.GetValueOrDefault("b2")?.Status);

        await controller.ProbeStorageAsync("b2");
        await controller.ProbeStorageAsync("broken");

        Assert.Contains(ProbeStatus.Checking, states);
        Assert.Equal(StorageProbeState.Ok, controller.State.Probes["b2"].Result!.State);
        Assert.Equal(new StorageProbeResult(StorageProbeState.Error, null, null, "probe failed"), controller.State.Probes["broken"].Result);
    }

    [Fact]
    public async Task Backups_UnsupportedGatewayIsReportedSeparatelyFromErrors()
    {
        var (controller, gateway) = Create();
        gateway.Backups = () => null;
        controller.UpdateInputs(Connected());
        await controller.RefreshBackupsAsync();
        Assert.True(controller.State.BackupsUnsupported);
        Assert.Null(controller.State.BackupsError);
    }

    [Fact]
    public async Task EnableHostDesktop_RequiresAdminAndReportsFailure()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected("operator.read"));
        Assert.False(await controller.EnableHostDesktopAsync());
        Assert.Equal(0, gateway.EnableCalls);

        controller.UpdateInputs(Connected("operator.admin"));
        gateway.EnableError = new InvalidOperationException("read-only");
        Assert.False(await controller.EnableHostDesktopAsync());
        Assert.Equal("read-only", controller.State.DesktopError);
        Assert.False(controller.State.DesktopEnabling);

        gateway.EnableError = null;
        Assert.True(await controller.EnableHostDesktopAsync());
        Assert.True(controller.State.HostDesktopEnabled);
        Assert.Null(controller.State.DesktopError);
    }

    [Theory]
    [InlineData("MY-NODE")]
    [InlineData("FULL-DEVICE-ID")]
    public void OwnNodePending_SurvivesTheApprovalQueuesOwnNodeFilter(string pendingNodeId)
    {
        var (controller, _) = Create();
        string[] ownIds = ["my-node", "full-device-id"];
        var requests = new PairingListInfo
        {
            Pending = [new PairingRequest { RequestId = "r", NodeId = pendingNodeId }],
        };
        var queue = new PairingApprovalQueue();
        queue.Reconcile(null, requests, ownIds, 0);
        Assert.Empty(queue.Current);
        var inputs = Connected() with
        {
            LocalNodeId = "full-device-id",
            OwnNodeIds = ownIds,
            NodePairList = requests,
            PendingApprovals = queue.Current,
        };
        controller.UpdateInputs(inputs);
        Assert.True(controller.State.OwnNodePending);
        Assert.Empty(controller.State.PendingApprovals);

        controller.UpdateInputs(inputs with { OwnNodeIds = ["other"] });
        Assert.False(controller.State.OwnNodePending);
        controller.UpdateInputs(inputs with { NodePairList = new PairingListInfo() });
        Assert.False(controller.State.OwnNodePending);
        controller.UpdateInputs(inputs with { Connected = false });
        Assert.False(controller.State.OwnNodePending);
    }

    [Fact]
    public async Task SessionsRelateToMachinesThroughPlacement()
    {
        var (controller, _) = Create();
        controller.UpdateInputs(Connected() with
        {
            Sessions =
            [
                new SessionInfo { Key = "agent:main:placed", DisplayName = "Placed", Placement = new(SessionPlacementState.Active, "node:studio", "studio") },
                new SessionInfo { Key = "agent:main:archived", Archived = true, ExecNode = "studio" },
                new SessionInfo { Key = "agent:main:main" },
            ],
        });
        await controller.RefreshAsync(manual: false);

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal([(SessionRelationKind.Placement, "Placed"), (SessionRelationKind.Runner, "Placed")],
            studio.Sessions.Select(r => (r.Kind, r.Session.Title)));
        Assert.Equal("agent:main:main", controller.State.Rows.Single(row => row.Id == "gateway").Sessions.Single().Session.Key);
    }

    [Fact]
    public async Task EnableHostDesktop_CancelledByAConnectionDropDoesNotStayBusy()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected("operator.admin"));
        gateway.EnableError = new OperationCanceledException("connection lost");

        Assert.False(await controller.EnableHostDesktopAsync());
        Assert.False(controller.State.DesktopEnabling);
        Assert.Null(controller.State.DesktopError);

        gateway.EnableError = null;
        Assert.True(await controller.EnableHostDesktopAsync());
    }

    [Fact]
    public async Task HostDesktopEnabled_FollowsConfigSnapshotsAfterAnOptimisticEnable()
    {
        static System.Text.Json.JsonElement Config(bool enabled) =>
            System.Text.Json.JsonDocument.Parse("{\"config\":{\"desktop\":{\"host\":{\"enabled\":" + (enabled ? "true" : "false") + "}}}}").RootElement.Clone();
        var (controller, _) = Create();
        var before = Config(false);
        controller.UpdateInputs(Connected("operator.admin") with { Config = before });
        Assert.False(controller.State.HostDesktopEnabled);

        await controller.EnableHostDesktopAsync();
        // The snapshot already held does not undo the optimistic enable...
        controller.UpdateInputs(Connected("operator.admin") with { Config = before });
        Assert.True(controller.State.HostDesktopEnabled);
        // ...but every new response is authoritative, even with the same contents.
        controller.UpdateInputs(Connected("operator.admin") with { Config = Config(false) });
        Assert.False(controller.State.HostDesktopEnabled);
        controller.UpdateInputs(Connected("operator.admin") with { Config = Config(true) });
        Assert.True(controller.State.HostDesktopEnabled);
    }

    [Fact]
    public void Connecting_StartsExactlyOneInventoryLoad()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        controller.UpdateInputs(Connected() with { Presence = [] });

        Assert.Equal(1, gateway.InventoryReads);
        Assert.True(controller.State.HasLoaded);
    }

    [Fact]
    public void ConnectionEpoch_ChangesOnDisconnectReconnectAndGatewaySwitch()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected() with { GatewayId = "a" });
        var connected = controller.State.ConnectionEpoch;
        controller.UpdateInputs(Connected() with { GatewayId = "a", Presence = [] });
        Assert.Equal(connected, controller.State.ConnectionEpoch);

        controller.UpdateInputs(new SystemsInputs { GatewayId = "a" });
        Assert.NotEqual(connected, controller.State.ConnectionEpoch);
        var disconnected = controller.State.ConnectionEpoch;
        controller.UpdateInputs(Connected() with { GatewayId = "a" });
        Assert.NotEqual(disconnected, controller.State.ConnectionEpoch);

        // Another Gateway: its inventory must not mix with the previous one.
        controller.Select("node:studio");
        gateway.HeldInventory = new TaskCompletionSource<SystemsInventorySnapshot>();
        var beforeSwitch = controller.State.ConnectionEpoch;
        controller.UpdateInputs(Connected() with { GatewayId = "b" });
        Assert.NotEqual(beforeSwitch, controller.State.ConnectionEpoch);
        Assert.Empty(controller.State.Rows);
        Assert.Null(controller.State.SelectedId);
        Assert.False(controller.State.HasLoaded);
    }

    [Fact]
    public async Task RequestRefresh_CoalescesAnEventBurstIntoOneLoad()
    {
        var delay = new TaskCompletionSource();
        var (controller, gateway) = Create(delay: (_, _) => delay.Task);
        controller.UpdateInputs(Connected());
        var reads = gateway.InventoryReads;

        for (var i = 0; i < 5; i++)
            controller.RequestRefresh();
        Assert.Equal(reads, gateway.InventoryReads);

        delay.SetResult();
        await Task.Yield();
        Assert.Equal(reads + 1, gateway.InventoryReads);
    }

    [Fact]
    public async Task PartialFailure_KeepsLastKnownNodesAndMarksThemStale()
    {
        var (controller, gateway) = Create();
        gateway.Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")) with
        {
            Nodes = [new GatewayNodeInfo
            {
                NodeId = "studio", DisplayName = "Studio", HasExplicitDisplayName = true, IsOnline = true,
                HostStats = Stats(40, Now).Stats,
            }],
        };
        controller.UpdateInputs(Connected());
        Assert.NotNull(controller.State.Rows.Single(row => row.Id == "node:studio").Measurements);

        gateway.Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")) with
        {
            Nodes = [],
            Errors = new SystemsPartialErrors(Nodes: "timeout"),
        };
        await controller.RefreshAsync(manual: false);

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(40, studio.Measurements?.MemoryUsedBytes);
        Assert.Equal("timeout", controller.State.PartialErrors.Nodes);
        Assert.Single(controller.Telemetry.For("node:studio"));
    }

    [Fact]
    public async Task SupersededLoad_StillRunsTheRefreshQueuedMeanwhile()
    {
        var (controller, gateway) = Create();
        gateway.HeldInventory = new TaskCompletionSource<SystemsInventorySnapshot>();
        controller.UpdateInputs(Connected());
        controller.UpdateInputs(new SystemsInputs());
        controller.UpdateInputs(Connected());
        Assert.Equal(1, gateway.InventoryReads);

        var held = gateway.HeldInventory;
        gateway.HeldInventory = null;
        held.SetResult(gateway.Inventory());
        await Task.Yield();

        Assert.Equal(2, gateway.InventoryReads);
        Assert.True(controller.State.HasLoaded);
    }

    [Fact]
    public async Task HostStats_IgnoreOlderReportsAndUseTheLocalClockForAge()
    {
        // The Gateway clock runs ten minutes behind the local one.
        var skew = TimeSpan.FromMinutes(10);
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);

        controller.ApplyHostStats(Stats(30, Now - skew));
        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(Now, studio.MeasuredAt);
        Assert.False(SystemsTelemetryHistory.IsLastKnown(studio, connected: true, refreshFailed: false, clock));

        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(35, Now - skew + TimeSpan.FromSeconds(60)));
        // A late, older report changes neither the numbers nor the clock calibration.
        controller.ApplyHostStats(Stats(99, Now - skew));
        studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(35, studio.Measurements?.MemoryUsedBytes);
        Assert.Equal(clock, studio.MeasuredAt);
        Assert.Equal(2, controller.Telemetry.For("node:studio").Count);
    }

    [Fact]
    public async Task HostStats_LearningAGatewayAheadOfTheLocalClockKeepsBuildingHistory()
    {
        // node.list arrives before any event, so its first sample uses the raw (future) Gateway time.
        var skew = TimeSpan.FromMinutes(10);
        var clock = Now;
        var (controller, gateway) = Create(() => clock);
        gateway.Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")) with
        {
            Nodes = [new GatewayNodeInfo { NodeId = "studio", IsOnline = true, HostStats = Stats(10, Now + skew).Stats }],
        };
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);

        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(20, Now + skew + TimeSpan.FromSeconds(60)));
        clock = Now.AddSeconds(120);
        controller.ApplyHostStats(Stats(30, Now + skew + TimeSpan.FromSeconds(120)));

        Assert.Equal(-skew, controller.State.GatewayClockOffset);
        Assert.Equal([Now.AddSeconds(60), Now.AddSeconds(120)], controller.Telemetry.For("node:studio").Select(s => s.At));
    }

    [Fact]
    public async Task LateTelemetryFromAnEarlierConnection_IsDropped()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        controller.Select("node:studio");
        gateway.HeldNodes = new TaskCompletionSource<IReadOnlyList<GatewayNodeInfo>>();
        // Starts the node read and leaves it held; awaiting it would wait for the test itself.
        var pending = controller.RefreshTelemetryAsync();
        var held = gateway.HeldNodes;

        controller.UpdateInputs(new SystemsInputs());
        controller.UpdateInputs(Connected());
        held.SetException(new InvalidOperationException("old connection"));
        await pending;

        Assert.Null(controller.State.PartialErrors.Nodes);
        // The new connection is not blocked by the abandoned read.
        gateway.HeldNodes = null;
        var reads = gateway.NodeReads;
        await controller.RefreshTelemetryAsync();
        Assert.Equal(reads + 1, gateway.NodeReads);
    }

    [Fact]
    public void GatewaySwitch_IsDetectedEvenWithoutAnActiveGatewayInBetween()
    {
        var (controller, _) = Create();
        controller.UpdateInputs(Connected() with { GatewayId = "a" });
        Assert.NotEmpty(controller.State.Rows);

        controller.UpdateInputs(new SystemsInputs());
        controller.UpdateInputs(new SystemsInputs { GatewayId = "b" });

        Assert.Empty(controller.State.Rows);
        Assert.False(controller.State.HasLoaded);
    }

    [Fact]
    public async Task HostStats_OneLateEventDoesNotMoveTheClockOrWipeHistory()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);

        for (var i = 0; i < 4; i++)
        {
            clock = Now.AddSeconds(i * 60);
            // The third report reaches the page 30 s after the Gateway stamped it (a UI stall).
            var stamped = i == 2 ? clock - TimeSpan.FromSeconds(30) : clock;
            controller.ApplyHostStats(Stats(10 + i, stamped));
        }

        Assert.Equal(TimeSpan.Zero, controller.State.GatewayClockOffset);
        Assert.Equal(4, controller.Telemetry.For("node:studio").Count);
    }

    [Fact]
    public async Task HostStats_ReportsReleasedTogetherByOneStallDoNotConfirmEachOther()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        controller.ApplyHostStats(Stats(10, clock));

        // A 30 s stall releases two queued reports back to back, then reports arrive on time again.
        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(11, clock - TimeSpan.FromSeconds(30)));
        controller.ApplyHostStats(Stats(12, clock - TimeSpan.FromSeconds(29)));
        clock = Now.AddSeconds(120);
        controller.ApplyHostStats(Stats(13, clock));

        Assert.Equal(TimeSpan.Zero, controller.State.GatewayClockOffset);
        Assert.Equal(4, controller.Telemetry.For("node:studio").Count);
    }

    [Fact]
    public async Task HostStats_ALargerOffsetIsAdoptedOnceALaterReportConfirmsIt()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync(manual: false);
        controller.ApplyHostStats(Stats(10, Now));

        // From here on, every report is stamped 30 s behind the local clock.
        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(11, clock - TimeSpan.FromSeconds(30)));
        clock = Now.AddSeconds(70);
        controller.ApplyHostStats(Stats(12, clock - TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, controller.State.GatewayClockOffset);

        clock = Now.AddSeconds(90);
        controller.ApplyHostStats(Stats(13, clock - TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), controller.State.GatewayClockOffset);
    }

    [Fact]
    public async Task Disconnect_EndsProbesThatWereStillChecking()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        var probe = new TaskCompletionSource<StorageProbeResult>();
        gateway.HeldProbe = probe;
        var pending = controller.ProbeStorageAsync("b2");
        Assert.Equal(ProbeStatus.Checking, controller.State.Probes["b2"].Status);

        controller.UpdateInputs(new SystemsInputs());
        probe.SetResult(new StorageProbeResult(StorageProbeState.Ok, 1, 2, null));
        await pending;

        Assert.Empty(controller.State.Probes);
    }
}
