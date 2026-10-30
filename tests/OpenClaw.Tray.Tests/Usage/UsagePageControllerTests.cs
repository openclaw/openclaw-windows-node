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
        public int? CostDays;
        public readonly List<SessionsUsageQuery> Queries = [];
        public readonly List<string> Calls = [];
        public Func<SessionsUsageQuery, Task<UsageReadResult<SessionsUsageReport>>> Usage =
            _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a")));
        public Func<Task<UsageReadResult<SessionsUsageReport>>> Cost =
            () => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(new SessionsUsageReport { Totals = new UsageTotals { TotalCost = 4 } }));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            return method.Name switch
            {
                "get_IsConnectedToGateway" => Connected,
                "GetSessionsUsageAsync" => Query((SessionsUsageQuery)args![0]!),
                "GetUsageCostSummaryAsync" => CostFor((int)args![0]!),
                "GetProviderUsageAsync" => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(new ProviderUsageSummary(null, [], false))),
                "GetSessionUsageTimeseriesAsync" => Task.FromResult(UsageReadResult<SessionUsageTimeseries>.Ok(new SessionUsageTimeseries(UsageReportState.Available, null, []))),
                "GetSessionUsageLogsAsync" => Task.FromResult(UsageReadResult<IReadOnlyList<SessionUsageLogEntry>>.Ok(
                    [new SessionUsageLogEntry(DateTimeOffset.UnixEpoch, "assistant", "Done\n[Tool: read]", null, null)])),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<UsageReadResult<SessionsUsageReport>> CostFor(int days)
        {
            CostDays = days;
            return Cost();
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
    public async Task LegacyFallback_ReachesBackToACustomRangeAndTotalsOnlyItsDays()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
        UsageDailyTotals Day(string date, long tokens, double cost) =>
            new(date, new UsageTotals { TotalTokens = tokens, TotalCost = cost });
        gateway.Cost = () => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(new SessionsUsageReport
        {
            Totals = new UsageTotals { TotalTokens = 1000, TotalCost = 10 },
            Aggregates = new UsageAggregates
            {
                CostDaily = [Day("2026-10-01", 100, 1), Day("2026-10-02", 200, 2), Day("2026-10-03", 300, 3), Day("2026-10-25", 400, 4)],
            },
        }));

        controller.SetRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Legacy });

        // Today is 2026-10-26, so a range starting on 2026-10-01 needs 26 days of legacy history.
        Assert.Equal(26, gateway.CostDays);
        var report = controller.State.Report!;
        Assert.Equal(600, report.Totals.TotalTokens);
        Assert.Equal(6, report.Totals.TotalCost, 6);
        Assert.Equal(["2026-10-01", "2026-10-02", "2026-10-03"], report.Aggregates.CostDaily.Select(d => d.Date));
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
        Assert.Equal(UsageLoadPhase.Loaded, controller.State.Providers.Phase);
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:late")));
        dispatcher.Drain();
        Assert.Equal("agent:main:a", Assert.Single(controller.State.Report!.Sessions).Key);
    }

    [Fact]
    public async Task ReleasedGatewayReply_DropsServerOnlyFiltersAndIgnoresThem()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.SetQuery("agent:main");
        controller.ToggleDay("2026-10-21", append: false);
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a", "agent:main:b")));

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);

        Assert.True(controller.State.BasicGateway);
        Assert.Equal("", controller.State.Filters.Query);
        Assert.Empty(controller.State.Filters.SelectedDays);
        var requests = gateway.Queries.Count;

        controller.SetQuery("model:gpt");
        controller.ToggleHour(9, append: false);
        controller.SetSort(UsageSort.Tokens);
        controller.SetPage(50);
        controller.SelectSession("agent:main:b", "main", append: false);
        await dispatcher.PumpUntilAsync(() => controller.State.Detail?.Logs.Phase == UsageLoadPhase.Loaded);
        controller.CloseDetail();

        // Selection is projected locally; only the detail reads went to the Gateway.
        Assert.Equal(requests, gateway.Queries.Count(q => q.Key is null));
        Assert.Empty(controller.State.Filters.SelectedSessions);
        Assert.Equal("", controller.State.Filters.Query);
        Assert.Empty(controller.State.Filters.SelectedHours);
        Assert.Equal(UsageSort.Cost, controller.State.Filters.Sort);

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
    public async Task SelectingASession_LoadsDetailsAndRecordsItAsRecent()
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);

        controller.SelectSession("agent:main:a", "main", append: false);
        await dispatcher.PumpUntilAsync(() => controller.State.Detail is { Row.Phase: UsageLoadPhase.Loaded, Timeseries.Phase: UsageLoadPhase.Loaded, Logs.Phase: UsageLoadPhase.Loaded }
            && controller.State.Phase == UsageLoadPhase.Loaded);

        var detailQuery = Assert.Single(gateway.Queries, q => q.Key is not null);
        Assert.True(detailQuery.IncludeContextWeight);
        Assert.Equal("main", detailQuery.AgentId);
        Assert.Equal(["agent:main:a"], controller.State.Filters.SelectedSessions);
        Assert.Equal(["agent:main:a"], controller.State.RecentKeys);
        Assert.Equal([("read", 1)], controller.State.Detail!.Logs.Value![0].Tools);

        controller.SelectSession("agent:main:a", "main", append: false);
        Assert.Null(controller.State.Detail);
        Assert.Empty(controller.State.Filters.SelectedSessions);
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
        Assert.Equal(UsageExportError.TooMany, (await controller.ExportAsync(UsageExportFormat.Json)).Error);
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
    public async Task LegacyGateway_DropsAndIgnoresFiltersUsageCostCannotApply()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
        controller.SetCreator("profile:ada");
        controller.SetQuery("agent:main");

        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Legacy });

        Assert.True(controller.State.ServerFiltersUnavailable);
        Assert.Null(controller.State.Filters.CreatorKey);
        Assert.Equal("", controller.State.Filters.Query);
        controller.SetQuery("model:gpt");
        controller.ToggleDay("2026-10-21", append: false);
        Assert.Equal("", controller.State.Filters.Query);
        Assert.Empty(controller.State.Filters.SelectedDays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFallback_ClosesSelectionAndRejectsLateDetailReplies(bool costUnsupported)
    {
        var (controller, gateway, dispatcher) = Create();
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Loaded);
        var pending = new TaskCompletionSource<UsageReadResult<SessionsUsageReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Usage = q => q.Key is not null ? pending.Task
            : Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
        if (costUnsupported)
            gateway.Cost = () => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());

        controller.SelectSession("agent:main:a", "main", append: false);
        controller.Refresh();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Legacy });

        Assert.Empty(controller.State.Filters.SelectedSessions);
        Assert.Null(controller.State.Detail);
        dispatcher.Drain();
        var postedBeforeReply = dispatcher.Posted;
        var pendingReads = gateway.Queries.Count(query => query.Key is not null);
        Assert.True(pendingReads > 0);
        pending.SetResult(UsageReadResult<SessionsUsageReport>.Ok(Report("agent:main:a")));
        await dispatcher.PumpUntilAsync(() => dispatcher.Posted >= postedBeforeReply + pendingReads);
        dispatcher.Drain();
        Assert.Null(controller.State.Detail);
        controller.SelectSession("agent:main:a", "main", append: false);
        Assert.Empty(controller.State.Filters.SelectedSessions);
    }

    [Fact]
    public async Task LegacyReloadFailure_ShowsTheErrorWhileRetainingTheLastSuccessfulReport()
    {
        var (controller, gateway, dispatcher) = Create();
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
        controller.Activate();
        await dispatcher.PumpUntilAsync(() => controller.State is { Phase: UsageLoadPhase.Loaded, Availability: UsageAvailability.Legacy });
        var previous = controller.State.Report;
        gateway.Cost = () => Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("Usage read failed"));

        controller.Refresh();
        await dispatcher.PumpUntilAsync(() => controller.State.Phase == UsageLoadPhase.Failed);

        Assert.Equal(UsageAvailability.Legacy, controller.State.Availability);
        Assert.Same(previous, controller.State.Report);
        Assert.Equal("Usage read failed", controller.State.Error);
        Assert.True(controller.State.ShowLoadError);
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
        controller.SelectSession("b", "main", append: false);
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
}
