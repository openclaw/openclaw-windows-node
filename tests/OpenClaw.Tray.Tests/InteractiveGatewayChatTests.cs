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
    public async Task FinalAfterPersistedReplyInLaterTurnRendersOnceWithoutHistoryReload()
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
        provider.Changed += (_, args) => snapshots.Enqueue(args.Snapshot);
        await provider.LoadAsync();
        const string key = GatewayScenario.EmptySessionKey;

        async Task Turn(int number, string prompt, string reply)
        {
            var runId = $"fixture-chat-{number}";
            var finalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Subscribed after the bridge: this final's provider update completes before the barrier.
            void OnMessage(object? sender, ChatMessageInfo message)
            {
                if (message.RunId == runId && message.IsFinal && string.IsNullOrEmpty(message.OpenClawId))
                    finalReceived.TrySetResult();
            }
            client.ChatMessageReceived += OnMessage;
            try
            {
                await provider.SendMessageAsync(key, prompt);
                await server.PublishChatRunEventAsync(key, runId, "lifecycle", new { phase = "start" });
                await server.PublishSessionToolEventAsync(key, runId, new
                {
                    phase = "start", name = "read", toolCallId = $"{runId}-tool", args = new { path = "fixture.txt" },
                });
                await server.PublishSessionToolEventAsync(key, runId, new
                {
                    phase = "result", name = "read", toolCallId = $"{runId}-tool",
                    result = new { content = "fixture-only" },
                });
                await server.PublishChatContentAsync(key, runId,
                    new object[] { new { type = "text", text = reply } }, isFinal: false);
                await server.PublishPersistedAssistantAsync(key, runId, $"fixture-reply-{number}", number * 2, reply);
                await server.PublishChatRunEventAsync(key, runId, "lifecycle", new { phase = "end" });
                await server.PublishChatFinalAsync(key, runId, reply);
                await finalReceived.Task.WaitAsync(HistoryMergeTimeout);
            }
            finally { client.ChatMessageReceived -= OnMessage; }
        }

        await Turn(1, "First fixture question.", "First fixture reply.");
        await Turn(2, "Second fixture question.", "Second fixture reply.");
        var timeline = Latest(snapshots, key)!;
        Assert.Equal(
            ["First fixture question.", "First fixture reply.", "Second fixture question.", "Second fixture reply."],
            timeline.Entries.Where(entry => entry.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(entry => entry.Text));
        Assert.False(timeline.TurnActive);
        Assert.DoesNotContain(timeline.Entries, entry => entry.IsStreaming);
        Assert.DoesNotContain(server.Requests, request => request.Method == "chat.history");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SessionToolMirrorLinksAndCompletesExistingLiveChildWithoutHistory()
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
        var linkedWhileRunning = new TaskCompletionSource<ChatTimelineItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string key = GatewayScenario.EmptySessionKey;
        const string runId = "fixture-chat-1";
        provider.Changed += (_, args) =>
        {
            snapshots.Enqueue(args.Snapshot);
            if (args.Snapshot.Timelines.GetValueOrDefault(key) is not { } timeline) return;
            var child = timeline.Entries.FirstOrDefault(entry => entry.ToolCallId == "child");
            if (timeline.TurnActive && child is { ToolParentCallId: "parent", ToolResult: ChatToolCallStatus.InProgress })
                linkedWhileRunning.TrySetResult(child);
            if (!timeline.TurnActive && child is not null) finished.TrySetResult();
        };
        await provider.LoadAsync();
        await provider.SendMessageAsync(key, "Synthetic nested tool proof.");
        await server.PublishChatRunEventAsync(key, runId, "lifecycle", new { phase = "start" });
        await server.PublishChatRunEventAsync(key, runId, "item", new
        {
            itemId = "tool:parent", toolCallId = "parent", name = "exec", kind = "tool",
            phase = "start", title = "Wrapper", status = "running",
        });
        await server.PublishChatRunEventAsync(key, runId, "item", new
        {
            itemId = "tool:child", toolCallId = "child", name = "read", kind = "tool",
            phase = "start", title = "Read fixture.txt", status = "running",
        });
        await server.PublishSessionToolEventAsync(key, runId, new
        {
            phase = "start", name = "read", toolCallId = "child", parentToolCallId = "parent",
            args = new { path = "fixture.txt" },
        });
        await server.PublishSessionToolEventAsync(key, runId, new
        {
            phase = "result", name = "read", toolCallId = "child",
            result = new { content = "fixture child result" },
        });
        await server.PublishChatRunEventAsync(key, runId, "item", new
        {
            itemId = "tool:child", toolCallId = "child", name = "read", kind = "tool",
            phase = "end", title = "Read fixture.txt", status = "completed",
        });
        // The ordinary lifecycle event is a same-socket barrier even if the mirror is dropped.
        await server.PublishChatRunEventAsync(key, runId, "lifecycle", new { phase = "end" });
        await finished.Task.WaitAsync(HistoryMergeTimeout);
        Assert.True(linkedWhileRunning.Task.IsCompletedSuccessfully);
        var timeline = Latest(snapshots, key)!;
        var tools = timeline.Entries.Where(entry => entry.Kind == ChatTimelineItemKind.ToolCall).ToArray();
        Assert.Equal(2, tools.Length);
        var child = Assert.Single(tools, entry => entry.ToolCallId == "child");
        Assert.Equal("parent", child.ToolParentCallId);
        Assert.Equal(ChatToolCallStatus.Success, child.ToolResult);
        Assert.Equal("fixture child result", child.ToolOutput);
        Assert.False(timeline.HistoryLoaded);
        Assert.DoesNotContain(server.Requests, request => request.Method == "chat.history");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LegacyTerminalHistory_WaitsForIndependentQueueObservation(bool followUpQueued, bool sameRun)
    {
        const string key = GatewayScenario.EmptySessionKey;
        const string sessionId = "legacy-queue-session";
        const string run = "external-terminal-run";
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        server.OverrideSessionListRead(_ => new { sessions = new[] { new { key, sessionId, status = "running", hasActiveRun = true } } });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyPainted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseConfirmation = new ManualResetEventSlim();
        client.SessionsUpdated += (_, _) => { if (provider.GetSessionId(key) == sessionId) ready.TrySetResult(); };
        provider.Changed += (_, e) =>
        {
            if (e.Snapshot.Timelines.GetValueOrDefault(key) is { TurnActive: true } timeline &&
                timeline.Entries.Any(row => row.Text == "Finished external reply"))
                replyPainted.TrySetResult();
        };
        const long stamp = 1795800000000L;
        var prompt = new { role = "user", content = "External prompt", timestamp = stamp,
            __openclaw = new { id = "legacy-user", seq = 1 } };
        var reply = new { role = "assistant", content = "Finished external reply", timestamp = stamp + 1,
            __openclaw = new { id = "legacy-reply", seq = 2, runId = run } };
        var received = 0;
        server.OverrideChatRead("chat.history", request =>
        {
            Assert.Equal(key, request.GetProperty("sessionKey").GetString());
            var confirmation = Interlocked.Increment(ref received) == 2;
            if (confirmation)
            {
                confirmationStarted.TrySetResult();
                Assert.True(releaseConfirmation.Wait(HistoryMergeTimeout));
            }
            var items = confirmation && followUpQueued
                ? new object[] { new { id = "durable-follow-up", state = "queued" } }
                : Array.Empty<object>();
            return new
            {
                sessionKey = key, sessionId, messages = new object[] { prompt, reply },
                sessionInfo = new { sessionId, lastRunId = confirmation && !sameRun ? "new-run" : run,
                    status = "done", hasActiveRun = false },
                pendingInputs = new { items, total = items.Length, queuedCount = 0 },
            };
        });
        await client.ConnectAsync();
        await ready.Task.WaitAsync(HistoryMergeTimeout);
        await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId,
            status = "running", hasActiveRun = true, message = prompt });
        await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId,
            status = "running", hasActiveRun = true, runId = run, message = reply });
        await replyPainted.Task.WaitAsync(HistoryMergeTimeout);
        Assert.Equal(0, Volatile.Read(ref received));

        var load = provider.LoadHistoryAsync(key, force: true);
        try
        {
            await Task.WhenAny(confirmationStarted.Task, load).WaitAsync(HistoryMergeTimeout);
            Assert.True((await provider.LoadAsync()).Timelines[key].TurnActive,
                "A stale-empty legacy terminal cannot settle activity before an independent read returns.");
            Assert.True(confirmationStarted.Task.IsCompletedSuccessfully, "The legacy terminal must trigger one fresh queue read.");
        }
        finally { releaseConfirmation.Set(); }
        await load.WaitAsync(HistoryMergeTimeout);
        Assert.Equal(2, Volatile.Read(ref received));
        Assert.Equal(followUpQueued || !sameRun, (await provider.LoadAsync()).Timelines[key].TurnActive);
        await client.DisconnectAsync();
    }

    public enum ExternalTranscriptRecovery
    {
        IdleListBeforeSavedReply,
        QuotedReplyInvalidation,
        LegacyPartialQueueDoesNotProveIdle,
        NewerPromptOnlyInHistory,
        QueueMovesBetweenPendingPages,
        QueueWideEmptyObservation,
        TrackedRunWithQueuedInput,
    }

    [Theory]
    [InlineData(ExternalTranscriptRecovery.IdleListBeforeSavedReply)]
    [InlineData(ExternalTranscriptRecovery.QuotedReplyInvalidation)]
    [InlineData(ExternalTranscriptRecovery.LegacyPartialQueueDoesNotProveIdle)]
    [InlineData(ExternalTranscriptRecovery.NewerPromptOnlyInHistory)]
    [InlineData(ExternalTranscriptRecovery.QueueMovesBetweenPendingPages)]
    [InlineData(ExternalTranscriptRecovery.QueueWideEmptyObservation)]
    [InlineData(ExternalTranscriptRecovery.TrackedRunWithQueuedInput)]
    public async Task ExternalTranscript_RecoversFromGatewayEventsAndPendingHistory(ExternalTranscriptRecovery scenario)
    {
        const string key = GatewayScenario.EmptySessionKey;
        const string sessionId = "physical-session";
        var quoted = scenario == ExternalTranscriptRecovery.QuotedReplyInvalidation;
        var newerPrompt = scenario == ExternalTranscriptRecovery.NewerPromptOnlyInHistory;
        var tracked = scenario == ExternalTranscriptRecovery.TrackedRunWithQueuedInput;
        var run = tracked ? "fixture-chat-1" : "external-run";
        bool? hasQueuedInput = scenario switch
        {
            ExternalTranscriptRecovery.QueueMovesBetweenPendingPages or ExternalTranscriptRecovery.TrackedRunWithQueuedInput => true,
            ExternalTranscriptRecovery.QueueWideEmptyObservation => false,
            _ => null,
        };
        var paged = scenario is not (ExternalTranscriptRecovery.IdleListBeforeSavedReply or
            ExternalTranscriptRecovery.QuotedReplyInvalidation or ExternalTranscriptRecovery.NewerPromptOnlyInHistory);
        var pendingTotal = scenario == ExternalTranscriptRecovery.QueueWideEmptyObservation ? 201 : paged ? 21 : 0;
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: tracked), token);
        var idle = 0;
        server.OverrideSessionListRead(_ =>
        {
            var active = Volatile.Read(ref idle) == 0;
            return new { sessions = new[] { new { key, sessionId, status = active ? "running" : "done",
                hasActiveRun = active, lastRunId = run } } };
        });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var runningRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        client.SessionUsageSnapshotUpdated += (_, usage) =>
        {
            if (usage.Sessions.Any(s => s.Key == key && s.HasActiveRun == true)) runningRow.TrySetResult();
            if (usage.Sessions.Any(s => s.Key == key && s.HasActiveRun == false)) idleRow.TrySetResult();
        };
        var promptPainted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyPainted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<ChatTimelineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<ChatDataSnapshot>();
        provider.Changed += (_, e) =>
        {
            snapshots.Enqueue(e.Snapshot);
            if (!e.Snapshot.Timelines.TryGetValue(key, out var timeline)) return;
            if (timeline.TurnActive && timeline.Entries.Any(row => row.Text == "External prompt"))
                promptPainted.TrySetResult();
            if (timeline.Entries.Any(row => row.Text == "Delivered reply"))
            {
                replyPainted.TrySetResult();
                if (!timeline.TurnActive) settled.TrySetResult(timeline);
            }
        };
        await client.ConnectAsync();
        await runningRow.Task.WaitAsync(HistoryMergeTimeout);
        await provider.LoadAsync();
        var timestamp = tracked ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 1795800000000L;
        var prompt = new { role = "user", content = "External prompt", timestamp, __openclaw = new { id = "user-1", seq = 1 } };
        var reply = new { role = "assistant", content = "Delivered reply", timestamp = timestamp + 1,
            replyToId = quoted ? "user-1" : null, __openclaw = new { id = "reply-1", seq = newerPrompt ? 3 : 2, runId = run } };
        object[] historyMessages = newerPrompt
            ? [prompt, new { role = "user", content = "Newer external prompt", timestamp,
                __openclaw = new { id = "user-2", seq = 2 } }, reply]
            : [prompt, reply];
        var cursors = new ConcurrentQueue<long?>();
        server.OverrideChatRead("chat.history", parameters =>
        {
            long? before = parameters.TryGetProperty("pendingBefore", out var cursor) ? cursor.GetInt64() : null;
            cursors.Enqueue(before);
            var total = pendingTotal;
            var oldest = Math.Max(1, total - 19);
            var items = Enumerable.Range(oldest, total - oldest + 1).Select(i => new { id = $"pending-{i}", state = "cancelled" }).ToArray();
            var nextBefore = oldest > 1 ? (long?)oldest : null;
            object pendingInputs = hasQueuedInput is { } observed
                ? new { items, total, nextBefore, queuedCount = 0, hasQueuedInput = observed }
                : new { items, total, nextBefore, queuedCount = 0 };
            return new
            {
                sessionKey = key, sessionId, messages = historyMessages,
                sessionInfo = new { sessionId, lastRunId = run, hasActiveRun = false, status = "done", activeRunIds = Array.Empty<string>() },
                pendingInputs,
            };
        });
        if (tracked)
            await provider.SendMessageAsync(key, "External prompt");
        else
            await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId, status = "running",
                hasActiveRun = true, message = prompt });
        await promptPainted.Task.WaitAsync(HistoryMergeTimeout);
        if (tracked)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.AgentEventReceived += (_, evt) =>
            {
                if (evt.RunId == run && evt.Stream == "lifecycle") started.TrySetResult();
            };
            await server.PublishChatRunEventAsync(key, run, "lifecycle", new { phase = "start" });
            await started.Task.WaitAsync(HistoryMergeTimeout);
            await provider.SendMessageAsync(key, "Next local prompt");
            Assert.Single(server.Requests, request => request.Method == "chat.send");
        }

        if (newerPrompt)
        {
            await provider.LoadHistoryAsync(key, force: true).WaitAsync(HistoryMergeTimeout);
            Assert.True(Latest(snapshots, key)!.TurnActive);
            Assert.Equal(new long?[] { null }, cursors);
        }
        else if (paged)
        {
            await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId, status = "running",
                hasActiveRun = true, runId = run, message = reply });
            await replyPainted.Task.WaitAsync(HistoryMergeTimeout);
            await provider.LoadHistoryAsync(key, force: true).WaitAsync(HistoryMergeTimeout);
            var timeline = Latest(snapshots, key)!;
            if (tracked)
            {
                await settled.Task.WaitAsync(HistoryMergeTimeout);
                using var drainTimeout = new CancellationTokenSource(HistoryMergeTimeout);
                await server.WaitForRequestAsync("chat.send", key, occurrence: 2, cancellationToken: drainTimeout.Token);
                Assert.Equal(new long?[] { null }, cursors);
            }
            else
            {
                Assert.Equal(scenario != ExternalTranscriptRecovery.QueueWideEmptyObservation, timeline.TurnActive);
                Assert.Equal(new long?[] { null }, cursors);
            }
        }
        else
        {
            Volatile.Write(ref idle, 1);
            if (quoted)
            {
                await server.PublishTranscriptEventAsync("sessions.changed", new { sessionKey = key, sessionId, phase = "message",
                    status = "done", hasActiveRun = false, lastRunId = run });
            }
            else
            {
                await server.NotifySessionsChangedAsync();
                await idleRow.Task.WaitAsync(HistoryMergeTimeout);
                Assert.Empty(cursors);
                await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId, status = "done",
                    hasActiveRun = false, lastRunId = run, runId = run, message = reply });
            }
            await settled.Task.WaitAsync(HistoryMergeTimeout);
            Assert.Equal(new long?[] { null, null }, cursors);
        }
        string[] transcript = newerPrompt ? ["External prompt", "Newer external prompt", "Delivered reply"]
            : ["External prompt", "Delivered reply"];
        var observedTranscript = tracked ? await settled.Task : Latest(snapshots, key)!;
        Assert.Equal(transcript, observedTranscript.Entries.Select(row => row.Text));
        if (!tracked)
            Assert.DoesNotContain(server.Requests, request => request.Method == "chat.send");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalHistoryRefresh_PreservesLoadedPrefixBeforeNewestWindow(bool authoritative)
    {
        const string key = GatewayScenario.EmptySessionKey;
        const string sessionId = "window-session";
        const string run = "external-window-run";
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        server.OverrideSessionListRead(_ => new { sessions = new[] { new { key, sessionId, status = "running", hasActiveRun = true } } });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyPainted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<ChatDataSnapshot>();
        client.SessionsUpdated += (_, _) => { if (provider.GetSessionId(key) == sessionId) ready.TrySetResult(); };
        provider.Changed += (_, e) =>
        {
            snapshots.Enqueue(e.Snapshot);
            if (e.Snapshot.Timelines.TryGetValue(key, out var timeline) && timeline.TurnActive &&
                timeline.Entries.Any(row => row.Text == "Transcript row 202")) replyPainted.TrySetResult();
        };
        static string Text(int seq) => seq is 1 or 3 ? "Repeated prompt" : $"Transcript row {seq}";
        static object Message(int seq) => new
        {
            role = seq % 2 == 1 ? "user" : "assistant", content = seq == 2 ? (object)new object[]
            {
                new { type = "text", text = Text(seq) },
                new { type = "toolCall", id = "prefix-call", name = "read", arguments = new { path = "fixture.txt" } },
                new { type = "text", text = "End of multipart reply" },
            } : Text(seq),
            timestamp = 1795800000000L + seq,
            __openclaw = new { id = $"message-{seq}", seq, runId = seq == 202 ? run : null },
        };
        server.OverrideChatRead("chat.history", _ => new
        {
            sessionKey = key, sessionId, messages = Enumerable.Range(1, 200).Select(Message).ToArray(),
        });
        await client.ConnectAsync();
        await ready.Task.WaitAsync(HistoryMergeTimeout);
        await provider.LoadAsync();
        await provider.LoadHistoryAsync(key).WaitAsync(HistoryMergeTimeout);
        foreach (var seq in new[] { 201, 202 })
            await server.PublishTranscriptEventAsync("session.message", new { sessionKey = key, sessionId,
                runId = seq == 202 ? run : null, status = "running", hasActiveRun = true, message = Message(seq) });
        await replyPainted.Task.WaitAsync(HistoryMergeTimeout);
        var original = Latest(snapshots, key)!.Entries;
        var prefix = original.TakeWhile(row => row.Text != Text(4)).SkipLast(1).ToArray();
        Assert.Contains(prefix, row => row.ToolCallId == "prefix-call");
        Assert.Equal("End of multipart reply", prefix.Last().Text);

        server.OverrideChatRead("chat.history", _ => new
        {
            sessionKey = key, sessionId, messages = Enumerable.Range(3, 200).Select(Message).ToArray(),
            hasMore = true, olderCursor = "fixture-older-window",
            sessionInfo = new { sessionId, lastRunId = run, status = "done", hasActiveRun = false },
            pendingInputs = new { total = 0, items = Array.Empty<object>() },
        });
        await provider.LoadHistoryAsync(key, force: true, authoritative: authoritative).WaitAsync(HistoryMergeTimeout);

        var refreshed = Latest(snapshots, key)!;
        Assert.Equal(prefix.Select(row => row.Text).Concat(Enumerable.Range(3, 200).Select(Text)),
            refreshed.Entries.Select(row => row.Text));
        Assert.Equal(Assert.Single(original, row => row.Text == Text(100)).Id,
            Assert.Single(refreshed.Entries, row => row.Text == Text(100)).Id);
        Assert.False(refreshed.TurnActive);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalSessionAcknowledgment_PreservesNewTranscriptPromptAndClockBeforeRoster(bool replyBeforeRoster)
    {
        const string key = GatewayScenario.EmptySessionKey;
        const string beforeId = "physical-before";
        const string afterId = "physical-after";
        const string prompt = "New external prompt";
        const string replyText = "New quoted reply";
        const string runId = "replacement-run";
        var announcedId = beforeId;
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        server.OverrideSessionListRead(_ => new { sessions = new[] { new { key, sessionId = Volatile.Read(ref announcedId) } } });
        server.OverrideChatRead("chat.history", _ => new
        {
            sessionKey = key, sessionId = beforeId, messages = new[]
            {
                new { role = "user", content = "Old prompt", __openclaw = new { id = "before-prompt", seq = 1 } },
                new { role = "assistant", content = "Old reply", __openclaw = new { id = "before-reply", seq = 2 } },
            },
        });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var before = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRoster = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectLateRoster = 0;
        var newTurn = new TaskCompletionSource<ChatTimelineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<ChatTimelineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, _) =>
        {
            if (provider.GetSessionId(key) == beforeId)
            {
                before.TrySetResult();
                if (Volatile.Read(ref expectLateRoster) == 1) lateRoster.TrySetResult();
            }
            if (provider.GetSessionId(key) == afterId) after.TrySetResult();
        };
        provider.Changed += (_, update) =>
        {
            if (update.Snapshot.Timelines.GetValueOrDefault(key) is not { } timeline) return;
            if (timeline.TurnActive && timeline.Entries.Any(row => row.Text == prompt)) newTurn.TrySetResult(timeline);
            if (!timeline.TurnActive && timeline.Entries.Any(row => row.Text == replyText)) completed.TrySetResult(timeline);
        };
        await client.ConnectAsync();
        await before.Task.WaitAsync(HistoryMergeTimeout);
        await provider.LoadHistoryAsync(key).WaitAsync(HistoryMergeTimeout);
        Assert.Contains((await provider.LoadAsync()).Timelines[key].Entries, row => row.Text == "Old reply");
        var savedPrompt = new { role = "user", content = prompt, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            __openclaw = new { id = "replacement-prompt", seq = 1 } };
        await server.PublishTranscriptEventAsync("session.message", new
        {
            sessionKey = key, sessionId = afterId, status = "running", hasActiveRun = true, message = savedPrompt,
        });
        var started = await newTurn.Task.WaitAsync(HistoryMergeTimeout);
        Assert.NotNull(started.TurnStartedAt);
        await provider.LoadHistoryAsync(key, force: true, authoritative: true).WaitAsync(HistoryMergeTimeout);
        Assert.Equal(started.TurnStartedAt, (await provider.LoadAsync()).Timelines[key].TurnStartedAt);

        object[] savedMessages = replyBeforeRoster ? [savedPrompt, new { role = "assistant", content = replyText,
            replyToId = "replacement-prompt", __openclaw = new { id = "replacement-reply", seq = 2, runId } }] : [savedPrompt];
        server.OverrideChatRead("chat.history", _ => new
        {
            sessionKey = key, sessionId = afterId, messages = savedMessages,
            sessionInfo = new { sessionId = afterId, lastRunId = runId,
                status = replyBeforeRoster ? "done" : "running", hasActiveRun = !replyBeforeRoster },
            pendingInputs = new { total = 0, items = Array.Empty<object>() },
        });
        if (replyBeforeRoster)
        {
            await server.PublishTranscriptEventAsync("sessions.changed", new
            {
                sessionKey = key, sessionId = afterId, phase = "message", status = "done", hasActiveRun = false, lastRunId = runId,
            });
            await provider.LoadHistoryAsync(key, force: true).WaitAsync(HistoryMergeTimeout);
            Volatile.Write(ref expectLateRoster, 1);
            await server.NotifySessionsChangedAsync();
            await lateRoster.Task.WaitAsync(HistoryMergeTimeout);
            Assert.True((await provider.LoadAsync()).Timelines[key].TurnActive);
        }

        Volatile.Write(ref announcedId, afterId);
        await server.NotifySessionsChangedAsync();
        await after.Task.WaitAsync(HistoryMergeTimeout);
        if (replyBeforeRoster)
        {
            var settled = await completed.Task.WaitAsync(HistoryMergeTimeout);
            Assert.Equal(new[] { prompt, replyText }, settled.Entries.Select(row => row.Text));
        }
        else
        {
            var current = (await provider.LoadAsync()).Timelines[key];
            Assert.Equal(prompt, Assert.Single(current.Entries).Text);
            Assert.True(current.TurnActive);
            Assert.Equal(started.TurnStartedAt, current.TurnStartedAt);
            Assert.Equal(started.TurnGeneration, current.TurnGeneration);
        }
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IdleHistoryResponse_CannotSurfaceAfterProviderAcknowledgesNewPhysicalSession(
        bool userBackfill, bool holdDelivery)
    {
        const string key = GatewayScenario.EmptySessionKey;
        var sessionId = "old-session";
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        server.OverrideSessionListRead(_ => new { sessions = new[] { new { key, sessionId = Volatile.Read(ref sessionId),
            status = "done", hasActiveRun = false } } });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var oldAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newHistoryLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDelivery = new ManualResetEventSlim();
        if (holdDelivery)
        {
            provider.BeforePublishForTests = snapshot =>
            {
                if (snapshot.Timelines.TryGetValue(key, out var timeline) &&
                    timeline.Entries.Any(row => row.Text == "Old prompt"))
                {
                    deliveryReady.TrySetResult();
                    releaseDelivery.Wait(HistoryMergeTimeout);
                }
            };
        }
        var snapshotsAfterRotation = new ConcurrentQueue<ChatDataSnapshot>();
        client.SessionsUpdated += (_, _) =>
        {
            if (provider.GetSessionId(key) == "old-session") oldAcknowledged.TrySetResult();
            if (provider.GetSessionId(key) == "new-session") newAcknowledged.TrySetResult();
        };
        provider.Changed += (_, e) =>
        {
            if (newAcknowledged.Task.IsCompletedSuccessfully) snapshotsAfterRotation.Enqueue(e.Snapshot);
            if (e.Snapshot.Timelines.TryGetValue(key, out var timeline) &&
                timeline.Entries.Any(row => row.Text == "New reply"))
                newHistoryLoaded.TrySetResult();
        };
        server.OverrideChatRead("chat.history", _ =>
        {
            captured.TrySetResult();
            return new { sessionKey = key, sessionId = "old-session", messages = new[] {
                new { role = "user", content = "Old prompt" }, new { role = "assistant", content = "Old reply" },
            } };
        });
        await client.ConnectAsync();
        await oldAcknowledged.Task.WaitAsync(HistoryMergeTimeout);
        var before = await provider.LoadAsync();
        Assert.False(before.Timelines[key].TurnActive);
        if (!holdDelivery)
            server.HoldHistory(key);
        var oldRead = userBackfill ? provider.FetchRemoteUserMessageForTestsAsync(key, openResetGateOnSuccess: false)
            : provider.LoadHistoryAsync(key);
        try
        {
            await (holdDelivery ? deliveryReady.Task : captured.Task).WaitAsync(HistoryMergeTimeout);
            Volatile.Write(ref sessionId, "new-session");
            await server.NotifySessionsChangedAsync();
            await newAcknowledged.Task.WaitAsync(HistoryMergeTimeout);
            if (!userBackfill)
            {
                server.OverrideChatRead("chat.history", _ => new { sessionKey = key, sessionId = "new-session", messages = new[] {
                    new { role = "user", content = "New prompt" }, new { role = "assistant", content = "New reply" },
                } });
                await provider.LoadHistoryAsync(key).WaitAsync(HistoryMergeTimeout);
            }
        }
        finally
        {
            releaseDelivery.Set();
            if (!holdDelivery)
                server.ReleaseHistory(key);
        }
        await oldRead.WaitAsync(HistoryMergeTimeout);
        if (!userBackfill)
            await newHistoryLoaded.Task.WaitAsync(HistoryMergeTimeout);

        Assert.Equal("new-session", provider.GetSessionId(key));
        Assert.All(snapshotsAfterRotation, snapshot =>
            Assert.DoesNotContain(snapshot.Timelines[key].Entries, row => row.Text is "Old prompt" or "Old reply"));
        Assert.Equal(userBackfill ? [] : new[] { "New prompt", "New reply" },
            (await provider.LoadAsync()).Timelines[key].Entries.Select(row => row.Text));
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task PhysicalSessionAcknowledgment_RetainsUnresolvedOptimisticSend()
    {
        const string key = GatewayScenario.EmptySessionKey;
        const string prompt = "Pending local prompt";
        var sessionId = "old-session";
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: true), token);
        server.OverrideSessionListRead(_ => new { sessions = new[] { new { key, sessionId = Volatile.Read(ref sessionId),
            status = "done", hasActiveRun = false } } });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        var oldAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, _) =>
        {
            if (provider.GetSessionId(key) == "old-session") oldAcknowledged.TrySetResult();
            if (provider.GetSessionId(key) == "new-session") newAcknowledged.TrySetResult();
        };
        await client.ConnectAsync();
        await oldAcknowledged.Task.WaitAsync(HistoryMergeTimeout);
        await provider.LoadAsync();
        server.HoldChatSends();
        using var timeout = new CancellationTokenSource(HistoryMergeTimeout);
        var send = provider.SendMessageAsync(key, prompt, timeout.Token);
        try
        {
            await server.WaitForRequestAsync("chat.send", key, cancellationToken: timeout.Token);
            Volatile.Write(ref sessionId, "new-session");
            await server.NotifySessionsChangedAsync();
            await newAcknowledged.Task.WaitAsync(timeout.Token);

            var timeline = (await provider.LoadAsync()).Timelines[key];
            Assert.Equal(prompt, Assert.Single(timeline.Entries).Text);
            Assert.True(timeline.TurnActive);
        }
        finally { server.ReleaseChatSends(); }
        await send.WaitAsync(HistoryMergeTimeout);
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
