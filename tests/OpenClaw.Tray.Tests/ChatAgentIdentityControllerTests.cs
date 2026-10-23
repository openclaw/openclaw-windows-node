using System.Security.Cryptography;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

[Collection("Chat telemetry")]
public sealed class ChatAgentIdentityControllerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ProductionBridgeProviderAndFactory_ProjectResolvedSelectedAgentFromAuthenticatedSocket()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAgentIdentity(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => handshake.TrySetResult();
        await client.ConnectAsync();
        await handshake.Task.WaitAsync(Deadline);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        using var dispatcher = new RecordingUiDispatcher();
        using var session = new ChatComposerFactory(dispatcher).Create(
            provider, new(null, null, null, null), false);
        Assert.Same(client, provider.AgentIdentityGateway);
        var controller = Assert.IsType<ChatAgentIdentityController>(session.Identity);
        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () => { if (controller.Identity?.AgentId == "research") resolved.TrySetResult(); };
        session.ApplyInputs(Inputs("research"));
        await resolved.Task.WaitAsync(Deadline);
        var agent = ChatWelcomePresentation.AgentIdentity(null, "research", controller.Identity);
        Assert.Equal("Resolved Research", agent.Name);
        Assert.Equal("\u2728", agent.Emoji);
        Assert.Null(agent.AvatarUrl);
        Assert.DoesNotContain(server.Requests, request => request.Method is "projects.list" or "environments.list");
        Assert.Single(server.Requests, request => request.Method == "agent.identity.get");
        session.ApplyInputs(Inputs("main") with { ConnectionState = "disconnected" });
        Assert.Null(controller.Identity);
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Fact]
    public void Selection_IsIndependentOfPlacementAndDoesNotReloadForSameAgentSessions()
    {
        var gateway = new Gateway();
        using var dispatcher = new RecordingUiDispatcher();
        using var controller = new ChatAgentIdentityController(gateway, dispatcher);
        controller.ApplyInputs(Inputs());
        Assert.Equal(new("main", "Resolved main", null, null), controller.Identity);
        controller.ApplyInputs(Inputs() with { CurrentThread = new() { Id = "another", Title = "Another", AgentId = "main" } });
        Assert.Equal(1, gateway.Calls);
        Assert.False(Inputs().ShowPlacementPickers);
    }

    [Fact]
    public void AgentSwitch_DropsQueuedPreviousAgentReplyAndClearsOldDisplayImmediately()
    {
        var gateway = new Gateway();
        using var dispatcher = new RecordingUiDispatcher { RunEnqueuedImmediately = false };
        using var controller = new ChatAgentIdentityController(gateway, dispatcher);
        controller.ApplyInputs(Inputs());
        controller.ApplyInputs(Inputs("research"));
        Assert.True(gateway.Tokens[0].IsCancellationRequested);
        Assert.Null(controller.Identity);
        dispatcher.FlushPending();
        Assert.Equal("research", controller.Identity?.AgentId);
        Assert.Equal("Resolved research", controller.Identity?.Name);
    }

    [Fact]
    public void ReconnectAndDisposal_RejectQueuedResultsEvenForTheSameAgent()
    {
        var gateway = new Gateway();
        using var dispatcher = new RecordingUiDispatcher { RunEnqueuedImmediately = false };
        using var controller = new ChatAgentIdentityController(gateway, dispatcher);
        controller.ApplyInputs(Inputs());
        gateway.IdentityConnectionEpoch++;
        dispatcher.FlushPending();
        Assert.Null(controller.Identity);
        controller.ApplyInputs(Inputs());
        controller.Dispose();
        dispatcher.FlushPending();
        Assert.Null(controller.Identity);
        Assert.True(gateway.Tokens.All(token => token.IsCancellationRequested));
    }

    [Theory]
    [InlineData("disconnected", true)]
    [InlineData("connected", false)]
    public void UnavailableIdentity_DoesNotDispatch(string connectionState, bool canRead)
    {
        var gateway = new Gateway { CanReadAgentIdentity = canRead };
        using var dispatcher = new RecordingUiDispatcher();
        using var controller = new ChatAgentIdentityController(gateway, dispatcher);
        controller.ApplyInputs(Inputs() with { ConnectionState = connectionState });
        Assert.Equal(0, gateway.Calls);
        Assert.Null(controller.Identity);
    }

    [Fact]
    public void LookupFailure_LeavesFallbackAndNeverLoopsOnOrdinaryInputUpdates()
    {
        var gateway = new Gateway { Fail = true };
        using var dispatcher = new RecordingUiDispatcher();
        using var controller = new ChatAgentIdentityController(gateway, dispatcher);
        controller.ApplyInputs(Inputs());
        controller.ApplyInputs(Inputs());
        Assert.Null(controller.Identity);
        Assert.Equal(1, gateway.Calls);
    }

    private static ChatComposerInputs Inputs(string agent = "main") => new(
        "connected", false, new ChatThread { Id = $"agent:{agent}:new", Title = "New chat", AgentId = agent },
        [], null, false, [], null, false);

    private sealed class Gateway : IAgentIdentityGateway
    {
        public long? IdentityConnectionEpoch { get; set; } = 1;
        public bool CanReadAgentIdentity { get; set; } = true;
        public int Calls;
        public bool Fail;
        public List<CancellationToken> Tokens { get; } = [];

        public Task<AgentDisplayIdentity> GetAgentIdentityAsync(string agentId, long connectionEpoch, CancellationToken cancellationToken)
        {
            Calls++;
            Tokens.Add(cancellationToken);
            return Fail ? Task.FromException<AgentDisplayIdentity>(new InvalidOperationException("Failed lookup"))
                : Task.FromResult(new AgentDisplayIdentity(agentId, "Resolved " + agentId, null, null));
        }
    }
}
