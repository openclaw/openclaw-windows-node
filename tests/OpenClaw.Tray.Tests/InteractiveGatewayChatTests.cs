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
    private static readonly TimeSpan ScopeInterleavingDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FirstSend_IdenticalSessionRefreshWhileSocketWaitsKeepsOriginalIncarnation()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        server.Interactive!.Playback.Pause();
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(ScopeInterleavingDeadline);
        var heldListing = server.HoldNextSessionList();
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        await provider.LoadAsync();
        await heldListing.WaitAsync(ScopeInterleavingDeadline);
        var key = GatewayScenario.EmptySessionKey;
        var origin = provider.CaptureInputOrigin(key);
        Assert.Equal(client.GetSessionList().Single(session => session.Key == key).SessionId, origin.SessionId);
        Assert.NotNull(origin.SessionId);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) =>
        {
            if (sessions.Any(session => session.Key == key && session.SessionId == origin.SessionId))
                refreshed.TrySetResult();
        };
        var socketGate = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        await socketGate.WaitAsync();
        Task<bool> send;
        try
        {
            send = provider.SendMessageReportingAcceptanceAsync(key, "First-send refresh race.");
            Assert.False(send.IsCompleted);
            server.ReleaseSessionList();
            await refreshed.Task.WaitAsync(ScopeInterleavingDeadline);
            Assert.True(provider.IsInputOriginCurrent(origin));
        }
        finally { socketGate.Release(); }
        Assert.True(await send.WaitAsync(ScopeInterleavingDeadline));
        Assert.Single(server.Requests, request => request.Method == "chat.send");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("sessions")]
    public async Task ScopedMutation_ActualPredicateReadLockContentionCompletesWithoutInversion(string owner)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(ScopeInterleavingDeadline);
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"),
            temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        await provider.LoadAsync();
        await provider.LoadHistoryAsync(GatewayScenario.EmptySessionKey, force: true);
        var origin = provider.CaptureInputOrigin(GatewayScenario.EmptySessionKey);
        Assert.True(provider.IsInputOriginCurrent(origin));
        var state = typeof(OpenClawChatDataProvider).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
        var gate = owner == "conversation"
            ? typeof(ChatConversationState).GetField("_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(state)!
            : typeof(OpenClawGatewayClient).GetField("_sessionsLock",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var socketGate = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finalCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        await socketGate.WaitAsync();
        var mutation = client.PatchSessionConfirmedScopedAsync(origin.ThreadId, new SessionPatch { Model = "test/model" },
            bridge.ConnectionEpoch, () =>
            {
                if (Interlocked.Increment(ref checks) == 2) finalCheck.TrySetResult();
                return provider.IsInputOriginCurrent(origin) && client.GetSessionList().Length == 5;
            });
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                held.Set();
                Assert.True(release.Wait(ScopeInterleavingDeadline));
            }
        });
        try
        {
            Assert.True(held.Wait(ScopeInterleavingDeadline));
            socketGate.Release();
            await finalCheck.Task.WaitAsync(ScopeInterleavingDeadline);
            Assert.False(mutation.IsCompleted);
        }
        finally { release.Set(); }
        await holder.WaitAsync(ScopeInterleavingDeadline);
        await mutation.WaitAsync(ScopeInterleavingDeadline);
        Assert.Single(server.Requests, request => request.Method == "sessions.patch");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ScopedAbort_RetiredWhileWaitingForSocketNeverWrites()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(ScopeInterleavingDeadline);
        using var bridge = new GatewayClientChatBridge(client);
        var socketGate = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var current = true;
        await socketGate.WaitAsync();
        var abort = bridge.SendChatAbortAtEpochAsync("original-run", GatewayScenario.EmptySessionKey,
            bridge.ConnectionEpoch, () => current);
        Assert.False(abort.IsCompleted);
        current = false;
        socketGate.Release();
        var refusal = await Assert.ThrowsAsync<ChatMutationDeliveryException>(() => abort);
        Assert.False(refusal.MayHaveApplied);
        await client.RequestChatHistoryAsync(GatewayScenario.EmptySessionKey);
        Assert.DoesNotContain(server.Requests, request => request.Method == "chat.abort");
        Assert.Empty(server.UnexpectedRequests);
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("chat.send")]
    [InlineData("chat.abort")]
    [InlineData("exec.approval.resolve")]
    public async Task ProductionBridge_RejectsOldEpochBeforeWritingMutation(string method)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var bridge = new GatewayClientChatBridge(client);
        var epoch = bridge.ConnectionEpoch;
        Assert.Equal(client.SessionMutationConnectionEpoch, epoch);
        var key = GatewayScenario.EmptySessionKey;

        await Assert.ThrowsAsync<InvalidOperationException>(() => method switch
        {
            "chat.send" => bridge.SendChatMessageForRunAtEpochAsync(
                "Synthetic prompt.", key, null, null, "original-input", epoch - 1),
            "chat.abort" => bridge.SendChatAbortAtEpochAsync("original-run", key, epoch - 1),
            _ => bridge.ResolveExecApprovalAtEpochAsync("original-approval", "allow-once", epoch - 1),
        });

        await client.RequestChatHistoryAsync(key);
        Assert.DoesNotContain(server.Requests, request => request.Method == method);
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
    public async Task FixtureDrivesRealProviderThroughRunAndHistory(string scenario)
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token,
            eventDelay: TimeSpan.FromMilliseconds(30));
        server.Interactive!.Playback.Select(scenario == "queued" ? "slow" : scenario);
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

    private static ChatTimelineState? Latest(ConcurrentQueue<ChatDataSnapshot> snapshots, string key) =>
        snapshots.LastOrDefault()?.Timelines.GetValueOrDefault(key);

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}
