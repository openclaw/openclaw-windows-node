// Real coordinator/provider/client replacement proof over an authenticated loopback producer.
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

public sealed class SuggestedTaskReplacementTests
{
    private static readonly TimeSpan Deadline =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("same")]
    [InlineData("forbidden")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("unverified")]
    [InlineData("gateway")]
    [InlineData("operator")]
    [InlineData("device")]
    public async Task SuggestedTasks_RealProviderReplacementCannotReopenUncertainTask(string binding)
    {
        using var temp = new TempDirectory();
        var scenario = GatewayScenario.CreateTaskSuggestions();
        const string token = "synthetic-replacement-proof";
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var first = Client(server, temp.Combine("identity"));
        await ConnectAsync(first);
        using var coordinator = new OpenClawChatCoordinator(new SettingsManager(temp.Combine("settings")),
            () => null, NullLogger.Instance, null, () => null);
        coordinator.SetOperatorClient(first, "gateway-A");
        var originalProvider = coordinator.Provider!;
        await originalProvider.LoadAsync();
        await UntilAsync(originalProvider.TaskSuggestions!, snapshot => snapshot.Cards.Count == 1);
        server.HoldResponses("taskSuggestions.accept");
        var original = new GatewayTaskAcceptance(FixtureTaskSuggestions.TaskId, "worktree", Cwd: "/original");
        var accepting = originalProvider.TaskSuggestions!.AcceptAsync(original);
        await server.WaitForRequestAsync("taskSuggestions.accept").WaitAsync(Deadline);
        coordinator.SetOperatorClient(null); // Normal Reconnect disposes the provider before a new client exists.
        if (binding == "operator") server.OverrideProfileRead(_ => new { profile = new { id = "different-operator" } });
        using var second = Client(server, temp.Combine(binding == "device" ? "different-device" : "identity"));
        await ConnectAsync(second);
        Assert.Equal(first.SessionMutationConnectionEpoch, second.SessionMutationConnectionEpoch);
        if (binding == "forbidden") server.ForbidProfileRead();
        if (binding == "malformed") server.OverrideProfileRead(_ => new { profile = new { displayName = "No authoritative profile ID" } });
        if (binding == "timeout") server.HoldResponses("users.self");
        var profileReads = server.Requests.Count(request => request.Method == "users.self");
        if (binding == "unverified") server.HoldResponses("users.self");
        coordinator.SetOperatorClient(second, binding == "gateway" ? "gateway-B" : "gateway-A");
        var replacement = coordinator.Provider!;
        Assert.NotSame(originalProvider, replacement);
        await replacement.LoadAsync();
        if (binding == "unverified")
        {
            await server.WaitForRequestAsync("users.self", occurrence: profileReads + 1).WaitAsync(Deadline);
            Assert.Empty(replacement.TaskSuggestions!.Snapshot().Cards);
            await replacement.TaskSuggestions.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "local"));
            Assert.Single(scenario.TaskSuggestions!.Acceptances);
            server.ReleaseResponses("users.self");
        }
        await replacement.TaskSuggestions!.RefreshAsync();
        if (binding is "forbidden" or "malformed" or "timeout")
        {
            Assert.Empty(replacement.TaskSuggestions.Snapshot().Cards);
            Assert.True(replacement.TaskSuggestions.Snapshot().IdentityUnavailable);
            await replacement.TaskSuggestions.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "local"));
            Assert.Single(scenario.TaskSuggestions!.Acceptances);
            server.OverrideProfileRead(_ => new { profile = new { id = GatewayScenario.OwnerProfileId } });
            if (binding == "timeout") server.ReleaseResponses("users.self");
            await replacement.TaskSuggestions.RefreshAsync();
            Assert.False(replacement.TaskSuggestions.Snapshot().IdentityUnavailable);
        }
        if (binding is "same" or "unverified" or "forbidden" or "malformed" or "timeout")
        {
            var receipt = Assert.Single(replacement.TaskSuggestions.Snapshot().Cards);
            Assert.Equal(original, receipt.Attempt);
            Assert.True(receipt.Unconfirmed);
            Assert.False(receipt.CanRetry);
        }
        else Assert.Empty(replacement.TaskSuggestions.Snapshot().Cards);
        await replacement.TaskSuggestions.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "cloud", "different"));
        await replacement.TaskSuggestions.DismissAsync(FixtureTaskSuggestions.TaskId);
        Assert.Single(scenario.TaskSuggestions!.Acceptances);
        Assert.Equal(0, scenario.TaskSuggestions.Dismissals);
        server.ReleaseResponses("taskSuggestions.accept");
        await accepting.WaitAsync(Deadline);
        Assert.DoesNotContain(replacement.TaskSuggestions.Snapshot().Cards, card => card.StartedKey is not null);
        scenario.TaskSuggestions.AddPendingTask("unrelated-new-task");
        await replacement.TaskSuggestions.RefreshAsync();
        Assert.Contains(replacement.TaskSuggestions.Snapshot().Cards, card => card.Suggestion.Id == "unrelated-new-task" && card.Attempt is null);
        await replacement.TaskSuggestions.AcceptAsync(new("unrelated-new-task", "local"));
        Assert.Equal(2, scenario.TaskSuggestions.Acceptances.Count);
        Assert.NotNull(Assert.Single(replacement.TaskSuggestions.Snapshot().Cards, card => card.Suggestion.Id == "unrelated-new-task").StartedKey);
    }

    [Fact]
    public async Task SuggestedTasks_IdentitySendQueueDeadlineCancelsBeforeDispatchAndRecovers()
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateTaskSuggestions(), "synthetic-replacement-proof");
        using var client = new ObservedIdentityClient(server.Endpoint.AbsoluteUri, temp.Combine("identity"));
        await ConnectAsync(client);
        using var bridge = new GatewayClientChatBridge(client);
        // The runner supplies a short process-only OPENCLAW_USER_SELF_TIMEOUT_MS for this focused lane.
        // The production default remains meaningful if this test is run without that override.
        var configuredMs = WorkspaceIdentitySource.UserSelfTimeoutMs();
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        await bridge.ReadTaskOperatorIdentityAsync(epoch); // Drain the fixture's ready baseline before taking the real lock.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sendLock = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock", flags)!.GetValue(client)!;
        var registry = typeof(OpenClawGatewayClient).GetField("_pendingRequests", flags)!.GetValue(client)!;
        string[] PendingMethods()
        {
            var gate = registry.GetType().GetField("_gate", flags)!.GetValue(registry)!;
            lock (gate)
            {
                var entries = (System.Collections.IDictionary)registry.GetType().GetField("_entries", flags)!.GetValue(registry)!;
                return entries.Values.Cast<object>().Select(entry => (string)entry.GetType().GetProperty("Method", flags)!.GetValue(entry)!).ToArray();
            }
        }
        int Pending() => PendingMethods().Count(method => method == "users.self");
        var before = server.Requests.Count(request => request.Method == "users.self");
        var pendingBefore = Pending();
        var queued = new List<Task<string?>>();
        await sendLock.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var read = bridge.ReadTaskOperatorIdentityAsync(epoch);
                queued.Add(read);
                var send = client.IdentitySends.Last();
                // test-timing-exception: observe the deliberately held send-lock deadline, not UI readiness or test ordering.
                var budget = Task.Delay(TimeSpan.FromMilliseconds(configuredMs * 3L + 500));
                Assert.Same(read, await Task.WhenAny(read, budget));
                await Assert.ThrowsAnyAsync<Exception>(async () => await read);
                Console.WriteLine($"Identity send expired: tokenCancelled={send.Token.IsCancellationRequested}; sendCompleted={send.Completion.Task.IsCompleted}");
                Assert.True(send.Token.IsCancellationRequested);
                Assert.False(await send.Completion.Task.WaitAsync(Deadline));
                Console.WriteLine($"Identity queue attempt={attempt}; configuredDeadlineMs={configuredMs}; pendingMethods={string.Join(',', PendingMethods())}");
                Assert.Equal(pendingBefore, Pending());
                Assert.Equal(before, server.Requests.Count(request => request.Method == "users.self"));
            }
        }
        finally
        {
            sendLock.Release();
            // Baseline cleanup releases any unbounded read, without abandoning a live child request.
            foreach (var read in queued) { try { await read.WaitAsync(Deadline); } catch { } }
        }
        Assert.Equal(before, server.Requests.Count(request => request.Method == "users.self"));
        Console.WriteLine($"Identity queue released; pendingMethods={string.Join(',', PendingMethods())}");
        Assert.Equal(pendingBefore, Pending());
        Assert.NotNull(await bridge.ReadTaskOperatorIdentityAsync(epoch));
        Console.WriteLine("Identity send results=" + string.Join(',', client.IdentitySends.Select(send => send.Completion.Task.IsCompletedSuccessfully ? send.Completion.Task.Result.ToString() : "pending")));
        Assert.Equal(before + 1, server.Requests.Count(request => request.Method == "users.self"));
        Assert.True(client.IsConnectedToGateway);
        Assert.Equal(epoch, client.SessionMutationConnectionEpoch);
    }

    private sealed class ObservedIdentityClient(string endpoint, string identity)
        : OpenClawGatewayClient(endpoint, "synthetic-replacement-proof", NullLogger.Instance,
            identityPath: identity, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false)
    {
        internal sealed record IdentitySend(CancellationToken Token)
        {
            internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal System.Collections.Concurrent.ConcurrentQueue<IdentitySend> IdentitySends { get; } = new();
        protected override async Task<bool> SendRawAsync(string message, long generation, CancellationToken token)
        {
            using var frame = System.Text.Json.JsonDocument.Parse(message);
            if (!frame.RootElement.TryGetProperty("method", out var method) || method.GetString() != "users.self")
                return await base.SendRawAsync(message, generation, token);
            var observed = new IdentitySend(token);
            IdentitySends.Enqueue(observed);
            Console.WriteLine("Identity send caller=" + string.Join(" > ", new System.Diagnostics.StackTrace().GetFrames().Take(10).Select(frame => frame.GetMethod()?.DeclaringType?.Name + "." + frame.GetMethod()?.Name)));
            try
            {
                var sent = await base.SendRawAsync(message, generation, token);
                observed.Completion.TrySetResult(sent);
                return sent;
            }
            catch (Exception ex) { observed.Completion.TrySetException(ex); throw; }
        }
    }

    private static OpenClawGatewayClient Client(FixtureGatewayServer server, string identity) =>
        new(server.Endpoint.AbsoluteUri, "synthetic-replacement-proof", NullLogger.Instance,
            identityPath: identity, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);

    private static async Task ConnectAsync(OpenClawGatewayClient client)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, status) => { if (status == ConnectionStatus.Connected) connected.TrySetResult(); };
        client.HandshakeSucceeded += (_, _) => ready.TrySetResult();
        await client.ConnectAsync();
        await ready.Task.WaitAsync(Deadline);
        await connected.Task.WaitAsync(Deadline);
    }

    private static async Task UntilAsync(ChatTaskSuggestionCoordinator tasks, Func<ChatTaskSnapshot, bool> predicate)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) { if (predicate(tasks.Snapshot())) signal.TrySetResult(); }
        tasks.Changed += Changed;
        try { Changed(null, EventArgs.Empty); await signal.Task.WaitAsync(Deadline); }
        finally { tasks.Changed -= Changed; }
    }
}
