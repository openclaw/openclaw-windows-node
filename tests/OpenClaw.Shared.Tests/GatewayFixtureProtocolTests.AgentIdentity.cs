using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task AgentIdentity_AuthenticatedProductionClientUsesSelectedAgentAndCurrentEpoch()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAgentIdentity(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        Assert.True(client.CanReadAgentIdentity);
        Assert.Contains("operator.read", client.GrantedOperatorScopes);
        var epoch = Assert.IsType<long>(client.IdentityConnectionEpoch);
        Assert.Equal(new("research", "Resolved Research", "\u2728", null),
            await client.GetAgentIdentityAsync(" research ", epoch));
        Assert.Equal(new("main", "Resolved Main", "\u2728", null),
            await client.GetAgentIdentityAsync("main", epoch));
        Assert.Equal(1, server.SharedCredentialConnects);
        Assert.Equal(2, server.Requests.Count(request => request.Method == "agent.identity.get"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAgentIdentityAsync("research", epoch + 1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAgentIdentityAsync("research", epoch, canceled.Token));
        Assert.Equal(2, server.Requests.Count(request => request.Method == "agent.identity.get"));
        await client.DisconnectAsync();
        Assert.Null(client.IdentityConnectionEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAgentIdentityAsync("research", epoch));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false, "operator.read")]
    [InlineData(true, "operator.write")]
    public async Task AgentIdentity_RequiresAdvertisedMethodAndReadAuthority(bool advertised, string scope)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateAgentIdentity(advertised: advertised, scopes: [scope]), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.False(connected.Client.CanReadAgentIdentity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.GetAgentIdentityAsync(
            "research", connected.Client.IdentityConnectionEpoch!.Value));
        Assert.DoesNotContain(server.Requests, request => request.Method == "agent.identity.get");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"agentId":"main","name":"Foreign"}""")]
    [InlineData("""{"agentId":"research","avatar":42}""")]
    [InlineData("error")]
    public async Task AgentIdentity_RejectsMalformedNullForeignAndErrorSocketReplies(string response)
    {
        var token = CreateToken();
        var scenario = response == "error" ? GatewayScenario.CreateAgentIdentityError("INVALID_REQUEST")
            : GatewayScenario.CreateAgentIdentity(_ => JsonSerializer.Deserialize<JsonElement>(response));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.GetAgentIdentityAsync(
            "research", connected.Client.IdentityConnectionEpoch!.Value));
        Assert.Single(server.Requests, request => request.Method == "agent.identity.get");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task AgentIdentity_CanceledReadDrainsLateReplyWithoutReplacingNextAgent()
    {
        using var release = new ManualResetEventSlim();
        var entered = Signal<bool>();
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAgentIdentity(parameters =>
        {
            var agent = parameters.GetProperty("agentId").GetString();
            if (agent == "research")
            {
                entered.TrySetResult(true);
                if (!release.Wait(Deadline))
                    throw new TimeoutException("Test did not release the held identity response.");
            }
            return new { agentId = agent, name = "Resolved " + agent };
        });
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.IdentityConnectionEpoch!.Value;
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAgentIdentityAsync("research", epoch, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        }
        finally { release.Set(); }
        var next = await client.GetAgentIdentityAsync("main", epoch).WaitAsync(Deadline);
        Assert.Equal("main", next.AgentId);
        Assert.Equal("Resolved main", next.Name);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task AgentIdentity_ReconnectRequiresNewAuthenticatedEpoch()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAgentIdentity(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var previousEpoch = client.IdentityConnectionEpoch!.Value;
        await client.GetAgentIdentityAsync("research", previousEpoch);
        await client.DisconnectAsync();
        var handshake = Signal<bool>();
        client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
        await client.ConnectAsync();
        await handshake.Task.WaitAsync(Deadline);
        var epoch = client.IdentityConnectionEpoch!.Value;
        Assert.NotEqual(previousEpoch, epoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAgentIdentityAsync("research", previousEpoch));
        Assert.Equal("Resolved Research", (await client.GetAgentIdentityAsync("research", epoch)).Name);
        Assert.Equal(2, server.SharedCredentialConnects);
        Assert.Equal(2, server.Requests.Count(request => request.Method == "agent.identity.get"));
    }
}
