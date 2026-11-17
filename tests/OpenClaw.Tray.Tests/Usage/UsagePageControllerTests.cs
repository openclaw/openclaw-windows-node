using System.Collections.Concurrent;
using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsagePageControllerTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    public class UsageGateway : DispatchProxy
    {
        public bool Connected = true;
        public long? SocketEpoch = 1;
        public readonly List<SessionsUsageQuery> Queries = [];
        public readonly List<string> Calls = [];
        public Func<SessionsUsageQuery, Task<UsageReadResult<SessionsUsageReport>>> Usage =
            _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a")));
        public Func<Task<UsageReadResult<ProviderUsageSummary>>> Providers =
            () => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(new ProviderUsageSummary(null, [], false)));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            return method.Name switch
            {
                "get_IsConnectedToGateway" => Connected,
                "get_SessionMutationConnectionEpoch" => Connected ? SocketEpoch : null,
                "GetSessionsUsageAsync" => Query((SessionsUsageQuery)args![0]!),
                "GetProviderUsageAsync" => Providers(),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<UsageReadResult<SessionsUsageReport>> Query(SessionsUsageQuery query)
        {
            Queries.Add(query);
            return Usage(query);
        }
    }

    /// <summary>Collects posted completions so the test controls when they reach the UI thread.</summary>
    private sealed class ManualDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private int _posted;
        public int Posted => Volatile.Read(ref _posted);

        public void Post(Action action)
        {
            _queue.Enqueue(action);
            Interlocked.Increment(ref _posted);
        }

        public async Task PumpUntilAsync(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                while (_queue.TryDequeue(out var action)) action();
                if (condition()) return;
                await Task.Delay(5, deadline.Token);
            }
        }

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }

    private static SessionsUsageReport Report(params string[] keys) => new()
    {
        Sessions = keys.Select(k => new SessionUsageRow { Key = k, AgentId = "main", Usage = new SessionUsageSummary() }).ToList(),
        Totals = new UsageTotals { TotalTokens = 10, Report = UsageReportState.Available },
        CacheStatus = new UsageCacheStatus("fresh", 0, 0, null),
    };

    private static (UsagePageController Controller, UsageGateway Gateway, ManualDispatcher Dispatcher) Create(
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateOnly>? today = null, Func<DateOnly>? utcToday = null)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var dispatcher = new ManualDispatcher();
        var controller = new UsagePageController(() => client, today ?? (() => Today), dispatcher.Post, delay, utcToday);
        return (controller, (UsageGateway)client, dispatcher);
    }

    [Fact]
    public async Task Activate_LoadsTheOverviewProjectionAndProviderUsage()
    {
        var (controller, gateway, dispatcher) = Create();

        controller.Activate();
        Assert.Equal(UsageLoadPhase.Loading, controller.State.Phase);
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded && controller.State.Providers.Phase == UsageLoadPhase.Loaded);

        var query = Assert.Single(gateway.Queries);
        Assert.True(query.OverviewProjection);
        Assert.Equal("2026-10-20", query.StartDate);
        Assert.Equal(UsageAvailability.Connected, controller.State.Availability);
        Assert.Equal("agent:main:a", Assert.Single(controller.State.Dashboard!.Sessions.Rows).Key);
    }

    [Fact]
    public void Activate_WhileDisconnectedShowsTheDisconnectedStateWithoutRequests()
    {
        var (controller, gateway, _) = Create();
        gateway.Connected = false;

        controller.Activate();

        Assert.Equal(UsageAvailability.Disconnected, controller.State.Availability);
        Assert.Empty(gateway.Queries);
    }

    [Fact]
    public async Task UnsupportedGateway_ShowsNoFiguresAndStillLoadsProviderPlans()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Unsupported }
            && controller.State.Providers.Phase == UsageLoadPhase.Loaded);

        // Older than any Gateway setup installs: no substitute read and no partial figures.
        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.False(controller.State.ShowLoadError);
        Assert.Equal(["GetProviderUsageAsync", "GetSessionsUsageAsync", "get_IsConnectedToGateway", "get_SessionMutationConnectionEpoch"],
            gateway.Calls.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DisconnectingMidLoad_SettlesEveryLoadingSection()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded && controller.State.Providers.Phase == UsageLoadPhase.Loaded);

        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        gateway.Usage = _ => pending.Task;
        controller.Refresh();
        Assert.Equal(UsageLoadPhase.Loading, controller.State.Phase);

        gateway.Connected = false;
        controller.Activate();
        dispatcher.Drain();

        Assert.Equal(UsageAvailability.Disconnected, controller.State.Availability);
        Assert.Equal(UsageLoadPhase.Loaded, controller.State.Phase);
        Assert.Equal(UsageLoadPhase.Idle, controller.State.Providers.Phase);
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:late")));
        dispatcher.Drain();
        Assert.Equal("agent:main:a", Assert.Single(controller.State.Report!.Sessions).Key);
    }

    [Fact]
    public async Task ReleasedGatewayReply_DropsServerOnlyFiltersAndIgnoresThem()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.SetQuery("agent:main");
        controller.SetSort(UsageSort.Tokens);
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a", "agent:main:b")));

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);

        Assert.True(controller.State.BasicGateway);
        Assert.Equal("", controller.State.Filters.Query);
        var requests = gateway.Queries.Count;

        controller.SetQuery("model:gpt");
        controller.SetSort(UsageSort.Errors);
        controller.ToggleSortDirection();
        controller.SetPage(50);
        controller.SelectSession("agent:main:b", "main");
        await dispatcher.PumpUntilAsync(() => controller.State.Detail?.Row.Phase == UsageLoadPhase.Loaded);
        controller.CloseDetail();

        // Search, sort and paging are ignored; only the single-session detail read went to the Gateway.
        Assert.Equal(requests, gateway.Queries.Count(q => q.Key is null));
        Assert.Equal("", controller.State.Filters.Query);
        Assert.Equal(0, controller.State.Filters.Offset);
        Assert.True(controller.State.Filters.SortDescending);

        // A Gateway that returns the overview re-enables them.
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a") with { Overview = new UsageOverview() }));
        controller.Refresh();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, BasicGateway: false });
        controller.SetQuery("model:gpt");
        Assert.Equal("model:gpt", controller.State.Filters.Query);
    }

    [Fact]
    public async Task SupersededOverviewReplies_AreDiscarded()
    {
        var (controller, gateway, dispatcher) = Create();
        var slow = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        gateway.Usage = _ => slow.Task;
        controller.Activate();

        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:new")));
        controller.SetPreset(UsagePreset.Last30Days);
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        slow.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:stale")));
        await Task.Delay(20);
        dispatcher.Drain();

        Assert.Equal("agent:main:new", Assert.Single(controller.State.Report!.Sessions).Key);
        Assert.Equal("2026-09-27", gateway.Queries[^1].StartDate);
    }

    [Fact]
    public async Task SelectingASession_ReadsThatSessionWithoutReloadingTheOverviewAndTogglesClosed()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        var overviewReads = gateway.Queries.Count;

        controller.SelectSession("agent:main:a", "main");
        await dispatcher.PumpUntilAsync(() => controller.State.Detail is { Row.Phase: UsageLoadPhase.Loaded });

        var detailQuery = Assert.Single(gateway.Queries, q => q.Key is not null);
        Assert.Equal(("agent:main:a", "main"), (detailQuery.Key, detailQuery.AgentId));
        Assert.Equal(("2026-10-20", "2026-10-26"), (detailQuery.StartDate, detailQuery.EndDate));
        Assert.Equal(overviewReads, gateway.Queries.Count(q => q.Key is null));
        Assert.Equal("agent:main:a", controller.State.Detail!.Row.Value!.Key);
        Assert.True(Assert.Single(controller.State.Dashboard!.Sessions.Rows).Selected);

        controller.SelectSession("agent:main:a", "main");
        Assert.Null(controller.State.Detail);
        Assert.False(Assert.Single(controller.State.Dashboard!.Sessions.Rows).Selected);
    }

    [Fact]
    public async Task OpenDetails_ReloadForANewRangeButNotForSearchSortOrPaging()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a") with { Overview = new UsageOverview() }));
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        controller.SelectSession("agent:main:a", "main");
        await dispatcher.PumpUntilAsync(() => controller.State.Detail is { Row.Phase: UsageLoadPhase.Loaded });
        int DetailReads() => gateway.Queries.Count(q => q.Key is not null);

        controller.SetQuery("model:gpt");
        controller.SetSort(UsageSort.Tokens);
        controller.SetPage(50);
        Assert.Equal(1, DetailReads());

        controller.SetPreset(UsagePreset.Last30Days);
        Assert.Equal(2, DetailReads());
        Assert.Equal("2026-09-27", gateway.Queries[^1].StartDate);
        controller.SetScope(UsageScope.Family);
        Assert.Equal(UsageScope.Family, Assert.Single(gateway.Queries.Skip(gateway.Queries.Count - 2), q => q.Key is not null).Scope);
    }

    [Fact]
    public async Task IncompleteUsage_IsRecheckedAutomaticallyThenPauses()
    {
        var delays = 0;
        var (controller, gateway, dispatcher) = Create((_, _) => { delays++; return Task.CompletedTask; });
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("a") with
        {
            CacheStatus = new UsageCacheStatus("refreshing", 3, 0, null),
        }));

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.AutoRefreshPaused);

        Assert.Equal(UsageRefreshPolicy.MaxAutomaticRechecks, delays);
        Assert.Equal(UsageRefreshPolicy.MaxAutomaticRechecks + 1, gateway.Queries.Count);

        controller.Refresh();
        Assert.False(controller.State.AutoRefreshPaused);
    }

    [Fact]
    public async Task ForbiddenReads_ReportTheMissingScope()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Forbidden("missing scope: operator.read"));

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Failed);

        Assert.Equal(UsageAvailability.Forbidden, controller.State.Availability);
    }

    [Fact]
    public void QueryTermToggle_AddsAndRemovesChips()
    {
        var (controller, gateway, _) = Create();

        controller.ToggleQueryTerm("model:gpt");
        controller.ToggleQueryTerm("agent:main");
        Assert.Equal("model:gpt agent:main", controller.State.Filters.Query);
        controller.ToggleQueryTerm("model:gpt");
        Assert.Equal("agent:main", controller.State.Filters.Query);
        Assert.Equal("agent:main", gateway.Queries[^1].Query);
    }

    [Fact]
    public async Task Export_RefusesIncompleteResults()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("a") with
        {
            CacheStatus = new UsageCacheStatus("partial", 3, 0, null),
        }));

        var (content, error) = await controller.ExportAsync(UsageExportFormat.SessionsCsv);
        dispatcher.Drain();

        Assert.Null(content);
        Assert.Equal(UsageExportError.Incomplete, error);
        var query = Assert.Single(gateway.Queries);
        Assert.Equal(SessionsUsageQuery.FullRowLimit, query.Limit);
        Assert.False(controller.State.Exporting);
    }

    [Fact]
    public async Task Export_RefusesSessionsPastTheRowLimitAndResultsForAClosedPage()
    {
        var (controller, gateway, dispatcher) = Create();
        // Full-row reads stop at the row limit while aggregates count every matching session.
        var capped = Report("a") with { Aggregates = new UsageAggregates { SessionCount = 1500 } };
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(capped));

        Assert.Equal(UsageExportError.TooMany, (await controller.ExportAsync(UsageExportFormat.SessionsCsv)).Error);
        Assert.NotNull((await controller.ExportAsync(UsageExportFormat.DailyCsv)).Content);

        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        gateway.Usage = _ => pending.Task;
        var export = controller.ExportAsync(UsageExportFormat.SessionsCsv);
        controller.Dispose();
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("a")));

        Assert.Equal((null, null), await export);
        dispatcher.Drain();
    }

    [Fact]
    public async Task UnsupportedGateway_DropsAndIgnoresServerFilters()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
        controller.SetQuery("agent:main");

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Unsupported });

        Assert.True(controller.State.ServerFiltersUnavailable);
        Assert.Equal("", controller.State.Filters.Query);
        controller.SetQuery("model:gpt");
        controller.SetPage(50);
        Assert.Equal("", controller.State.Filters.Query);
        Assert.Equal(0, controller.State.Filters.Offset);
    }

    [Fact]
    public async Task UnsupportedReply_ClosesSelectionAndRejectsLateDetailReplies()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Usage = q => q.Key is not null ? pending.Task
            : Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());

        controller.SelectSession("agent:main:a", "main");
        controller.Refresh();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Unsupported });

        Assert.Null(controller.State.Detail);
        dispatcher.Drain();
        var postedBeforeReply = dispatcher.Posted;
        var pendingReads = gateway.Queries.Count(query => query.Key is not null);
        Assert.True(pendingReads > 0);
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a")));
        await dispatcher.PumpUntilAsync(() => dispatcher.Posted >= postedBeforeReply + pendingReads);
        dispatcher.Drain();
        Assert.Null(controller.State.Detail);
        controller.SelectSession("agent:main:a", "main");
        Assert.Null(controller.State.Detail);
    }

    [Fact]
    public async Task ReloadFailure_ShowsTheErrorWhileRetainingTheLastSuccessfulReport()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Connected });
        var previous = controller.State.Report;
        Assert.NotNull(previous);
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("Usage read failed"));

        controller.Refresh();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Failed);

        Assert.Equal(UsageAvailability.Connected, controller.State.Availability);
        Assert.Same(previous, controller.State.Report);
        Assert.Equal("Usage read failed", controller.State.Error);
        Assert.True(controller.State.ShowLoadError);
    }

    [Theory]
    [InlineData("range", true)]
    [InlineData("custom", true)]
    [InlineData("scope", true)]
    [InlineData("zone", true)]
    [InlineData("query", true)]
    [InlineData("sort", true)]
    [InlineData("page", true)]
    [InlineData("range", false)]
    [InlineData("custom", false)]
    [InlineData("scope", false)]
    [InlineData("zone", false)]
    [InlineData("query", false)]
    [InlineData("sort", false)]
    [InlineData("page", false)]
    public async Task DifferentQuery_NeverRelabelsRetainedFigures(string change, bool connected)
    {
        var (controller, gateway, dispatcher) = Create();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(
            Report("old-range") with { Overview = new UsageOverview() }));
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        Assert.NotNull(controller.State.Dashboard);
        gateway.Connected = connected;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("new query failed"));

        switch (change)
        {
            case "range": controller.SetPreset(UsagePreset.Today); break;
            case "custom": controller.SetRange(Today.AddDays(-20), Today.AddDays(-10)); break;
            case "scope": controller.SetScope(UsageScope.Family); break;
            case "zone": controller.SetTimeZone(UsageTimeZoneMode.Utc); break;
            case "query": controller.SetQuery("agent:other"); break;
            case "sort": controller.SetSort(UsageSort.Tokens); break;
            case "page": controller.SetPage(50); break;
        }

        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Null(controller.State.LoadedAt);
        dispatcher.Drain();
        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Equal(connected ? UsageLoadPhase.Failed : UsageLoadPhase.Idle, controller.State.Phase);
    }

    [Fact]
    public async Task DisconnectedQueryChange_FencesThePreviousPendingRefresh()
    {
        var (controller, gateway, dispatcher) = Create();
        using var lifetime = controller;
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        gateway.Usage = _ => pending.Task;
        controller.Refresh();
        gateway.Connected = false;

        controller.SetPreset(UsagePreset.Today);
        var posted = dispatcher.Posted;
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("late-old-range")));
        await dispatcher.PumpUntilAsync(() => dispatcher.Posted > posted);
        dispatcher.Drain();

        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Equal(UsagePreset.Today, controller.State.Filters.Preset);
    }

    [Fact]
    public async Task RebasedPreset_DropsYesterdayFiguresButKeepsBasicCapability()
    {
        var today = Today;
        var (controller, gateway, dispatcher) = Create(today: () => today);
        using var lifetime = controller;
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        today = today.AddDays(1);
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("offline"));

        controller.Activate();
        dispatcher.Drain();

        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Equal(today, controller.State.Filters.End);
        Assert.True(controller.State.ServerFiltersUnavailable);
    }

    [Fact]
    public async Task InjectedProviderSource_BindsAfterActivationAndDeduplicatesPendingRead()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var gateway = (UsageGateway)client;
        var dispatcher = new ManualDispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post);
        using var controller = new UsagePageController(() => client, () => Today, dispatcher.Post, providers: source);
        var pending = new TaskCompletionSource<UsageReadResult<ProviderUsageSummary>>();
        gateway.Providers = () => pending.Task;
        controller.Activate();
        Assert.DoesNotContain("GetProviderUsageAsync", gateway.Calls);

        source.SetConnection(client, 1);
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
        source.SetConnection(client, 1);
        controller.Activate();
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
        pending.SetResult(UsageReadResult<ProviderUsageSummary>.Failed("quota service unavailable"));
        await dispatcher.PumpUntilAsync(() => controller.State.Providers.Phase == UsageLoadPhase.Failed);
        Assert.Equal(UsageLoadPhase.Failed, controller.State.Providers.Phase);
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
    }

    [Fact]
    public async Task InjectedProviderSource_ReconnectActivationWaitsForNewSocketBinding()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var gateway = (UsageGateway)client;
        var dispatcher = new ManualDispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post);
        using var controller = new UsagePageController(() => client, () => Today, dispatcher.Post, providers: source);
        source.SetConnection(client, 1);
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Providers.Phase == UsageLoadPhase.Loaded);
        gateway.SocketEpoch = 2;

        controller.Activate();
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
        source.SetConnection(client, 2);
        await dispatcher.PumpUntilAsync(() => controller.State.Providers.Phase == UsageLoadPhase.Loaded);

        Assert.Equal(2, gateway.Calls.Count(call => call == "GetProviderUsageAsync"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectedProviderSource_DelayedBindingDoesNotReadAfterDisconnectOrDisposal(bool dispose)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var gateway = (UsageGateway)client;
        var dispatcher = new ManualDispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post);
        using var controller = new UsagePageController(() => client, () => Today, dispatcher.Post, providers: source);
        controller.Activate();
        if (dispose) controller.Dispose();
        else
        {
            gateway.Connected = false;
            controller.Activate();
            gateway.Connected = true;
        }

        source.SetConnection(client, 1);
        dispatcher.Drain();

        Assert.DoesNotContain("GetProviderUsageAsync", gateway.Calls);
    }

    [Fact]
    public async Task BasicGateway_SelectionMadeDuringARefreshSurvivesItsReply()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("a", "b")));
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, BasicGateway: true });

        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        gateway.Usage = query => query.Key is null ? pending.Task : Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("b")));
        controller.Refresh();
        controller.SelectSession("b", "main");
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("a", "b")));
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);

        Assert.True(Assert.Single(controller.State.Dashboard!.Sessions.Rows, r => r.Key == "b").Selected);
    }

    [Fact]
    public async Task RelativePresets_FollowTheCalendarInTheSelectedTimeZone()
    {
        var localToday = Today;
        var (controller, gateway, dispatcher) = Create(today: () => localToday, utcToday: () => Today.AddDays(2));
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);

        // The page stayed open past midnight.
        localToday = Today.AddDays(1);
        controller.Refresh();
        Assert.Equal(("2026-10-21", "2026-10-27"), (gateway.Queries[^1].StartDate, gateway.Queries[^1].EndDate));

        controller.SetTimeZone(UsageTimeZoneMode.Utc);
        Assert.Equal("2026-10-28", gateway.Queries[^1].EndDate);

        controller.SetRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));
        localToday = Today.AddDays(5);
        controller.Refresh();
        Assert.Equal("2026-10-03", gateway.Queries[^1].EndDate);
    }

    [Fact]
    public async Task ProviderUsage_IsReadOnActivationAndRefreshOnly()
    {
        var (controller, gateway, dispatcher) = Create();
        int ProviderReads() => gateway.Calls.Count(c => c == "GetProviderUsageAsync");
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Providers.Phase == UsageLoadPhase.Loaded);

        controller.SetPreset(UsagePreset.Last30Days);
        controller.SetQuery("agent:main");
        Assert.Equal(1, ProviderReads());

        controller.Refresh();
        Assert.Equal(2, ProviderReads());
    }

    private static (UsagePageController Controller, UsageGateway A, UsageGateway B, Action SwitchToB, ManualDispatcher Dispatcher) CreateSwitchable()
    {
        var a = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var b = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var current = a;
        var dispatcher = new ManualDispatcher();
        var controller = new UsagePageController(() => current, () => Today, dispatcher.Post, utcToday: () => Today);
        return (controller, (UsageGateway)a, (UsageGateway)b, () => current = b, dispatcher);
    }

    [Fact]
    public async Task GatewaySwitch_DropsOldFiguresSelectionPlansAndExportOfferButKeepsPortablePreferences()
    {
        var (controller, a, b, switchToB, dispatcher) = CreateSwitchable();
        using var lifetime = controller;
        a.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a") with { Overview = new UsageOverview() }));
        var oldPlans = new ProviderUsageSummary(null,
            [new ProviderUsageSnapshot { Provider = "openai", DisplayName = "OpenAI", Plan = "Old Gateway plan" }], false);
        a.Providers = () => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(oldPlans));
        controller.Activate();
        controller.SetPreset(UsagePreset.Last30Days);
        controller.SetTimeZone(UsageTimeZoneMode.Utc);
        controller.SetScope(UsageScope.Family);
        controller.SetQuery("agent:main");
        controller.SetSort(UsageSort.Tokens);
        controller.ToggleSortDirection();
        controller.SetPage(50);
        controller.SetMetric(UsageMetric.Tokens);
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Providers.Phase: UsageLoadPhase.Loaded });
        controller.SelectSession("agent:main:a", "main");
        controller.InspectProvider("openai");
        await dispatcher.PumpUntilAsync(() => controller.State.Detail is { Row.Phase: UsageLoadPhase.Loaded });
        Assert.NotNull(controller.State.Report);
        Assert.NotNull(controller.State.Dashboard);
        Assert.NotNull(controller.State.LoadedAt);
        Assert.Same(oldPlans, controller.State.Providers.Value);
        await controller.ExportAsync(UsageExportFormat.DailyCsv);
        dispatcher.Drain();
        Assert.NotNull(controller.State.ExportCoverage);
        var exportOffer = controller.OfferDailyExportAsync(controller.State.Filters);
        Assert.NotNull(controller.State.ExportBlock);

        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>();
        b.Usage = _ => pending.Task;
        switchToB();
        controller.Activate();

        // Nothing read from A is shown while B loads, and B is never asked for the session selected on A.
        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Null(controller.State.Detail);
        Assert.Null(controller.State.FocusProvider);
        Assert.Null(controller.State.Providers.Value);
        Assert.Null(controller.State.LoadedAt);
        Assert.Null(controller.State.Error);
        Assert.Null(controller.State.ExportCoverage);
        Assert.Null(controller.State.ExportBlock);
        Assert.True(exportOffer.IsCompletedSuccessfully);
        Assert.False(await exportOffer);
        Assert.Equal(UsageLoadPhase.Loading, controller.State.Phase);
        var query = Assert.Single(b.Queries);
        Assert.Null(query.Key);
        Assert.Equal(("2026-09-27", "agent:main"), (query.StartDate, query.Query));
        Assert.Equal((UsagePreset.Last30Days, "agent:main"), (controller.State.Filters.Preset, controller.State.Filters.Query));
        Assert.Equal(0, controller.State.Filters.Offset);
        Assert.Equal(0, query.Offset);
        Assert.Equal(UsageTimeZoneMode.Utc, query.TimeZone);
        Assert.Equal(UsageScope.Family, query.Scope);
        Assert.Equal(UsageSort.Tokens, query.Sort);
        Assert.False(query.SortDescending);
        Assert.Equal(UsageMetric.Tokens, controller.State.Metric);
    }

    [Fact]
    public async Task GatewaySwitch_IgnoresLateRepliesFromTheOldGatewayAndAFailedReadShowsNoOldFigures()
    {
        var (controller, a, b, switchToB, dispatcher) = CreateSwitchable();
        using var lifetime = controller;
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Providers.Phase: UsageLoadPhase.Loaded });

        var lateOverview = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDetail = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateProviders = new TaskCompletionSource<UsageReadResult<ProviderUsageSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Usage = query => query.Key is null ? lateOverview.Task : lateDetail.Task;
        a.Providers = () => lateProviders.Task;
        controller.Refresh();
        controller.SelectSession("agent:main:a", "main");

        b.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("B read failed"));
        var newPlans = new ProviderUsageSummary(null,
            [new ProviderUsageSnapshot { Provider = "anthropic", DisplayName = "Claude", Plan = "New Gateway plan" }], false);
        b.Providers = () => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(newPlans));
        switchToB();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Failed, Providers.Phase: UsageLoadPhase.Loaded });

        var posted = dispatcher.Posted;
        var lateSummary = new ProviderUsageSummary(null, [], false);
        lateOverview.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:late")));
        lateDetail.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a")));
        lateProviders.SetResult(UsageReadResult<ProviderUsageSummary>.Ok(lateSummary));
        await dispatcher.PumpUntilAsync(() => dispatcher.Posted >= posted + 3);
        dispatcher.Drain();

        Assert.Null(controller.State.Report);
        Assert.Null(controller.State.Dashboard);
        Assert.Null(controller.State.Detail);
        Assert.Same(newPlans, controller.State.Providers.Value);
        Assert.True(controller.State.ShowLoadError);
        Assert.Equal("B read failed", controller.State.Error);
        Assert.DoesNotContain(b.Queries, query => query.Key is not null);
    }

    /// <summary>The connection manager replaces its client off the UI thread, so it can change between two reads.</summary>
    private static (UsagePageController Controller, UsageGateway A, UsageGateway B, Action ReplaceAfterNextRead, ManualDispatcher Dispatcher) CreateReplacedMidOperation()
    {
        var a = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var b = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var current = a;
        var replace = false;
        var dispatcher = new ManualDispatcher();
        var controller = new UsagePageController(() =>
        {
            var client = current;
            if (replace) (replace, current) = (false, b);
            return client;
        }, () => Today, dispatcher.Post, utcToday: () => Today);
        return (controller, (UsageGateway)a, (UsageGateway)b, () => replace = true, dispatcher);
    }

    [Fact]
    public async Task GatewaySwitchDuringSelection_NeverSendsTheOldSessionKeyToTheNewGateway()
    {
        var (controller, a, b, replaceAfterNextRead, dispatcher) = CreateReplacedMidOperation();
        using var lifetime = controller;
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Providers.Phase: UsageLoadPhase.Loaded });

        replaceAfterNextRead();
        controller.SelectSession("agent:main:a", "main");

        Assert.Empty(b.Calls);
        Assert.Equal("agent:main:a", a.Queries[^1].Key);

        // The queued replacement notification then drops the selection before B is read.
        controller.Activate();
        dispatcher.Drain();
        Assert.Null(controller.State.Detail);
        Assert.Null(Assert.Single(b.Queries).Key);
    }

    [Fact]
    public async Task GatewaySwitchDuringActivation_KeepsEveryReadOnOneGatewayAndTheNextActivationFencesThem()
    {
        var (controller, a, b, replaceAfterNextRead, dispatcher) = CreateReplacedMidOperation();
        using var lifetime = controller;
        b.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:b")));
        var oldPlans = new ProviderUsageSummary(null,
            [new ProviderUsageSnapshot { Provider = "openai", DisplayName = "OpenAI", Plan = "Old Gateway plan" }], false);
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Providers.Phase: UsageLoadPhase.Loaded });
        controller.SelectSession("agent:main:a", "main");
        await dispatcher.PumpUntilAsync(() => controller.State.Detail is { Row.Phase: UsageLoadPhase.Loaded });
        var lateProviders = new TaskCompletionSource<UsageReadResult<ProviderUsageSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Providers = () => lateProviders.Task;
        var readsBefore = a.Queries.Count;

        replaceAfterNextRead();
        controller.Activate();

        // Overview, plans and the open detail all go to the client this activation fenced, never half to B.
        Assert.Empty(b.Calls);
        Assert.Equal(new string?[] { null, "agent:main:a" }, a.Queries.Skip(readsBefore).Select(query => query.Key));

        controller.Activate();
        var posted = dispatcher.Posted;
        lateProviders.SetResult(UsageReadResult<ProviderUsageSummary>.Ok(oldPlans));
        await dispatcher.PumpUntilAsync(() => dispatcher.Posted > posted
            && controller.State is { Phase: UsageLoadPhase.Loaded, Providers.Phase: UsageLoadPhase.Loaded });
        dispatcher.Drain();

        Assert.Null(controller.State.Detail);
        Assert.NotSame(oldPlans, controller.State.Providers.Value);
        Assert.DoesNotContain(b.Queries, query => query.Key is not null);
        Assert.Equal(["agent:main:b"], controller.State.Dashboard!.Sessions.Rows.Select(row => row.Key));
        Assert.Single(b.Calls, call => call == "GetProviderUsageAsync");
    }
}
