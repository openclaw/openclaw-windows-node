using System.Collections.Immutable;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;
using OpenClawTray.Presentation;
using static OpenClaw.Tray.Tests.Dashboards.DashboardTestAsync;

namespace OpenClaw.Tray.Tests.Dashboards;

public sealed class DashboardsRouteTests
{
    [Fact]
    public void SessionRoute_RoundTripsKeysWithColonsAndInfersAgent()
    {
        var tag = DashboardsRoute.ForSession("agent:research:dashboard:study");

        Assert.Equal("dashboards?session=agent%3Aresearch%3Adashboard%3Astudy&agent=research", tag);
        Assert.Equal(new BoardSessionTarget("agent:research:dashboard:study", "research"), DashboardsRoute.TryParseSession(tag));
        Assert.True(DashboardsRoute.IsDashboardsTag(tag));
    }

    [Fact]
    public void SessionRoute_KeepsExplicitAgentAndGlobalKeys()
    {
        Assert.Equal(new BoardSessionTarget("global", "ops"), DashboardsRoute.TryParseSession(DashboardsRoute.ForSession("global", "ops")));
        Assert.Equal(new BoardSessionTarget("global", null), DashboardsRoute.TryParseSession(DashboardsRoute.ForSession("global")));
    }

    [Theory]
    [InlineData("dashboards")]
    [InlineData("dashboards?session=")]
    [InlineData("dashboards?agent=main")]
    [InlineData("sessions")]
    [InlineData(null)]
    public void SessionRoute_GalleryAndInvalidTagsOpenNoSession(string? tag) =>
        Assert.Null(DashboardsRoute.TryParseSession(tag));
}

public sealed class DashboardGalleryModelTests
{
    [Fact]
    public async Task Load_ReadsEveryPageAndDeduplicatesMovedRows()
    {
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(true, 2, "a", "b"));
        client.Pages.Enqueue(Page(false, null, "b", "c"));
        using var model = new DashboardGalleryModel(client, action => action());

        await model.LoadAsync("main");

        Assert.Equal(DashboardLoadStatus.Ready, model.Snapshot.Status);
        Assert.Equal(["a", "b", "c"], model.Snapshot.Rows.Select(row => row.Key));
        Assert.Equal([("main", 0), ("main", 2)], client.ListCalls);
    }

    [Fact]
    public async Task Load_FailureKeepsRowsAsStaleData()
    {
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(false, null, "a"));
        using var model = new DashboardGalleryModel(client, action => action());
        await model.LoadAsync("main");

        client.ListFailure = new InvalidOperationException("offline");
        await model.LoadAsync("main");

        Assert.Equal(DashboardLoadStatus.Error, model.Snapshot.Status);
        Assert.Equal("offline", model.Snapshot.Error);
        Assert.Equal(["a"], model.Snapshot.Rows.Select(row => row.Key));
    }

    [Fact]
    public async Task Load_ChangingAgentDropsPreviousAgentsRows()
    {
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(false, null, "a"));
        using var model = new DashboardGalleryModel(client, action => action());
        await model.LoadAsync("main");

        client.ListFailure = new InvalidOperationException("offline");
        await model.LoadAsync("research");

        Assert.Empty(model.Snapshot.Rows);
        Assert.Equal("research", model.Snapshot.AgentId);
    }

    [Fact]
    public async Task Load_ReportsUnavailableWithoutReadAccessOrMethod()
    {
        var client = new FakeBoardClient { Scopes = ["operator.pairing"] };
        using var model = new DashboardGalleryModel(client, action => action());
        await model.LoadAsync(null);
        Assert.Equal(DashboardLoadStatus.Unavailable, model.Snapshot.Status);

        var unsupported = new FakeBoardClient();
        unsupported.Pages.Enqueue(null);
        using var second = new DashboardGalleryModel(unsupported, action => action());
        await second.LoadAsync(null);
        Assert.Equal(DashboardLoadStatus.Unavailable, second.Snapshot.Status);
    }

    [Fact]
    public async Task Load_ReportsDisconnected()
    {
        using var model = new DashboardGalleryModel(new FakeBoardClient { Connected = false }, action => action());
        await model.LoadAsync("main");
        Assert.Equal(DashboardLoadStatus.Disconnected, model.Snapshot.Status);
    }

    [Fact]
    public async Task BoardChanged_RefreshesTheGallery()
    {
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(false, null, "a"));
        client.Pages.Enqueue(Page(false, null, "a", "b"));
        using var model = new DashboardGalleryModel(client, action => action());
        await model.LoadAsync("main");

        client.RaiseChanged(new BoardChangedEvent("b", 2, null));

        Assert.Equal(["a", "b"], model.Snapshot.Rows.Select(row => row.Key));
    }

    [Fact]
    public async Task BoardChanges_DuringALoadReloadOnceAfterIt()
    {
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(false, null, "a"));
        using var model = new DashboardGalleryModel(client, action => action());
        await model.LoadAsync("main");

        var inFlight = new TaskCompletionSource<DashboardSessionPage?>();
        client.PendingPages.Enqueue(inFlight);
        client.Pages.Enqueue(Page(false, null, "a", "b", "c"));
        client.RaiseChanged(new BoardChangedEvent("b", 2, null));
        client.RaiseChanged(new BoardChangedEvent("c", 1, null));
        client.RaiseChanged(new BoardChangedEvent("c", 2, null));
        Assert.Equal(2, client.ListCalls.Count);

        await CompleteOffContext(() => inFlight.SetResult(Page(false, null, "a", "b")));

        Assert.Equal(3, client.ListCalls.Count);
        Assert.Equal(["a", "b", "c"], model.Snapshot.Rows.Select(row => row.Key));
    }

    [Fact]
    public async Task QueuedDispatcher_BoardChangeQueuedDuringALoadDoesNotDiscardItsResult()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var client = new FakeBoardClient();
        client.Pages.Enqueue(Page(false, null, "a"));
        using var model = new DashboardGalleryModel(client, queue.Enqueue);
        var published = new List<string>();
        model.Changed += snapshot => published.Add(string.Join(",", snapshot.Rows.Select(row => row.Key)));
        await DashboardTestDispatcher.Pump(queue, model.LoadAsync("main"));

        var inFlight = new TaskCompletionSource<DashboardSessionPage?>();
        client.PendingPages.Enqueue(inFlight);
        client.Pages.Enqueue(Page(false, null, "a", "b", "c"));
        var load = model.LoadAsync("main");
        client.RaiseChanged(new BoardChangedEvent("c", 1, null));
        await CompleteOffContext(() => inFlight.SetResult(Page(false, null, "a", "b")));
        await DashboardTestDispatcher.Pump(queue, load);
        await DashboardTestDispatcher.Pump(queue, Task.Delay(50));

        Assert.Equal(["", "a", "a,b", "a,b,c"], published);
    }

    internal static DashboardSessionPage Page(bool hasMore, int? next, params string[] keys) =>
        new([.. keys.Select(key => new DashboardSessionRow { Session = new SessionInfo { Key = key, AgentId = "main" } })], hasMore, next, keys.Length);
}

public sealed class DashboardsPageStateTests
{
    [Theory]
    [InlineData("main", null, "research")]
    [InlineData("main", "ops", "ops")]
    [InlineData("ops", null, "ops")]
    [InlineData("ops", "research", "research")]
    [InlineData("main", "unlisted", "unlisted")]
    public void Navigation_ExplicitAgentWinsOtherwisePreservesSelectionUntilTheHubChanges(
        string hubAgent, string? routeAgent, string expectedAgent)
    {
        var state = new DashboardsPageState
        {
            Post = action => action(),
            Schedule = new FakeScheduler().Schedule,
        };
        state.ApplyNavigation(["main", "research", "ops"], "main", null);
        state.SelectAgent("research");
        var target = routeAgent is null ? null : new BoardSessionTarget("global", routeAgent);

        state.ApplyNavigation(["main", "research", "ops"], hubAgent, target);

        Assert.Equal(expectedAgent, state.AgentId);
        Assert.Contains(expectedAgent, state.AgentIds);
        Assert.Equal(target, state.OpenTarget);
    }
}

public sealed class BoardDocumentModelTests
{
    private static readonly BoardSessionTarget Target = new("agent:main:dashboard:ops", "main");

    [Fact]
    public async Task Load_SelectsFirstTabAndRetainsVisitedTabs()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("overview", 0), ("metrics", 1)));
        client.Boards.Enqueue(Board(2, ("overview", 0), ("metrics", 1)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);

        await model.LoadAsync();
        Assert.Equal("overview", model.Snapshot.ActiveTabId);

        model.SelectTab("metrics");
        model.SelectTab("missing");
        await model.LoadAsync();

        Assert.Equal("metrics", model.Snapshot.ActiveTabId);
        Assert.Equal(["metrics", "overview"], model.Snapshot.VisitedTabs.Order());
        Assert.Equal(2, model.Snapshot.Board!.Revision);
        Assert.Equal([new PluginWidgetKind("session", "session:report", "Report")], model.Snapshot.PluginWidgetKinds.ToArray());
    }

    [Fact]
    public async Task Load_SchedulesTicketRenewalBeforeExpiry()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)) with { Widgets = [Widget("a", 600_000), Widget("b", 100_000)] });
        var scheduler = new FakeScheduler();
        using var model = new BoardDocumentModel(client, Target, action => action(), scheduler.Schedule);

        await model.LoadAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(80_000), Assert.Single(scheduler.Delays));
    }

    [Fact]
    public async Task Unauthorized_RefreshesThenFailsAfterRepeatedRejection()
    {
        var client = new FakeBoardClient();
        for (var i = 0; i < 4; i++)
            client.Boards.Enqueue(Board(i, ("main", 0)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        model.ReportWidgetUnauthorized("chart");
        model.ReportWidgetUnauthorized("chart");
        Assert.DoesNotContain("chart", model.Snapshot.TicketFailures.Keys);

        model.ReportWidgetUnauthorized("chart");
        Assert.Equal(BoardWidgetTicketFailure.RepeatedRejection, model.Snapshot.TicketFailures["chart"]);

        model.RetryWidget("chart");
        Assert.DoesNotContain("chart", model.Snapshot.TicketFailures.Keys);
    }

    [Fact]
    public async Task Events_RefreshAndFocusOnlyTheirOwnBoard()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("overview", 0), ("metrics", 1)));
        client.Boards.Enqueue(Board(2, ("overview", 0), ("metrics", 1)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        client.RaiseCommand(new BoardCommandEvent("agent:main:other", "focus_tab", "metrics", null));
        Assert.Equal("overview", model.Snapshot.ActiveTabId);
        client.RaiseCommand(new BoardCommandEvent(Target.SessionKey, "focus_tab", "metrics", null));
        Assert.Equal("metrics", model.Snapshot.ActiveTabId);

        client.RaiseChanged(new BoardChangedEvent("agent:main:other", 9, null));
        Assert.Equal(1, client.BoardCalls);
        client.RaiseChanged(new BoardChangedEvent(Target.SessionKey, 2, null));
        Assert.Equal(2, model.Snapshot.Board!.Revision);
    }

    [Fact]
    public async Task Changes_DuringALoadCoalesceIntoOneFollowUpLoad()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        var inFlight = new TaskCompletionSource<BoardSnapshot>();
        client.PendingBoards.Enqueue(inFlight);
        client.Boards.Enqueue(Board(5, ("main", 0)));
        for (var revision = 2; revision <= 4; revision++)
            client.RaiseChanged(new BoardChangedEvent(Target.SessionKey, revision, null));
        Assert.Equal(2, client.BoardCalls);

        await CompleteOffContext(() => inFlight.SetResult(Board(2, ("main", 0))));

        Assert.Equal(3, client.BoardCalls);
        Assert.Equal(5, model.Snapshot.Board!.Revision);
    }

    [Fact]
    public async Task FocusTab_ForATabStillLoadingAppliesWhenItArrives()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("overview", 0)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        var inFlight = new TaskCompletionSource<BoardSnapshot>();
        client.PendingBoards.Enqueue(inFlight);
        client.Boards.Enqueue(Board(3, ("overview", 0), ("fresh", 1)));
        client.RaiseChanged(new BoardChangedEvent(Target.SessionKey, 2, null));
        client.RaiseCommand(new BoardCommandEvent(Target.SessionKey, "focus_tab", "fresh", null));
        Assert.Equal("overview", model.Snapshot.ActiveTabId);

        // The in-flight response predates the tab; the follow-up load carries it.
        await CompleteOffContext(() => inFlight.SetResult(Board(2, ("overview", 0))));

        Assert.Equal(3, model.Snapshot.Board!.Revision);
        Assert.Equal("fresh", model.Snapshot.ActiveTabId);
    }

    [Fact]
    public async Task QueuedDispatcher_RerunKeepsTheQueuedResult()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)));
        using var model = new BoardDocumentModel(client, Target, queue.Enqueue, new FakeScheduler().Schedule);
        var published = new List<int>();
        model.Changed += snapshot => published.Add(snapshot.Board?.Revision ?? 0);
        await DashboardTestDispatcher.Pump(queue, model.LoadAsync());

        var inFlight = new TaskCompletionSource<BoardSnapshot>();
        client.PendingBoards.Enqueue(inFlight);
        client.Boards.Enqueue(Board(3, ("main", 0)));
        var load = model.LoadAsync();
        _ = model.LoadAsync();
        await CompleteOffContext(() => inFlight.SetResult(Board(2, ("main", 0))));
        await DashboardTestDispatcher.Pump(queue, load);

        Assert.Equal([1, 2, 3], published);
    }

    [Fact]
    public async Task FailedRefresh_ShowsRetryForWidgetsWaitingOnATicket()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        client.BoardFailure = new InvalidOperationException("board unavailable");
        model.ReportWidgetUnauthorized("chart");
        await model.LoadAsync();

        Assert.Equal(BoardWidgetTicketFailure.RefreshUnavailable, model.Snapshot.TicketFailures["chart"]);
        Assert.NotNull(model.Snapshot.Board);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotRejectAWidgetAlreadyGivenANewTicket()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)) with
        {
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=old" }],
        });
        client.Boards.Enqueue(Board(2, ("main", 0)) with
        {
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=new" }],
        });
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();
        model.ReportWidgetUnauthorized("chart");

        client.BoardFailure = new InvalidOperationException("unrelated refresh failed");
        await model.LoadAsync();

        Assert.Equal("/chart?bt=new", Assert.Single(model.Snapshot.Board!.Widgets).FrameUrl);
        Assert.DoesNotContain("chart", model.Snapshot.TicketFailures.Keys);
    }

    [Fact]
    public async Task RenewedTicket_ClearsAnEarlierRefreshFailure()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)) with
        {
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=old" }],
        });
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();
        client.BoardFailure = new InvalidOperationException("ticket refresh failed");
        model.ReportWidgetUnauthorized("chart");
        Assert.Equal(BoardWidgetTicketFailure.RefreshUnavailable, model.Snapshot.TicketFailures["chart"]);

        client.BoardFailure = null;
        client.Boards.Enqueue(Board(2, ("main", 0)) with
        {
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=new" }],
        });
        await model.LoadAsync();

        Assert.DoesNotContain("chart", model.Snapshot.TicketFailures.Keys);
    }

    [Fact]
    public async Task RepeatedRejection_KeepsItsReasonUntilExplicitRetry()
    {
        var client = new FakeBoardClient();
        var original = Board(1, ("main", 0)) with
        {
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=old" }],
        };
        client.Boards.Enqueue(original);
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();
        client.BoardFailure = new InvalidOperationException("refresh unavailable");
        for (var attempt = 0; attempt < BoardDocumentModel.MaxUnauthorizedRefreshes; attempt++)
            model.ReportWidgetUnauthorized("chart");

        await model.LoadAsync();
        Assert.Equal(BoardWidgetTicketFailure.RepeatedRejection, model.Snapshot.TicketFailures["chart"]);

        client.BoardFailure = null;
        client.Boards.Enqueue(original with
        {
            Revision = 2,
            Widgets = [Widget("chart", 100_000) with { FrameUrl = "/chart?bt=new" }],
        });
        await model.LoadAsync();
        Assert.Equal(BoardWidgetTicketFailure.RepeatedRejection, model.Snapshot.TicketFailures["chart"]);

        client.Boards.Enqueue(original with { Revision = 3 });
        model.RetryWidget("chart");
        Assert.DoesNotContain("chart", model.Snapshot.TicketFailures.Keys);
    }

    [Fact]
    public async Task RefreshFailure_KeepsTheRenderedBoard()
    {
        var client = new FakeBoardClient();
        client.Boards.Enqueue(Board(1, ("main", 0)));
        using var model = new BoardDocumentModel(client, Target, action => action(), new FakeScheduler().Schedule);
        await model.LoadAsync();

        client.BoardFailure = new InvalidOperationException("board unavailable");
        await model.LoadAsync();

        Assert.Equal(DashboardLoadStatus.Ready, model.Snapshot.Status);
        Assert.Equal("board unavailable", model.Snapshot.Error);
        Assert.NotNull(model.Snapshot.Board);
    }

    private static BoardSnapshot Board(int revision, params (string Id, int Position)[] tabs) =>
        new(Target.SessionKey, revision, [.. tabs.Select(tab => new BoardTab(tab.Id, tab.Id, tab.Position, "right"))], []);

    private static BoardWidget Widget(string name, int ttl) => new()
    {
        Name = name,
        TabId = "main",
        ContentKind = BoardWidgetContentKind.Html,
        SizeW = 6,
        SizeH = 4,
        Position = 0,
        Revision = 1,
        ViewTicket = "t-" + name,
        ViewTicketTtlMs = ttl,
    };
}

public sealed class BoardPreviewSlotLeaseTests
{
    [Fact]
    public void RetryQueuedBeforeDisposeClaimsNoSlot()
    {
        var slots = new BoardPreviewSlots(1);
        var queued = new List<Action>();
        var other = new object();
        Assert.True(slots.TryAcquire(other));
        var lease = new BoardPreviewSlotLease(slots, ["chart"], queued.Add, _ => { });

        slots.Release(other);
        lease.Dispose();
        foreach (var retry in queued)
            retry();

        Assert.Equal(0, slots.InUse);
    }

    [Fact]
    public void WaitingPreviewTakesTheNextFreedSlot()
    {
        var slots = new BoardPreviewSlots(1);
        var granted = ImmutableHashSet<string>.Empty;
        var first = new BoardPreviewSlotLease(slots, ["a"], action => action(), _ => { });
        using var second = new BoardPreviewSlotLease(slots, ["b"], action => action(), next => granted = next);
        Assert.Empty(granted);

        first.Dispose();

        Assert.Equal(["b"], granted);
    }
}

public sealed class BoardWidgetWebHostRequestTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:18789/");

    [Fact]
    public void DocumentKey_KeepsTheDocumentAcrossTicketRenewal()
    {
        var widget = new BoardWidget
        {
            Name = "chart",
            TabId = "main",
            ContentKind = BoardWidgetContentKind.Html,
            SizeW = 6,
            SizeH = 4,
            Position = 0,
            Revision = 3,
            FrameUrl = "/__openclaw__/board/k/chart/index.html?bt=one",
            ViewTicket = "one",
        };
        var key = new BoardWidgetWebHostRequest(widget, Origin).DocumentKey;

        Assert.Equal(key, new BoardWidgetWebHostRequest(widget with { FrameUrl = "/__openclaw__/board/k/chart/index.html?bt=two", ViewTicket = "two" }, Origin).DocumentKey);
        Assert.NotEqual(key, new BoardWidgetWebHostRequest(widget with { Revision = 4 }, Origin).DocumentKey);

        // Preview resizes rescale in place; switching between preview and full view reloads.
        var preview = new BoardWidgetWebHostRequest(widget, Origin, Scale: 0.25).DocumentKey;
        Assert.Equal(preview, new BoardWidgetWebHostRequest(widget, Origin, Scale: 0.31).DocumentKey);
        Assert.NotEqual(key, preview);
    }
}

public sealed class BoardWidgetRetryPolicyTests
{
    [Theory]
    [InlineData(true, 3, 1, null)]
    [InlineData(true, 3, 3, null)]
    [InlineData(true, 1, 4, BoardWidgetWebHostStatus.Failed)]
    [InlineData(false, 2, 0, null)]
    [InlineData(false, 3, 0, BoardWidgetWebHostStatus.SandboxUnreachable)]
    [InlineData(false, 3, 3, BoardWidgetWebHostStatus.SandboxUnreachable)]
    public void TerminalStatus_CrashesAreNotSandboxFailures(
        bool crashed, int frameAttempts, int crashRestarts, object? expected)
    {
        Assert.Equal(expected, BoardWidgetRetryPolicy.TerminalStatus(crashed, frameAttempts, crashRestarts));
    }
}

internal static class DashboardTestAsync
{
    /// <summary>
    /// Completes an in-flight fake response off the test's synchronization
    /// context, so the model's continuations (and the inline post) finish
    /// before the returned task does.
    /// </summary>
    public static Task CompleteOffContext(Action complete) => Task.Run(complete);
}

/// <summary>A queued (non-inline) stand-in for the UI dispatcher.</summary>
internal static class DashboardTestDispatcher
{
    /// <summary>Runs queued callbacks until <paramref name="until"/> completes, then drains the queue.</summary>
    public static async Task Pump(System.Collections.Concurrent.ConcurrentQueue<Action> queue, Task until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until.IsCompleted && DateTime.UtcNow < deadline)
        {
            while (queue.TryDequeue(out var action))
                action();
            await Task.Delay(1);
        }
        while (queue.TryDequeue(out var action))
            action();
        await until;
    }
}
internal sealed class FakeScheduler
{
    public List<TimeSpan> Delays { get; } = [];

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        Delays.Add(delay);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class FakeBoardClient : IBoardGatewayClient
{
    public bool Connected { get; set; } = true;
    public string[] Scopes { get; set; } = ["operator.read", "operator.write", "operator.approvals"];
    public Queue<DashboardSessionPage?> Pages { get; } = new();
    public Queue<BoardSnapshot> Boards { get; } = new();
    /// <summary>Responses that stay in flight until the test completes them; used before <see cref="Pages"/>/<see cref="Boards"/>.</summary>
    public Queue<TaskCompletionSource<DashboardSessionPage?>> PendingPages { get; } = new();
    public Queue<TaskCompletionSource<BoardSnapshot>> PendingBoards { get; } = new();
    public Exception? ListFailure { get; set; }
    public Exception? BoardFailure { get; set; }
    public List<(string? Agent, int Offset)> ListCalls { get; } = [];
    public int BoardCalls { get; private set; }

    public bool IsConnectedToGateway => Connected;
    public IReadOnlyList<string> GrantedOperatorScopes => Scopes;
    public ImmutableArray<PluginWidgetKind> PluginWidgetKinds => [new("session", "session:report", "Report")];
    public Uri? GatewayHttpOrigin => new("http://127.0.0.1:18789/");
    public IReadOnlyList<string> AdvertisedServerMethods => ["sessions.list", "board.get", "board.update", "board.widget.grant"];

    public event EventHandler<BoardChangedEvent>? BoardChanged;
    public event EventHandler<BoardCommandEvent>? BoardCommandReceived;

    public void RaiseChanged(BoardChangedEvent e) => BoardChanged?.Invoke(this, e);
    public void RaiseCommand(BoardCommandEvent e) => BoardCommandReceived?.Invoke(this, e);

    public Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000)
    {
        lock (ListCalls) ListCalls.Add((agentId, offset));
        if (ListFailure is { } failure)
            return Task.FromException<DashboardSessionPage?>(failure);
        if (PendingPages.Count > 0)
            return PendingPages.Dequeue().Task;
        return Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : DashboardGalleryModelTests.Page(false, null));
    }

    public Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000)
    {
        BoardCalls++;
        if (BoardFailure is { } failure)
            return Task.FromException<BoardSnapshot>(failure);
        if (PendingBoards.Count > 0)
            return PendingBoards.Dequeue().Task;
        return Task.FromResult(Boards.Dequeue());
    }
}
