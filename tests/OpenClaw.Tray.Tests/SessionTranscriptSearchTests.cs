using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SessionTranscriptSearchTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 1;
        public bool Connected = true;
        public int Calls;
        public SessionSearchRequest? Request;
        public Func<Task<SessionSearchResult>> Response = () => Task.FromResult(Result("current"));
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_SessionMutationConnectionEpoch" => (long?)Epoch,
            "SearchSessionsAsync" => Load((SessionSearchRequest)args![0]!),
            _ => throw new NotSupportedException(method?.Name)
        };
        private Task<SessionSearchResult> Load(SessionSearchRequest request)
        {
            Calls++;
            Request = request;
            return Response();
        }
    }

    private static SessionSearchResult Result(string key) =>
        new([new(key, "session", "message", "assistant", 1, "Synthetic snippet.", 1)]);

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    [Fact]
    public async Task SubmitRoutesQueryAndBackgroundScope_EmptyQueryOnlyClears()
    {
        var (client, proxy) = Gateway();
        var source = new SessionTranscriptSearch(() => { }, _ => Assert.Fail("Unexpected error."));
        await source.SearchAsync(client, () => client, "needle", true);
        Assert.Equal(new SessionSearchRequest("needle", true), proxy.Request);
        Assert.Equal(SessionSearchStatus.Ready, source.Status);
        Assert.Equal("current", Assert.Single(source.Result!.Results).SessionKey);
        await source.SearchAsync(client, () => client, " ", false);
        Assert.Equal(1, proxy.Calls);
        Assert.Equal(SessionSearchStatus.Idle, source.Status);
        Assert.Null(source.Result);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("unload")]
    [InlineData("reconnect")]
    [InlineData("replacement")]
    public async Task PendingResultsNeverSurviveAuthorityChanges(string change)
    {
        var (client, proxy) = Gateway();
        IOperatorGatewayClient? current = client;
        var pending = new TaskCompletionSource<SessionSearchResult>();
        proxy.Response = () => pending.Task;
        var source = new SessionTranscriptSearch(() => { }, _ => Assert.Fail("Unexpected error."));
        var oldSearch = source.SearchAsync(client, () => current, "old", false);
        Assert.Equal(SessionSearchStatus.Loading, source.Status);
        switch (change)
        {
            case "query":
                proxy.Response = () => Task.FromResult(Result("new"));
                await source.SearchAsync(client, () => current, "new", false);
                break;
            case "unload": source.Clear(); current = null; break;
            case "reconnect": proxy.Epoch++; source.Clear(); break;
            case "replacement": current = Gateway().Client; source.Clear(); break;
        }
        pending.SetResult(Result("stale"));
        await oldSearch;
        if (change == "query")
            Assert.Equal("new", Assert.Single(source.Result!.Results).SessionKey);
        else
            Assert.Null(source.Result);
    }

    [Fact]
    public async Task UnsupportedAndFailedSearchAreNotSuccessfulEmptyResults()
    {
        var (client, proxy) = Gateway();
        Exception? reported = null;
        var source = new SessionTranscriptSearch(() => { }, ex => reported = ex);
        proxy.Response = () => Task.FromResult(new SessionSearchResult([], IsSupported: false));
        await source.SearchAsync(client, () => client, "needle", false);
        Assert.Equal(SessionSearchStatus.Unsupported, source.Status);
        var failure = new TimeoutException();
        proxy.Response = () => Task.FromException<SessionSearchResult>(failure);
        await source.SearchAsync(client, () => client, "needle", false);
        Assert.Equal(SessionSearchStatus.Failed, source.Status);
        Assert.Null(source.Result);
        Assert.Same(failure, reported);
        proxy.Connected = false;
        await source.SearchAsync(client, () => client, "needle", false);
        Assert.Equal(2, proxy.Calls);
        Assert.Equal(SessionSearchStatus.Failed, source.Status);
    }
}
