using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task Interactive_RealClientStreamsCreatesSessionsAndKeepsHistory()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var events = new ConcurrentQueue<AgentEventInfo>();
        var final = Signal<ChatMessageInfo>();
        client.AgentEventReceived += (_, data) => events.Enqueue(data);
        client.ChatMessageReceived += (_, data) =>
        {
            if (data.Text.Contains("FIXTURE INTERACTIVE COMPLETE", StringComparison.Ordinal))
                final.TrySetResult(data);
        };
        var created = await client.CreateSessionAsync(new SessionCreateRequest { AgentId = "research" });
        Assert.True(created.Ok, created.Error);
        var key = created.Key!;
        var send = await client.SendChatMessageForRunAsync("A synthetic prompt.", key, idempotencyKey: "first");
        Assert.StartsWith("fixture-run-", send.RunId);
        var answer = await final.Task.WaitAsync(Deadline);
        Assert.Equal(key, answer.SessionKey);
        await WaitIdleAsync(client, key);
        var history = await client.RequestChatHistoryAsync(key);
        Assert.Equal(created.SessionId, history.SessionId);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("A synthetic prompt.", history.Messages[0].Text);
        Assert.Equal(answer.Text, history.Messages[1].Text);
        Assert.Contains(events, e => e.Stream == "assistant" && e.Data.GetProperty("delta").GetString()!.Contains("synthetic"));
        Assert.Contains(events, e => e.Stream == "reasoning");
        Assert.All(events, e => { Assert.Equal(send.RunId, e.RunId); Assert.Equal(key, e.SessionKey); });
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Seq < pair.Second.Seq));
        var retry = await client.SendChatMessageForRunAsync("A synthetic prompt.", key, idempotencyKey: "first");
        Assert.Equal(send.RunId, retry.RunId);
        Assert.Equal(2, (await client.RequestChatHistoryAsync(key)).Messages.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendChatMessageForRunAsync("Changed input.", key, idempotencyKey: "first"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Interactive_PausedRunsAbortWithoutWaitingForPlaybackAndAcceptNextSend()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.Interactive!.Playback.Pause();
        var client = connected.Client;
        var key = GatewayScenario.EmptySessionKey;
        var first = await client.SendChatMessageForRunAsync("Stop before the first event.", key);
        await client.SendChatAbortAsync(first.RunId!, key).WaitAsync(Deadline);
        await WaitIdleAsync(client, key);
        Assert.Single((await client.RequestChatHistoryAsync(key)).Messages);
        var second = await client.SendChatMessageForRunAsync("Next turn.", key);
        server.Interactive.Playback.Resume();
        await WaitIdleAsync(client, key);
        var history = await client.RequestChatHistoryAsync(key);
        Assert.Equal(3, history.Messages.Count);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Single(history.Messages, m => m.Text.Contains("FIXTURE INTERACTIVE COMPLETE"));
    }

    [Theory]
    [InlineData("tools", "final")]
    [InlineData("tool-error", "final")]
    [InlineData("agent-activity", "final")]
    [InlineData("chat-deltas", "final")]
    [InlineData("stream-error", "error")]
    [InlineData("slow", "final")]
    public async Task Interactive_RepeatableScenariosHaveCorrelatedTerminalEvents(string scenario, string terminal)
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select(scenario);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var accepted = await peer.RequestAsync("chat.send", SendParams("one"));
        var run = accepted.GetProperty("runId").GetString();
        var frames = await peer.ThroughAsync(e => IsChat(e, terminal));
        Assert.Equal(run, frames[^1].GetProperty("payload").GetProperty("runId").GetString());
        if (scenario.StartsWith("tool", StringComparison.Ordinal))
        {
            var tools = frames.Where(e => e.GetProperty("event").GetString() == "agent" && e.GetProperty("payload").GetProperty("stream").GetString() == "tool").ToArray();
            Assert.Equal(2, tools.Length);
            Assert.Equal(scenario == "tool-error", tools[1].GetProperty("payload").GetProperty("data").GetProperty("isError").GetBoolean());
        }
        if (scenario == "slow") Assert.Contains(frames, e => IsChat(e, "status"));
        if (scenario == "chat-deltas") Assert.Contains(frames, e => IsChat(e, "delta") && e.GetProperty("payload").GetProperty("deltaText").GetString()!.Length > 0);
        var history = await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey });
        Assert.Equal(scenario.StartsWith("tool", StringComparison.Ordinal) ? 4 : 2, history.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task WelcomeRecovery_CatalogAndEmptySessionSupportRejectedThenAcceptedSuggestion()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateWelcomeRecovery();
        Assert.Equal(GatewayScenario.WelcomeRecoveryName, scenario.Name);
        Assert.Contains("controlUi.welcome.get", scenario.ReadMethods);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var catalog = await client.GetChatWelcomeCatalogAsync("en-US");
        Assert.NotNull(catalog);
        Assert.Equal(["fixture-first", "fixture-second"], catalog.Suggestions.Select(suggestion => suggestion.Id));
        var key = GatewayScenario.WelcomeRecoverySessionKey;
        Assert.Empty((await client.RequestChatHistoryAsync(key)).Messages);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var sessions = await peer.RequestAsync("sessions.list", new { agentId = "research" });
        Assert.Equal(key, Assert.Single(sessions.GetProperty("sessions").EnumerateArray()).GetProperty("key").GetString());

        server.Interactive!.Playback.Select("send-error");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendChatMessageForRunAsync(catalog.Suggestions[0].Prompt, key, idempotencyKey: "welcome-first"));
        Assert.Empty((await client.RequestChatHistoryAsync(key)).Messages);

        await client.SendChatMessageForRunAsync(catalog.Suggestions[1].Prompt, key, idempotencyKey: "welcome-second");
        await WaitIdleAsync(client, key);
        var history = await client.RequestChatHistoryAsync(key);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("Fixture welcome: second suggestion", history.Messages[0].Text);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Interactive_SendRejectionDoesNotPersistInputOrConsumeIdempotency()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select("send-error");
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("chat.send", SendParams("retry")));
        var empty = await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey });
        Assert.Empty(empty.GetProperty("messages").EnumerateArray());
        await peer.RequestAsync("chat.send", SendParams("retry"));
        await peer.ThroughAsync(e => IsChat(e, "final"));
        Assert.Equal(2, (await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey })).GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData("question-choice", "[\"Inspect\"]")]
    [InlineData("question-text", "[\"My task\"]")]
    [InlineData("question-multi", "[\"Inspect\",\"Summarize\"]")]
    public async Task Interactive_QuestionsWaitValidateResolveAndResume(string scenario, string answerJson)
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select(scenario);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var send = await peer.RequestAsync("chat.send", SendParams(scenario));
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "question.requested");
        var question = frames[^1].GetProperty("payload");
        var id = question.GetProperty("id").GetString();
        Assert.Equal(send.GetProperty("runId").GetString(), question.GetProperty("runId").GetString());
        Assert.Equal("pending", question.GetProperty("status").GetString());
        Assert.Single((await peer.RequestAsync("question.list", new { })).GetProperty("questions").EnumerateArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("question.resolve", new { id, answers = new { answers = new { wrong = new[] { "Inspect" } } } }));
        var recovered = await peer.RequestAsync("question.get", new { id });
        Assert.Equal("pending", recovered.GetProperty("question").GetProperty("status").GetString());
        var waiting = await peer.RequestAsync("question.waitAnswer", new { id, timeoutMs = 10 });
        Assert.Equal("pending", waiting.GetProperty("status").GetString());
        var resolution = new { id, resolutionId = "fixture-resolution", answers = new { answers = new { fixture_choice = JsonSerializer.Deserialize<string[]>(answerJson) } } };
        var result = await peer.RequestAsync("question.resolve", resolution);
        Assert.Equal("answered", result.GetProperty("status").GetString());
        var after = await peer.ThroughAsync(e => IsChat(e, "final"));
        Assert.Contains(after, e => e.GetProperty("event").GetString() == "question.resolved" && e.GetProperty("payload").GetProperty("id").GetString() == id);
        Assert.Equal("answered", (await peer.RequestAsync("question.resolve", resolution)).GetProperty("status").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("question.resolve", new { id, cancel = true }));
        var history = await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey });
        Assert.Contains("Your answer was accepted", history.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Interactive_QuestionRecoveryAcrossSocketsAndResolutionBroadcast()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select("question-choice");
        string id;
        await using (var peer = await FixturePeer.ConnectAsync(server, token))
        {
            await peer.RequestAsync("chat.send", SendParams("recover"));
            var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "question.requested");
            id = frames[^1].GetProperty("payload").GetProperty("id").GetString()!;
        }
        await using var first = await FixturePeer.ConnectAsync(server, token);
        await using var second = await FixturePeer.ConnectAsync(server, token);
        Assert.Equal(id, (await first.RequestAsync("question.list", new { })).GetProperty("questions")[0].GetProperty("id").GetString());
        await second.RequestAsync("question.resolve", new { id, cancel = true });
        var framesAfter = await first.ThroughAsync(e => e.GetProperty("event").GetString() == "question.resolved");
        Assert.Equal("cancelled", framesAfter[^1].GetProperty("payload").GetProperty("status").GetString());
        await first.ThroughAsync(e => IsChat(e, "final"));
    }

    [Fact]
    public async Task Interactive_QuestionExpiryAndWaitAnswerUseTerminalContract()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var request = await peer.RequestAsync("question.request", new
        {
            id = "fixture-expiry", sessionKey = GatewayScenario.MainSessionKey, timeoutMs = 80,
            questions = new[] { new { questionId = "name", header = "Name", question = "Synthetic name?", options = Array.Empty<object>() } }
        });
        Assert.Equal("fixture-expiry", request.GetProperty("id").GetString());
        var result = await peer.RequestAsync("question.waitAnswer", new { id = "fixture-expiry", timeoutMs = 1000 });
        Assert.Equal("expired", result.GetProperty("status").GetString());
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "question.resolved");
        Assert.Equal("expired", frames[^1].GetProperty("payload").GetProperty("status").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("question.resolve", new { id = "fixture-expiry", cancel = true }));
    }

    [Theory]
    [InlineData("allow-once")]
    [InlineData("allow-always")]
    [InlineData("deny")]
    public async Task Interactive_ApprovalDecisionsResumeWithoutExecutingCommands(string decision)
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select("approval");
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await peer.RequestAsync("chat.send", SendParams(decision));
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "exec.approval.requested");
        var id = frames[^1].GetProperty("payload").GetProperty("id").GetString();
        await peer.RequestAsync("exec.approval.resolve", new { id, decision });
        await peer.ThroughAsync(e => IsChat(e, "final"));
        var history = await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey });
        Assert.Contains($"Approval decision: {decision}. No command was executed.", history.GetProperty("messages")[1].GetProperty("content").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("exec.approval.resolve", new { id, decision }));
    }

    [Fact]
    public async Task Interactive_DenyOnlyAndAbortInvalidatePendingApproval()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select("approval-deny-only");
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var send = await peer.RequestAsync("chat.send", SendParams("deny-only"));
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "exec.approval.requested");
        var id = frames[^1].GetProperty("payload").GetProperty("id").GetString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("exec.approval.resolve", new { id, decision = "allow-once" }));
        server.Interactive.Playback.Pause();
        await peer.RequestAsync("chat.abort", new { sessionKey = GatewayScenario.EmptySessionKey, runId = send.GetProperty("runId").GetString() });
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("exec.approval.resolve", new { id, decision = "deny" }));
        await peer.ThroughAsync(e => IsChat(e, "aborted"));
    }

    [Fact]
    public async Task Interactive_SessionsRemainIndependentAndMutationsAreCoherent()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var create = await peer.RequestAsync("sessions.create", new { agentId = "research" });
        var key = create.GetProperty("key").GetString()!;
        await peer.RequestAsync("chat.send", SendParams("a"));
        await peer.RequestAsync("chat.send", new { sessionKey = key, message = "Independent B", idempotencyKey = "b" });
        var finals = await peer.ThroughAsync(e => IsChat(e, "final"));
        var firstKey = finals[^1].GetProperty("payload").GetProperty("sessionKey").GetString();
        await peer.ThroughAsync(e => IsChat(e, "final") && e.GetProperty("payload").GetProperty("sessionKey").GetString() != firstKey);
        var history = await peer.RequestAsync("chat.history", new { sessionKey = key });
        Assert.Equal("Independent B", history.GetProperty("messages")[0].GetProperty("content").GetString());
        await peer.RequestAsync("sessions.patch", new { key, label = "Renamed", archived = true });
        var archived = await peer.RequestAsync("sessions.list", new { archived = true });
        Assert.Equal("Renamed", Assert.Single(archived.GetProperty("sessions").EnumerateArray()).GetProperty("label").GetString());
        await peer.RequestAsync("sessions.patch", new { key, archived = false });
        await peer.RequestAsync("sessions.reset", new { key });
        Assert.Empty((await peer.RequestAsync("chat.history", new { sessionKey = key })).GetProperty("messages").EnumerateArray());
        await peer.RequestAsync("sessions.delete", new { key, deleteTranscript = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("chat.history", new { sessionKey = key }));
        Assert.Equal(2, (await peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey })).GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData(GatewayScenario.EmptySessionKey, "main")]
    [InlineData(GatewayScenario.OtherSessionKey, "research")]
    public async Task Interactive_LastCompletedForkExcludesUnfinishedToolTurn(string key, string agentId)
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Pause();
        server.Interactive.Playback.Select("tools");
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var completed = await peer.RequestAsync("chat.history", new { sessionKey = key });
        var accepted = await peer.RequestAsync("chat.send", new { sessionKey = key, message = "Unfinished tool turn", idempotencyKey = "fork" });
        for (var step = 0; step < 4; step++)
        {
            server.Interactive.Playback.Step();
            await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "agent");
        }
        var active = await peer.RequestAsync("chat.history", new { sessionKey = key });
        Assert.Equal(completed.GetProperty("messages").GetArrayLength() + 3, active.GetProperty("messages").GetArrayLength());
        Assert.Equal("toolResult", active.GetProperty("messages").EnumerateArray().Last().GetProperty("role").GetString());

        var fork = await peer.RequestAsync("sessions.create", new { agentId, parentSessionKey = key, fork = true, forkFrom = "last-completed" });
        var forkKey = fork.GetProperty("key").GetString();
        Assert.NotEqual(key, forkKey);
        Assert.NotEqual(completed.GetProperty("sessionId").GetString(), fork.GetProperty("sessionId").GetString());
        var history = await peer.RequestAsync("chat.history", new { sessionKey = forkKey });
        Assert.Equal(completed.GetProperty("messages").GetRawText(), history.GetProperty("messages").GetRawText());

        var fullFork = await peer.RequestAsync("sessions.create", new { agentId, parentSessionKey = key, fork = true });
        var fullHistory = await peer.RequestAsync("chat.history", new { sessionKey = fullFork.GetProperty("key").GetString() });
        Assert.Equal(active.GetProperty("messages").GetRawText(), fullHistory.GetProperty("messages").GetRawText());
        await peer.RequestAsync("chat.abort", new { sessionKey = key, runId = accepted.GetProperty("runId").GetString() });
        await peer.RequestAsync("sessions.reset", new { key });
        Assert.Equal(completed.GetProperty("messages").GetRawText(),
            (await peer.RequestAsync("chat.history", new { sessionKey = forkKey })).GetProperty("messages").GetRawText());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Interactive_DisposedRuntimeStillReturnsInternalError()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await server.Interactive!.DisposeAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            peer.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey }));
        Assert.Contains("Interactive fixture failed", error.Message);
        Assert.Equal("error:INTERNAL", Assert.Single(server.UnexpectedRequests).Outcome);
        Assert.True((await peer.RequestAsync("health", new { })).GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("config.apply", "{}")]
    [InlineData("node.invoke", "{}")]
    [InlineData("chat.send", "{\"sessionKey\":\"agent:main:fixture-empty\",\"message\":\"x\",\"idempotencyKey\":\"x\",\"attachments\":[]}")]
    [InlineData("chat.history", "{\"sessionKey\":\"agent:main:main\",\"limit\":\"bad\"}")]
    [InlineData("question.resolve", "{\"id\":\"fixture-nonexistent\",\"cancel\":true}")]
    [InlineData("sessions.create", "{\"parentSessionKey\":\"agent:main:main\",\"fork\":true,\"forkFrom\":\"agent:main:main\"}")]
    [InlineData("sessions.create", "{\"parentSessionKey\":\"agent:main:main\",\"fork\":false,\"forkFrom\":\"last-completed\"}")]
    [InlineData("sessions.create", "{\"fork\":true,\"forkFrom\":\"last-completed\"}")]
    public async Task Interactive_UnsupportedAndInvalidRequestsAreErrors(string method, string json)
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync(method, document.RootElement));
        Assert.True((await peer.RequestAsync("health", new { })).GetProperty("ok").GetBoolean());
        server.Interactive!.ThrowIfFaulted();
    }

    [Fact]
    public async Task Interactive_DisconnectPersistsCompletedTranscriptForNewConnection()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Select("disconnect");
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await peer.RequestAsync("chat.send", SendParams("disconnect"));
        await peer.Closed.WaitAsync(Deadline);
        await using var replacement = await FixturePeer.ConnectAsync(server, token);
        using var deadline = new CancellationTokenSource(Deadline);
        JsonElement history;
        do
        {
            history = await replacement.RequestAsync("chat.history", new { sessionKey = GatewayScenario.EmptySessionKey });
            if (history.GetProperty("messages").GetArrayLength() == 2) break;
            await Task.Delay(10, deadline.Token);
        } while (true);
        Assert.Contains("FIXTURE INTERACTIVE COMPLETE", history.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Interactive_GeneratedQuestionsAvoidCallerOwnedIds()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await peer.RequestAsync("question.request", new
        {
            id = "fixture-question-2", sessionKey = GatewayScenario.EmptySessionKey,
            questions = new[] { new { questionId = "name", header = "Name", question = "Synthetic name?", options = Array.Empty<object>() } }
        });
        server.Interactive!.Playback.Select("question-text");
        await peer.RequestAsync("chat.send", SendParams("collision"));
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "question.requested"
            && e.GetProperty("payload").GetProperty("id").GetString() != "fixture-question-2");
        var id = frames[^1].GetProperty("payload").GetProperty("id").GetString();
        await peer.RequestAsync("question.resolve", new { id, cancel = true });
        await peer.ThroughAsync(e => IsChat(e, "final"));
        Assert.Equal(2, (await peer.RequestAsync("question.list", new { })).GetProperty("questions").GetArrayLength());
        server.Interactive.ThrowIfFaulted();
    }

    [Fact]
    public async Task Interactive_ConcurrentDisconnectsLeaveServerUsable()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        for (var i = 0; i < 5; i++)
        {
            var peer = await FixturePeer.ConnectAsync(server, token);
            await Task.WhenAll(server.CloseConnectionsAsync(), peer.DisposeAsync().AsTask(), server.CloseConnectionsAsync());
        }
        await using var replacement = await FixturePeer.ConnectAsync(server, token);
        await replacement.RequestAsync("chat.send", SendParams("after-close"));
        await replacement.ThroughAsync(e => IsChat(e, "final"));
        server.Interactive!.ThrowIfFaulted();
    }

    [Fact]
    public async Task Interactive_StepAndShutdownAreBounded()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Pause();
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await peer.RequestAsync("chat.send", SendParams("step"));
        server.Interactive.Playback.Step();
        var frames = await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "agent");
        Assert.Equal("lifecycle", frames[^1].GetProperty("payload").GetProperty("stream").GetString());
        peer.ExpectServerShutdown();
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(0, server.ActiveConnectionCount);
    }

    private static Task<FixtureGatewayServer> StartInteractiveAsync(string token) =>
        FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token, eventDelay: TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Interactive_ActiveSessionReadAcknowledgementsRespectCasAndResetInvalidatesRetry()
    {
        var token = CreateToken();
        await using var server = await StartInteractiveAsync(token);
        server.Interactive!.Playback.Pause();
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        var key = GatewayScenario.EmptySessionKey;
        var accepted = await peer.RequestAsync("chat.send", SendParams("cas"));
        await peer.RequestAsync("sessions.patch", new { key, unread = true });
        var list = await peer.RequestAsync("sessions.list", new { });
        var stamp = list.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("markedUnreadAt").GetInt64();
        await peer.RequestAsync("sessions.patch", new { key, unread = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("sessions.patch", new { key, unread = false, expectedMarkedUnreadAt = stamp }));
        list = await peer.RequestAsync("sessions.list", new { });
        stamp = list.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("markedUnreadAt").GetInt64();
        await peer.RequestAsync("sessions.patch", new { key, unread = false, expectedMarkedUnreadAt = stamp });
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("sessions.reset", new { key }));
        await peer.RequestAsync("chat.abort", new { sessionKey = key, runId = accepted.GetProperty("runId").GetString() });
        await peer.RequestAsync("sessions.reset", new { key });
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RequestAsync("chat.send", SendParams("cas")));
    }

    private static object SendParams(string nonce) => new { sessionKey = GatewayScenario.EmptySessionKey, message = "Synthetic prompt", idempotencyKey = nonce };
    private static bool IsChat(JsonElement frame, string state) =>
        frame.GetProperty("event").GetString() == "chat" && frame.GetProperty("payload").GetProperty("state").GetString() == state;

    private static async Task WaitIdleAsync(OpenClawGatewayClient client, string key)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (true)
        {
            var list = await client.SendWizardRequestAsync("sessions.list");
            if (list.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("hasActiveRun").GetBoolean() == false) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    // Uses actual Gateway WebSocket frames for question events not yet dispatched by
    // the Windows client. Production client tests above remain the chat proof.
    private sealed class FixturePeer : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _lifetime = new(Deadline);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private readonly SemaphoreSlim _send = new(1);
        private Task _reader = Task.CompletedTask;
        private volatile bool _expectServerShutdown;
        public Task Closed => _reader;
        public void ExpectServerShutdown() => _expectServerShutdown = true;

        public static async Task<FixturePeer> ConnectAsync(FixtureGatewayServer server, string token)
        {
            var peer = new FixturePeer();
            await peer._socket.ConnectAsync(server.Endpoint, peer._lifetime.Token);
            peer._reader = peer.ReadAsync();
            var challenge = (await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "connect.challenge"))[^1];
            await peer.RequestAsync("connect", new
            {
                minProtocol = 4, maxProtocol = 4, role = "operator", auth = new { token },
                client = new { id = "fixture-protocol-test" },
                device = new { id = "fixture-device", publicKey = "synthetic", signature = "synthetic", nonce = challenge.GetProperty("payload").GetProperty("nonce").GetString() }
            });
            return peer;
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var id = Guid.NewGuid().ToString();
            var completion = Signal<JsonElement>();
            _pending[id] = completion;
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "req", id, method, @params = parameters });
                await _send.WaitAsync(_lifetime.Token);
                try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _lifetime.Token); }
                finally { _send.Release(); }
                var response = await completion.Task.WaitAsync(_lifetime.Token);
                if (!response.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(response.GetProperty("error").GetProperty("message").GetString());
                return response.GetProperty("payload").Clone();
            }
            finally { _pending.TryRemove(id, out _); }
        }

        public async Task<List<JsonElement>> ThroughAsync(Func<JsonElement, bool> predicate)
        {
            var frames = new List<JsonElement>();
            while (true)
            {
                var frame = await _events.Reader.ReadAsync(_lifetime.Token);
                frames.Add(frame);
                if (predicate(frame)) return frames;
            }
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    using var document = JsonDocument.Parse(message.ToArray());
                    var frame = document.RootElement.Clone();
                    if (frame.GetProperty("type").GetString() == "event") _events.Writer.TryWrite(frame);
                    else if (frame.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetString()!, out var pending)) pending.TrySetResult(frame);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (WebSocketException) when (_lifetime.IsCancellationRequested || _expectServerShutdown) { }
            finally
            {
                _events.Writer.TryComplete();
                foreach (var pending in _pending.Values) pending.TrySetCanceled();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _socket.Abort();
            try { await _reader.WaitAsync(Deadline); }
            finally { _socket.Dispose(); _lifetime.Dispose(); _send.Dispose(); }
        }
    }
}
