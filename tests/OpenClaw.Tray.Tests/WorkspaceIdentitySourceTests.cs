using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspaceIdentitySourceTests
{
    private static JsonElement Profile(string? name, params string[] emails) =>
        JsonSerializer.SerializeToElement(new { profile = new { id = "person", displayName = name, emails } });

    private static (IOperatorGatewayClient Client, AgentCreationServiceTests.GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, AgentCreationServiceTests.GatewayProxy>();
        return (client, (AgentCreationServiceTests.GatewayProxy)client);
    }

    [Theory]
    [InlineData("Ada", "ada@example.test", "Ada")]
    [InlineData(null, "ada@example.test", "ada@example.test")]
    [InlineData("", "ada@example.test", "")]
    public void MatchesMacNullishNameThenEmail(string? name, string email, string expected) =>
        Assert.Equal(expected, WorkspaceIdentitySource.ReadDisplayName(Profile(name, email)));

    [Fact]
    public void MissingNameAndEmailsRetainsLocalizedOwnerFallback() =>
        Assert.Null(WorkspaceIdentitySource.ReadDisplayName(Profile(null)));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profile":null}""")]
    [InlineData("""{"profile":{"displayName":"Not authenticated"}}""")]
    public void RejectsNonProfilePayload(string json) =>
        Assert.Throws<JsonException>(() => WorkspaceIdentitySource.ReadDisplayName(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("operator.admin")]
    [InlineData("operator.read")]
    [InlineData("operator.write")]
    [InlineData("operator.sessions.read")]
    [InlineData("operator.sessions.write")]
    public async Task LoadsCanonicalSelfWithExactContract(string scope)
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = [scope];
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("users.self", request.Method);
        Assert.Equal("{}", request.Payload.GetRawText());
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected, "operator.admin")]
    [InlineData(ConnectionStatus.Connecting, "operator.admin")]
    [InlineData(ConnectionStatus.Error, "operator.admin")]
    [InlineData(ConnectionStatus.Connected, "operator.pairing")]
    [InlineData(ConnectionStatus.Connected, "")]
    public async Task DoesNotReadWithoutConnectedSelfReadGrant(ConnectionStatus status, string scope)
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = [scope];
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, status);
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task RefreshesProfileChangesAndClearsOnDisconnect()
    {
        var (client, proxy) = Gateway();
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        proxy.Response = () => Task.FromResult(Profile(null, "new@example.test"));
        await source.RefreshAsync();
        Assert.Equal("new@example.test", source.DisplayName);
        source.SetConnection(client, ConnectionStatus.Disconnected);
        Assert.Null(source.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateResponseCannotOverwriteNewConnection(bool reuseClient)
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var old = source.RefreshAsync();
        source.SetConnection(null, ConnectionStatus.Disconnected);
        var (replacement, replacementProxy) = reuseClient ? (client, proxy) : Gateway();
        replacementProxy.Response = () => Task.FromResult(Profile("New owner"));
        source.SetConnection(replacement, ConnectionStatus.Connected);
        await source.RefreshAsync();
        pending.SetResult(Profile("Old owner"));
        await old;
        Assert.Equal("New owner", source.DisplayName);
    }

    [Fact]
    public async Task CoalescesConcurrentRefreshesButDoesNotLoseInvalidation()
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var first = source.RefreshAsync();
        Assert.Same(first, source.RefreshAsync());
        Assert.Same(first, source.RefreshAsync());
        Assert.Single(proxy.Requests);
        proxy.Response = () => Task.FromResult(Profile("Updated"));
        pending.SetResult(Profile("Initial"));
        await first;
        Assert.Equal("Updated", source.DisplayName);
        Assert.Equal(2, proxy.Requests.Count);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("unsupported")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    public async Task UnavailableProfileClearsNameAndReportsOnlyErrorCategory(string failure)
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        await source.RefreshAsync();
        proxy.Response = () => failure switch
        {
            "timeout" => throw new TimeoutException("sensitive"),
            "malformed" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            _ => throw new InvalidOperationException($"sensitive {failure}")
        };
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        Assert.DoesNotContain("sensitive", Assert.Single(errors));
    }

    [Fact]
    public async Task StableFailureIsNotRetriedUntilProfileInvalidates()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        proxy.Response = () => throw new InvalidOperationException("unknown method");
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Single(proxy.Requests);
        Assert.Single(errors);
        proxy.Response = () => Task.FromResult(Profile("Newly attached"));
        await source.RefreshAsync(invalidate: true);
        Assert.Equal("Newly attached", source.DisplayName);
    }

    [Fact]
    public void ViewRefreshesOnProfileNotEverySessionAndInvalidatesAtClose()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("nameof(AppState.Presence) or nameof(AppState.SelfProfileRevision)", source);
        Assert.DoesNotContain("nameof(AppState.Presence) or nameof(AppState.Sessions)", source);
        Assert.Contains("_identity.SetConnection(null, ConnectionStatus.Disconnected);", source);
        var gatewayService = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "GatewayService.cs"));
        Assert.Contains("client.SelfProfileChanged += OnSelfProfileChanged;", gatewayService);
        Assert.Contains("client.SelfProfileChanged -= OnSelfProfileChanged;", gatewayService);
        Assert.Contains("EnqueueModelUpdate(() => _state.SelfProfileRevision++);", gatewayService);
    }
}
