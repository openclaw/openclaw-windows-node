using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class AgentCreationServiceTests
{
    public class GatewayProxy : DispatchProxy
    {
        public bool Connected = true;
        public long? Epoch = 1;
        public IReadOnlyList<string> Scopes = ["operator.admin"];
        public List<(string Method, JsonElement Payload)> Requests = [];
        public Func<Task<JsonElement>> Response = () => Task.FromResult(
            JsonSerializer.SerializeToElement(new { ok = true, agentId = "research", name = "Research", workspace = "~/research" }));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_GrantedOperatorScopes" => Scopes,
            "get_SessionMutationConnectionEpoch" => Epoch,
            "CreateAgentConfirmedAsync" => SendCreation(args!),
            "SendWizardRequestAsync" => Send(args!),
            _ => throw new NotSupportedException(method?.Name)
        };

        private Task<JsonElement> SendCreation(object?[] args)
        {
            Assert.Equal(Epoch, (long)args[2]!);
            Requests.Add(("agents.create", JsonSerializer.SerializeToElement(new { name = (string)args[0]!, workspace = (string)args[1]! })));
            return Response();
        }

        private Task<JsonElement> Send(object?[] args)
        {
            Requests.Add(((string)args[0]!, JsonSerializer.SerializeToElement(args[1])));
            return Response();
        }
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    [Fact]
    public async Task CreatesAgent_WithExactGatewayPayload_NotSessionOrLocalPath()
    {
        var (client, proxy) = Gateway();
        var service = new AgentCreationService(() => client);
        Assert.True(service.CanCreate);
        Assert.Equal("research", await service.CreateAsync(" Research ", " ~/.openclaw/research "));
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("agents.create", request.Method);
        Assert.Equal("""{"name":"Research","workspace":"~/.openclaw/research"}""", request.Payload.GetRawText());
    }

    [Theory]
    [InlineData(false, "operator.admin")]
    [InlineData(true, "operator.write")]
    [InlineData(true, "operator.read")]
    public async Task RequiresConnectedAdministrator(bool connected, string scope)
    {
        var (client, proxy) = Gateway();
        proxy.Connected = connected;
        proxy.Scopes = [scope];
        var service = new AgentCreationService(() => client);
        Assert.False(service.CanCreate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("", "~/research")]
    [InlineData("Research", " ")]
    public async Task RejectsMissingFieldsBeforeSending(string name, string workspace)
    {
        var (client, proxy) = Gateway();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new AgentCreationService(() => client).CreateAsync(name, workspace));
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("""{"ok":false,"agentId":"research"}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"agentId":""}""")]
    [InlineData("""{"ok":true,"agentId":"research"}""")]
    [InlineData("""{"ok":true,"agentId":"research","name":"","workspace":"~/research"}""")]
    [InlineData("""{"ok":true,"agentId":"research","name":"Research","workspace":null}""")]
    [InlineData("""{"ok":true,"agentId":"research","name":"Research","workspace":42}""")]
    [InlineData("""[]""")]
    public async Task DoesNotInventSuccessFromInvalidResponse(string json)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task PropagatesTimeoutWithoutRetry()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => throw new TimeoutException("No reply");
        await Assert.ThrowsAsync<TimeoutException>(() => new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task PropagatesGatewayRejectionWithoutRetry()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => throw new InvalidOperationException("unknown method: agents.create");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Contains("unknown method", error.Message);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task DoesNotApplySuccessToReplacementGateway()
    {
        var (client, proxy) = Gateway();
        var response = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => response.Task;
        IOperatorGatewayClient? current = client;
        var request = new AgentCreationService(() => current).CreateAsync("Research", "~/research");
        current = Gateway().Client;
        response.SetResult(CreatedAgent());
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Single(proxy.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DialogDoesNotFollowReplacementOrReconnectedGateway(bool sameClient)
    {
        var (client, proxy) = Gateway();
        IOperatorGatewayClient? current = client;
        var service = new AgentCreationService(() => current);
        if (sameClient) proxy.Epoch++;
        else current = Gateway().Client;

        Assert.False(service.CanCreate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("disconnect")]
    [InlineData("permission")]
    public async Task DoesNotApplyAcknowledgementAfterAuthorityChanges(string change)
    {
        var (client, proxy) = Gateway();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => response.Task;
        var request = new AgentCreationService(() => client).CreateAsync("Research", "~/research");
        if (change == "reconnect") proxy.Epoch++;
        if (change == "disconnect") proxy.Connected = false;
        if (change == "permission") proxy.Scopes = ["operator.read"];
        response.SetResult(CreatedAgent());

        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task RequiresAuthenticatedConnectionEpoch()
    {
        var (client, proxy) = Gateway();
        proxy.Epoch = null;
        var service = new AgentCreationService(() => client);
        Assert.False(service.CanCreate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
        Assert.Empty(proxy.Requests);
    }

    private static JsonElement CreatedAgent() => JsonSerializer.SerializeToElement(
        new { ok = true, agentId = "research", name = "Research", workspace = "~/research" });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealClient_CreatesAndListsOnlyWithAdminAuthority(bool allowCreation)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowAgentCreation: allowCreation), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            await ConnectAsync(client);
            var service = new AgentCreationService(() => client);
            var epoch = Assert.IsType<long>(client.SessionMutationConnectionEpoch);
            Assert.Equal(allowCreation, service.CanCreate);
            var before = await ReadAgentsAsync(client);
            Assert.Equal(2, before.GetProperty("agents").GetArrayLength());
            if (!allowCreation)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    client.CreateAgentConfirmedAsync("Research", "~/research", epoch));
                Assert.DoesNotContain(server.Requests, request => request.Method == "agents.create");
            }
            else
            {
                Assert.Equal("fixture-created", await service.CreateAsync(" Research ", " ~/.openclaw/research "));
                var after = await ReadAgentsAsync(client);
                Assert.Equal(3, after.GetProperty("agents").GetArrayLength());
                var created = Assert.Single(after.GetProperty("agents").EnumerateArray(),
                    agent => agent.GetProperty("id").GetString() == "fixture-created");
                Assert.Equal("Research", created.GetProperty("name").GetString());
                Assert.Equal("main", after.GetProperty("defaultId").GetString());
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/.openclaw/research"));
                Assert.Equal(3, (await ReadAgentsAsync(client)).GetProperty("agents").GetArrayLength());
                Assert.Collection(server.Requests.Where(request => request.Method == "agents.create"),
                    request => Assert.Equal("ok", request.Outcome),
                    request => Assert.Equal("error:INVALID_REQUEST", request.Outcome));
            }
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task RealClient_ReconnectionRejectsOriginalDialogAndOriginalDispatchEpoch()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowAgentCreation: true), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            await ConnectAsync(client);
            var service = new AgentCreationService(() => client);
            var originalEpoch = Assert.IsType<long>(client.SessionMutationConnectionEpoch);
            await client.DisconnectAsync();
            await ConnectAsync(client);
            Assert.NotEqual(originalEpoch, client.SessionMutationConnectionEpoch);
            Assert.False(service.CanCreate);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.CreateAgentConfirmedAsync("Research", "~/research", originalEpoch));
            Assert.DoesNotContain(server.Requests, request => request.Method == "agents.create");
            Assert.Equal("fixture-created", await new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
            Assert.Single(server.Requests, request => request.Method == "agents.create" && request.Outcome == "ok");
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    private static async Task ConnectAsync(OpenClawGatewayClient client)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(object? sender, EventArgs args) => ready.TrySetResult();
        void OnAgents(object? sender, JsonElement payload) => agentsReady.TrySetResult();
        client.HandshakeSucceeded += OnReady;
        client.AgentsListUpdated += OnAgents;
        try
        {
            await client.ConnectAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await agentsReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            client.HandshakeSucceeded -= OnReady;
            client.AgentsListUpdated -= OnAgents;
        }
    }

    private static async Task<JsonElement> ReadAgentsAsync(OpenClawGatewayClient client)
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnList(object? sender, JsonElement payload) => response.TrySetResult(payload.Clone());
        client.AgentsListUpdated += OnList;
        try
        {
            await client.RequestAgentsListAsync();
            return await response.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            client.AgentsListUpdated -= OnList;
        }
    }
}
