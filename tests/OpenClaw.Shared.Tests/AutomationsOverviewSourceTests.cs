// Exercises global overview freshness with external pages and controlled dispatcher completions.
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared.Cron;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>Host-lifetime inventory must not turn scoped, partial or stale data into a badge.</summary>
public sealed class AutomationsOverviewSourceTests
{
    [Fact]
    public async Task CompleteEnabledAndDisabledPagesDeduplicateAndCountOnlyActiveFailures()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        source.SetConnection(client);
        Drain(posts);
        Assert.Empty(wire.Requests);
        Assert.Null(source.State.AttentionCount);
        var enabled = Enumerable.Range(0, 201).Select(index => Job($"enabled-{index}", true, index == 0 ? "error" : "ok")).ToArray();
        wire.Pages.Enqueue(_ => Task.FromResult(Page(enabled.Take(200).ToArray(), 0, true)));
        wire.Pages.Enqueue(_ => Task.FromResult(Page([enabled[200]], 200)));
        wire.Pages.Enqueue(_ => Task.FromResult(Page([
            enabled[0], Job("automatic", false, "error") with { State = new() { AutoDisabled = new("schedule-errors", 1, 3) } },
            Job("paused-error", false, "error")], 0)));
        await source.RefreshAsync();
        Drain(posts);
        Assert.Equal(203, source.State.Jobs.Length);
        Assert.Equal(2, source.State.AttentionCount);
        Assert.All(wire.Requests, query => { Assert.Null(query.AgentId); Assert.Equal(200, query.Limit); });
        Assert.Equal(new[] { 0, 200, 0 }, wire.Requests.Select(query => query.Offset));
        Assert.Equal(new[] { CronEnabledFilter.Enabled, CronEnabledFilter.Enabled, CronEnabledFilter.Disabled },
            wire.Requests.Select(query => query.Enabled));
        Assert.All(source.State.Jobs, job => Assert.Null(job.Owner));
    }

    [Fact]
    public async Task RefreshAndFailuresInvalidateCountWithoutInventingAnEmptyInventory()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        QueueInventory(wire);
        await source.RefreshAsync();
        Drain(posts);
        Assert.Equal(1, source.State.AttentionCount);
        var held = new TaskCompletionSource<CronListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Pages.Enqueue(_ => held.Task);
        var refresh = source.RefreshAsync();
        Assert.True(source.State.IsLoading);
        Assert.Null(source.State.AttentionCount);
        held.SetException(new InvalidOperationException("Synthetic overview read failed."));
        await refresh;
        Drain(posts);
        Assert.Equal("Synthetic overview read failed.", source.State.Error);
        Assert.Single(source.State.Jobs);
        Assert.Null(source.State.AttentionCount);
        Assert.False(source.State.IsLoading);
    }

    [Fact]
    public async Task EmptyHasMorePageFailsInsteadOfPublishingAPartialCount()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        wire.Pages.Enqueue(_ => Task.FromResult(Page([], 0, true)));
        await source.RefreshAsync();
        Drain(posts);
        Assert.Contains("incomplete automation page", source.State.Error);
        Assert.False(source.State.HasLoaded);
        Assert.Null(source.State.AttentionCount);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task SameClientReconnectMasksAndRejectsQueuedOldSocketResultsBeforeHostNotification()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        QueueInventory(wire);
        await source.RefreshAsync();
        wire.Epoch++;
        Assert.Null(source.State.AttentionCount);
        Drain(posts);
        Assert.False(source.State.HasLoaded);
        QueueInventory(wire);
        await source.RefreshAsync();
        Drain(posts);
        Assert.Equal(wire.Epoch, source.State.ConnectionEpoch);
        Assert.Equal(1, source.State.AttentionCount);
        wire.Connected = false;
        Assert.Null(source.State.AttentionCount);
    }

    [Fact]
    public async Task IdleHostRetainsSnapshotButEventsInvalidateImmediatelyAndRefreshOnceActivated()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        source.SetConnection(client);
        wire.RaiseCron();
        Drain(posts);
        Assert.Empty(wire.Requests);
        QueueInventory(wire);
        await source.RefreshAsync();
        Drain(posts);
        var accepted = source.State;
        Drain(posts);
        Assert.Same(accepted, source.State);
        QueueInventory(wire);
        wire.RaiseCron();
        Assert.Null(source.State.AttentionCount);
        var invalidations = new List<AutomationsOverviewSnapshot>();
        source.Changed += (_, snapshot) => invalidations.Add(snapshot);
        Assert.True(posts.TryDequeue(out var invalidateAndRefresh));
        invalidateAndRefresh();
        Assert.Null(Assert.Single(invalidations).AttentionCount);
        Drain(posts);
        Assert.Equal(4, wire.Requests.Count);
        Assert.Equal(1, source.State.AttentionCount);
        source.Dispose();
        wire.RaiseCron();
        Drain(posts);
        Assert.Equal(4, wire.Requests.Count);
        Assert.Null(source.State.AttentionCount);
    }

    [Fact]
    public async Task ConcurrentRefreshesCoalesceAndDisposeRejectsLateResults()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        var held = new TaskCompletionSource<CronListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Pages.Enqueue(_ => held.Task);
        wire.Pages.Enqueue(_ => Task.FromResult(Page([], 0)));
        QueueInventory(wire);
        var first = source.RefreshAsync();
        Assert.Same(first, source.RefreshAsync());
        Assert.Same(first, source.RefreshAsync());
        held.SetResult(Page([Job("stale", true, "error")], 0));
        await first;
        source.Dispose();
        Drain(posts);
        Assert.Equal(4, wire.Requests.Count);
        Assert.False(source.State.HasLoaded);
        Assert.Null(source.State.AttentionCount);
    }

    private static CronJob Job(string id, bool enabled, string status) => new()
    {
        Id = id, Name = id, Enabled = enabled, Schedule = new(CronScheduleKind.Every, "every", EveryMs: 60_000),
        Payload = new(CronPayloadKind.AgentTurn, "agentTurn", "External test task."),
        State = new() { LastRunStatus = status },
    };

    [Fact]
    public async Task RefreshAtPublicationBoundaryStartsANewReadInsteadOfLosingInvalidation()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        AutomationsOverviewSource? source = null;
        Task? second = null;
        var requested = false;
        using (source = new AutomationsOverviewSource(() => client, action =>
        {
            if (!requested && wire.Requests.Count == 2)
            {
                requested = true;
                second = source!.RefreshAsync();
            }
            posts.Enqueue(action);
        }))
        {
            wire.Pages.Enqueue(_ => Task.FromResult(Page([Job("old", true, "ok")], 0)));
            wire.Pages.Enqueue(_ => Task.FromResult(Page([], 0)));
            QueueInventory(wire);
            var first = source.RefreshAsync();
            await first;
            Assert.NotNull(second);
            await second;
            Drain(posts);
            Assert.Equal(4, wire.Requests.Count);
            Assert.True(source.State.IsCurrent);
            Assert.False(source.State.IsLoading);
            Assert.Equal("failed", Assert.Single(source.State.Jobs).Id);
        }
    }

    [Fact]
    public async Task ForbiddenRefreshClearsReadableJobsAndReportsFailure()
    {
        var (client, wire) = Client();
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => client, posts.Enqueue);
        QueueInventory(wire);
        await source.RefreshAsync();
        Drain(posts);
        wire.Pages.Enqueue(_ => Task.FromException<CronListPage>(GatewayRequestErrors.Create("denied", "FORBIDDEN", null)));
        await source.RefreshAsync();
        Drain(posts);
        Assert.Empty(source.State.Jobs);
        Assert.False(source.State.HasLoaded);
        Assert.NotNull(source.State.Error);
        Assert.Null(source.State.AttentionCount);
    }

    [Fact]
    public async Task ClientReplacementStartsItsOwnReadAndRejectsTheOldHeldCompletion()
    {
        var (oldClient, oldWire) = Client();
        var (newClient, newWire) = Client();
        newWire.Epoch = 2;
        IOperatorGatewayClient current = oldClient;
        var posts = new ConcurrentQueue<Action>();
        using var source = new AutomationsOverviewSource(() => current, posts.Enqueue);
        var held = new TaskCompletionSource<CronListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldWire.Pages.Enqueue(_ => held.Task);
        var oldRead = source.RefreshAsync();
        current = newClient;
        Assert.Null(source.State.AttentionCount);
        QueueInventory(newWire);
        await source.RefreshAsync();
        Drain(posts);
        Assert.Same(newClient, source.State.Client);
        Assert.Equal(2, source.State.ConnectionEpoch);
        held.SetResult(Page([Job("old-socket", true, "error")], 0));
        await oldRead;
        Drain(posts);
        Assert.DoesNotContain(source.State.Jobs, job => job.Id == "old-socket");
        Assert.Single(oldWire.Requests);
        Assert.Equal(2, newWire.Requests.Count);
    }

    private static CronListPage Page(CronJob[] jobs, int offset, bool more = false) =>
        new(jobs, null, offset, 200, more, more ? offset + jobs.Length : null);

    private static void QueueInventory(OverviewClientProxy wire)
    {
        wire.Pages.Enqueue(_ => Task.FromResult(Page([Job("failed", true, "error")], 0)));
        wire.Pages.Enqueue(_ => Task.FromResult(Page([], 0)));
    }

    private static (IOperatorGatewayClient Client, OverviewClientProxy Wire) Client()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, OverviewClientProxy>();
        var wire = (OverviewClientProxy)(object)client;
        return (client, wire);
    }

    private static void Drain(ConcurrentQueue<Action> posts)
    {
        while (posts.TryDequeue(out var action)) action();
    }

    /// <summary>Provides wire inputs, socket identity and event delivery, not overview policy.</summary>
    public class OverviewClientProxy : DispatchProxy
    {
        public bool Connected { get; set; } = true;
        public long Epoch { get; set; } = 1;
        public ConcurrentQueue<Func<CronListQuery, Task<CronListPage>>> Pages { get; } = new();
        public List<CronListQuery> Requests { get; } = [];
        private event EventHandler<JsonElement>? Cron;
        public void RaiseCron() => Cron?.Invoke(this, default);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsConnectedToGateway": return Connected;
                case "get_SessionMutationConnectionEpoch": return Connected ? (long?)Epoch : null;
                case "add_CronEventReceived": Cron += (EventHandler<JsonElement>)args![0]!; return null;
                case "remove_CronEventReceived": Cron -= (EventHandler<JsonElement>)args![0]!; return null;
                case "ListCronJobsAsync":
                    var query = (CronListQuery)args![0]!;
                    Requests.Add(query);
                    if (!Pages.TryDequeue(out var page)) throw new InvalidOperationException("Unexpected extra overview read.");
                    return page(query);
                default: throw new NotSupportedException($"Unexpected overview client call: {method.Name}");
            }
        }
    }
}
