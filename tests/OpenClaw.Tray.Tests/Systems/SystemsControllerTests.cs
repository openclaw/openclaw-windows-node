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
        public TaskCompletionSource<SystemsInventorySnapshot>? HeldInventory;
        public Func<SystemsInventorySnapshot> Inventory = () => new SystemsInventorySnapshot();
        public Exception? InventoryError;
        public IReadOnlyList<GatewayNodeInfo> Nodes = Array.Empty<GatewayNodeInfo>();
        public int NodeReads;
        public TaskCompletionSource<IReadOnlyList<GatewayNodeInfo>>? HeldNodes;
        public int SystemInfoReads;

        public Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
        {
            InventoryReads++;
            if (InventoryError is { } error)
                return Task.FromException<SystemsInventorySnapshot>(error);
            return HeldInventory?.Task ?? Task.FromResult(Inventory());
        }

        public Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken)
        {
            SystemInfoReads++;
            return Task.FromResult<(GatewaySystemInfo, DateTimeOffset)?>(null);
        }

        public Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken)
        {
            NodeReads++;
            return HeldNodes?.Task ?? Task.FromResult(Nodes);
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
    public async Task Connecting_LoadsInventoryAndExplicitRefreshReadsAgain()
    {
        var (controller, gateway) = Create();
        using (controller)
        {
            // The fake completes synchronously, so connecting already finishes the initial read.
            controller.UpdateInputs(Connected());
            var state = controller.State;
            Assert.True(state.HasLoaded);
            Assert.False(state.Loading);
            Assert.Equal(["gateway", "node:studio"], state.Rows.Select(row => row.Id));
            Assert.Equal("gateway", state.SelectedId);
            Assert.Equal(Now, state.LastRefreshedAt);
            Assert.Equal(1, gateway.InventoryReads);

            gateway.Inventory = () => Inventory(Env("gateway", "local"), Env("node:refreshed"));
            await controller.RefreshAsync();

            Assert.Equal(2, gateway.InventoryReads);
            Assert.Equal(["gateway", "node:refreshed"], controller.State.Rows.Select(row => row.Id));
            Assert.Equal("gateway", controller.State.SelectedId);
            Assert.False(controller.State.Loading);
        }
    }

    [Fact]
    public async Task InlineSelection_CollapsesOnlyTheSelectedRowAndPreservesMissingDeepLinks()
    {
        var (controller, _) = Create();
        using (controller)
        {
            controller.UpdateInputs(Connected());
            await controller.RefreshAsync();
            controller.ToggleSelection("node:studio");
            Assert.Equal("node:studio", controller.State.SelectedId);
            controller.ToggleSelection("node:studio");
            Assert.Null(controller.State.SelectedId);
            controller.Select("node:missing");
            Assert.True(controller.State.SelectionMissing);
            Assert.Equal("node:missing", controller.State.SelectedId);
            Assert.Equal(["gateway", "node:studio"], controller.State.Rows.Select(row => row.Id));
        }
    }

    [Fact]
    public async Task RelatedSessions_KeepOnlyReportedAgentAndTimestampMetadata()
    {
        var (controller, _) = Create();
        using (controller)
        {
            controller.UpdateInputs(Connected() with
            {
                Sessions =
                [
                    new SessionInfo { Key = "reported", DisplayName = "Reported", AgentId = "builder", UpdatedAt = Now.UtcDateTime },
                    new SessionInfo { Key = "sparse", DisplayName = "Sparse" },
                ],
            });
            await controller.RefreshAsync();
            var sessions = controller.State.Rows.Single(row => row.Id == "gateway").Sessions;
            var reported = sessions.Single(relation => relation.Session.Key == "reported").Session;
            Assert.Equal("builder", reported.AgentId);
            Assert.Equal(Now, reported.UpdatedAt);
            var sparse = sessions.Single(relation => relation.Session.Key == "sparse").Session;
            Assert.Null(sparse.AgentId);
            Assert.Null(sparse.UpdatedAt);
        }
    }

    [Fact]
    public async Task Disconnected_DoesNotReadAndKeepsLastKnownRows()
    {
        var (controller, gateway) = Create();
        await controller.RefreshAsync();
        Assert.Equal(0, gateway.InventoryReads);

        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();
        var reads = gateway.InventoryReads;
        controller.UpdateInputs(new SystemsInputs { Connected = false });
        await controller.TickAsync();

        Assert.Equal(reads, gateway.InventoryReads);
        Assert.False(controller.State.Connected);
        Assert.Equal(2, controller.State.Rows.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SystemKind.Worker)]
    [InlineData(SystemKind.Client)]
    [InlineData(SystemKind.Node)]
    public async Task QuietTick_WithoutAnEligibleMeasurementRead_StillAdvancesPresentationTime(SystemKind? selectionKind)
    {
        var now = Now;
        var gateway = new FakeSystemsGateway
        {
            Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio"), Env("worker:build", "worker")),
        };
        using var controller = new SystemsController(() => gateway, () => now, (_, _) => Task.CompletedTask);
        controller.UpdateInputs(Connected() with
        {
            Presence = [new PresenceEntry { InstanceId = "phone-1", Host = "Phone", Mode = "ios", Ts = Now.ToUnixTimeMilliseconds() }],
        });
        // Node stands for a deep link that no longer resolves, so no machine read is possible.
        var selection = selectionKind switch
        {
            null => null,
            SystemKind.Node => "node:missing",
            _ => controller.State.Rows.Single(row => row.Kind == selectionKind).Id,
        };
        if (selection is null)
            controller.ToggleSelection("gateway");
        else
            controller.Select(selection);
        Assert.Equal(selection, controller.State.SelectedId);
        Assert.Equal(selectionKind is SystemKind.Worker or SystemKind.Client, controller.State.Selected is not null);
        var reads = (gateway.InventoryReads, gateway.NodeReads, gateway.SystemInfoReads);
        var rows = controller.State.Rows;
        var changes = 0;
        controller.Changed += (_, _) => changes++;

        now = Now.AddSeconds(31);
        await controller.TickAsync();

        Assert.Equal(1, changes);
        Assert.Equal(now, controller.State.PresentedAt);
        Assert.Same(rows, controller.State.Rows);
        Assert.Equal(reads, (gateway.InventoryReads, gateway.NodeReads, gateway.SystemInfoReads));

        now = Now.AddSeconds(46);
        await controller.TickAsync();
        Assert.Equal(2, changes);
        Assert.Equal(now, controller.State.PresentedAt);
    }

    [Fact]
    public async Task Tick_ThatPublishesMeasurements_DoesNotPublishASecondPresentationUpdate()
    {
        var now = Now;
        var gateway = new FakeSystemsGateway
        {
            Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")),
            Nodes = Inventory().Nodes,
        };
        using var controller = new SystemsController(() => gateway, () => now, (_, _) => Task.CompletedTask);
        controller.UpdateInputs(Connected());
        controller.Select("node:studio");
        var nodeReads = gateway.NodeReads;
        var changes = 0;
        controller.Changed += (_, _) => changes++;

        now = Now.AddSeconds(15);
        await controller.TickAsync();

        Assert.Equal(nodeReads + 1, gateway.NodeReads);
        Assert.Equal(1, changes);
        Assert.Null(controller.State.PresentedAt);
    }

    [Fact]
    public async Task DisconnectedTick_AdvancesLastKnownAgesWithoutReading()
    {
        var now = Now;
        var gateway = new FakeSystemsGateway { Inventory = () => Inventory(Env("gateway", "local"), Env("node:studio")) };
        using var controller = new SystemsController(() => gateway, () => now, (_, _) => Task.CompletedTask);
        controller.UpdateInputs(Connected());
        controller.UpdateInputs(new SystemsInputs { Connected = false });
        var reads = (gateway.InventoryReads, gateway.NodeReads, gateway.SystemInfoReads);

        now = Now.AddMinutes(2);
        await controller.TickAsync();

        Assert.Equal(now, controller.State.PresentedAt);
        Assert.Equal(reads, (gateway.InventoryReads, gateway.NodeReads, gateway.SystemInfoReads));
    }

    [Fact]
    public async Task SelectingANode_RefreshesItsMeasurementsImmediately()
    {
        var (controller, gateway) = Create();
        gateway.Nodes = Inventory().Nodes;
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();
        Assert.Equal(0, gateway.NodeReads);

        controller.Select("node:studio");
        Assert.Equal(1, gateway.NodeReads);

        // Reselecting the same machine does not read again.
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
        await controller.RefreshAsync();
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
        var first = controller.RefreshAsync();
        await controller.RefreshAsync();
        await controller.RefreshAsync();
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
        await controller.RefreshAsync();
        gateway.InventoryError = new InvalidOperationException("permission denied");

        await controller.RefreshAsync();

        Assert.Equal("permission denied", controller.State.InventoryError);
        Assert.Equal(2, controller.State.Rows.Count);
    }

    [Fact]
    public async Task WithoutReadScope_DoesNotLoadInventory()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected("operator.pairing"));
        await controller.RefreshAsync();

        Assert.False(controller.State.CanRead);
        Assert.Equal(0, gateway.InventoryReads);
    }

    [Theory]
    // Gateways that advertise no scopes keep read access and node management; the Gateway still enforces both.
    [InlineData(new string[0], true, true)]
    [InlineData(new[] { "operator.read" }, true, false)]
    [InlineData(new[] { "operator.pairing" }, false, true)]
    [InlineData(new[] { "operator.admin" }, true, true)]
    [InlineData(new[] { " operator.write " }, true, false)]
    public void Scopes_MapToReadAndNodeManagementAccess(string[] scopes, bool read, bool manage)
    {
        var (controller, _) = Create();
        controller.UpdateInputs(new SystemsInputs { Connected = false, Scopes = scopes });
        Assert.Equal((read, manage), (controller.State.CanRead, controller.State.CanManageNodes));
    }

    [Fact]
    public async Task HostStatsEvent_UpdatesTheNodeWithItsLatestReport()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();

        for (var i = 0; i < 3; i++)
        {
            clock = Now.AddSeconds(i * 30);
            controller.ApplyHostStats(Stats(50 + i, clock));
        }
        controller.ApplyHostStats(new NodeHostStatsEvent("unknown", new NodeHostStats(new HostMeasurements(), Now)));

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(52, studio.Measurements!.MemoryUsedBytes);
        Assert.Equal(Now.AddSeconds(60), studio.MeasuredAt);
    }

    [Fact]
    public async Task WebSystemsPage_FollowsInventorySupportAndResetsOnGatewaySwitch()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected() with { GatewayId = "a" });
        await controller.RefreshAsync();
        Assert.True(controller.State.HasWebSystemsPage);

        // An older Gateway without environments.list has no web Systems page to open.
        gateway.Inventory = () => Inventory() with { Environments = null };
        controller.UpdateInputs(Connected() with { GatewayId = "b" });
        Assert.False(controller.State.HasWebSystemsPage);
        await controller.RefreshAsync();
        Assert.False(controller.State.HasWebSystemsPage);
        Assert.Contains(controller.State.Rows, row => row.Id == "gateway");
    }

    [Fact]
    public async Task Selection_IsPreservedWhenTheMachineDisappears()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();
        controller.Select("node:studio");

        gateway.Inventory = () => Inventory(Env("gateway", "local"));
        await controller.RefreshAsync();

        Assert.Equal("node:studio", controller.State.SelectedId);
        Assert.True(controller.State.SelectionMissing);
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
        await controller.RefreshAsync();

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal([(SessionRelationKind.Placement, "Placed"), (SessionRelationKind.Runner, "Placed")],
            studio.Sessions.Select(r => (r.Kind, r.Session.Title)));
        Assert.Equal("agent:main:main", controller.State.Rows.Single(row => row.Id == "gateway").Sessions.Single().Session.Key);
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
        await controller.RefreshAsync();

        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(40, studio.Measurements?.MemoryUsedBytes);
        Assert.Equal("timeout", controller.State.PartialErrors.Nodes);
        Assert.True(SystemsFreshness.IsLastKnown(studio, connected: true, refreshFailed: true, Now));
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
        await controller.RefreshAsync();

        controller.ApplyHostStats(Stats(30, Now - skew));
        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(Now, studio.MeasuredAt);
        Assert.False(SystemsFreshness.IsLastKnown(studio, connected: true, refreshFailed: false, clock));

        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(35, Now - skew + TimeSpan.FromSeconds(60)));
        // A late, older report changes neither the numbers nor the clock calibration.
        controller.ApplyHostStats(Stats(99, Now - skew));
        studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(35, studio.Measurements?.MemoryUsedBytes);
        Assert.Equal(clock, studio.MeasuredAt);
    }

    [Fact]
    public async Task HostStats_LearningAGatewayAheadOfTheLocalClockAgesReportsOnTheLocalClock()
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
        await controller.RefreshAsync();

        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(20, Now + skew + TimeSpan.FromSeconds(60)));
        clock = Now.AddSeconds(120);
        controller.ApplyHostStats(Stats(30, Now + skew + TimeSpan.FromSeconds(120)));

        Assert.Equal(-skew, controller.State.GatewayClockOffset);
        var studio = controller.State.Rows.Single(row => row.Id == "node:studio");
        Assert.Equal(30, studio.Measurements?.MemoryUsedBytes);
        Assert.Equal(Now.AddSeconds(120), studio.MeasuredAt);
        Assert.False(SystemsFreshness.IsLastKnown(studio, connected: true, refreshFailed: false, clock));
    }

    [Fact]
    public async Task LateMeasurementsFromAnEarlierConnection_AreDropped()
    {
        var (controller, gateway) = Create();
        controller.UpdateInputs(Connected());
        controller.Select("node:studio");
        gateway.HeldNodes = new TaskCompletionSource<IReadOnlyList<GatewayNodeInfo>>();
        // Starts the node read and leaves it held; awaiting it would wait for the test itself.
        var pending = controller.RefreshMeasurementsAsync();
        var held = gateway.HeldNodes;

        controller.UpdateInputs(new SystemsInputs());
        controller.UpdateInputs(Connected());
        held.SetException(new InvalidOperationException("old connection"));
        await pending;

        Assert.Null(controller.State.PartialErrors.Nodes);
        // The new connection is not blocked by the abandoned read.
        gateway.HeldNodes = null;
        var reads = gateway.NodeReads;
        await controller.RefreshMeasurementsAsync();
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
    public async Task HostStats_OneLateEventDoesNotMoveTheClock()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();

        for (var i = 0; i < 4; i++)
        {
            clock = Now.AddSeconds(i * 60);
            // The third report reaches the page 30 s after the Gateway stamped it (a UI stall).
            var stamped = i == 2 ? clock - TimeSpan.FromSeconds(30) : clock;
            controller.ApplyHostStats(Stats(10 + i, stamped));
        }

        Assert.Equal(TimeSpan.Zero, controller.State.GatewayClockOffset);
        Assert.Equal(13, controller.State.Rows.Single(row => row.Id == "node:studio").Measurements?.MemoryUsedBytes);
    }

    [Fact]
    public async Task HostStats_ReportsReleasedTogetherByOneStallDoNotConfirmEachOther()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();
        controller.ApplyHostStats(Stats(10, clock));

        // A 30 s stall releases two queued reports back to back, then reports arrive on time again.
        clock = Now.AddSeconds(60);
        controller.ApplyHostStats(Stats(11, clock - TimeSpan.FromSeconds(30)));
        controller.ApplyHostStats(Stats(12, clock - TimeSpan.FromSeconds(29)));
        clock = Now.AddSeconds(120);
        controller.ApplyHostStats(Stats(13, clock));

        Assert.Equal(TimeSpan.Zero, controller.State.GatewayClockOffset);
        Assert.Equal(13, controller.State.Rows.Single(row => row.Id == "node:studio").Measurements?.MemoryUsedBytes);
    }

    [Fact]
    public async Task HostStats_ALargerOffsetIsAdoptedOnceALaterReportConfirmsIt()
    {
        var clock = Now;
        var (controller, _) = Create(() => clock);
        controller.UpdateInputs(Connected());
        await controller.RefreshAsync();
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
}
