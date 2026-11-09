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
        public Func<CronListQuery, Task<CronListPage>> List { get; set; } =
            query => Task.FromResult(Page(query));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_IsConnectedToGateway" => true,
            "get_SessionMutationConnectionEpoch" => (long?)1,
            "get_GrantedOperatorScopes" => new[] { CronProtocol.AdminScope },
            "add_CronEventReceived" or "remove_CronEventReceived" => null,
            "ListCronJobsAsync" => List((CronListQuery)args![0]!),
            "GetCronJobAsync" => Task.FromResult(Job((string)args![0]!)),
            "ListCronRunsAsync" => Task.FromResult(new CronRunsPage([], 0, 0, 50, false, null)),
            "GetCronStatusAsync" => Task.FromResult(new CronSchedulerStatus(true, null, null, null)),
            _ => throw new NotSupportedException(method.Name),
        };
    }
}
