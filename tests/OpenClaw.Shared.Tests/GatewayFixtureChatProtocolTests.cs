using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using Xunit;
using ConnectedClient = OpenClaw.Shared.Tests.GatewayFixtureProtocolTests.ConnectedClient;

namespace OpenClaw.Shared.Tests;

[Collection("WebSocketClientBase")]
public sealed class GatewayFixtureChatProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Input = "PRIVATE-SYNTHETIC-INPUT";

    [Fact]
    public void NamedChatScenario_IsExplicitAndKeepsTheFiveSessionReadCatalog()
    {
        var browse = GatewayScenario.CreateBrowse();
        var chat = GatewayScenario.LoadBuiltin(GatewayScenario.ChatName);
        Assert.Equal(GatewayScenario.ChatName, chat.Name);
        Assert.Equal(GatewayScenario.CreateChat().Sha256, chat.Sha256);
        Assert.NotEqual(browse.Sha256, chat.Sha256);
        Assert.Equal(browse.SessionKeys, chat.SessionKeys);
        Assert.Equal(browse.ReadMethods, chat.ReadMethods);
        Assert.Equal(5, chat.SessionKeys.Count);
        Assert.DoesNotContain("chat.send", browse.ReadMethods);
        Assert.Throws<ArgumentException>(() => GatewayScenario.ChatDeltas("unknown"));
        using var contract = LoadContract();
        Assert.Contains(contract.RootElement.GetProperty("upstreamCommit").GetString()!, chat.ContractProvenance);
        Assert.Equal(2, chat.Version);
    }

    [Theory]
    [InlineData(GatewayScenario.MainSessionKey)]
    [InlineData(GatewayScenario.OtherSessionKey)]
    public async Task AuthenticatedSend_ReturnsRunAckAndOrderedRealClientDeltasAndFinal(string sessionKey)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        Assert.Equal(["operator.read", "operator.write"], connected.Client.GrantedOperatorScopes);

        var requestedRunId = Guid.NewGuid().ToString();
        var ack = await connected.Client.SendChatMessageForRunAsync(Input, sessionKey, idempotencyKey: requestedRunId);
        Assert.Equal("started", ack.Status);
        Assert.Equal(sessionKey, ack.SessionKey);
        Assert.Equal(requestedRunId, ack.RunId);
        Assert.False(ack.Cached);
        Assert.Null(ack.Error);
        await events.WaitForEndAsync(ack.RunId!);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForChatRunAsync(sessionKey, GatewayFixtureChatPhase.Completed, cancellationToken: deadline.Token);
        var run = Assert.Single(server.ChatRuns);
        Assert.StartsWith("fixture-chat-run-", run.RunId);
        Assert.Equal(GatewayFixtureChatPhase.Completed, run.Phase);
        Assert.Equal(2, run.DeltaCount);
        Assert.True(run.FinalPublished);
        AssertRunEvents(events, run, ack.RunId!);
        var final = Assert.Single(events.Messages);
        Assert.Equal(sessionKey, final.SessionKey);
        Assert.Equal("assistant", final.Role);
        Assert.Equal("final", final.State);
        Assert.Equal(string.Concat(GatewayScenario.ChatDeltas(sessionKey)), final.Text);
        Assert.Equal(1_787_227_200_000L, final.Ts);
        Assert.Equal("ok", (await server.WaitForCompletedRequestAsync("chat.send", sessionKey)).Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ResponseAndDeltaBarriers_ProvePendingBeforeReleaseWithoutBlockingOtherRpc()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        server.HoldChatResponse(GatewayScenario.MainSessionKey);
        server.HoldChatDeltas(GatewayScenario.MainSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        var send = connected.Client.SendChatMessageForRunAsync(Input);
        var heldResponse = await server.WaitForChatRunAsync(
            GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.ResponseHeld, cancellationToken: deadline.Token);
        Assert.True((await connected.Client.SendWizardRequestAsync("health")).GetProperty("ok").GetBoolean());
        Assert.False(send.IsCompleted);
        Assert.Empty(events.AgentEvents);
        Assert.Empty(events.ChatFrames);
        Assert.Equal("pending", (await server.WaitForRequestAsync("chat.send", GatewayScenario.MainSessionKey)).Outcome);
        server.ReleaseChatResponse(GatewayScenario.MainSessionKey);
        var ack = await send.WaitAsync(Deadline);
        Assert.StartsWith("fixture-chat-run-", heldResponse.RunId);
        Assert.True(Guid.TryParse(ack.RunId, out _));
        var heldDelta = await server.WaitForChatRunAsync(
            GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.DeltasHeld, cancellationToken: deadline.Token);
        await connected.Client.SendWizardRequestAsync("health");
        Assert.Equal(0, heldDelta.DeltaCount);
        Assert.False(heldDelta.FinalPublished);
        Assert.Equal("lifecycle", Assert.Single(events.AgentEvents).Stream);
        Assert.Empty(events.ChatFrames);
        server.ReleaseChatDeltas(GatewayScenario.MainSessionKey);
        await events.WaitForEndAsync(ack.RunId!);
        AssertRunEvents(events, Assert.Single(server.ChatRuns), ack.RunId!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Abort_PublishesAbortedTerminalAndStopsFurtherOutputWithoutTaintingNextRun(int afterDeltaCount)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        server.HoldChatDeltas(GatewayScenario.MainSessionKey, afterDeltaCount);
        using var deadline = new CancellationTokenSource(Deadline);
        var ack = await connected.Client.SendChatMessageForRunAsync(Input);
        await server.WaitForChatRunAsync(
            GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.DeltasHeld, cancellationToken: deadline.Token);
        var result = await connected.Client.SendWizardRequestAsync("chat.abort",
            new { sessionKey = GatewayScenario.MainSessionKey, runId = ack.RunId });
        using var contract = LoadContract();
        var abortContract = contract.RootElement.GetProperty("abort");
        Assert.Equal(abortContract.GetProperty("resultFields").EnumerateArray().Select(field => field.GetString()).Order(),
            result.EnumerateObject().Select(field => field.Name).Order());
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("aborted").GetBoolean());
        Assert.Equal(ack.RunId, Assert.Single(result.GetProperty("runIds").EnumerateArray()).GetString());
        server.ReleaseChatDeltas(GatewayScenario.MainSessionKey);
        var next = await connected.Client.SendChatMessageForRunAsync(Input);
        await events.WaitForEndAsync(next.RunId!);
        Assert.NotEqual(ack.RunId, next.RunId);
        var aborted = server.ChatRuns[0];
        Assert.Equal(GatewayFixtureChatPhase.Aborted, aborted.Phase);
        Assert.Equal(afterDeltaCount, aborted.DeltaCount);
        Assert.False(aborted.FinalPublished);
        var oldEvents = events.AgentEvents.Where(evt => evt.RunId == ack.RunId).ToArray();
        Assert.Equal(1 + afterDeltaCount, oldEvents.Length);
        Assert.Equal("start", oldEvents[0].Data.GetProperty("phase").GetString());
        var abortedFrame = Assert.Single(events.ChatFrames, evt => evt.RunId == ack.RunId);
        Assert.Equal(abortContract.GetProperty("terminalState").GetString(), abortedFrame.Data.GetProperty("state").GetString());
        Assert.Equal(abortContract.GetProperty("stopReason").GetString(), abortedFrame.Data.GetProperty("stopReason").GetString());
        Assert.Equal(afterDeltaCount + 2, abortedFrame.Seq);
        Assert.Equal(next.RunId, Assert.Single(events.ChatFrames, evt =>
            evt.Data.GetProperty("state").GetString() == "final").RunId);
        AssertRunEvents(events, server.ChatRuns[1], next.RunId!);
        Assert.Equal("ok", (await server.WaitForCompletedRequestAsync("chat.abort")).Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task TwoSessions_HeldRunReleasedAfterAnotherKeepsRunSessionAndTextOwnership()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        server.HoldChatDeltas(GatewayScenario.MainSessionKey, afterDeltaCount: 1);
        using var deadline = new CancellationTokenSource(Deadline);
        var first = await connected.Client.SendChatMessageForRunAsync(Input, GatewayScenario.MainSessionKey);
        await server.WaitForChatRunAsync(
            GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.DeltasHeld, cancellationToken: deadline.Token);
        var second = await connected.Client.SendChatMessageForRunAsync(Input, GatewayScenario.OtherSessionKey);
        await events.WaitForEndAsync(second.RunId!);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(second.RunId, Assert.Single(events.ChatFrames).RunId);
        Assert.Equal(GatewayFixtureChatPhase.DeltasHeld, server.ChatRuns[0].Phase);
        server.ReleaseChatDeltas(GatewayScenario.MainSessionKey);
        await events.WaitForEndAsync(first.RunId!);
        Assert.Collection(events.ChatFrames,
            evt => Assert.Equal(second.RunId, evt.RunId),
            evt => Assert.Equal(first.RunId, evt.RunId));
        AssertRunEvents(events, server.ChatRuns[0], first.RunId!);
        AssertRunEvents(events, server.ChatRuns[1], second.RunId!);
        Assert.All(events.Messages, message =>
            Assert.Equal(string.Concat(GatewayScenario.ChatDeltas(message.SessionKey)), message.Text));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Abort_RejectsWrongSessionAndConnectionAndUnknownOrFinishedRun()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var first = await ConnectedClient.OpenAsync(server, token);
        await using var second = await ConnectedClient.OpenAsync(server, token);
        using var firstEvents = new ChatEvents(first.Client);
        using var secondEvents = new ChatEvents(second.Client);
        server.HoldChatDeltas(GatewayScenario.MainSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        var ack = await first.Client.SendChatMessageForRunAsync(Input);
        await server.WaitForChatRunAsync(
            GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.DeltasHeld, cancellationToken: deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.Client.SendChatAbortAsync(ack.RunId!, GatewayScenario.OtherSessionKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.Client.SendChatAbortAsync(ack.RunId!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Client.SendChatAbortAsync("fixture-never-issued"));
        Assert.Equal(GatewayFixtureChatPhase.DeltasHeld, Assert.Single(server.ChatRuns).Phase);
        server.ReleaseChatDeltas(GatewayScenario.MainSessionKey);
        await firstEvents.WaitForEndAsync(ack.RunId!);
        await second.Client.SendWizardRequestAsync("health");
        Assert.Empty(secondEvents.AgentEvents);
        Assert.Empty(secondEvents.ChatFrames);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Client.SendChatAbortAsync(ack.RunId!));
        Assert.All(server.Requests.Where(request => request.Method == "chat.abort"),
            request => Assert.Equal("error:INVALID_PARAMS", request.Outcome));
        AssertRunEvents(firstEvents, Assert.Single(server.ChatRuns), ack.RunId!);
    }

    [Theory]
    [InlineData("chat.send", """{"sessionKey":"unknown","message":"synthetic","idempotencyKey":"fixture-input"}""")]
    [InlineData("chat.send", """{"sessionKey":"agent:main:main","message":42,"idempotencyKey":"fixture-input"}""")]
    [InlineData("chat.send", """{"sessionKey":"agent:main:main","message":"synthetic"}""")]
    [InlineData("chat.send", """{"sessionKey":"agent:main:main","message":"synthetic","idempotencyKey":"fixture-input","sessionId":"invented"}""")]
    [InlineData("chat.send", """{"sessionKey":"agent:main:main","message":"synthetic","idempotencyKey":"fixture-input","attachments":[]}""")]
    [InlineData("chat.abort", """{"sessionKey":"unknown","runId":"fixture-never-issued"}""")]
    [InlineData("chat.abort", """{"sessionKey":"agent:main:main","runId":42}""")]
    public async Task InvalidOrUnsupportedChatParameters_RejectBeforeCreatingAnyRun(string method, string json)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var parameters = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync(method, parameters.RootElement));
        Assert.Equal("error:INVALID_PARAMS", (await server.WaitForCompletedRequestAsync(method)).Outcome);
        Assert.Empty(server.ChatRuns);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Metadata_RetainsOnlyFixtureOwnedCorrelationAndNeverInputCredentialsOrArbitraryRunIds()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        var ack = await connected.Client.SendChatMessageForRunAsync(
            Input, idempotencyKey: "PRIVATE-IDEMPOTENCY-KEY");
        await events.WaitForEndAsync(ack.RunId!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendChatAbortAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync("config.patch", new { token, input = Input }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync("unknown.mutation", new { token, input = Input }));
        Assert.Equal("error:FIXTURE_READ_ONLY",
            (await server.WaitForCompletedRequestAsync("config.patch")).Outcome);
        Assert.Equal("error:METHOD_NOT_FOUND", Assert.Single(server.UnexpectedRequests).Outcome);
        var metadata = JsonSerializer.Serialize(new { server.Requests, server.ChatRuns });
        Assert.DoesNotContain(token, metadata);
        Assert.DoesNotContain(Input, metadata);
        Assert.DoesNotContain("PRIVATE-IDEMPOTENCY-KEY", metadata);
        Assert.DoesNotContain("signature", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.All(server.ChatRuns, run => Assert.StartsWith("fixture-chat-run-", run.RunId));
        Assert.All(server.Requests, request => Assert.True(request.SessionKey is null or "<unknown>"
            || GatewayScenario.CreateBrowse().SessionKeys.Contains(request.SessionKey)));
    }

    [Fact]
    public async Task WrongCredential_CannotAuthenticateOrSendChat()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), CreateToken());
        using var identity = new TempDirectory(Path.Combine(Directory.GetCurrentDirectory(), ".fixture-chat-identity-"));
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, CreateToken(),
            NullLogger.Instance, identityPath: identity.Path, ignoreStoredDeviceToken: true,
            persistHandshakeDeviceTokens: false);
        var failure = new TaskCompletionSource<GatewayErrorKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionFailure += (_, kind) => failure.TrySetResult(kind);
        try
        {
            await client.ConnectAsync();
            Assert.Equal(GatewayErrorKind.Auth, await failure.Task.WaitAsync(Deadline));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendChatMessageForRunAsync(Input));
            Assert.Empty(server.ChatRuns);
            Assert.DoesNotContain(server.Requests, request => request.Method == "chat.send");
        }
        finally { await client.DisconnectAsync(); }
    }

    [Fact]
    public async Task IdempotencyReplay_IsExplicitlyUnsupportedRatherThanCreatingAnotherRun()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        var key = Guid.NewGuid().ToString();
        var ack = await connected.Client.SendChatMessageForRunAsync(Input, idempotencyKey: key);
        await events.WaitForEndAsync(ack.RunId!);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendChatMessageForRunAsync(Input, idempotencyKey: key));
        Assert.Single(server.ChatRuns);
        Assert.Single(events.ChatFrames);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shutdown_CancelsHeldChatAndWaitersAndDrainsOwnedConnections(bool holdResponse)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        if (holdResponse)
            server.HoldChatResponse(GatewayScenario.MainSessionKey);
        else
            server.HoldChatDeltas(GatewayScenario.MainSessionKey, afterDeltaCount: 1);
        using var deadline = new CancellationTokenSource(Deadline);
        var send = connected.Client.SendChatMessageForRunAsync(Input);
        await server.WaitForChatRunAsync(GatewayScenario.MainSessionKey,
            holdResponse ? GatewayFixtureChatPhase.ResponseHeld : GatewayFixtureChatPhase.DeltasHeld,
            cancellationToken: deadline.Token);
        var missing = server.WaitForChatRunAsync(GatewayScenario.OtherSessionKey, GatewayFixtureChatPhase.Completed);
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        if (holdResponse)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Deadline));
        else
            Assert.Equal("started", (await send.WaitAsync(Deadline)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing);
        Assert.Equal(GatewayFixtureChatPhase.Cancelled, Assert.Single(server.ChatRuns).Phase);
        Assert.False(Assert.Single(server.ChatRuns).FinalPublished);
        Assert.Equal(0, server.ActiveConnectionCount);
        Assert.Equal(0, server.SubscriptionCount);
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port));
    }

    [Fact]
    public async Task ShutdownAfterFinal_PreservesPublishedOutputWithoutInventingLifecycleEnd()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        server.HoldChatCompletion(GatewayScenario.MainSessionKey);
        var ack = await connected.Client.SendChatMessageForRunAsync(Input);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForChatRunAsync(GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.CompletionHeld,
            cancellationToken: deadline.Token);
        await events.WaitForFinalAsync(ack.RunId!);
        Assert.True(Assert.Single(server.ChatRuns).FinalPublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendChatAbortAsync(ack.RunId!));
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        var run = Assert.Single(server.ChatRuns);
        Assert.Equal(GatewayFixtureChatPhase.Cancelled, run.Phase);
        Assert.True(run.FinalPublished);
        Assert.Equal([1, 2, 3, 4], events.Timeline.Select(item => item.Event.Seq));
        Assert.DoesNotContain(events.AgentEvents, evt => evt.Stream == "lifecycle" && evt.Data.GetProperty("phase").GetString() == "end");
    }

    [Fact]
    public async Task ReleasingDeltasWhileAborting_DoesNotPublishAfterAcceptedAbort()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateChat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var events = new ChatEvents(connected.Client);
        server.HoldChatDeltas(GatewayScenario.MainSessionKey, afterDeltaCount: 1);
        var ack = await connected.Client.SendChatMessageForRunAsync(Input);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForChatRunAsync(GatewayScenario.MainSessionKey, GatewayFixtureChatPhase.DeltasHeld,
            cancellationToken: deadline.Token);
        var abort = Task.Run(async () =>
        {
            try
            {
                await connected.Client.SendChatAbortAsync(ack.RunId!);
                return true;
            }
            catch (InvalidOperationException failure)
            {
                Assert.Contains("not active", failure.Message);
                return false;
            }
        });
        await Task.Run(() => server.ReleaseChatDeltas(GatewayScenario.MainSessionKey));
        var aborted = await abort.WaitAsync(Deadline);
        var next = await connected.Client.SendChatMessageForRunAsync(Input);
        await events.WaitForEndAsync(next.RunId!);
        if (aborted)
        {
            Assert.Equal(GatewayFixtureChatPhase.Aborted, server.ChatRuns[0].Phase);
            Assert.False(server.ChatRuns[0].FinalPublished);
            var abortedFrame = Assert.Single(events.ChatFrames, evt => evt.RunId == ack.RunId);
            Assert.Equal("aborted", abortedFrame.Data.GetProperty("state").GetString());
            Assert.DoesNotContain(events.AgentEvents, evt => evt.RunId == ack.RunId
                && evt.Stream == "lifecycle" && evt.Data.GetProperty("phase").GetString() == "end");
        }
        else
        {
            Assert.True(server.ChatRuns[0].FinalPublished);
            AssertRunEvents(events, server.ChatRuns[0], ack.RunId!);
        }
        AssertRunEvents(events, server.ChatRuns[1], next.RunId!);
    }

    private static void AssertRunEvents(ChatEvents events, GatewayFixtureChatRun run, string wireRunId)
    {
        var timeline = events.Timeline.Where(item => item.Event.RunId == wireRunId).ToArray();
        Assert.Equal([1, 2, 3, 4, 5], timeline.Select(item => item.Event.Seq));
        Assert.Equal(["agent", "agent", "agent", "chat", "agent"], timeline.Select(item => item.Kind));
        var agent = events.AgentEvents.Where(evt => evt.RunId == wireRunId).ToArray();
        Assert.Equal([1, 2, 3, 5], agent.Select(evt => evt.Seq));
        Assert.All(agent, evt => Assert.Equal(run.SessionKey, evt.SessionKey));
        Assert.Equal("lifecycle", agent[0].Stream);
        Assert.Equal("start", agent[0].Data.GetProperty("phase").GetString());
        Assert.Equal(["assistant", "assistant"], agent.Skip(1).Take(2).Select(evt => evt.Stream));
        Assert.Equal(GatewayScenario.ChatDeltas(run.SessionKey),
            agent.Skip(1).Take(2).Select(evt => evt.Data.GetProperty("delta").GetString()));
        Assert.Equal("lifecycle", agent[^1].Stream);
        Assert.Equal("end", agent[^1].Data.GetProperty("phase").GetString());
        var final = Assert.Single(events.ChatFrames, evt => evt.RunId == wireRunId);
        Assert.Equal(run.SessionKey, final.SessionKey);
        Assert.Equal(4, final.Seq);
        Assert.Equal("final", final.Data.GetProperty("state").GetString());
        Assert.Equal(string.Concat(GatewayScenario.ChatDeltas(run.SessionKey)),
            final.Data.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static JsonDocument LoadContract() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Protocol", "gateway-chat-fixture-contract.json")));

    private sealed class ChatEvents : IDisposable
    {
        private readonly OpenClawGatewayClient _client;
        private readonly Channel<AgentEventInfo> _agent = Channel.CreateUnbounded<AgentEventInfo>();
        private readonly Channel<AgentEventInfo> _chat = Channel.CreateUnbounded<AgentEventInfo>();
        public ConcurrentQueue<(string Kind, AgentEventInfo Event)> Timeline { get; } = new();
        public ConcurrentQueue<AgentEventInfo> AgentEvents { get; } = new();
        public ConcurrentQueue<AgentEventInfo> ChatFrames { get; } = new();
        public ConcurrentQueue<ChatMessageInfo> Messages { get; } = new();

        public ChatEvents(OpenClawGatewayClient client)
        {
            _client = client;
            client.AgentEventReceived += OnAgent;
            client.ChatEventReceived += OnChat;
            client.ChatMessageReceived += OnMessage;
        }

        private void OnAgent(object? sender, AgentEventInfo evt)
        {
            Timeline.Enqueue(("agent", evt));
            AgentEvents.Enqueue(evt);
            _agent.Writer.TryWrite(evt);
        }

        private void OnChat(object? sender, AgentEventInfo evt)
        {
            Timeline.Enqueue(("chat", evt));
            ChatFrames.Enqueue(evt);
            _chat.Writer.TryWrite(evt);
        }
        private void OnMessage(object? sender, ChatMessageInfo message) => Messages.Enqueue(message);

        public async Task WaitForEndAsync(string runId)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                var evt = await _agent.Reader.ReadAsync(deadline.Token);
                if (evt.RunId == runId && evt.Stream == "lifecycle" && evt.Data.GetProperty("phase").GetString() == "end")
                    return;
            }
        }

        public async Task WaitForFinalAsync(string runId)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while ((await _chat.Reader.ReadAsync(deadline.Token)).RunId != runId) { }
        }

        public void Dispose()
        {
            _client.AgentEventReceived -= OnAgent;
            _client.ChatEventReceived -= OnChat;
            _client.ChatMessageReceived -= OnMessage;
            _agent.Writer.TryComplete();
            _chat.Writer.TryComplete();
        }
    }
}
