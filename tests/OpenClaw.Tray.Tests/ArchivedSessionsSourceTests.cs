using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ArchivedSessionsSourceTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 1;
        public int Calls;
        public Func<Task<SessionListResult>> Response = () => Task.FromResult(Result("archived"));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => true,
            "get_SessionMutationConnectionEpoch" => (long?)Epoch,
            "ListArchivedSessionsAsync" => Load(),
            _ => throw new NotSupportedException(method?.Name),
        };

        private Task<SessionListResult> Load()
        {
            Calls++;
            return Response();
        }
    }

    private static SessionListResult Result(string key) =>
        new() { IsSupported = true, Sessions = [new SessionInfo { Key = key, Archived = true }] };

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    [Fact]
    public async Task CollapsedSectionDoesNotFetchArchives()
    {
        var (client, proxy) = Gateway();
        var source = new ArchivedSessionsSource(() => { }, _ => Assert.Fail("Unexpected failure"));
        await source.RefreshAsync(client);
        Assert.Equal(0, proxy.Calls);
        source.SetExpanded(true);
        await source.RefreshAsync(client);
        Assert.True(source.HasLoaded);
        Assert.Equal("archived", Assert.Single(source.Sessions).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewGatewayOrConnectionDropsOldPendingSnapshot(bool reconnect)
    {
        var (original, oldProxy) = Gateway();
        var pending = new TaskCompletionSource<SessionListResult>();
        oldProxy.Response = () => pending.Task;
        var source = new ArchivedSessionsSource(() => { }, _ => Assert.Fail("Unexpected failure"));
        source.SetExpanded(true);
        var oldLoad = source.RefreshAsync(original);
        var replacement = original;
        if (reconnect)
        {
            oldProxy.Epoch++;
            oldProxy.Response = () => Task.FromResult(Result("current"));
        }
        else
        {
            var next = Gateway();
            next.Proxy.Response = () => Task.FromResult(Result("current"));
            replacement = next.Client;
        }
        await source.RefreshAsync(replacement);
        pending.SetResult(Result("stale"));
        await oldLoad;
        Assert.Equal("current", Assert.Single(source.Sessions).Key);
    }

    [Fact]
    public async Task ClearDropsPendingSnapshotWhenPageUnloads()
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<SessionListResult>();
        proxy.Response = () => pending.Task;
        var source = new ArchivedSessionsSource(() => { }, _ => Assert.Fail("Unexpected failure"));
        source.SetExpanded(true);
        var load = source.RefreshAsync(client);
        source.Clear();
        pending.SetResult(Result("stale"));
        await load;
        Assert.Empty(source.Sessions);
        Assert.False(source.HasLoaded);
    }

    [Fact]
    public async Task FailedLoadIsReportedAndDoesNotLookLikeAnEmptyArchive()
    {
        var (client, proxy) = Gateway();
        var failure = new TimeoutException("Timed out");
        proxy.Response = () => Task.FromException<SessionListResult>(failure);
        Exception? reported = null;
        var source = new ArchivedSessionsSource(() => { }, ex => reported = ex);
        source.SetExpanded(true);
        await source.RefreshAsync(client);
        Assert.True(source.IsUnavailable);
        Assert.True(source.HasLoaded);
        Assert.Same(failure, reported);
    }
}
