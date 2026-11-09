// Drives store request/dispatcher boundaries without a socket or native UI.
// Run with FullyQualifiedName~AutomationsStoreConcurrencyTests.
using System.Reflection;
using System.Collections.Concurrent;
using OpenClaw.Shared.Cron;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>Late wire completions must preserve scope and pending user refreshes.</summary>
public sealed class AutomationsStoreConcurrencyTests
{
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedOverviewCannotPublishAfterSwitchingToAllAgents(bool fail)
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        var held = new TaskCompletionSource<CronListPage>();
        wire.List = query => query.AgentId == "main" && query.Limit == 200
            ? held.Task : Task.FromResult(Page(query, "global"));
        using var store = new AutomationsStore("main", () => client, posts.Enqueue);
        store.Refresh();
        Drain(posts);
        store.SetAgentFilter(null);
        Drain(posts);
        Assert.True(store.State.OverviewLoaded);
        Assert.Equal("global", Assert.Single(store.State.OverviewJobs).Id);
        if (fail) held.SetException(new InvalidOperationException("Old scoped failure"));
        else held.SetResult(Page(new CronListQuery(), "old-scoped"));
        await held.Task.ContinueWith(_ => { }, TaskScheduler.Default);
        Drain(posts);
        Assert.True(store.State.IsAllAgents);
        Assert.Equal("global", Assert.Single(store.State.OverviewJobs).Id);
        Assert.Null(store.State.OverviewError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevealDrainsAQueryRequestedWhileItsReadIsPending(bool fail)
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var store = new AutomationsStore("main", () => client, posts.Enqueue);
        store.Refresh();
        Drain(posts);
        var held = new TaskCompletionSource<CronListPage>();
        wire.List = query => query.Query == "new query"
            ? Task.FromResult(Page(query, "fresh"))
            : held.Task;
        var reveal = store.RevealAsync("missing");
        store.SetQuery(new CronListQuery { Query = "new query" });
        if (fail) held.SetException(new InvalidOperationException("Reveal read failed"));
        else held.SetResult(Page(new CronListQuery()));
        await reveal;
        Drain(posts);
        Assert.Equal("new query", store.State.ListQuery.Query);
        Assert.Equal("fresh", Assert.Single(store.State.Jobs).Id);
        Assert.False(store.State.ListLoading);
        Assert.Null(store.State.ListError);
    }

    [Fact]
    public async Task WidenedRevealRetainsThePendingSearchInItsNewAgentScope()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var store = new AutomationsStore("main", () => client, posts.Enqueue);
        store.Refresh();
        Drain(posts);
        var held = new TaskCompletionSource<CronListPage>();
        wire.List = query => query.AgentId == "main" ? held.Task
            : Task.FromResult(Page(query, query.Query == "new query" ? "fresh" : "target"));
        var reveal = store.RevealAsync("target", widenToAllAgents: true);
        store.SetQuery(new CronListQuery { Query = "new query" });
        held.SetResult(Page(new CronListQuery()));
        Assert.Equal(AutomationRevealOutcome.Revealed, await reveal);
        Drain(posts);
        Assert.True(store.State.IsAllAgents);
        Assert.Equal("new query", store.State.ListQuery.Query);
        Assert.Equal("fresh", Assert.Single(store.State.Jobs).Id);
        Assert.False(store.State.ListLoading);
    }

    [Fact]
    public async Task SuccessiveRunsRequireTheAcceptedRunIdDespiteStaleHistoryAndListState()
    {
        var (client, wire) = Client();
        var pump = new PostPump();
        var polls = new PollGates();
        wire.List = query => Task.FromResult(RunJobPage(query, 100));
        wire.Run = _ => Task.FromResult(new CronRunRequestResult(true, true, "run-A"));
        using var store = new AutomationsStore("main", () => client, pump.Post, polls.Delay);
        store.Refresh();
        pump.Drain();
        await store.RunNowAsync("job");
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        var first = await polls.NextAsync();
        var historyQueries = new ConcurrentQueue<CronRunsQuery>();
        wire.Runs = query =>
        {
            historyQueries.Enqueue(query);
            return Task.FromResult(RunPage(new CronRunEntry { JobId = "job", RunId = "run-A", RunAtMs = 200, Status = "ok" }));
        };
        first.SetResult();
        await pump.UntilAsync(() => !store.State.IsRunning("job"));
        pump.Drain();
        Assert.True(historyQueries.TryDequeue(out var firstQuery));
        Assert.Equal("run-A", firstQuery!.RunId);
        // The independent inventory still has the baseline from before A completed.
        Assert.Equal(100L, Assert.Single(store.State.Jobs).State.LastRunAtMs);
        wire.Run = _ => Task.FromResult(new CronRunRequestResult(true, true, "run-B"));
        await store.RunNowAsync("job");
        pump.Drain();
        var second = await polls.NextAsync();
        wire.Runs = query =>
        {
            historyQueries.Enqueue(query);
            // An older server may ignore the runId filter. The owner must still check the entry.
            return Task.FromResult(RunPage(new CronRunEntry { JobId = "job", RunId = "run-A", RunAtMs = 200, Ts = 400, Status = "ok" }));
        };
        second.SetResult();
        var next = await polls.NextAsync(); // Proves stale history was processed, not merely delivered.
        pump.Drain();
        Assert.True(historyQueries.TryDequeue(out var secondQuery));
        Assert.Equal("run-B", secondQuery!.RunId);
        Assert.True(store.State.IsRunning("job"));
        wire.List = query => Task.FromResult(RunJobPage(query, 200));
        await store.LoadListAsync();
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        wire.Runs = _ => Task.FromResult(RunPage(new CronRunEntry { JobId = "other-job", RunId = "run-B", RunAtMs = 500, Status = "ok" }));
        next.SetResult();
        next = await polls.NextAsync();
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        // Identity, not clock ordering: the matching completion may carry an older timestamp.
        wire.Runs = _ => Task.FromResult(RunPage(new CronRunEntry { JobId = "job", RunId = "run-B", RunAtMs = 50, Status = "ok" }));
        next.SetResult();
        await pump.UntilAsync(() => !store.State.IsRunning("job"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentListTimestampsCannotCompleteIdentifiedOrIdlessLocalRuns(bool idless)
    {
        var (client, wire) = Client();
        var pump = new PostPump();
        var polls = new PollGates();
        wire.List = query => Task.FromResult(RunJobPage(query, 100));
        wire.Run = _ => Task.FromResult(new CronRunRequestResult(true, true, idless ? null : "current-run"));
        var historyReads = 0;
        wire.Runs = _ =>
        {
            historyReads++;
            return Task.FromResult(RunPage(new CronRunEntry { JobId = "job", RunId = "old-run", RunAtMs = 900, Ts = 1000, Status = "ok" }));
        };
        using var store = new AutomationsStore("main", () => client, pump.Post, polls.Delay);
        store.Refresh();
        pump.Drain();
        await store.RunNowAsync("job");
        pump.Drain();
        var pendingPoll = await polls.NextAsync();
        wire.List = query => Task.FromResult(RunJobPage(query, 900));
        await store.LoadListAsync();
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        pendingPoll.SetResult();
        _ = await polls.NextAsync();
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        Assert.Equal(idless ? 0 : 1, historyReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAcknowledgementCannotRetireOrReplaceANewerInvocation(bool accepted)
    {
        var (client, wire) = Client();
        var pump = new PostPump();
        var polls = new PollGates();
        wire.List = query => Task.FromResult(RunJobPage(query, 100));
        var oldReply = new TaskCompletionSource<CronRunRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Run = _ => oldReply.Task;
        using var store = new AutomationsStore("main", () => client, pump.Post, polls.Delay);
        store.Refresh();
        pump.Drain();
        var old = store.RunNowAsync("job");
        wire.Connected = false;
        store.Refresh();
        pump.Drain();
        wire.Connected = true;
        store.Refresh();
        pump.Drain();
        wire.Run = _ => Task.FromResult(new CronRunRequestResult(true, true, "new-run"));
        await store.RunNowAsync("job");
        pump.Drain();
        _ = await polls.NextAsync();
        oldReply.SetResult(new(accepted, accepted, accepted ? "old-run" : null));
        await old; // The old RunNow method has enqueued its publication before returning.
        pump.Drain();
        Assert.True(store.State.IsRunning("job"));
        Assert.Equal("new-run", store.State.Notice?.RunId);
    }

    private static CronListPage RunJobPage(CronListQuery query, long lastRunAtMs) =>
        new([Job("job") with { Enabled = true, State = new() { LastRunAtMs = lastRunAtMs, LastRunStatus = "ok" } }],
            1, query.Offset, query.Limit, false, null);

    private static CronRunsPage RunPage(params CronRunEntry[] entries) => new(entries, entries.Length, 0, 20, false, null);

    private sealed class PostPump
    {
        private readonly System.Threading.Channels.Channel<Action> _posts = System.Threading.Channels.Channel.CreateUnbounded<Action>();
        public void Post(Action action) => _posts.Writer.TryWrite(action);
        public void Drain() { while (_posts.Reader.TryRead(out var action)) action(); }
        public async Task UntilAsync(Func<bool> complete)
        {
            using var deadline = new CancellationTokenSource(RequestDeadline);
            while (!complete())
            {
                Drain();
                if (complete()) return;
                var action = await _posts.Reader.ReadAsync(deadline.Token);
                action();
            }
        }
    }

    private sealed class PollGates
    {
        private readonly System.Threading.Channels.Channel<TaskCompletionSource> _waiting =
            System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        public Task Delay(TimeSpan interval, CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Writer.TryWrite(gate);
            return gate.Task.WaitAsync(cancellationToken);
        }
        public Task<TaskCompletionSource> NextAsync() => _waiting.Reader.ReadAsync().AsTask().WaitAsync(RequestDeadline);
    }

    private static CronJob Job(string id) => new()
    {
        Id = id, Name = id, Schedule = new(CronScheduleKind.Every, "every", EveryMs: 60_000),
        Payload = new(CronPayloadKind.AgentTurn, "agentTurn", "Synthetic task"),
    };

    private static CronListPage Page(CronListQuery query, params string[] ids) =>
        new(ids.Select(Job).ToArray(),
            ids.Length, query.Offset, query.Limit, false, null);

    private static void Drain(ConcurrentQueue<Action> posts)
    {
        while (posts.TryDequeue(out var action)) action();
    }

    private static (IOperatorGatewayClient, StoreClient) Client()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, StoreClient>();
        return (client, (StoreClient)(object)client);
    }

    /// <summary>Supplies external responses, never store behavior.</summary>
    public class StoreClient : DispatchProxy
    {
        public bool Connected { get; set; } = true;
        public Func<CronListQuery, Task<CronListPage>> List { get; set; } =
            query => Task.FromResult(Page(query));
        public Func<string, Task<CronRunRequestResult>> Run { get; set; } =
            _ => Task.FromResult(new CronRunRequestResult(true, true, "run"));
        public Func<CronRunsQuery, Task<CronRunsPage>> Runs { get; set; } =
            _ => Task.FromResult(new CronRunsPage([], 0, 0, 50, false, null));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_SessionMutationConnectionEpoch" => (long?)1,
            "get_GrantedOperatorScopes" => new[] { CronProtocol.AdminScope },
            "add_CronEventReceived" or "remove_CronEventReceived" => null,
            "ListCronJobsAsync" => List((CronListQuery)args![0]!),
            "GetCronJobAsync" => Task.FromResult(Job((string)args![0]!)),
            "RunCronJobDetailedAsync" => Run((string)args![0]!),
            "ListCronRunsAsync" => Runs((CronRunsQuery)args![0]!),
            "GetCronStatusAsync" => Task.FromResult(new CronSchedulerStatus(true, null, null, null)),
            _ => throw new NotSupportedException(method.Name),
        };
    }
}
