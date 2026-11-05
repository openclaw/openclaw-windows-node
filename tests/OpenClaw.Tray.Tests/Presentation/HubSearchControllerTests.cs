using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubSearchControllerTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    public class GatewayProxy : DispatchProxy
    {
        internal bool Connected = true;
        internal long Generation = 1;
        internal string[] Scopes = ["operator.read"];
        internal string[] Methods = ["sessions.search", "sessions.list"];
        internal JsonElement Catalog;
        internal readonly List<string> Requests = [];
        internal readonly Dictionary<string, TaskCompletionSource<GatewaySearchResult>> Queries = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" or "get_HasHandshakeSnapshot" => Connected,
            "get_ServerHandshakeGeneration" => Generation,
            "get_GrantedOperatorScopes" => Scopes,
            "get_AdvertisedServerMethods" => Methods,
            "SearchSessionsAsync" => Search((string)args![0]!, (bool)args[1]!),
            "ReadSearchCatalogAsync" => Task.FromResult(Catalog),
            _ => throw new NotSupportedException(method?.Name)
        };

        private Task<GatewaySearchResult> Search(string query, bool messages)
        {
            Requests.Add(messages ? "sessions.search" : "sessions.list");
            return messages ? (Queries[query] = new()).Task : Task.FromResult(new GatewaySearchResult([], []));
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        private TaskCompletionSource? _nextPost;
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action)
        {
            action();
            Interlocked.Exchange(ref _nextPost, null)?.TrySetResult();
            return true;
        }
        internal Task ObservePost(Action complete)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextPost = pending;
            complete();
            return pending.Task.WaitAsync(CompletionTimeout);
        }
    }

    [Fact]
    public void ReleasedSkillsRouteFlowsThroughTheControllerWithoutEnablingTheWholeDashboardCatalog()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        proxy.Methods = ["skills.status"];
        using var json = JsonDocument.Parse("""{"skills":[{"name":"Weather","skillKey":"weather"}]}""");
        proxy.Catalog = json.RootElement;
        using var controller = new HubSearchController(() => new(client, "main", "2026.11.1", [], []),
            new InlineDispatcher(), _ => { }, TimeSpan.Zero);
        controller.Open();
        controller.SetQuery("Weather");

        var skill = Assert.Single(controller.Results).Item;
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), skill.Target);
        Assert.Equal("/skills", skill.DashboardPath);
        Assert.True(controller.HasDashboardCompatibilityWarning);
    }

    [Fact]
    public async Task QueryChangeRejectsLateResponseAndOldActivation()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("first");
        var old = proxy.Queries["first"];
        controller.SetQuery("second");
        await dispatcher.ObservePost(() => proxy.Queries["second"].SetResult(Hit("two")));
        var current = Assert.Single(controller.Results).Item;
        var generation = controller.Generation;
        await dispatcher.ObservePost(() => old.SetResult(Hit("one")));
        Assert.Equal("agent:main:two", Assert.Single(controller.Results).Item.Target.Value);
        Assert.True(controller.CanActivate(generation, current));
        controller.SetQuery("third");
        Assert.False(controller.CanActivate(generation, current));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionChangesFenceActivationEvenBeforeTheUiRefresh(bool disconnect)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("deploy");
        await dispatcher.ObservePost(() => proxy.Queries["deploy"].SetResult(Hit("one")));
        var item = Assert.Single(controller.Results).Item;
        if (disconnect) proxy.Connected = false;
        else proxy.Generation++;
        Assert.False(controller.CanActivate(controller.Generation, item));
    }

    [Fact]
    public async Task ClosingSearchPreventsLateResultsFromReopeningIt()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("deploy");
        var pending = proxy.Queries["deploy"];
        controller.Close();
        await dispatcher.ObservePost(() => pending.SetResult(Hit("one")));
        Assert.Empty(controller.Results);
    }

    [Fact]
    public async Task FailedMessageSearchIsDistinctFromSuccessfulZeroMatches()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var failures = new List<string>();
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, failures.Add, dispatcher);
        controller.Open();
        controller.SetQuery("query-private");
        await dispatcher.ObservePost(() => proxy.Queries["query-private"].SetException(new TimeoutException("query-private")));
        Assert.Contains(controller.Statuses, status => status.Group == HubSearchGroup.Messages && status.State == "Failed");
        Assert.Equal(["sessions.search:Failed"], failures);
        controller.SetQuery("empty");
        await dispatcher.ObservePost(() => proxy.Queries["empty"].SetResult(new([], [])));
        Assert.DoesNotContain(controller.Statuses, status => status.Group == HubSearchGroup.Messages);
    }

    [Fact]
    public void OpeningAndSingleCharacterQueriesDoNotSearchTranscripts()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        using var controller = Create(client);
        controller.Open();
        controller.SetQuery("a");
        Assert.Empty(proxy.Queries);
    }

    [Fact]
    public void RemoteSearchIsMarkedPendingDuringDebounceRatherThanLookingComplete()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        using var controller = new HubSearchController(() => new(client, "main", "2026.10.9", [], []),
            new InlineDispatcher(), _ => { }, TimeSpan.FromSeconds(1));
        controller.Open();
        controller.SetQuery("camera");
        Assert.Empty(proxy.Queries);
        Assert.Contains(controller.Statuses, status => status.Group == HubSearchGroup.Messages && status.State == "Loading");
        Assert.Contains(controller.Statuses, status => status.Group == HubSearchGroup.Settings && status.State == "Loading");
    }

    [Fact]
    public async Task UnsupportedMethodCodeIsLatchedWithoutDependingOnServerErrorWording()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("first");
        await dispatcher.ObservePost(() => proxy.Queries["first"].SetException(
            new GatewayRequestException("opaque error", "METHOD_NOT_FOUND")));
        controller.SetQuery("second");
        Assert.False(proxy.Queries.ContainsKey("second"));
        Assert.Contains(controller.Statuses, status => status.Group == HubSearchGroup.Messages && status.State == "Unavailable");
    }

    [Fact]
    public async Task ReturnedUncachedMetadataMatchBelongsToChatsRatherThanMessages()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("Uncached conversation");
        await dispatcher.ObservePost(() => proxy.Queries["Uncached conversation"].SetResult(Hit("one")));
        Assert.Equal(HubSearchGroup.Chats, Assert.Single(controller.Results).Item.Group);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionActivityRefreshKeepsPendingReadsAndRemoteResults(bool completed)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        IReadOnlyList<HubSearchItem> local = [];
        using var controller = new HubSearchController(
            () => new(client, "main", "2026.10.9", local, []), dispatcher, _ => { }, TimeSpan.Zero);
        controller.Open();
        controller.SetQuery("lighthouse");
        var pending = proxy.Queries["lighthouse"];
        if (completed) await dispatcher.ObservePost(() => pending.SetResult(Hit("remote")));
        var generation = controller.Generation;
        local = [new("local", HubSearchGroup.Settings, "Lighthouse settings", null, "",
            new(HubSearchTargetKind.Page, "settings"))];
        controller.Refresh();
        Assert.Equal(generation, controller.Generation);
        Assert.Equal(["sessions.list", "sessions.search"], proxy.Requests);
        Assert.Contains(controller.Results, match => match.Item.Id == "local");
        if (!completed) await dispatcher.ObservePost(() => pending.SetResult(Hit("remote")));
        var remote = Assert.Single(controller.Results, match => match.Item.Target.Kind == HubSearchTargetKind.Session).Item;
        Assert.Equal("agent:main:remote", remote.Target.Value);
        Assert.True(controller.CanActivate(generation, remote));
    }

    [Theory]
    [InlineData("operator.write")]
    [InlineData("operator.sessions.read")]
    [InlineData("operator.sessions.write")]
    public async Task ValidRestrictedGrantsReachServerEnforcement(string scope)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        proxy.Scopes = [scope];
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("lighthouse");
        Assert.Equal(["sessions.list", "sessions.search"], proxy.Requests);
        await dispatcher.ObservePost(() => proxy.Queries["lighthouse"].SetException(
            new GatewayRequestException("Server denied this request", "FORBIDDEN")));
        Assert.Contains(controller.Statuses, status => status.Group == HubSearchGroup.Messages && status.State == "Denied");
    }

    [Fact]
    public async Task ClosingThePreviousSurfaceCannotCloseTheCurrentSearch()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        var workspace = new object();
        var settings = new object();
        controller.Open(workspace);
        controller.SetQuery("workspace");
        var old = proxy.Queries["workspace"];
        controller.Open(settings);
        controller.SetQuery("settings");
        var generation = controller.Generation;
        controller.Close(workspace);
        Assert.Equal(generation, controller.Generation);
        Assert.True(controller.IsOwner(settings));
        Assert.False(controller.IsOwner(workspace));
        await dispatcher.ObservePost(() => old.SetResult(Hit("stale")));
        await dispatcher.ObservePost(() => proxy.Queries["settings"].SetResult(Hit("current")));
        var item = Assert.Single(controller.Results).Item;
        Assert.Equal("agent:main:current", item.Target.Value);
        Assert.True(controller.CanActivate(generation, item));
        controller.Close(settings);
        Assert.False(controller.IsOwner(settings));
        Assert.False(controller.CanActivate(generation, item));
    }

    [Fact]
    public async Task ExplicitRetryRestartsFailedReadsButContextChangesStillFenceOldResults()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)client;
        var dispatcher = new InlineDispatcher();
        using var controller = Create(client, dispatcher: dispatcher);
        controller.Open();
        controller.SetQuery("query");
        await dispatcher.ObservePost(() => proxy.Queries["query"].SetException(new TimeoutException()));
        var generation = controller.Generation;
        controller.Retry();
        Assert.True(controller.Generation > generation);
        var old = proxy.Queries["query"];
        proxy.Generation++;
        controller.Refresh();
        await dispatcher.ObservePost(() => old.SetResult(Hit("stale")));
        Assert.Empty(controller.Results);
        await dispatcher.ObservePost(() => proxy.Queries["query"].SetResult(Hit("new")));
        Assert.Equal("agent:main:new", Assert.Single(controller.Results).Item.Target.Value);
    }

    private static HubSearchController Create(IOperatorGatewayClient client, Action<string>? report = null,
        InlineDispatcher? dispatcher = null) =>
        new(() => new(client, "main", "2026.10.9", [], []), dispatcher ?? new InlineDispatcher(), report ?? (_ => { }), TimeSpan.Zero);

    private static GatewaySearchResult Hit(string id) =>
        new([new() { Key = $"agent:main:{id}", Label = "Uncached conversation" }],
            [new($"agent:main:{id}", "A matching transcript fragment", 1)]);
}
