using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests;

[Collection("Chat telemetry")]
public sealed class InteractiveGatewayChatTests
{
    private static readonly TimeSpan HistoryMergeTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_GATEWAY_STEP_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HistoryReadMergesRetainedItemStreamWithoutLosingRunningState()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: true), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(HistoryMergeTimeout);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var snapshots = new ConcurrentQueue<ChatDataSnapshot>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string key = GatewayScenario.EmptySessionKey;
        const string runId = "fixture-chat-1";
        const string callId = "fixture-history-call";
        const string prompt = "Synthetic history merge proof.";
        provider.Changed += (_, args) =>
        {
            snapshots.Enqueue(args.Snapshot);
            var tool = args.Snapshot.Timelines.GetValueOrDefault(key)?.Entries
                .FirstOrDefault(entry => entry.ToolCallId == callId);
            if (tool?.ToolResult == ChatToolCallStatus.InProgress) started.TrySetResult();
            if (tool?.ToolResult == ChatToolCallStatus.Success) completed.TrySetResult();
        };
        await provider.LoadAsync();
        await provider.SendMessageAsync(key, prompt);
        await server.PublishChatRunEventAsync(key, runId, "lifecycle", new { phase = "start" });
        await server.PublishChatRunEventAsync(key, runId, "item", new
        {
            itemId = $"tool:{callId}", toolCallId = callId, name = "exec", kind = "tool",
            phase = "start", title = "Exec echo fixture-only", status = "running",
            meta = "echo fixture-only", commandBearing = true,
        });
        await started.Task.WaitAsync(HistoryMergeTimeout);
        var live = Assert.Single(Latest(snapshots, key)!.Entries, entry => entry.Kind == ChatTimelineItemKind.ToolCall);
        Assert.False(Latest(snapshots, key)!.HistoryLoaded);
        Assert.Equal("Exec echo fixture-only", live.Text);
        Assert.Null(live.ToolArgs);
        Assert.DoesNotContain(server.Requests, request => request.Method == "chat.history");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var messages = new List<object>
        {
            new { role = "user", content = prompt, timestamp, __openclaw = new { id = "fixture-history-user", seq = 1 } },
            new
            {
                role = "assistant", timestamp,
                __openclaw = new { id = "fixture-history-call-message", seq = 2, runId },
                content = new[] { new { type = "toolCall", id = callId, name = "exec", arguments = new { command = "echo fixture-only" } } },
            },
        };
        var sessionId = client.GetSessionList().Single(session => session.Key == key).SessionId;
        server.OverrideChatRead("chat.history", _ => new { sessionKey = key, sessionId, messages });
        // The same provider retains the live row across both initial and forced wire reads.
        for (var read = 0; read < 2; read++)
        {
            await provider.LoadHistoryAsync(key, force: read > 0);
            var merged = Latest(snapshots, key)!;
            Assert.True(merged.HistoryLoaded);
            var running = Assert.Single(merged.Entries, entry => entry.Kind == ChatTimelineItemKind.ToolCall);
            Assert.Equal(callId, running.ToolCallId);
            Assert.Equal(runId, running.ToolRunId);
            Assert.Equal(ChatToolCallStatus.InProgress, running.ToolResult);
            Assert.Equal(read + 1, server.Requests.Count(request => request.Method == "chat.history"));
        }

        await server.PublishChatRunEventAsync(key, runId, "item", new
        {
            itemId = $"tool:{callId}", toolCallId = callId, name = "exec", kind = "tool",
            phase = "end", title = "Exec echo fixture-only", status = "completed",
        });
        await completed.Task.WaitAsync(HistoryMergeTimeout);
        messages.Add(new
        {
            role = "toolResult", toolCallId = callId, toolName = "exec", content = "fixture-only", timestamp,
            __openclaw = new { id = "fixture-history-result", seq = 3, runId },
        });
        await provider.LoadHistoryAsync(key, force: true);
        var saved = Assert.Single(Latest(snapshots, key)!.Entries, entry => entry.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Equal(callId, saved.ToolCallId);
        Assert.Equal(ChatToolCallStatus.Success, saved.ToolResult);
        Assert.Equal("fixture-only", saved.ToolOutput);
        Assert.Equal(3, server.Requests.Count(request => request.Method == "chat.history"));
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SidebarForkPreservesCompletedHistory(bool active)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        server.Interactive!.Playback.Pause();
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = GatewayScenario.OtherSessionKey;
        client.SessionsUpdated += (_, sessions) =>
        {
            if (sessions.Length == 5) ready.TrySetResult();
            if (sessions.FirstOrDefault(s => s.Key == key && s.HasActiveRun == true) is { } session)
                running.TrySetResult(session);
        };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var completed = await client.RequestChatHistoryAsync(key);
        var parent = client.GetSessionList().Single(s => s.Key == key);
        if (active)
        {
            await client.SendChatMessageForRunAsync("Unfinished sidebar turn", key);
            server.Interactive.Playback.Step();
            parent = await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var request = WorkspaceSessionMenu.ForkRequest(parent);
        Assert.Equal(active ? "last-completed" : null, request.ForkFrom);
        var fork = await client.CreateSessionAsync(request);
        Assert.True(fork.Ok, fork.Error);
        Assert.StartsWith("agent:research:", fork.Key);
        Assert.NotEqual(key, fork.Key);
        Assert.NotEqual(completed.SessionId, fork.SessionId);
        var history = await client.RequestChatHistoryAsync(fork.Key!);
        Assert.Equal(completed.Messages.Select(m => m.Text), history.Messages.Select(m => m.Text));
        Assert.DoesNotContain(history.Messages, m => m.Text.Contains("Unfinished sidebar turn"));
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("streaming")]
    [InlineData("tools")]
    [InlineData("tool-error")]
    [InlineData("approval")]
    [InlineData("queued")]
    [InlineData("chat-deltas")]
    [InlineData("question-choice")]
    [InlineData("question-text")]
    [InlineData("question-multi")]
    [InlineData("question-cancel")]
    public async Task FixtureDrivesRealProviderThroughRunAndHistory(string scenario)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token,
            eventDelay: TimeSpan.FromMilliseconds(30));
        server.Interactive!.Playback.Select(scenario switch
        {
            "queued" => "slow",
            "question-cancel" => "question-choice",
            _ => scenario,
        });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var snapshots = new ConcurrentQueue<ChatDataSnapshot>();
        provider.Changed += (_, args) => snapshots.Enqueue(args.Snapshot);
        await provider.LoadAsync();
        var key = GatewayScenario.EmptySessionKey;
        await provider.SendMessageAsync(key, "Synthetic provider proof.");
        if (scenario == "queued")
            await provider.SendMessageAsync(key, "Synthetic queued turn.");
        if (scenario == "approval")
        {
            await WaitAsync(() => Latest(snapshots, key)?.PendingPermission is not null);
            var permission = Latest(snapshots, key)!.PendingPermission!;
            await provider.RespondToPermissionAsync(key, permission.RequestId, "allow-once");
        }
        if (scenario.StartsWith("question-", StringComparison.Ordinal))
        {
            var questions = Assert.IsType<ChatQuestionCoordinator>(provider.Questions);
            await WaitAsync(() => questions.Snapshot().Prompts.Any(p => p.Request.Status == "pending"));
            var prompt = Assert.Single(questions.Snapshot().Prompts);
            Assert.True(ChatQuestionCoordinator.Matches(prompt.Request, key, "main"));
            var answers = scenario == "question-cancel" ? null : new Dictionary<string, string[]>
            {
                ["fixture_choice"] = scenario switch
                {
                    "question-text" => ["Native question proof"],
                    "question-multi" => ["Inspect", "Summarize"],
                    _ => ["Inspect"],
                },
            };
            await questions.SubmitAsync(key, "main", prompt.Request.Id, answers);
            await WaitAsync(() => questions.Snapshot().Prompts.Single().Request.Status
                == (answers is null ? "cancelled" : "answered"));
            Assert.Null(questions.Snapshot().Error);
            Assert.Null(questions.Snapshot().Prompts.Single().Error);
            Assert.Contains(server.Requests, request => request.Method == "question.resolve");
        }
        var turns = scenario == "queued" ? 2 : 1;
        await WaitAsync(() => Latest(snapshots, key)?.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text.Contains("FIXTURE INTERACTIVE COMPLETE") && !e.IsStreaming) == turns);
        var timeline = Latest(snapshots, key)!;
        Assert.Equal(turns, timeline.Entries.Count(e => e.Kind == ChatTimelineItemKind.User));
        Assert.Equal(turns, timeline.Entries.Count(e => e.Kind == ChatTimelineItemKind.Assistant && e.Text.Contains("FIXTURE INTERACTIVE COMPLETE")));
        if (scenario is "tools" or "tool-error")
            Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.ToolCall);
        if (scenario == "approval") Assert.Null(timeline.PendingPermission);
        var history = await client.RequestChatHistoryAsync(key);
        Assert.Contains(history.Messages, m => m.Text.Contains("FIXTURE INTERACTIVE COMPLETE"));
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveReplyRendersOneBubbleWithOrWithoutAgentAssistantText(bool chatOnlyAssistantText)
    {
        // The Tray's approval-capable profile advertises chat-only-assistant-text, so the fixture
        // omits agent assistant text; the other profile receives both streams like an older Gateway.
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token,
            eventDelay: TimeSpan.FromMilliseconds(30));
        server.Interactive!.Playback.Select("streaming");
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false)
        {
            SupportsExecApprovals = chatOnlyAssistantText,
        };
        var agentAssistantText = 0;
        client.AgentEventReceived += (_, evt) => { if (evt.Stream == "assistant") Interlocked.Increment(ref agentAssistantText); };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var snapshots = new ConcurrentQueue<ChatDataSnapshot>();
        provider.Changed += (_, args) => snapshots.Enqueue(args.Snapshot);
        await provider.LoadAsync();
        var key = GatewayScenario.EmptySessionKey;
        await provider.SendMessageAsync(key, "Synthetic provider proof.");

        await WaitAsync(() => Latest(snapshots, key)?.Entries.Any(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text.Contains("FIXTURE INTERACTIVE COMPLETE") && !e.IsStreaming) == true);
        Assert.Equal(chatOnlyAssistantText, agentAssistantText == 0);
        Assert.All(snapshots, snapshot => Assert.True(
            snapshot.Timelines.GetValueOrDefault(key)?.Entries.Count(e => e.Kind == ChatTimelineItemKind.Assistant) is null or <= 1));
        var reply = Assert.Single(Latest(snapshots, key)!.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.StartsWith("This is a **synthetic streaming answer**.", reply.Text);
        await client.DisconnectAsync();
    }

    private static ChatTimelineState? Latest(ConcurrentQueue<ChatDataSnapshot> snapshots, string key) =>
        snapshots.LastOrDefault()?.Timelines.GetValueOrDefault(key);

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}
