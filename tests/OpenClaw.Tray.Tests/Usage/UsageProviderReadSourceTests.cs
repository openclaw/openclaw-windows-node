using System.Collections.Concurrent;
using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageProviderReadSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class Dispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);
        public void Post(Action action)
        {
            _queue.Enqueue(action);
            _posted.Release();
        }
        public async Task RunNextAsync()
        {
            Assert.True(await _posted.WaitAsync(TimeSpan.FromSeconds(10)), "Provider completion was not posted.");
            if (!_queue.TryDequeue(out var action))
                throw new InvalidOperationException("Posted provider completion was not queued.");
            action();
        }
    }

    private static (IOperatorGatewayClient Client, UsagePageControllerTests.UsageGateway Gateway) Client()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsagePageControllerTests.UsageGateway>();
        return (client, (UsagePageControllerTests.UsageGateway)client);
    }

    private static ProviderUsageSnapshot Provider(string name, double used, DateTimeOffset? reset = null,
        string? error = null) => new()
    {
        Provider = name,
        Error = error,
        Windows = [new("weekly", null, used, reset)],
    };

    private static UsageReadResult<ProviderUsageSummary> Success(params ProviderUsageSnapshot[] providers) =>
        UsageReadResult<ProviderUsageSummary>.Ok(new(Now, providers, false));

    private static IReadOnlyList<ProviderUsageSnapshot> Attention(UsageProviderReadSource source, DateTimeOffset? now = null) =>
        UsageInsights.Attention(new UsagePageState
        {
            Filters = UsageFilters.Default(new(2026, 11, 6)),
            Availability = UsageAvailability.Connected,
            Providers = source.State.Read,
        }, now ?? Now);

    [Fact]
    public async Task BindingAndFirstRead_PublishUnknownUntilSettledAndPreserveIdenticalBinding()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 4);
        Assert.Equal(UsageLoadPhase.Idle, source.State.Read.Phase);
        Assert.DoesNotContain("GetProviderUsageAsync", gateway.Calls);
        gateway.Providers = () => Task.FromResult(Success(
            Provider("threshold", 75), Provider("below", 74.99), Provider("expired", 99, Now),
            new ProviderUsageSnapshot
            {
                Provider = "multiple",
                Windows = [new("one", null, 80, null), new("two", null, 90, null)],
            }));

        source.Refresh(client);
        Assert.Equal(UsageLoadPhase.Loading, source.State.Read.Phase);
        Assert.Empty(Attention(source));
        await dispatcher.RunNextAsync();
        Assert.Equal(["multiple", "threshold"], Attention(source).Select(provider => provider.Provider));
        source.SetConnection(client, 4);
        Assert.Equal(["multiple", "threshold"], Attention(source).Select(provider => provider.Provider));
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
    }

    [Fact]
    public async Task IndividualProviderFailure_DoesNotHideValidPageAttention()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(
            Provider("valid", 90), Provider("failed", 100, error: "unavailable")));
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.Equal("unavailable", source.State.Read.Value!.Providers.Single(provider => provider.Provider == "failed").Error);
        Assert.Equal("valid", Assert.Single(Attention(source)).Provider);
    }

    [Theory]
    [InlineData(UsageReadStatus.Failed)]
    [InlineData(UsageReadStatus.Forbidden)]
    [InlineData(UsageReadStatus.Unsupported)]
    [InlineData(UsageReadStatus.Disconnected)]
    public async Task FailedRefresh_RetainsOneDisplaySnapshotWithoutShowingStaleAttention(UsageReadStatus status)
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("high", 90)));
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        var previous = source.State.Read.Value;
        gateway.Providers = () => Task.FromResult(new UsageReadResult<ProviderUsageSummary>(status, null, "unavailable"));

        source.Refresh(client);
        Assert.Empty(Attention(source));
        await dispatcher.RunNextAsync();
        Assert.Equal(UsageLoadPhase.Failed, source.State.Read.Phase);
        Assert.Equal(status, source.State.Read.Failure);
        Assert.Same(previous, source.State.Read.Value);
        Assert.Empty(Attention(source));
    }

    [Fact]
    public async Task RefreshingResponseAndSuccessfulEmptyResponseRemainDistinct()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(
            new(Now, [Provider("high", 90)], true)));
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.True(source.State.Read.Value!.Refreshing);
        Assert.Empty(Attention(source));
        gateway.Providers = () => Task.FromResult(Success());
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.False(source.State.Read.Value!.Refreshing);
        Assert.Empty(source.State.Read.Value.Providers);
        Assert.Equal(UsageLoadPhase.Loaded, source.State.Read.Phase);
    }

    [Fact]
    public async Task SameClientSocketChangeBeforeHostHook_FencesPostedReplyAndInvalidatesVisibleData()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("old-socket", 90)));
        source.Refresh(client);
        gateway.SocketEpoch = 2;
        await dispatcher.RunNextAsync();
        Assert.Empty(Attention(source));
        Assert.Equal(UsageLoadPhase.Idle, source.State.Read.Phase);
        source.SetConnection(client, 2);
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.Single(Attention(source));
        gateway.SocketEpoch = 3;
        Assert.Empty(Attention(source));
        Assert.Equal(UsageLoadPhase.Idle, source.State.Read.Phase);
    }

    [Fact]
    public async Task ReplacementClientCannotReceiveOrDisplayOldClientResult()
    {
        var (first, gateway) = Client();
        var (second, secondGateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(first, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("old", 90)));
        source.Refresh(first);
        source.SetConnection(second, 2);
        await dispatcher.RunNextAsync();
        Assert.Same(second, source.State.Client);
        Assert.Null(source.State.Read.Value);
        Assert.Empty(Attention(source));
        Assert.DoesNotContain("GetProviderUsageAsync", secondGateway.Calls);
        source.Refresh(first);
        Assert.DoesNotContain("GetProviderUsageAsync", secondGateway.Calls);
        secondGateway.Providers = () => Task.FromResult(Success(Provider("current", 90)));
        source.Refresh(second);
        source.Refresh(first);
        await dispatcher.RunNextAsync();
        Assert.Single(Attention(source));
        Assert.Equal("current", Assert.Single(source.State.Read.Value!.Providers).Provider);
    }

    [Fact]
    public void MissingAuthenticatedEpochAndDisconnect_NeverReadOrPublishData()
    {
        var (client, gateway) = Client();
        using var source = new UsageProviderReadSource(action => action(), () => Now);
        gateway.SocketEpoch = null;
        source.SetConnection(client, 1);
        source.Refresh(client);
        Assert.Null(source.State.Read.Value);
        Assert.DoesNotContain("GetProviderUsageAsync", gateway.Calls);
        gateway.SocketEpoch = 1;
        gateway.Connected = false;
        source.SetConnection(client, 2);
        source.Refresh(client);
        Assert.Null(source.State.Read.Value);
        Assert.DoesNotContain("GetProviderUsageAsync", gateway.Calls);
    }

    [Fact]
    public async Task DisposedHostFencesAlreadyPostedSuccess()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("high", 90)));
        source.Refresh(client);
        var changes = 0;
        source.Changed += () => changes++;
        source.Dispose();
        await dispatcher.RunNextAsync();
        Assert.Empty(Attention(source));
        Assert.Null(source.State.Read.Value);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ResetExpiryInvalidatesBeforeQueuedTimer_WithoutReadingAgain()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        var now = Now;
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new UsageProviderReadSource(dispatcher.Post, () => now, (_, _) => timer.Task);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("high", 90, Now.AddHours(1))));
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.Single(Attention(source, now));
        now = Now.AddHours(1);
        Assert.NotNull(source.State.Read.Value);
        Assert.Empty(Attention(source, now));
        timer.SetResult();
        await dispatcher.RunNextAsync();
        Assert.Empty(Attention(source, now));
        Assert.Equal(90, Assert.Single(source.State.Read.Value!.Providers.Single().Windows).UsedPercent);
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
    }

    [Fact]
    public async Task EveryRetainedWindowExpiryNotifiesWithoutReReading()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        var now = Now;
        var timers = new List<TaskCompletionSource>();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => now, (_, _) =>
        {
            var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timers.Add(timer);
            return timer.Task;
        });
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(new ProviderUsageSnapshot
        {
            Provider = "two-windows", Windows =
                [new("First", null, 90, Now.AddHours(1)), new("Second", null, 80, Now.AddHours(2))],
        }));
        using var controller = new UsagePageController(() => client, () => new(2026, 11, 6),
            dispatcher.Post, providers: source);
        controller.Activate();
        await dispatcher.RunNextAsync(); // Overview completion.
        await dispatcher.RunNextAsync(); // Provider completion.
        var before = controller.State;
        Assert.Single(Attention(source, now));
        Assert.Single(timers);
        now = Now.AddHours(1);
        timers[0].SetResult();
        await dispatcher.RunNextAsync();
        Assert.NotEqual(before, controller.State);
        Assert.Same(before.Providers.Value, controller.State.Providers.Value);
        before = controller.State;
        Assert.Equal("Second", UsageProviderAttention.HighestWindow(source.State.Read.Value!.Providers.Single(), now)!.Label);
        Assert.Equal(2, timers.Count);
        now = Now.AddHours(2);
        timers[1].SetResult();
        await dispatcher.RunNextAsync();
        Assert.NotEqual(before, controller.State);
        Assert.Same(before.Providers.Value, controller.State.Providers.Value);
        Assert.Empty(UsageInsights.Attention(controller.State, now));
        Assert.Equal(2, source.State.Read.Value!.Providers.Single().Windows.Count);
        Assert.Equal(2, timers.Count);
        Assert.Single(gateway.Calls, call => call == "GetProviderUsageAsync");
    }

    [Fact]
    public async Task DisposedPageUnsubscribes_WhileHostReadRemainsAlive()
    {
        var (client, gateway) = Client();
        var dispatcher = new Dispatcher();
        using var source = new UsageProviderReadSource(dispatcher.Post, () => Now);
        source.SetConnection(client, 1);
        gateway.Providers = () => Task.FromResult(Success(Provider("high", 90)));
        var controller = new UsagePageController(() => client, () => new(2026, 11, 6),
            dispatcher.Post, providers: source);
        controller.Activate();
        // Overview posts first; the second completion is the shared provider read.
        await dispatcher.RunNextAsync();
        await dispatcher.RunNextAsync();
        Assert.Equal(UsageLoadPhase.Loaded, controller.State.Providers.Phase);
        var changes = 0;
        controller.Changed += () => changes++;
        controller.Dispose();
        source.Refresh(client);
        await dispatcher.RunNextAsync();
        Assert.Single(Attention(source));
        Assert.Equal(0, changes);
        Assert.Equal(2, gateway.Calls.Count(call => call == "GetProviderUsageAsync"));
    }
}
