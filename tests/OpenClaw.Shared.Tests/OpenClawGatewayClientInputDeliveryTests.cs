using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public partial class OpenClawGatewayClientTests
{
    private static Task<JsonElement> SendScopedMutation(
        OpenClawGatewayClient client, string method, long epoch, Func<bool> current)
    {
        var sender = typeof(OpenClawGatewayClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "SendResponseRequestAsync" && candidate.GetParameters().Length == 5);
        return Assert.IsAssignableFrom<Task<JsonElement>>(sender.Invoke(client,
            [method, new { key = "agent:test:source", sessionKey = "agent:test:source" }, 15000, epoch, current]));
    }

    [Theory]
    [InlineData("sessions.patch", false)]
    [InlineData("sessions.create", false)]
    [InlineData("sessions.dispatch", false)]
    [InlineData("sessions.patch", true)]
    [InlineData("sessions.create", true)]
    [InlineData("sessions.dispatch", true)]
    public async Task ScopedMutation_StaleAfterSocketWaitNeverWritesAndRetiresPendingEntry(string method, bool predicateThrows)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("scoped-mutation-never-written-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var semaphore = Assert.IsType<SemaphoreSlim>(typeof(WebSocketClientBase)
            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
        var current = true;
        await semaphore.WaitAsync();
        Task<JsonElement> mutation;
        try
        {
            mutation = SendScopedMutation(client, method, client.SessionMutationConnectionEpoch!.Value,
                () => !current && predicateThrows ? throw new InvalidOperationException("Failed origin lookup.") : current);
            Assert.False(mutation.IsCompleted);
            current = false;
        }
        finally { semaphore.Release(); }
        var refusal = await Assert.ThrowsAsync<ChatMutationDeliveryException>(() => mutation);
        Assert.False(refusal.MayHaveApplied);
        Assert.Contains("No request was written", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, helper.GetPendingRequestCount());
        var probe = client.SendWizardRequestAsync("health");
        var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        using var frame = JsonDocument.Parse(query);
        Assert.Equal("health", frame.RootElement.GetProperty("method").GetString());
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(query), ok = true, payload = new { },
        }));
        await probe.WaitAsync(CloseHandshakeTimeout);
    }

    [Theory]
    [InlineData("sessions.patch")]
    [InlineData("sessions.create")]
    [InlineData("sessions.dispatch")]
    public async Task ScopedMutation_LostResponseAfterActualWriteIsUnknownWithoutReplay(string method)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("scoped-mutation-unknown-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var mutation = SendScopedMutation(client, method, client.SessionMutationConnectionEpoch!.Value, () => true);
        var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        using var frame = JsonDocument.Parse(query);
        Assert.Equal(method, frame.RootElement.GetProperty("method").GetString());
        using var close = new CancellationTokenSource(CloseHandshakeTimeout);
        var disconnect = client.DisconnectAsync();
        await server.CompleteClientCloseAsync(0, close.Token);
        await disconnect.WaitAsync(CloseHandshakeTimeout);
        var uncertain = await Assert.ThrowsAsync<ChatMutationDeliveryException>(() => mutation);
        Assert.True(uncertain.MayHaveApplied);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task ScopedMutation_NullPredicateFailsClosedBeforeRegistration()
    {
        using var identity = new TempDirectory("scoped-mutation-null-");
        using var client = new OpenClawGatewayClient("ws://127.0.0.1:1", "test-token", identityPath: identity.Path);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.PatchSessionConfirmedScopedAsync("agent:test:source", new SessionPatch { Model = "test/model" }, 0, null!));
    }

    [Fact]
    public async Task ScopedMutation_ServerRejectionRemainsDefiniteStructuredRejection()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("scoped-mutation-rejection-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var mutation = SendScopedMutation(client, "sessions.patch", client.SessionMutationConnectionEpoch!.Value, () => true);
        var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(query), ok = false,
            error = new { message = "Setting denied.", code = "FORBIDDEN", details = new { reason = "policy" } },
        }));
        var rejection = await Assert.ThrowsAsync<GatewayRequestException>(() => mutation);
        Assert.Equal("FORBIDDEN", rejection.Code);
        Assert.Equal("Setting denied.", rejection.Message);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    private sealed class LegacyPreWriteRefusalClient(string endpoint, string identityPath)
        : OpenClawGatewayClient(endpoint, "test-token", identityPath: identityPath)
    {
        public int CapturedWrites { get; private set; }
        protected override Task<bool> SendRawAsync(string message, long epoch, CancellationToken token)
        {
            CapturedWrites++;
            return Task.FromResult(false);
        }
    }

    private sealed class InputOrigin(IOperatorGatewayClient client, long epoch) : IChatInputOrigin
    {
        public IOperatorGatewayClient Client { get; set; } = client;
        public long ConnectionEpoch { get; set; } = epoch;
        public string SessionKey { get; set; } = "agent:test:source";
        public string? SessionId => "source-incarnation";
        public string? AgentId => "test";
        public string? RunId { get; set; } = "source-run";
        public CancellationToken CancellationToken => CancellationToken.None;
        public bool IsCurrent { get; set; } = true;
    }

    [Theory]
    [InlineData("account")]
    [InlineData("client")]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("environment")]
    [InlineData("throws")]
    public async Task ScopedChatSend_RechecksOriginAfterHeldSocketLockWithoutWriting(string change)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-origin-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        using var otherIdentity = new TempDirectory("chat-input-other-");
        using var other = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: otherIdentity.Path);
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var origin = new InputOrigin(client, epoch);
        var environmentCurrent = true;
        var throwOnCheck = false;
        bool Current()
        {
            if (throwOnCheck) throw new InvalidOperationException("Scope lookup failed.");
            return environmentCurrent && origin.RunId == "source-run";
        }
        var semaphore = Assert.IsType<SemaphoreSlim>(typeof(WebSocketClientBase)
            .GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client));
        await semaphore.WaitAsync();
        Task<ChatSendResult> send;
        try
        {
            send = client.SendChatMessageForRunAsync("Keep this exact payload.", origin.SessionKey, origin.SessionId,
                idempotencyKey: "retained-input", expectedConnectionEpoch: epoch,
                options: new(false, AgentId: origin.AgentId, Origin: origin, IsCurrent: Current));
            Assert.False(send.IsCompleted);
            switch (change)
            {
                case "account": origin.IsCurrent = false; break;
                case "client": origin.Client = other; break;
                case "epoch": origin.ConnectionEpoch++; break;
                case "session": origin.SessionKey = "agent:test:replacement"; break;
                case "run": origin.RunId = "replacement-run"; break;
                case "environment": environmentCurrent = false; break;
                case "throws": throwOnCheck = true; break;
            }
        }
        finally
        {
            semaphore.Release();
        }

        var rejected = await Assert.ThrowsAsync<ChatSendDeliveryException>(() => send);
        Assert.False(rejected.DeliveryUncertain);
        Assert.Equal(0, helper.GetPendingRequestCount());
        var probe = client.SendWizardRequestAsync("health");
        var text = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var frame = JsonDocument.Parse(text);
        Assert.Equal("health", frame.RootElement.GetProperty("method").GetString());
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(text), ok = true, payload = new { },
        }));
        await probe.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ScopedChatSend_DisconnectAfterActualWriteIsUnconfirmedWithOriginalId()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-written-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var send = client.SendChatMessageForRunAsync("Uncertain payload.", "agent:test:source",
            idempotencyKey: "stable-input-id",
            expectedConnectionEpoch: client.SessionMutationConnectionEpoch,
            options: new(false, IsCurrent: () => true));
        var text = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var frame = JsonDocument.Parse(text);
        Assert.Equal("stable-input-id", frame.RootElement.GetProperty("params").GetProperty("idempotencyKey").GetString());
        Assert.False(send.IsCompleted);

        using var closeDeadline = new CancellationTokenSource(CloseHandshakeTimeout);
        var disconnect = client.DisconnectAsync();
        await server.CompleteClientCloseAsync(0, closeDeadline.Token);
        await disconnect.WaitAsync(CloseHandshakeTimeout);

        var failure = await Assert.ThrowsAsync<ChatSendDeliveryException>(() => send);
        Assert.True(failure.DeliveryUncertain);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Theory]
    [InlineData(ChatInputAction.Send, null)]
    [InlineData(ChatInputAction.Queue, null)]
    [InlineData(ChatInputAction.Steer, "steer")]
    [InlineData(ChatInputAction.Followup, "followup")]
    [InlineData(ChatInputAction.Collect, "collect")]
    [InlineData(ChatInputAction.Interrupt, "interrupt")]
    public async Task ScopedChatSend_AdvertisedTransportWritesExactModeAndWaitsForAcknowledgement(
        ChatInputAction action, string? expectedMode)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-steer-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        helper.SetGrantedScopes(["operator.write"]);
        typeof(OpenClawGatewayClient).GetField("_advertisedServerCapabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, new[] { ChatInputCustody.Capability });
        var send = client.SendChatMessageForRunAsync("Steer bytes.", "agent:test:source", "exact-session-id",
            idempotencyKey: "input-1", expectedConnectionEpoch: client.SessionMutationConnectionEpoch,
            options: new(true, ChatInputCustody.WireMode(action), "test", IsCurrent: () => true));
        var text = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var frame = JsonDocument.Parse(text);
        var parameters = frame.RootElement.GetProperty("params");
        Assert.Equal("chat.send", frame.RootElement.GetProperty("method").GetString());
        if (expectedMode is null)
            Assert.False(parameters.TryGetProperty("queueMode", out _));
        else
            Assert.Equal(expectedMode, parameters.GetProperty("queueMode").GetString());
        Assert.Equal("exact-session-id", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("test", parameters.GetProperty("agentId").GetString());
        Assert.False(parameters.GetProperty("deliver").GetBoolean());
        Assert.Equal("Steer bytes.", parameters.GetProperty("message").GetString());
        Assert.Equal("input-1", parameters.GetProperty("idempotencyKey").GetString());
        Assert.Equal("agent:test:source", parameters.GetProperty("sessionKey").GetString());
        var expectedProperties = new[] { "sessionKey", "message", "idempotencyKey", "deliver", "sessionId", "agentId" }
            .Concat(expectedMode is null ? [] : new[] { "queueMode" }).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedProperties, parameters.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).ToArray());
        Assert.False(parameters.TryGetProperty("expectedLeafEntryId", out _));
        Assert.False(send.IsCompleted);
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(text), ok = true,
            payload = new { status = "in_flight", runId = "input-1" },
        }));
        var acknowledgement = await send.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("in_flight", acknowledgement.Status);
        Assert.Equal("input-1", acknowledgement.RunId);
    }

    [Fact]
    public async Task ScopedChatSend_ExplicitInvalidParamsIsRejectedRatherThanUnconfirmedOrDowngraded()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-rejected-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var send = client.SendChatMessageForRunAsync("Keep rejected payload.", "agent:test:source",
            idempotencyKey: "rejected-input", expectedConnectionEpoch: client.SessionMutationConnectionEpoch,
            options: new(false, IsCurrent: () => true));
        var request = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(request), ok = false,
            error = new { code = "INVALID_PARAMS", message = "Unsupported input field." },
        }));
        var rejected = await Assert.ThrowsAsync<ChatSendRejectedException>(() => send);
        Assert.Equal("INVALID_PARAMS", rejected.Code);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Theory]
    [InlineData(51, 5, false)]
    [InlineData(1, 257, false)]
    [InlineData(2, 5, true)]
    public async Task InputReceiptQuery_RejectsOutOfContractIdsBeforeWriting(int count, int length, bool duplicates)
    {
        using var identity = new TempDirectory("chat-input-receipt-bounds-");
        var helper = new GatewayClientTestHelper(identityPath: identity.Path);
        using var client = helper.Client;
        typeof(OpenClawGatewayClient).GetField("_advertisedServerCapabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, new[] { ChatInputCustody.Capability });
        var ids = Enumerable.Range(0, count)
            .Select(index => duplicates ? "same-id" : index.ToString().PadLeft(length, 'x')).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestChatInputHistoryAsync(
            "agent:test:source", "source-session", ids, 0));
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public void InputHistory_PreservesExactReceiptsAndStructuredPendingPageBeyondDocumentLifetime()
    {
        ChatHistoryInfo history;
        using (var document = JsonDocument.Parse("""
        {
          "sessionId":"source-session",
          "inputReceipts":[
            {"runId":"pending-id","state":"pending","queued":true},
            {"runId":"consumed-id","state":"consumed","consumedByEventId":"persisted-event"}
          ],
          "pendingInputs":{
            "items":[{"id":"row-id","runId":"pending-id","message":{"text":"structured","attachments":[{"id":"attachment-id"}]},"acceptedAt":123,"state":"queued","queued":true}],
            "total":21,"queuedCount":20,"nextBefore":100
          }
        }
        """))
        {
            history = Assert.IsType<ChatHistoryInfo>(typeof(OpenClawGatewayClient)
                .GetMethod("ParseChatHistory", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [document.RootElement, "agent:test:source"]));
        }
        Assert.Collection(history.InputReceipts!,
            receipt => { Assert.Equal("pending-id", receipt.RunId); Assert.True(receipt.Queued); },
            receipt => { Assert.Equal("consumed", receipt.State); Assert.Equal("persisted-event", receipt.ConsumedByEventId); });
        var page = Assert.IsType<ChatPendingInputPage>(history.PendingInputs);
        Assert.Equal(21, page.Total);
        Assert.Equal(20, page.QueuedCount);
        Assert.Equal(100, page.NextBefore);
        Assert.True(Assert.Single(page.Items).Queued);
        Assert.Equal("attachment-id", Assert.Single(page.Items).Message.GetProperty("attachments")[0].GetProperty("id").GetString());
        using var compatibilityOnly = JsonDocument.Parse("""{"inputConsumptions":[{"runId":"old-id","consumedByEventId":"event"}]}""");
        var compatibility = Assert.IsType<ChatHistoryInfo>(typeof(OpenClawGatewayClient)
            .GetMethod("ParseChatHistory", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [compatibilityOnly.RootElement, "agent:test:source"]));
        Assert.Null(compatibility.InputReceipts);
    }

    [Fact]
    public async Task InputHistory_WireQueryIncludesExactIdsAndOrdinaryUnownedHistoryIncludesNone()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-history-wire-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        typeof(OpenClawGatewayClient).GetField("_advertisedServerCapabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, new[] { ChatInputCustody.Capability });
        var ids = new[] { "retained-input" }.Concat(Enumerable.Range(1, 48).Select(index => $"input-{index}"))
            .Append(new string('x', 256)).ToArray();
        var history = client.RequestChatInputHistoryAsync("agent:test:source", "source-incarnation",
            ids, client.SessionMutationConnectionEpoch!.Value, "test");
        var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        using (var frame = JsonDocument.Parse(query))
        {
            Assert.Equal("chat.history", frame.RootElement.GetProperty("method").GetString());
            var parameters = frame.RootElement.GetProperty("params");
            Assert.Equal("retained-input", parameters.GetProperty("inputRunIds")[0].GetString());
            Assert.Equal(50, parameters.GetProperty("inputRunIds").GetArrayLength());
            Assert.Equal(256, parameters.GetProperty("inputRunIds")[49].GetString()!.Length);
            Assert.Equal("source-incarnation", parameters.GetProperty("sessionId").GetString());
            Assert.Equal("test", parameters.GetProperty("agentId").GetString());
        }
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(query), ok = true,
            payload = new { sessionId = "source-incarnation", messages = Array.Empty<object>(),
                inputReceipts = new[] { new { runId = "retained-input", state = "consumed", consumedByEventId = "event-1" } } },
        }));
        Assert.Equal("event-1", Assert.Single((await history).InputReceipts!).ConsumedByEventId);
        var ordinary = client.RequestChatHistoryAsync("agent:test:source");
        query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        using (var frame = JsonDocument.Parse(query))
            Assert.False(frame.RootElement.GetProperty("params").TryGetProperty("inputRunIds", out _));
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(query), ok = true,
            payload = new { sessionId = "source-incarnation", messages = Array.Empty<object>() },
        }));
        await ordinary.WaitAsync(CloseHandshakeTimeout);
    }

    [Fact]
    public void InputHistory_MalformedCustodyIsDiagnosedWithoutDiscardingTranscript()
    {
        using var document = JsonDocument.Parse("""
        {"messages":[{"role":"user","content":"valid transcript","timestamp":123}],
         "inputReceipts":[{"runId":"bad","state":"consumed"}],
         "pendingInputs":{"items":[{"id":"bad","state":"interrupted"}],"total":1}}
        """);
        var history = Assert.IsType<ChatHistoryInfo>(typeof(OpenClawGatewayClient)
            .GetMethod("ParseChatHistory", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [document.RootElement, "agent:test:source"]));
        Assert.Equal("valid transcript", Assert.Single(history.Messages).Text);
        Assert.NotEmpty(history.InputCustodyError!);
        Assert.Empty(history.InputReceipts!);
        Assert.Empty(history.PendingInputs!.Items);
    }

    [Fact]
    public async Task ScopedChatSend_StateLockIsReleasedWhileItsQueuedWriteWaitsForOtherOriginCheck()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("chat-input-lock-order-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var gate = new object();
        using var stateHeld = new ManualResetEventSlim();
        using var beforeWrite = new ManualResetEventSlim();
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var first = Task.Run(async () =>
        {
            Task<ChatSendResult> queued;
            lock (gate)
            {
                stateHeld.Set();
                Assert.True(beforeWrite.Wait(CloseHandshakeTimeout));
                queued = client.SendChatMessageForRunAsync("queued while state is held", "agent:test:source",
                    idempotencyKey: "queued", expectedConnectionEpoch: epoch,
                    options: new(false, IsCurrent: () => { lock (gate) return true; }));
                Assert.False(queued.IsCompleted);
            }
            return await queued;
        });
        Assert.True(stateHeld.Wait(CloseHandshakeTimeout));
        var checks = 0;
        var second = client.SendChatMessageForRunAsync("socket owner", "agent:test:source",
            idempotencyKey: "socket-owner", expectedConnectionEpoch: epoch,
            options: new(false, IsCurrent: () =>
            {
                if (Interlocked.Increment(ref checks) == 1)
                    return true;
                beforeWrite.Set();
                lock (gate) return true;
            }));
        foreach (var expectedId in new[] { "socket-owner", "queued" })
        {
            var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
            using var frame = JsonDocument.Parse(query);
            Assert.Equal(expectedId, frame.RootElement.GetProperty("params").GetProperty("idempotencyKey").GetString());
            await server.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "res", id = ReadRequestId(query), ok = true, payload = new { status = "started" },
            }));
        }
        await Task.WhenAll(first, second).WaitAsync(CloseHandshakeTimeout);
    }

    [Fact]
    public async Task LegacyChatSend_PreWriteFalsePreservesExistingVirtualRejectionSemantics()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("legacy-chat-prewrite-");
        await server.StartAsync();
        using var client = new LegacyPreWriteRefusalClient(server.WebSocketUrl, identity.Path);
        await client.ConnectAsync();
        var generation = typeof(WebSocketClientBase).GetProperty("CurrentConnectionGeneration",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client);
        typeof(OpenClawGatewayClient).GetField("_handshakeConnectionGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, generation);
        typeof(OpenClawGatewayClient).GetField("_hasHandshakeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendChatMessageForRunAsync("retain caller input", "agent:test:source"));

        Assert.Equal(1, client.CapturedWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyChatSend_RealFrameDoesNotGainCustodyFieldsWithoutAdvertisement(bool scoped)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("legacy-chat-frame-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(gatewayUrl: server.WebSocketUrl, identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.MarkHandshakeReady();
        var send = client.SendChatMessageForRunAsync("legacy exact payload", "agent:test:source", "local-only-id",
            idempotencyKey: "legacy-input", options: scoped ? new(false, IsCurrent: () => true) : null);
        var query = await server.ReceiveTextAsync().WaitAsync(CloseHandshakeTimeout);
        using var frame = JsonDocument.Parse(query);
        var parameters = frame.RootElement.GetProperty("params");
        Assert.Equal(new[] { "idempotencyKey", "message", "sessionKey" },
            parameters.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("legacy exact payload", parameters.GetProperty("message").GetString());
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = ReadRequestId(query), ok = true, payload = new { status = "started" },
        }));
        await send.WaitAsync(CloseHandshakeTimeout);
    }
}
