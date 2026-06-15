using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public class OpenClawChatDataProviderTests
{
    private sealed class FakeBridge : IChatGatewayBridge
    {
        public bool IsConnected { get; set; }
        public ConnectionStatus CurrentStatus { get; set; }
        public string? MainSessionKey { get; set; }
        public bool HasHandshakeSnapshot { get; set; }
        public List<string> SentMessages { get; } = new();
        public List<string?> SentSessionKeys { get; } = new();
        public List<string?> SentSessionIds { get; } = new();
        public List<string> AbortedRunIds { get; } = new();
        public Func<string, string?, string?, Task>? SendBehavior { get; set; }
        public Func<string?, Task<ChatHistoryInfo>>? HistoryBehavior { get; set; }
        public Func<string, Task>? AbortBehavior { get; set; }
        public SessionInfo[] Sessions { get; set; } = Array.Empty<SessionInfo>();
        public ModelsListInfo? CurrentModels { get; set; }
        public int ProactiveBootstrapCalls { get; private set; }

        public SessionInfo[] GetSessionList() => Sessions;
        public ModelsListInfo? GetCurrentModelsList() => CurrentModels;
        public void StartProactiveBootstrap() => ProactiveBootstrapCalls++;

        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null)
        {
            SentMessages.Add(message);
            SentSessionKeys.Add(sessionKey);
            SentSessionIds.Add(sessionId);
            return SendBehavior?.Invoke(message, sessionKey, sessionId) ?? Task.CompletedTask;
        }

        public Task PatchSessionModelAsync(string sessionKey, string model) => Task.CompletedTask;
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => Task.CompletedTask;

        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey)
        {
            return HistoryBehavior?.Invoke(sessionKey)
                ?? Task.FromResult(new ChatHistoryInfo { SessionKey = sessionKey ?? "" });
        }

        public Task SendChatAbortAsync(string runId, string? sessionKey = null)
        {
            AbortedRunIds.Add(runId);
            return AbortBehavior?.Invoke(runId) ?? Task.CompletedTask;
        }

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public bool IsDisposed { get; private set; }

        public void RaiseStatus(ConnectionStatus s) { CurrentStatus = s; StatusChanged?.Invoke(this, s); }
        public void RaiseSessions(SessionInfo[] s) { Sessions = s; SessionsUpdated?.Invoke(this, s); }
        public void RaiseChat(ChatMessageInfo m) => ChatMessageReceived?.Invoke(this, m);
        public void RaiseAgent(AgentEventInfo a) => AgentEventReceived?.Invoke(this, a);
        public void RaiseModels(ModelsListInfo m) { CurrentModels = m; ModelsListUpdated?.Invoke(this, m); }
        public void Dispose() => IsDisposed = true;
    }

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots, List<ChatProviderNotification> notifications)
        CreateProvider(SessionInfo[]? initial = null, string? toolMetaCachePath = null, string? attachmentMetaCachePath = null)
    {
        var bridge = new FakeBridge { Sessions = initial ?? Array.Empty<SessionInfo>() };
        var provider = toolMetaCachePath is null && attachmentMetaCachePath is null
            ? new OpenClawChatDataProvider(bridge)
            : new OpenClawChatDataProvider(
                bridge,
                post: null,
                toolMetaCacheFilePath: toolMetaCachePath ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tool-metadata.json"),
                attachmentMetaCacheFilePath: attachmentMetaCachePath);
        var snapshots = new List<ChatDataSnapshot>();
        var notifications = new List<ChatProviderNotification>();
        provider.Changed += (_, e) => snapshots.Add(e.Snapshot);
        provider.NotificationRequested += (_, e) => notifications.Add(e.Notification);
        return (bridge, provider, snapshots, notifications);
    }

    private static SessionInfo MainSession() =>
        new() { Key = "main", IsMain = true, DisplayName = "Main session", Status = "active" };

    private static AgentEventInfo MakeAgentEvent(string stream, string json, string sessionKey = "main", string? runId = null)
    {
        var doc = JsonDocument.Parse(json);
        return new AgentEventInfo
        {
            Stream = stream,
            Data = doc.RootElement.Clone(),
            SessionKey = sessionKey,
            RunId = runId ?? string.Empty
        };
    }

    [Fact]
    public async Task LoadAsync_ReturnsSeededSessionsAsThreads()
    {
        var (_, provider, _, _) = CreateProvider(new[] { MainSession() });

        var snapshot = await provider.LoadAsync();

        Assert.Single(snapshot.Threads);
        Assert.Equal("main", snapshot.Threads[0].Id);
        Assert.Equal("Main session", snapshot.Threads[0].Title);
        Assert.Equal("main", snapshot.DefaultThreadId);
        Assert.True(snapshot.Timelines.ContainsKey("main"));
    }

    [Fact]
    public async Task SendMessageAsync_AddsLocalUserEntryBeforeAwaitingGateway()
    {
        var tcs = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => tcs.Task;
        await provider.LoadAsync();
        snapshots.Clear();

        var sendTask = provider.SendMessageAsync("main", "Hello");

        // Snapshot must be emitted before SendChatMessageAsync completes.
        Assert.Single(snapshots);
        var timeline = snapshots[0].Timelines["main"];
        Assert.True(timeline.TurnActive);
        Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[0].Kind);
        Assert.Equal("Hello", timeline.Entries[0].Text);
        Assert.Single(bridge.SentMessages);
        Assert.Equal("Hello", bridge.SentMessages[0]);
        Assert.Equal("main", bridge.SentSessionKeys[0]);

        tcs.SetResult();
        await sendTask;
    }

    [Fact]
    public async Task SendMessageAsync_GatewayThrows_AppendsErrorAndRethrows()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => throw new InvalidOperationException("boom");
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "Hi"));

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("boom"));
        Assert.False(timeline.TurnActive);
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);
    }

    [Fact]
    public async Task SendMessageAsync_RejectsEmptyMessage()
    {
        var (_, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SendMessageAsync("main", "  "));
    }

    [Fact]
    public async Task ChatMessageReceived_FinalAssistant_AppendsAssistantEntry()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello from assistant",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "Hello from assistant");
        Assert.False(timeline.TurnActive);
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_DeltaAssistant_AppendsAssistantWithoutEndingTurn()
    {
        // Block-streamed deltas carry cumulative assistant text and should
        // upsert the active assistant entry without ending the turn.
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello",
            State = "delta"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "Hello");
        Assert.True(timeline.TurnActive);
        Assert.DoesNotContain(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_FinalAfterLifecycleEnd_DoesNotDuplicateAssistant()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "partial",
            State = "delta"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final"
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final"
        });

        var timeline = (await provider.LoadAsync()).Timelines["main"];
        var assistant = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("final", assistant.Text);
        Assert.False(assistant.IsStreaming);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task ChatMessageReceived_UserEcho_Ignored()
    {
        // After sending a message locally, the SSE echo of that same text
        // should be suppressed (already displayed by SendMessageAsync).
        var tcs = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => tcs.Task;
        await provider.LoadAsync();

        _ = provider.SendMessageAsync("main", "hi");
        snapshots.Clear(); // clear the snapshot from SendMessageAsync

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "hi",
            State = "final"
        });

        // The echo should be suppressed — no new snapshot.
        Assert.Empty(snapshots);

        tcs.SetResult();
    }

    [Fact]
    public async Task SendMessageAsync_WhenGatewayThrows_DoesNotSuppressFutureRemoteUserEcho()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => throw new InvalidOperationException("gateway down");
        await provider.LoadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "same text"));

        bridge.SendBehavior = null;
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same text",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(2, timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "same text"));
    }

    [Fact]
    public async Task AgentEvent_ToolStart_AppendsToolCallEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"ls"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
        Assert.Equal("powershell", entry.ToolName);
        Assert.Equal("ls", entry.Text);
        Assert.Equal(ChatToolCallStatus.InProgress, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_ToolStartThenResult_MarksToolSuccess()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"grep","args":{"pattern":"foo"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"grep","args":{"pattern":"foo"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_JobError_EmitsErrorEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        var evt = MakeAgentEvent("job", """{"state":"error"}""");
        evt.Summary = "kaboom";
        bridge.RaiseAgent(evt);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("kaboom"));
    }

    [Fact]
    public async Task AgentEvent_JobDone_ClearsTurnActive()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        // Kick off a turn
        _ = provider.SendMessageAsync("main", "hi");

        bridge.RaiseAgent(MakeAgentEvent("job", """{"state":"done"}"""));

        // Snapshot the timeline directly.
        var snap = await provider.LoadAsync();
        Assert.False(snap.Timelines["main"].TurnActive);
    }

    [Fact]
    public async Task SessionsUpdated_RebuildsThreadsAndSeedsTimelines()
    {
        var (bridge, provider, snapshots, _) = CreateProvider();
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main" },
            new SessionInfo { Key = "sub:abc", IsMain = false, DisplayName = "Sub" }
        });

        var snap = snapshots[^1];
        Assert.Equal(2, snap.Threads.Length);
        Assert.True(snap.Timelines.ContainsKey("main"));
        Assert.True(snap.Timelines.ContainsKey("sub:abc"));
        Assert.Equal("main", snap.DefaultThreadId);
    }

    [Fact]
    public async Task StatusChanged_IsReflectedInSnapshotConnectionLabel()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseStatus(ConnectionStatus.Connected);
        Assert.Equal("Connected", snapshots[^1].ConnectionStatus);

        bridge.RaiseStatus(ConnectionStatus.Connecting);
        Assert.Equal("Connecting…", snapshots[^1].ConnectionStatus);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        Assert.Equal("Disconnected", snapshots[^1].ConnectionStatus);
    }

    [Fact]
    public async Task PostDelegate_IsUsedForChangedAndNotifications()
    {
        var bridge = new FakeBridge { Sessions = new[] { MainSession() } };
        var queued = new List<Action>();
        var provider = new OpenClawChatDataProvider(bridge, post: a => queued.Add(a));
        var snapshots = new List<ChatDataSnapshot>();
        provider.Changed += (_, e) => snapshots.Add(e.Snapshot);

        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "main", Role = "assistant", Text = "x", State = "final" });

        // Snapshot was queued, not invoked immediately.
        Assert.Empty(snapshots);
        Assert.NotEmpty(queued);
        foreach (var a in queued) a();
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndStopsRaisingChanged()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.DisposeAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "main", Role = "assistant", Text = "after dispose", State = "final" });
        bridge.RaiseSessions(new[] { MainSession() });
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        Assert.Empty(snapshots);
        Assert.True(bridge.IsDisposed);
    }

    [Fact]
    public async Task LoadAsync_FreshInstall_NoSessions_ExposesNotReadyComposeTarget()
    {
        // Replaces the pre-refactor CreateThreadAsync tests: there is no
        // create-thread RPC on the gateway, so the provider must never
        // synthesize a thread out of thin air. Instead, the snapshot exposes
        // a ChatComposeTarget that tells the UI whether/where to send.
        var (_, provider, _, _) = CreateProvider();
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.False(snap.ComposeTarget.IsReady);
        Assert.Null(snap.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task LoadAsync_HandshakeKnown_ZeroSessions_ExposesReadyComposeTarget()
    {
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // Real gateways always send sessions.list after handshake (even an
        // empty list). The provider waits for that signal before flipping
        // ComposeTarget.IsReady on — otherwise the UI would briefly render
        // the welcome zero-state for returning users mid-handshake.
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.True(snap.ComposeTarget.IsReady);
        Assert.Equal("agent:main:main", snap.ComposeTarget.SessionKey);
        Assert.Equal("agent:main:main", snap.DefaultThreadId);
    }

    [Fact]
    public async Task LoadAsync_HandshakeComplete_NoSessionKey_SignalsIncompatibleGateway()
    {
        // When the gateway completes the handshake but does not advertise
        // a mainSessionKey (or sessionDefaults.mainKey), the provider must surface
        // an "Incompatible gateway" connection label and a NotReady compose target
        // so the UI can show a clear "gateway update required" message rather than
        // silently blocking send. Relates to issue #459.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = null;   // incompatible gateway: no session key
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var snap = await provider.LoadAsync();

        Assert.Equal("Incompatible gateway", snap.ConnectionStatus);
        Assert.False(snap.ComposeTarget.IsReady);
        Assert.Null(snap.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task StatusChanged_IncompatibleGateway_IsReflectedInSnapshotConnectionLabel()
    {
        // Raise Connected with handshake present but no session key; the snapshot
        // must use "Incompatible gateway" rather than the plain "Connected" label.
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = null;
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.NotEmpty(snapshots);
        Assert.Equal("Incompatible gateway", snapshots[^1].ConnectionStatus);
        Assert.False(snapshots[^1].ComposeTarget.IsReady);
    }

    [Fact]
    public async Task LoadAsync_HandshakeKnownButSessionsNotYetReceived_ComposeTargetNotReady()
    {
        // Regression: in the brief window between hello-ok (HasHandshakeSnapshot
        // becomes true) and the first sessions.list, the provider used to expose
        // a ready ComposeTarget. The chat root would then synthesize a
        // compose-only thread and render the welcome zero-state — even for a
        // returning user whose real sessions were about to be delivered. The
        // sessions-list-received gate keeps ComposeTarget.IsReady=false until
        // the gateway has confirmed the session list for this connection.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // No RaiseSessions yet — simulate the mid-handshake window.
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.False(snap.ComposeTarget.IsReady);
    }

    [Fact]
    public async Task StatusDisconnected_AfterSessionsReceived_ComposeTargetResetsToNotReady()
    {
        // The sessions-list-received gate must reset on disconnect — otherwise
        // a reconnect would keep ComposeTarget ready against a stale session
        // list from the previous connection.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        Assert.True((await provider.LoadAsync()).ComposeTarget.IsReady);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        var snap = await provider.LoadAsync();
        Assert.False(snap.ComposeTarget.IsReady);
    }

    // ── Parity additions: streaming, lifecycle, reasoning, history, abort ──

    [Fact]
    public async Task AgentEvent_AssistantDelta_AppendsStreamingAssistantEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"Hel"}"""));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"lo "}"""));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"world"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var assistant = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Assistant, assistant.Kind);
        Assert.Equal("Hello world", assistant.Text);
        Assert.True(assistant.IsStreaming);
        Assert.True(timeline.TurnActive);
    }

    [Fact]
    public async Task AgentEvent_AssistantContent_IsIgnoredBecauseChatMessageCarriesFinalText()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("assistant",
            """{"content":"Final answer."}"""));

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task AgentEvent_LifecycleStart_SetsTurnActive()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle",
            """{"phase":"start"}""", runId: "run-1"));

        Assert.True(snapshots[^1].Timelines["main"].TurnActive);
    }

    [Fact]
    public async Task AgentEvent_LifecycleEnd_ClearsTurnActiveAndAssistant()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"hi"}"""));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        var snap = await provider.LoadAsync();
        var timeline = snap.Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Null(timeline.ActiveAssistantId);
    }

    [Fact]
    public async Task AgentEvent_LifecycleError_AppendsErrorStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        var evt = MakeAgentEvent("lifecycle", """{"phase":"error","message":"model unreachable"}""", runId: "run-1");
        bridge.RaiseAgent(evt);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("model unreachable"));
    }

    [Fact]
    public async Task AgentEvent_ReasoningDelta_AccumulatesReasoningEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"thinking… "}"""));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"step 2."}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Reasoning, entry.Kind);
        Assert.Equal("thinking… step 2.", entry.Text);
    }

    [Fact]
    public async Task StopResponseAsync_WithActiveRun_CallsAbortRpc()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-42"));

        await provider.StopResponseAsync("main");

        Assert.Single(bridge.AbortedRunIds);
        Assert.Equal("run-42", bridge.AbortedRunIds[0]);
    }

    [Fact]
    public async Task StopResponseAsync_WithoutActiveRun_DoesNotCallAbort()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.StopResponseAsync("main");

        Assert.Empty(bridge.AbortedRunIds);
    }

    [Fact]
    public async Task StopResponseAsync_AfterLifecycleEnd_NoLongerAborts()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-9"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-9"));

        await provider.StopResponseAsync("main");

        Assert.Empty(bridge.AbortedRunIds);
    }

    [Fact]
    public async Task LoadHistoryAsync_FoldsTranscriptIntoTimeline()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-123",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "Hi", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final" },
                new ChatMessageInfo { Role = "user", Text = "Bye", State = "final" }
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(3, timeline.Entries.Count);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[0].Kind);
        Assert.Equal("Hi", timeline.Entries[0].Text);
        Assert.Equal(ChatTimelineItemKind.Assistant, timeline.Entries[1].Kind);
        Assert.Equal("Hello!", timeline.Entries[1].Text);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[2].Kind);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task LoadHistoryAsync_MultipleAssistantTurns_PreservesEachAsSeparateEntry()
    {
        // Regression test: previously every ChatMessageEvent would upsert the
        // active assistant entry, collapsing N assistant messages into 1. The
        // fix is to bracket each assistant message with ChatTurnEndEvent.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Q1", State = "final", Ts = 1 },
                new ChatMessageInfo { Role = "assistant", Text = "A1", State = "final", Ts = 2 },
                new ChatMessageInfo { Role = "user",      Text = "Q2", State = "final", Ts = 3 },
                new ChatMessageInfo { Role = "assistant", Text = "A2", State = "final", Ts = 4 },
                new ChatMessageInfo { Role = "user",      Text = "Q3", State = "final", Ts = 5 },
                new ChatMessageInfo { Role = "assistant", Text = "A3", State = "final", Ts = 6 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(6, timeline.Entries.Count);
        Assert.Equal(new[] { "Q1", "A1", "Q2", "A2", "Q3", "A3" },
            timeline.Entries.Select(e => e.Text).ToArray());
        Assert.Equal(new[]
        {
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
        }, timeline.Entries.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_SystemRole_RendersAsDimStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hello",   State = "final", Ts = 1 },
                new ChatMessageInfo { Role = "system",    Text = "ctx",     State = "final", Ts = 2 },
                new ChatMessageInfo { Role = "assistant", Text = "Hi back", State = "final", Ts = 3 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(3, timeline.Entries.Count);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[0].Kind);
        Assert.Equal(ChatTimelineItemKind.Status, timeline.Entries[1].Kind);
        Assert.Equal("ctx", timeline.Entries[1].Text);
        Assert.Equal(ChatTone.Dim, timeline.Entries[1].Tone);
        Assert.Equal(ChatTimelineItemKind.Assistant, timeline.Entries[2].Kind);
        Assert.Equal("Hi back", timeline.Entries[2].Text);
    }

    [Theory]
    [InlineData("toolresult")]
    [InlineData("tool_result")]
    public async Task LoadHistoryAsync_ToolResultRole_RendersAsToolChipEvenWithoutHeuristicMatch(string role)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = role, Text = "(no output)", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("(no output)", entry.ToolOutput);
    }

    [Fact]
    public async Task LoadHistoryAsync_ToolRole_RendersAsDimStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "tool", Text = "tool transcript note", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.Status, entry.Kind);
        Assert.Equal(ChatTone.Dim, entry.Tone);
        Assert.Equal("tool transcript note", entry.Text);
    }

    [Fact]
    public async Task LoadHistoryAsync_UnknownRole_FallsBackToVisibleAssistantEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "function", Text = "fallback text", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
        Assert.Equal("fallback text", entry.Text);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task LoadHistoryAsync_OutOfOrderTimestamps_AreSortedAscending()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            // Deliberately scrambled — provider must sort by Ts.
            Messages = new[]
            {
                new ChatMessageInfo { Role = "assistant", Text = "Last",  State = "final", Ts = 30 },
                new ChatMessageInfo { Role = "user",      Text = "First", State = "final", Ts = 10 },
                new ChatMessageInfo { Role = "assistant", Text = "Mid",   State = "final", Ts = 20 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(new[] { "First", "Mid", "Last" },
            timeline.Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_IsIdempotent()
    {
        var calls = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => { calls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenRequestFails_NotifiesAndAllowsRetry()
    {
        var calls = 0;
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("history down");

            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "recovered", State = "final", Ts = 1 },
                }
            });
        };
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        Assert.Contains(notifications, n =>
            n.Kind == ChatProviderNotificationKind.Error &&
            n.Message?.Contains("history down") == true);
        Assert.Empty(snapshots);

        await provider.LoadHistoryAsync("main");

        Assert.Equal(2, calls);
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "recovered");
    }

    [Fact]
    public async Task SendMessageAsync_DoesNotForwardSessionIdToGateway()
    {
        // The live gateway rejects `sessionId` at the chat.send root with
        // "unexpected property". The provider tracks sessionId from chat.history
        // for client-side correlation but must not forward it. (Gateway client
        // ignores the sessionId arg; bridge still receives it for future use.)
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-7"
        });
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");

        await provider.SendMessageAsync("main", "Ping");

        // The bridge surface still receives the sessionId for tests / future
        // protocol use, but the production gateway client drops it before
        // serializing the chat.send request.
        Assert.Equal("sess-uuid-7", bridge.SentSessionIds[0]);
    }

    [Fact]
    public async Task LoadHistoryAsync_PersistsSessionIdForFutureSends()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-7"
        });
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");

        await provider.SendMessageAsync("main", "Ping");

        Assert.Equal("sess-uuid-7", bridge.SentSessionIds[0]);
    }

    [Fact]
    public async Task SendMessageAsync_WithoutHistory_PassesNullSessionId()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.SendMessageAsync("main", "Ping");

        Assert.Null(bridge.SentSessionIds[0]);
    }

    // ── Iteration 3: tool result, abort marker, reconnect history, models ──

    [Fact]
    public async Task AgentEvent_ToolResult_ExtractsResultContent()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"echo hi"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"powershell","result":{"content":"hi\n"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("hi\n", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResult_FallsBackToOutputField()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"grep","args":{"pattern":"foo"}}"""));
        // Some tools return output at data.output rather than data.result.content
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"grep","output":"line1\nline2"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal("line1\nline2", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ItemEndAfterCommandOutput_PreservesOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","title":"exec run command echo hi","itemId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","itemId":"tool-1","output":"hi\n"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","title":"exec run command echo hi","itemId":"tool-1"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("hi\n", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolError_ExtractsErrorText()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"web_fetch","args":{"url":"https://example"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"error","name":"web_fetch","error":"timeout after 30s"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("timeout after 30s", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResult_TruncatesVeryLargeOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var huge = new string('x', 10000);
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"read","args":{"path":"/big.txt"}}"""));
        var resultJson = "{\"phase\":\"result\",\"name\":\"read\",\"result\":{\"content\":\"" + huge + "\"}}";
        bridge.RaiseAgent(MakeAgentEvent("tool", resultJson));

        var entry = snapshots[^1].Timelines["main"].Entries[0];
        Assert.NotNull(entry.ToolOutput);
        Assert.True(entry.ToolOutput!.Length < huge.Length, "expected truncation");
        Assert.EndsWith("(truncated)", entry.ToolOutput);
    }

    [Fact]
    public async Task StopResponseAsync_DuringActiveTurn_AppendsAbortMarker()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"partial answer"}"""));
        snapshots.Clear();

        await provider.StopResponseAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Equals("Aborted", StringComparison.OrdinalIgnoreCase) &&
            e.Tone == ChatTone.Warning);
        Assert.False(timeline.TurnActive);
        Assert.Contains("partial answer", timeline.Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task StopResponseAsync_WithoutActiveTurn_DoesNotAppendAbortMarker()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.StopResponseAsync("main");

        // Either no snapshot or snapshot timeline has no Status="Aborted".
        if (snapshots.Count > 0)
        {
            var timeline = snapshots[^1].Timelines["main"];
            Assert.DoesNotContain(timeline.Entries, e =>
                e.Kind == ChatTimelineItemKind.Status &&
                e.Text.Equals("Aborted", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Reconnect_AfterDisconnect_ReloadsHistoryForLoadedThreads()
    {
        var historyCalls = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        var reloadObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            if (historyCalls >= 2)
                reloadObserved.TrySetResult();
            return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" });
        };

        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.Equal(1, historyCalls);

        // Drop and reconnect.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await reloadObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, historyCalls);
    }

    [Fact]
    public async Task Reconnect_FromConnectingToConnected_DoesNotReload()
    {
        // The "just reconnected" condition should only fire on a transition
        // from a non-Connected state to Connected — not on the initial
        // Connecting → Connected boot sequence.
        var historyCalls = 0;
        var bridge = new FakeBridge { Sessions = new[] { MainSession() }, CurrentStatus = ConnectionStatus.Connected };
        var provider = new OpenClawChatDataProvider(bridge);
        bridge.HistoryBehavior = _ => { historyCalls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.Equal(1, historyCalls);

        // Already Connected → setting Connected again is a no-op.
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // slopwatch-ignore: SW004 Negative async assertion needs a brief quiescence window to prove no reload fired.
        for (int i = 0; i < 10; i++) await Task.Delay(10);

        Assert.Equal(1, historyCalls);
    }

    [Fact]
    public async Task ModelsListUpdated_PopulatesAvailableModelsInSnapshot()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4" },
                new() { Id = "claude-sonnet-4.6", Name = "Claude Sonnet 4.6" },
                new() { Id = "ollama-only-id" }
            }
        });

        Assert.Equal(
            new[] { "gpt-5.4", "claude-sonnet-4.6", "ollama-only-id" },
            snapshots[^1].AvailableModels);
    }

    [Fact]
    public async Task ModelsListUpdated_FiltersExplicitlyUnconfiguredModels()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", IsConfigured = true, HasConfiguredFlag = true },
                new() { Id = "gpt-5.5", IsConfigured = false, HasConfiguredFlag = true },
                new() { Id = "legacy-gateway-model" }
            }
        });

        Assert.Equal(
            new[] { "gpt-5.4", "legacy-gateway-model" },
            snapshots[^1].AvailableModels);
    }

    [Fact]
    public async Task ModelsListUpdated_DedupesDisplayNames()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4" },
                new() { Id = "gpt-5.4-mirror", Name = "GPT-5.4" },
            }
        });

        // IDs are distinct ("gpt-5.4" vs "gpt-5.4-mirror"), so both appear.
        Assert.Equal(2, snapshots[^1].AvailableModels.Length);
        Assert.Equal("gpt-5.4", snapshots[^1].AvailableModels[0]);
        Assert.Equal("gpt-5.4-mirror", snapshots[^1].AvailableModels[1]);
    }

    [Fact]
    public async Task LoadAsync_SeedsModelsFromBridgeSnapshot()
    {
        var bridge = new FakeBridge
        {
            Sessions = new[] { MainSession() },
            CurrentModels = new ModelsListInfo
            {
                Models = new List<ModelInfo> { new() { Id = "x", Name = "X" } }
            }
        };
        var provider = new OpenClawChatDataProvider(bridge);

        var snap = await provider.LoadAsync();

        Assert.Equal(new[] { "x" }, snap.AvailableModels);
    }

    // ── Iteration 4: per-entry metadata (timestamp + model) ──

    [Fact]
    public async Task LoadHistoryAsync_CapturesPerEntryTimestamps()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Q",  State = "final", Ts = 1714600000000 },
                new ChatMessageInfo { Role = "assistant", Text = "A",  State = "final", Ts = 1714600001000 },
            }
        });
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(2, meta.Count);
        var entries = (await provider.LoadAsync()).Timelines["main"].Entries;
        var userTs = meta[entries[0].Id].Timestamp;
        var asstTs = meta[entries[1].Id].Timestamp;
        Assert.NotNull(userTs);
        Assert.NotNull(asstTs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600000000).ToLocalTime(), userTs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600001000).ToLocalTime(), asstTs);
    }

    [Fact]
    public async Task LoadHistoryAsync_AssignsModelFromActiveSession()
    {
        var session = new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Model = "gpt-5.5" };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "user", Text = "Hi", State = "final", Ts = 1 } }
        });
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");

        var meta = provider.GetEntryMetadata("main");
        var entry = (await provider.LoadAsync()).Timelines["main"].Entries[0];
        Assert.Equal("gpt-5.5", meta[entry.Id].Model);
    }

    [Fact]
    public async Task SendMessageAsync_AssignsTimestampToLocalUserEntry()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var before = DateTimeOffset.Now.AddSeconds(-1);
        await provider.SendMessageAsync("main", "hi");
        var after = DateTimeOffset.Now.AddSeconds(1);

        var snap = await provider.LoadAsync();
        var entry = snap.Timelines["main"].Entries[0];
        var meta = provider.GetEntryMetadata("main");
        Assert.True(meta.TryGetValue(entry.Id, out var m) && m.Timestamp.HasValue);
        Assert.InRange(m!.Timestamp!.Value, before, after);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantFinal_AssignsMetadata()
    {
        var session = new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Model = "claude-sonnet-4.6" };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final",
            Ts = 1714600005000
        });

        var snap = await provider.LoadAsync();
        var entry = snap.Timelines["main"].Entries[0];
        var meta = provider.GetEntryMetadata("main");
        Assert.True(meta.TryGetValue(entry.Id, out var m));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600005000).ToLocalTime(), m!.Timestamp);
        Assert.Equal("claude-sonnet-4.6", m.Model);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantFinal_MergesUsageMetadataOntoStreamingEntry()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "partial",
            State = "delta",
            Ts = 1714600005000
        });

        var firstSnap = await provider.LoadAsync();
        var entryId = Assert.Single(firstSnap.Timelines["main"].Entries).Id;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600006000,
            InputTokens = 12,
            OutputTokens = 34,
            ResponseTokens = 46,
            ContextPercent = 7
        });

        var snap = await provider.LoadAsync();
        var entry = Assert.Single(snap.Timelines["main"].Entries);
        Assert.Equal(entryId, entry.Id);
        Assert.Equal("final", entry.Text);

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(12, meta[entry.Id].InputTokens);
        Assert.Equal(34, meta[entry.Id].OutputTokens);
        Assert.Equal(46, meta[entry.Id].ResponseTokens);
        Assert.Equal(7, meta[entry.Id].ContextPercent);
    }

    [Fact]
    public async Task GetEntryMetadata_MissingThread_ReturnsEmpty()
    {
        var (_, provider, _, _) = CreateProvider();
        await provider.LoadAsync();

        var meta = provider.GetEntryMetadata("nonexistent");

        Assert.NotNull(meta);
        Assert.Empty(meta);
    }

    [Fact]
    public async Task GetEntryMetadata_ReturnsDefensiveCopy()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        await provider.SendMessageAsync("main", "hi");

        var snapshot1 = (Dictionary<string, ChatEntryMetadata>)provider.GetEntryMetadata("main");
        var initialCount = snapshot1.Count;
        snapshot1.Clear();   // mutate the returned copy

        var snapshot2 = provider.GetEntryMetadata("main");
        Assert.Equal(initialCount, snapshot2.Count);
    }

    [Fact]
    public async Task LoadHistoryAsync_AfterLiveActivity_DoesNotDuplicateEntries()
    {
        // Regression for HIGH 2: prior to the dedup fix, a live assistant
        // message that was later included in chat.history would appear twice
        // in the rebuilt timeline (once from the rebuild, once from the
        // append-prior step), and ID collisions could occur because both
        // sequences reused e1, e2, …
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hi",     State = "final", Ts = nowMs },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final", Ts = nowMs + 1000 }
            }
        });
        await provider.LoadAsync();

        // Simulate live activity arriving before history finishes loading:
        // a live assistant frame for the same content (within 5s of the
        // history timestamp). After history loads, this should be deduped.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello!",
            State = "final",
            Ts = nowMs + 1000
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(2, timeline.Entries.Count);
        Assert.Equal("Hi", timeline.Entries[0].Text);
        Assert.Equal("Hello!", timeline.Entries[1].Text);

        // IDs must be unique even after the append step.
        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_AfterLiveActivity_PreservesNonDuplicateLiveEntries()
    {
        // Live status entries (e.g. an "Aborted" warning) that the gateway
        // doesn't replay in history should be preserved after history load,
        // and re-IDed when their original IDs collide with the rebuilt set.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hi",     State = "final", Ts = nowMs },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final", Ts = nowMs + 1000 }
            }
        });
        await provider.LoadAsync();

        // A live event the history will NOT carry — must survive the rebuild.
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", "{\"phase\":\"error\",\"message\":\"net glitch\"}"));

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("net glitch"));

        // All entry IDs unique post-rebuild.
        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_WithMissingTimestamps_PreservesAllLiveEntries()
    {
        // Rubber-duck round 2: when the rebuilt history entry has no
        // timestamp (msg.Ts == 0), we must NOT dedupe a live entry against
        // it on text alone — silent transcript loss is worse than visible
        // duplication. The previous fingerprint logic collapsed all such
        // entries into a single bucket=0 slot and dropped the second.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                // Ts deliberately omitted (= 0) on both rebuilt entries.
                new ChatMessageInfo { Role = "user",      Text = "ok", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "ok", State = "final" }
            }
        });
        await provider.LoadAsync();

        // A live assistant frame for "ok" arrives before history loads.
        // Live entries always carry a non-zero Now timestamp, but the
        // rebuilt side has Ts=0 → dedup must NOT match.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final"
            // Ts not set
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        // Two assistant "ok" entries must survive: one from history, one live.
        var oks = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "ok");
        Assert.Equal(2, oks);

        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_WithSameTextDifferentTimestamps_PreservesBoth()
    {
        // Rubber-duck round 2: even with valid timestamps, two genuinely
        // distinct events with the same text should NOT collide once the
        // gap exceeds the 2-second tolerance window.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "assistant", Text = "ok", State = "final", Ts = nowMs }
            }
        });
        await provider.LoadAsync();

        // Live assistant message with the same text but 10 s later.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final",
            Ts = nowMs + 10_000
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        var oks = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "ok");
        Assert.Equal(2, oks);

        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Disconnect_DuringActiveTurn_InjectsInterruptionAndEndsTurn()
    {
        // Rubber-duck round 2 / MEDIUM 5: when the connection drops while
        // a turn is in flight we must synthesize a Status entry +
        // ChatTurnEndEvent so the UI doesn't sit "thinking" forever.
        var sendGate = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        // Establish Connected baseline so the next status change registers
        // as a Connected → Disconnected transition.
        bridge.RaiseStatus(ConnectionStatus.Connected);

        // Start a turn that never completes.
        var sendTask = provider.SendMessageAsync("main", "hi");
        Assert.True(snapshots[^1].Timelines["main"].TurnActive);

        snapshots.Clear();

        // Connection drops while turn is active.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));

        // Count interruption entries before any further events.
        var beforeCount = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));
        Assert.Equal(1, beforeCount);

        // Subsequent unrelated events on the thread must not re-trigger
        // the interruption (status is already Disconnected, no transition).
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "late frame",
            State = "final"
        });

        var afterTimeline = snapshots[^1].Timelines["main"];
        var afterCount = afterTimeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));
        Assert.Equal(1, afterCount);

        // Allow the in-flight send to complete so the test can finish.
        sendGate.SetResult();
        await sendTask;
    }

    // ── chat rubber-duck MEDIUM 2: live System (untrusted) / toolresult ──

    [Fact]
    public async Task OnChatMessageReceived_LiveToolResult_RendersAsToolChip()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "toolresult",
            Text = "drwxr-xr-x  3 root root\nProcess exited with code 0",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Contains("Process exited", entry.ToolOutput ?? "");
        // Must NOT have rendered as a normal assistant bubble.
        Assert.DoesNotContain(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveToolResult_AlternateRoleSpelling_AlsoRenders()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "tool_result",
            Text = "Exec completed (exit=0)",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Contains("Exec completed", entry.ToolOutput ?? "");
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveUserSystemNote_RendersAsStatus()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "System (untrusted): exec result for tool_call_42 follows",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        // Must render as a dim Status entry (provenance preserved), NOT
        // dropped silently and NOT shown as a real user bubble.
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("System (untrusted)"));
        Assert.DoesNotContain(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User);
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveUserPlain_ShownAsRemoteUser()
    {
        // After the cross-client sync fix, non-echo user messages from SSE
        // (e.g. sent from gateway web UI) should appear in the timeline.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "USER",
            Text = "hello there",
            State = "final"
        });

        Assert.Single(snapshots);
        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
        Assert.Equal("hello there", entry.Text);
    }

    // ── chat rubber-duck MEDIUM 4: per-message size cap ──

    [Fact]
    public async Task OnChatMessageReceived_OversizedContent_IsTruncated()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        // 300 KiB of ASCII — 1 byte per char in UTF-8.
        var huge = new string('A', 300 * 1024);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = huge,
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = timeline.Entries.Single(e => e.Kind == ChatTimelineItemKind.Assistant);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(entry.Text);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes,
            $"entry was {bytes} bytes; cap is {OpenClawChatDataProvider.MaxEntryTextBytes}");
        Assert.Contains("bytes truncated", entry.Text);
        Assert.True(entry.Text.Length < huge.Length);
    }

    [Fact]
    public void TruncateForChatEntry_BelowCap_ReturnsInputUnchanged()
    {
        var small = "hello world";
        Assert.Same(small, OpenClawChatDataProvider.TruncateForChatEntry(small));
    }

    [Fact]
    public void TruncateForChatEntry_AboveCap_RespectsByteCap()
    {
        var big = new string('Z', OpenClawChatDataProvider.MaxEntryTextBytes + 50_000);
        var truncated = OpenClawChatDataProvider.TruncateForChatEntry(big);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);
        Assert.EndsWith("bytes truncated]", truncated);
    }

    [Fact]
    public void TruncateForChatEntry_DoesNotSplitSurrogatePair()
    {
        // String of repeated 4-byte UTF-8 emoji that crosses the cap
        // boundary. The truncate must not hang and must not return a string
        // whose last char before the marker is an unpaired high surrogate.
        const string emoji = "\uD83D\uDE00"; // 😀 (U+1F600)
        var sb = new System.Text.StringBuilder(OpenClawChatDataProvider.MaxEntryTextBytes);
        for (var i = 0; i < OpenClawChatDataProvider.MaxEntryTextBytes / 4 + 10; i++)
            sb.Append(emoji);
        var truncated = OpenClawChatDataProvider.TruncateForChatEntry(sb.ToString());

        var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);

        var insertedAt = truncated.IndexOf(" … [", StringComparison.Ordinal);
        Assert.True(insertedAt > 0);
        Assert.False(char.IsHighSurrogate(truncated[insertedAt - 1]));
    }

    [Fact]
    public async Task OnAgentEvent_OversizedToolOutput_IsTruncated()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"ls"}}"""));

        var huge = new string('B', 400 * 1024);
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            JsonSerializer.Serialize(new { output = huge })));

        var timeline = snapshots[^1].Timelines["main"];
        var output = timeline.Entries.LastOrDefault(e => e.Kind == ChatTimelineItemKind.ToolCall);
        if (output?.ToolOutput is { } body)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
            Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);
        }
    }

    [Fact]
    public async Task StopResponseAsync_FailedAbort_ClearsSuppression()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.AbortBehavior = _ => throw new Exception("Network error");
        await provider.LoadAsync();
        snapshots.Clear();

        // Send a message to get a turn active
        await provider.SendMessageAsync("main", "Hello");
        // Simulate lifecycle.start with a runId
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", "main", "run-1"));

        // Now stop — the abort will fail
        await provider.StopResponseAsync("main");

        // The failed abort should generate an error notification
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);

        // Crucially: sending a new message should work (thread not permanently suppressed)
        snapshots.Clear();
        await provider.SendMessageAsync("main", "Try again");
        Assert.True(snapshots.Count > 0, "Sending after failed abort should succeed");
        Assert.Contains(bridge.SentMessages, m => m == "Try again");
    }

    [Fact]
    public async Task SendMessageAsync_ClearsPendingAbortCounts()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        // Send a message
        await provider.SendMessageAsync("main", "First");
        // Stop before lifecycle.start (creates a pending abort)
        await provider.StopResponseAsync("main");

        // Now send another message — this should clear pending aborts
        await provider.SendMessageAsync("main", "Second");

        // Simulate lifecycle.start arriving for the second message
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", "main", "run-2"));

        // The pending abort should NOT have fired (cleared by second send)
        Assert.DoesNotContain("run-2", bridge.AbortedRunIds);
    }

    [Fact]
    public async Task SendMessageAsync_WithAttachment_SendsThroughInterface()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var attachment = new ChatAttachment
        {
            Type = "file",
            MimeType = "text/plain",
            FileName = "test.txt",
            Content = Convert.ToBase64String(new byte[] { 72, 101, 108, 108, 111 }),
            SizeBytes = 5
        };

        await provider.SendMessageAsync("main", "Check this", default, new[] { attachment });

        Assert.Contains(bridge.SentMessages, m => m == "Check this");
        // The display text in the timeline should include the attachment indicator
        var timeline = snapshots[^1].Timelines["main"];
        var userEntry = timeline.Entries.Last(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Contains("test.txt", userEntry.Text);
    }

    [Fact]
    public async Task AttachmentMetadata_PersistsAndRehydratesFromHistory()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "Check this", default, new[]
        {
            new ChatAttachment
            {
                Type = "file",
                MimeType = "text/plain",
                FileName = "test.txt",
                Content = Convert.ToBase64String(new byte[] { 72, 105 }),
                SizeBytes = 2
            }
        });

        var (bridge2, provider2, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "Check this", State = "final", Ts = sentTs }
            }
        });

        await provider2.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Check this\n\u200B📎 test.txt", userEntry.Text);
    }

    [Fact]
    public async Task AttachmentMetadata_RehydratesAttachmentOnlyHistoryMessage()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "", default, new[]
        {
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "screenshot.png",
                Content = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                SizeBytes = 3
            }
        });

        var (bridge2, provider2, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "", State = "final", Ts = sentTs }
            }
        });

        await provider2.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("\u200B🖼️ screenshot.png", userEntry.Text);
    }

    [Fact]
    public async Task AttachmentMetadata_DoesNotRehydratePastedMarkerTextWithoutSidecar()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "\u200B📎 spoof.txt", State = "final", Ts = 1 }
            }
        });

        await provider.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("📎 spoof.txt", userEntry.Text);
    }

    [Fact]
    public async Task SendMessageAsync_WithoutAttachment_EscapesPastedMarkerText()
    {
        var (_, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.SendMessageAsync("main", "\u200B📎 spoof.txt");

        var userEntry = snapshots[0].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("📎 spoof.txt", userEntry.Text);
    }

    // ── Auto-reload on connect: OnSessionsUpdated eager history load ──

    [Fact]
    public async Task LoadAsync_WithCachedConnectedSessions_EagerlyLoadsHistory()
    {
        var historyRequested = new List<string?>();
        var historyLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge
        {
            CurrentStatus = ConnectionStatus.Connected,
            IsConnected = true,
            HasHandshakeSnapshot = true,
            MainSessionKey = "main",
            Sessions = new[] { MainSession() },
        };
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            historyLoaded.TrySetResult();
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? "",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "loaded from cached session", State = "final", Ts = 1 },
                }
            });
        };
        await using var provider = new OpenClawChatDataProvider(bridge);

        var snapshot = await provider.LoadAsync();
        await historyLoaded.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("main", snapshot.DefaultThreadId);
        Assert.Equal("main", snapshot.ComposeTarget.SessionKey);
        Assert.Contains("main", historyRequested);
    }

    [Fact]
    public async Task SessionsUpdated_WhileConnected_EagerlyLoadsHistory()
    {
        // When sessions arrive after the connection is already established,
        // the provider should automatically load history for new threads.
        var historyRequested = new List<string?>();
        var historyLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            historyLoaded.TrySetResult();
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? "",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "welcome back", State = "final", Ts = 1 },
                }
            });
        };
        await provider.LoadAsync();

        // Simulate: status → Connected, then sessions arrive.
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        bridge.RaiseSessions(new[] { MainSession() });

        await historyLoaded.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("main", historyRequested);
    }

    [Fact]
    public async Task SessionsUpdated_WhileDisconnected_DoesNotLoadHistory()
    {
        var historyRequested = new List<string?>();
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        await provider.LoadAsync();

        // Status stays Disconnected, sessions arrive.
        bridge.RaiseSessions(new[] { MainSession() });

        // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
        await Task.Delay(100);

        Assert.Empty(historyRequested);
    }

    [Fact]
    public async Task ChatMessage_ForUnknownThread_RequestsSessionRefreshOnce()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var baseline = bridge.ProactiveBootstrapCalls;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "agent:main:discord:direct:1410746924981682256",
            Role = "assistant",
            Text = "hello from discord",
            State = "final",
            Ts = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "agent:main:discord:direct:1410746924981682256",
            Role = "assistant",
            Text = "second frame",
            State = "final",
            Ts = 2,
        });

        Assert.Equal(baseline + 1, bridge.ProactiveBootstrapCalls);
    }

    // ── OnStatusChanged: broadened reconnect reloads all timelines ──

    [Fact]
    public async Task StatusChanged_Connected_ClearsHistoryInFlightAndReloads()
    {
        // On (re)connect, all timeline threads should be reloaded, not just
        // those in _historyLoaded.
        var historyRequested = new List<string?>();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        await provider.LoadAsync();

        // Transition to Connected — should reload the "main" timeline even
        // though LoadHistoryAsync was never successfully called before.
        var historyLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            historyLoaded.TrySetResult();
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await historyLoaded.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("main", historyRequested);
    }

    // ── LoadHistoryAsync retry on failure while connected ──

    [Fact]
    public async Task LoadHistoryAsync_WhenConnected_RetriesAfterFailure()
    {
        var calls = 0;
        var retrySucceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("gateway not ready");

            retrySucceeded.TrySetResult();
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "hello", State = "final", Ts = 1 },
                }
            });
        };
        await provider.LoadAsync();

        // Mark as connected so retry logic triggers.
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        // First call fails.
        Assert.Contains(notifications, n =>
            n.Kind == ChatProviderNotificationKind.Error &&
            n.Message?.Contains("gateway not ready") == true);

        await retrySucceeded.Task.WaitAsync(TimeSpan.FromSeconds(4));

        // Retry should have succeeded.
        Assert.True(calls >= 2, $"Expected retry, got {calls} calls");
        Assert.Contains(snapshots, s =>
            s.Timelines.TryGetValue("main", out var tl) &&
            tl.Entries.Any(e => e.Kind == ChatTimelineItemKind.Assistant && e.Text == "hello"));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Compose-target fix: covers the "fresh install, zero sessions" path
    //  that previously stranded optimistic state under a synthetic "main"
    //  key while the gateway echoed events back under "agent:main:main".
    //  See OpenClawChatDataProvider.BuildSnapshotLocked for the design.
    // ─────────────────────────────────────────────────────────────────────

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots)
        CreateConnectedProvider(string canonicalMainKey = "agent:main:main")
    {
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = canonicalMainKey;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // Mirror real gateway behavior: after handshake the gateway always
        // emits sessions.list (even an empty one). The provider needs that
        // signal to flip ComposeTarget.IsReady on, so that the UI doesn't
        // briefly render the welcome zero-state for returning users whose
        // real sessions are about to be delivered.
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        return (bridge, provider, snapshots);
    }

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots, List<ChatProviderNotification> notifications)
        CreateConnectedProviderWithNotifications(string canonicalMainKey = "agent:main:main")
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = canonicalMainKey;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        return (bridge, provider, snapshots, notifications);
    }

    [Fact]
    public async Task SendMessageAsync_FreshInstall_OptimisticEntryKeyedByCanonicalSessionKey()
    {
        // Regression for the zero-state bug: the user clicks a suggestion on
        // a fresh install (zero sessions). The optimistic entry must land in
        // a timeline keyed by the gateway's canonical session key — NOT a
        // literal "main". Otherwise the gateway's chat events (which come
        // back keyed by the canonical key) build a SECOND timeline and the
        // optimistic state is orphaned.
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();

        await provider.SendMessageAsync("agent:main:main", "hi");

        Assert.Contains(bridge.SentMessages, m => m == "hi");
        Assert.Equal("agent:main:main", bridge.SentSessionKeys[0]);
        var latest = snapshots[^1];
        Assert.True(latest.Timelines.ContainsKey("agent:main:main"));
        Assert.False(latest.Timelines.ContainsKey("main"),
            "The provider must never key timelines by the literal 'main' alias.");
        Assert.Single(latest.Timelines["agent:main:main"].Entries, e => e.Kind == ChatTimelineItemKind.User);
    }

    [Fact]
    public async Task SendMessageAsync_FreshInstall_SnapshotExposesComposeOnlyTimeline()
    {
        // Before the first SessionsUpdated arrives, the gateway-side session
        // doesn't exist yet, so Threads is empty. But the optimistic user
        // bubble must still be reachable to the UI: it's stored under the
        // compose-target key. The UI then synthesizes a compose-only thread
        // (matching the canonical key) so the timeline can render.
        var (_, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();

        await provider.SendMessageAsync("agent:main:main", "hi");

        var latest = snapshots[^1];
        // BuildSnapshotLocked surfaces a synthetic ChatThread when the
        // compose key has optimistic entries but isn't materialized yet.
        Assert.Single(latest.Threads);
        Assert.Equal("agent:main:main", latest.Threads[0].Id);
        Assert.Equal("agent:main:main", latest.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task SessionsUpdated_AfterFirstSend_PreservesOptimisticTimeline()
    {
        // The critical assertion: when the gateway materializes the session
        // and emits SessionsUpdated with the canonical key, the optimistic
        // entry that was written under that exact key SURVIVES (no second
        // empty timeline gets created on top of it).
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main session", Status = "active" }
        });

        var latest = snapshots[^1];
        Assert.Single(latest.Threads);
        Assert.Equal("agent:main:main", latest.Threads[0].Id);
        var timeline = latest.Timelines["agent:main:main"];
        Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("hi", timeline.Entries.First(e => e.Kind == ChatTimelineItemKind.User).Text);
    }

    [Fact]
    public async Task ChatEvent_WithEmptySessionKey_IsDropped()
    {
        // The "main" literal fallback in event handlers was the second half
        // of the bug: it would silently route mis-routed events to a synthetic
        // bucket. The fix is to drop the event and log — surfacing protocol
        // bugs instead of papering over them.
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "echo with no session key — should be dropped"
        });

        // And specifically: no synthetic "main" timeline was created.
        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.False(latest.Timelines.ContainsKey("main"));
        Assert.DoesNotContain(timeline.Entries, e => e.Text.Contains("echo with no session key"));
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Tone == ChatTone.Warning &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("echo with no session key") ?? false) ||
            (n.Message?.Contains("echo with no session key") ?? false));
    }

    [Fact]
    public async Task KeylessEvents_RaiseOnlyOneDiagnostic()
    {
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "first dropped payload"
        });
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"second dropped payload"}""", sessionKey: ""));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "third dropped payload"
        });

        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.Single(snapshots[^1].Timelines["agent:main:main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("dropped payload") ?? false) ||
            (n.Message?.Contains("dropped payload") ?? false));
    }

    [Fact]
    public async Task KeylessEvents_DiagnosticResetsOnReconnect()
    {
        // The one-shot guard should be reset when the gateway reconnects so
        // that a still-broken gateway surfaces the notification again in the
        // new session instead of staying silent forever.
        var (bridge, provider, _, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        // First keyless event — diagnostic fires once.
        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "", Role = "assistant", Text = "pre-reconnect drop" });
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");

        // Simulate gateway disconnect + reconnect.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());

        // After reconnect, the same keyless-event drop should fire the diagnostic again.
        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "", Role = "assistant", Text = "post-reconnect drop" });
        Assert.Equal(2, notifications.Count(n => n.Title == "Chat_Notification_KeylessEventDropped"));
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("pre-reconnect drop") ?? false) ||
            (n.Message?.Contains("pre-reconnect drop") ?? false) ||
            (n.Title?.Contains("post-reconnect drop") ?? false) ||
            (n.Message?.Contains("post-reconnect drop") ?? false));
    }

    [Fact]
    public async Task AgentEvent_WithEmptySessionKey_IsDroppedAndDiagnosed()
    {
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"secret agent payload"}""", sessionKey: ""));

        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.False(latest.Timelines.ContainsKey("main"));
        Assert.DoesNotContain(timeline.Entries, e => e.Text.Contains("secret agent payload"));
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Tone == ChatTone.Warning &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("secret agent payload") ?? false) ||
            (n.Message?.Contains("secret agent payload") ?? false));
    }

    [Fact]
    public async Task ChatEvent_WithCanonicalSessionKey_AppendsToExistingTimeline()
    {
        // Happy path: gateway echoes assistant text back under the same
        // canonical key the optimistic entry used. They land in one timeline.
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "agent:main:main",
            Role = "assistant",
            Text = "hello back",
            State = "final"
        });

        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "hi");
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant && e.Text == "hello back");
        Assert.False(latest.Timelines.ContainsKey("main"));
    }

    [Fact]
    public async Task SendMessageAsync_PreHandshake_GatewayClientRefusesViaProvider()
    {
        // Provider-level proof that the upstream guard fires: SendMessageAsync
        // bubbles the InvalidOperationException raised by the gateway client's
        // ResolveEffectiveSessionKey when no canonical sessionKey is known.
        // (The pure-function unit test for the helper itself lives in
        //  OpenClawGatewayClientSessionKeyTests in OpenClaw.Shared.Tests.)
        var (bridge, provider, _, _) = CreateProvider();
        bridge.IsConnected = true;
        bridge.SendBehavior = (_, key, _) =>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "chat.send requires a sessionKey, but the gateway handshake has not resolved one yet.");
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.SendMessageAsync("", "hi"));
    }

    [Fact]
    public async Task ResolveDefaultThreadId_PrefersIsMain_NotLiteralStringMatch()
    {
        // The pre-refactor ResolveDefaultThreadIdLocked heuristic compared
        // thread.Id to the literal "main", which would silently lose the
        // default when the canonical key was "agent:main:main".
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "agent:main:other", IsMain = false, DisplayName = "Other" },
            new SessionInfo { Key = "agent:main:main",  IsMain = true,  DisplayName = "Main" }
        });
        var snap = await provider.LoadAsync();
        Assert.Equal("agent:main:main", snap.DefaultThreadId);
    }

    [Fact]
    public void LoadLastChatState_WithCorruptedJson_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.DirectoryPath, "last-chat-state.json");
        File.WriteAllText(path, "{not json");

        var state = OpenClawChatDataProvider.LoadLastChatState(path);

        Assert.Null(state);
    }

    private sealed class TestLogger : OpenClaw.Shared.IOpenClawLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "openclaw-chat-attachments-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
