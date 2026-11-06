using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

[Collection("Chat telemetry")]
public sealed partial class ChatProviderConnectionSecurityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const int QueueTurnMs = 10;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationTimeoutRetainsReadableConversationWithBlockedIo(bool profileRead)
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        var before = await retained.LoadAsync();
        using var timeout = new EnvironmentScope("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", "150");
        h.Lifetime.SetClient(null);
        var firstRequest = h.Server.Requests.Count;
        var captured = profileRead ? Task.CompletedTask : h.Server.HoldNextSessionList();
        if (profileRead) h.Server.HoldUserProfiles();
        var held = true;
        try
        {
            h.Lifetime.SetClient(h.Client);
            await captured.WaitAsync(Deadline);
            var expiredRead = await h.WaitForAuthorityRequestAsync(profileRead ? "users.self" : "sessions.list", firstRequest);
            await h.PumpUntilAsync(() => h.Lifetime.VerificationState == ChatVerificationState.Failed);
            Assert.True(h.Client.IsConnectedToGateway);
            Assert.Same(retained, h.Lifetime.Provider);
            Assert.Equal(before.Timelines[GatewayScenario.OtherSessionKey].Entries,
                (await retained.LoadAsync()).Timelines[GatewayScenario.OtherSessionKey].Entries);
            Assert.False(h.IsBound);
            await Assert.ThrowsAsync<InvalidOperationException>(() => retained.SendMessageAsync(GatewayScenario.OtherSessionKey, "Must remain offline."));
            Assert.DoesNotContain(h.Server.Requests, request => request.Method == "chat.send");
            if (profileRead) h.Server.ReleaseUserProfiles(); else h.Server.ReleaseSessionList();
            held = false;
            // Failed acknowledges the old owner completion. Join that exact
            // released wire response before testing late delivery or starting retry.
            await h.JoinResponseAsync(expiredRead);
            Assert.False(h.IsBound);
            Assert.Equal(ChatVerificationState.Failed, h.Lifetime.VerificationState);
            timeout.Set("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", null);
            var retryStart = h.Server.Requests.Count;
            Assert.True(retained.RetryVerification());
            Assert.False(retained.RetryVerification());
            await h.WaitForAuthorityRequestAsync("users.self", retryStart);
            await h.PumpUntilAsync(() => h.IsBound);
            Assert.Same(retained, h.Lifetime.Provider);
            Assert.Equal(ChatVerificationState.None, retained.VerificationState);
            await retained.SendMessageAsync(GatewayScenario.OtherSessionKey, "Current explicit-retry control.");
            Assert.Equal("ok", (await h.Server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey).WaitAsync(Deadline)).Outcome);
            output.WriteLine($"verification-timeout method={(profileRead ? "users.self" : "sessions.list")}; automatic-rebind=false; explicit-retry=true; same-provider=true; current-send=ok");
        }
        finally { if (held) { if (profileRead) h.Server.ReleaseUserProfiles(); else h.Server.ReleaseSessionList(); } }
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("removed")]
    [InlineData("replaced")]
    [InlineData("missing-id")]
    public async Task ReconnectWaitsForItsAuthoritativeRosterDespiteLiveCachedActivity(string change)
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        var originalRows = h.Client.GetSessionList();
        var originalId = originalRows.Single(row => row.Key == GatewayScenario.OtherSessionKey).SessionId;
        Assert.False(string.IsNullOrWhiteSpace(originalId));
        var rows = originalRows.Where(row => change != "removed" || row.Key != GatewayScenario.OtherSessionKey)
            .Select(row => new
            {
                key = row.Key,
                sessionId = row.Key == GatewayScenario.OtherSessionKey ? change switch
                {
                    "replaced" => "fixture-replacement-session",
                    "missing-id" => null,
                    _ => row.SessionId,
                } : row.SessionId,
                displayName = row.DisplayName,
            }).ToArray();
        h.Server.SetSessionListing(JsonSerializer.SerializeToElement(new { sessions = rows }));
        var listCount = h.Server.Requests.Count(request => request.Method == "sessions.list");
        var profileCount = h.Server.Requests.Count(request => request.Method == "users.self");
        h.Server.HoldSessionListings();
        var held = true;
        try
        {
            await h.Server.CloseConnectionsAsync();
            await h.PumpUntilAsync(() => h.Server.Requests.Count(request => request.Method == "users.self" && request.Outcome == "ok") > profileCount &&
                h.Server.Requests.Count(request => request.Method == "sessions.list") >= listCount + 2);
            Assert.Equal(originalId, h.Client.GetSessionList().Single(row => row.Key == GatewayScenario.OtherSessionKey).SessionId);
            var activity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CachedActivity(object? sender, SessionInfo[] snapshot)
            {
                if (snapshot.Any(row => row.Key == GatewayScenario.OtherSessionKey && row.CurrentActivity is not null)) activity.TrySetResult();
            }
            h.Client.SessionsUpdated += CachedActivity;
            try
            {
                await h.Server.PublishToolActivityAsync(GatewayScenario.OtherSessionKey);
                await activity.Task.WaitAsync(Deadline);
                // Receive-loop acknowledgement follows the cached event. Drain
                // its already-posted UI work, not a fixed absence window.
                await h.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
                h.Drain();
                Assert.False(h.IsBound, "A live cached roster must not reactivate a suspended binding before its requested listing responds.");
                Assert.Same(retained, h.Lifetime.Provider);
            }
            finally { h.Client.SessionsUpdated -= CachedActivity; }
            h.Server.ReleaseSessionListings();
            held = false;
            await h.PumpUntilAsync(() => h.IsBound);
            if (change == "unchanged") Assert.Same(retained, h.Lifetime.Provider);
            else
            {
                Assert.NotSame(retained, h.Lifetime.Provider);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.LoadAsync());
            }
            output.WriteLine($"authoritative-roster change={change}; premature-reuse=false; current-binding-ready=true");
        }
        finally { if (held) h.Server.ReleaseSessionListings(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredProfileCannotWriteQueuedChatToTheSurvivingSocket(bool revoked)
    {
        await using var h = await Harness.StartAsync();
        var old = h.Lifetime.Provider!;
        var handshake = h.Client.ServerHandshakeGeneration;
        var censusStart = h.Server.Requests.Count;
        var sequence = 0;
        var eventStart = h.Server.Events.Count;
        void Trace(string stage)
        {
            var rows = h.Server.Requests.Skip(censusStart).ToArray();
            output.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, stage,
                invalidation = revoked ? "manager-auth-observation" : "wire-profile-identity",
                connected = h.Client.IsConnectedToGateway, handshake = h.Client.ServerHandshakeGeneration,
                bound = h.IsBound, chatSendCount = rows.Count(r => r.Method == "chat.send"),
                events = h.Server.Events.Skip(eventStart).ToArray(),
                requests = rows.Select((r, index) => new { ordinal = censusStart + index, r.Method, r.SessionKey, r.AuthorityScoped, r.Outcome }) }));
        }
        var sendLock = (SemaphoreSlim)typeof(WebSocketClientBase).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Client)!;
        await sendLock.WaitAsync();
        var held = true;
        try
        {
            var staleSend = old.SendMessageAsync(GatewayScenario.OtherSessionKey, "Fixture old-profile payload must never be written.");
            Assert.False(staleSend.IsCompleted);
            Trace("old-send-queued-send-lock-held");
            if (revoked)
                h.Lifetime.ObserveConnection(new GatewayConnectionSnapshot { GatewayId = h.Record.Id, OperatorErrorKind = GatewayErrorKind.Auth });
            else
            {
                var profileChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, EventArgs args) => profileChanged.TrySetResult();
                h.Client.SelfProfileChanged += Changed;
                try
                {
                    h.Server.SetUserProfile("fixture-reassigned-owner");
                    await h.Server.NotifyProfileIdentityChangedAsync();
                    await profileChanged.Task.WaitAsync(Deadline);
                }
                finally { h.Client.SelfProfileChanged -= Changed; }
            }
            h.Drain();
            Assert.Null(h.Lifetime.Provider);
            Trace("authority-invalidated-provider-retired-send-lock-held");
            var staleOutcome = "completed";
            try { await staleSend.WaitAsync(Deadline); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            { staleOutcome = ex.GetType().Name; }
            output.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, stage = "old-send-task-settled-before-release", outcome = staleOutcome }));
            sendLock.Release();
            held = false;
            Trace("serialized-send-lock-released");
            // A subsequent response is a wire barrier behind the queued request.
            await h.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
            Trace("health-response-acknowledged-after-settled-old-send");
            var staleWrites = h.Server.Requests.Count(request => request.Method == "chat.send");
            Assert.Equal(0, staleWrites);
            Assert.True(h.Client.IsConnectedToGateway);
            Assert.Equal(handshake, h.Client.ServerHandshakeGeneration);
            if (revoked)
            {
                h.Server.SetUserProfile("fixture-reauthorized-owner");
                await h.Server.NotifyProfileIdentityChangedAsync();
            }
            await h.PumpUntilAsync(() => h.IsBound);
            Assert.NotSame(old, h.Lifetime.Provider);
            output.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, stage = "fresh-authority-verified",
                sessionKey = GatewayScenario.OtherSessionKey,
                sessionId = h.Client.GetSessionList().Single(r => r.Key == GatewayScenario.OtherSessionKey).SessionId }));
            await h.Lifetime.Provider!.SendMessageAsync(GatewayScenario.OtherSessionKey, "Fixture current-binding control.");
            await h.Server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey).WaitAsync(Deadline);
            await h.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
            Trace("authorized-chat-send-acknowledged-final-health-barrier");
            var allowed = Assert.Single(h.Server.Requests, request => request.Method == "chat.send");
            Assert.Equal(GatewayScenario.OtherSessionKey, allowed.SessionKey);
            Assert.Equal("ok", allowed.Outcome);
            output.WriteLine($"final-effect invalidation={(revoked ? "manager-auth-observation" : "wire-profile-identity")}; stale-chat-writes={staleWrites}; current-chat-writes={h.Server.Requests.Count(r => r.Method == "chat.send")}; unchanged-socket-handshake={handshake}");
        }
        finally { if (held) sendLock.Release(); }
    }

    [Fact]
    public async Task RetiredRosterReplyCannotReplaceTheNewProfileCache()
    {
        await using var h = await Harness.StartAsync();
        await h.PumpUntilAsync(() => h.Server.Requests.Any(request => request.Method == "agents.list" && request.Outcome == "ok"));
        var original = h.Client.GetSessionList();
        h.Server.SetSessionListing(JsonSerializer.SerializeToElement(new { sessions = original.Select(row => new
        {
            key = row.Key, sessionId = row.SessionId, displayName = row.DisplayName,
        }).ToArray() }));
        var held = h.Server.HoldNextSessionList();
        h.Lifetime.SetClient(null);
        h.Lifetime.SetClient(h.Client);
        await held.WaitAsync(Deadline);
        var retiredIndex = h.Server.Requests.Select((request, index) => (request, index))
            .Single(item => item.request.Method == "sessions.list" && item.request.Outcome == "pending").index;
        var released = false;
        try
        {
            h.Server.SetSessionListing(JsonSerializer.SerializeToElement(new { sessions = original.Select(row => new
            {
                key = row.Key, sessionId = row.Key == GatewayScenario.OtherSessionKey ? "fixture-new-profile-session" : row.SessionId,
                displayName = row.DisplayName,
            }).ToArray() }));
            h.Server.SetUserProfile("fixture-profile-after-held-roster");
            await h.Server.NotifyProfileIdentityChangedAsync();
            await h.PumpUntilAsync(() => h.IsBound && h.Client.GetSessionList().Any(row => row.SessionId == "fixture-new-profile-session"));
            var stalePublished = 0;
            void ObserveRoster(object? sender, SessionInfo[] rows)
            {
                if (rows.Any(row => row.Key == GatewayScenario.OtherSessionKey && row.SessionId != "fixture-new-profile-session")) Interlocked.Exchange(ref stalePublished, 1);
            }
            h.Client.SessionsUpdated += ObserveRoster;
            try
            {
                h.Server.ReleaseSessionList();
                released = true;
                await h.PumpUntilAsync(() => h.Server.Requests[retiredIndex].Outcome == "ok");
                await h.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
                Assert.False(Volatile.Read(ref stalePublished) != 0, "The retired roster must never publish into the new profile, even if a newer bootstrap later overwrites it.");
                Assert.Equal("fixture-new-profile-session", h.Client.GetSessionList().Single(row => row.Key == GatewayScenario.OtherSessionKey).SessionId);
            }
            finally { h.Client.SessionsUpdated -= ObserveRoster; }
            output.WriteLine("late-retired-roster ignored; current-profile cache identity preserved");
        }
        finally { if (!released) h.Server.ReleaseSessionList(); }
    }

    [Fact]
    public async Task QueuedVerificationCannotReattachAfterWireProfileInvalidation()
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        var reusedAfterInvalidation = false;
        h.Lifetime.Changed += (_, _) =>
        {
            if (h.IsBound && ReferenceEquals(retained, h.Lifetime.Provider)) reusedAfterInvalidation = true;
        };
        h.Lifetime.SetClient(null);
        var first = h.Server.Requests.Count;
        var captured = h.Server.HoldNextSessionList();
        var held = true;
        try
        {
            h.Lifetime.SetClient(h.Client);
            await captured.WaitAsync(Deadline);
            var roster = await h.WaitForAuthorityRequestAsync("sessions.list", first);
            await h.ReleaseAndQueueVerificationCommitAsync(roster, h.Server.ReleaseSessionList);
            held = false;
            // The exact verification's final UI commit is now queued. Invalidate
            // before dispatching it, rather than guessing which two callbacks ran.
            await h.ChangeProfileAsync("fixture-after-queued-verification");
            h.Drain();
            Assert.False(reusedAfterInvalidation, "A queued UI commit must not revive the profile invalidated by the wire event.");
            await h.PumpUntilAsync(() => h.IsBound);
            Assert.NotSame(retained, h.Lifetime.Provider);
        }
        finally { if (held) h.Server.ReleaseSessionList(); }
    }

    [Theory]
    [InlineData("{} ")]
    [InlineData("{\"profile\":{}}")]
    [InlineData("{\"profile\":{\"id\":\"\"}}")]
    [InlineData("{\"profile\":{\"id\":\"   \"}}")]
    [InlineData("{\"profile\":{\"id\":42}}")]
    public async Task MalformedProfileReadCannotUseTheOlderGatewayFallback(string payload)
    {
        await using var h = await Harness.StartAsync();
        var count = h.Server.Requests.Count(request => request.Method == "users.self");
        h.Server.SetUserProfilePayload(JsonSerializer.Deserialize<JsonElement>(payload));
        await h.Server.NotifyProfileIdentityChangedAsync();
        await h.PumpUntilAsync(() => h.Server.Requests.Count(request => request.Method == "users.self" && request.Outcome == "ok") > count);
        await h.PumpUntilAsync(() => h.IsBound || h.Lifetime.VerificationState == ChatVerificationState.Failed);
        Assert.Equal(ChatVerificationState.Failed, h.Lifetime.VerificationState);
        Assert.Null(h.Lifetime.Provider);
        Assert.True(h.Client.IsConnectedToGateway);
    }

    [Fact]
    public async Task RejectedProfileReadCannotUseTheOlderGatewayFallback()
    {
        await using var h = await Harness.StartAsync();
        var count = h.Server.Requests.Count(request => request.Method == "users.self");
        h.Server.RejectUserProfileAsUnauthorized();
        await h.Server.NotifyProfileIdentityChangedAsync();
        await h.PumpUntilAsync(() => h.Server.Requests.Count(request => request.Method == "users.self" && request.Outcome != "pending") > count);
        await h.PumpUntilAsync(() => h.IsBound || h.Lifetime.VerificationState == ChatVerificationState.Failed);
        Assert.Equal(ChatVerificationState.Failed, h.Lifetime.VerificationState);
        Assert.Null(h.Lifetime.Provider);
        Assert.True(h.Client.IsConnectedToGateway);
    }

    [Fact]
    public async Task UnsupportedProfileMethodMountsFreshChatButCannotReuseOldHistory()
    {
        await using var h = await Harness.StartAsync(legacy: true);
        var first = h.Lifetime.Provider!;
        Assert.True((await first.LoadAsync()).Timelines[GatewayScenario.OtherSessionKey].Entries.Count > 0);
        h.Lifetime.SetClient(null);
        Assert.Null(h.Lifetime.Provider);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.LoadAsync());
        h.Lifetime.SetClient(h.Client);
        await h.PumpUntilAsync(() => h.IsBound);
        Assert.NotSame(first, h.Lifetime.Provider);
        await h.Lifetime.Provider!.LoadHistoryAsync(GatewayScenario.OtherSessionKey);
        await h.Lifetime.Provider.SendMessageAsync(GatewayScenario.OtherSessionKey, "Fixture legacy fresh-chat control.");
        var sent = await h.Server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey).WaitAsync(Deadline);
        Assert.Equal("ok", sent.Outcome);
    }

    [Fact]
    public async Task LatestStableStoredProfileAndSigningIdentitySurviveCurrentReconnect()
    {
        using var temp = new TempDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Stable2026_9_8_1");
        var registryPath = temp.Combine("gateways.json");
        File.Copy(Path.Combine(fixture, "gateways.json"), registryPath);
        var registry = new GatewayRegistry(temp.Path);
        registry.Load();
        var record = Assert.IsType<GatewayRecord>(registry.GetActive());
        Assert.Equal("stable-synthetic-profile", record.Id);
        Assert.Equal("fixture-stable-shared-token", record.SharedGatewayToken);
        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(identityDirectory);
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        File.Copy(Path.Combine(fixture, "device-key-ed25519.json"), identityPath);
        var registryBefore = File.ReadAllBytes(registryPath);
        var identityBefore = File.ReadAllBytes(identityPath);
        using var key = JsonDocument.Parse(identityBefore);
        var expectedId = key.RootElement.GetProperty("DeviceId").GetString();
        var publicKey = Convert.FromBase64String(key.RootElement.GetProperty("PublicKeyBase64").GetString()!);
        var identity = new DeviceIdentity(identityDirectory, NullLogger.Instance);
        identity.LoadExisting();
        Assert.Equal(expectedId, identity.DeviceId);
        Assert.Equal("fixture-stable-operator-token", identity.DeviceToken);
        Assert.Equal("fixture-stable-node-token", identity.NodeDeviceToken);
        const string nonce = "fixture-stable-nonce";
        const long signedAt = 1_792_000_000_000;
        var signature = DecodeUrl(identity.SignConnectPayloadV3(nonce, signedAt, "fixture-client", "ui", "operator",
            ["operator.read", "operator.write"], identity.DeviceToken!, "windows", "desktop"));
        var canonical = Encoding.UTF8.GetBytes($"v3|{expectedId}|fixture-client|ui|operator|operator.read,operator.write|{signedAt}|fixture-stable-operator-token|{nonce}|windows|desktop");
        Assert.True(Ed25519.Verify(signature, 0, publicKey, 0, canonical, 0, canonical.Length));

        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: true), "fixture-not-the-stored-shared-token");
        server.AcceptedDeviceToken = "fixture-stable-operator-token";
        // Repoint only the in-memory route to an owned ephemeral server. The
        // released stored bytes, token precedence and signing identity stay intact.
        registry.AddOrUpdate(record with { Url = server.Endpoint.AbsoluteUri });
        var queue = new ConcurrentQueue<Action>();
        using var lifetime = new ChatProviderConnection(action => queue.Enqueue(action), bridge =>
            new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"), temp.Combine("attachments.json"), temp.Combine("last-chat.json")));
        lifetime.ConfigureRegistry(registry);
        using var first = Client();
        lifetime.SetClient(first);
        await first.ConnectAsync();
        await PumpUntil(() => Bound(first));
        var retained = lifetime.Provider!;
        await retained.LoadAsync();
        await retained.LoadHistoryAsync(GatewayScenario.OtherSessionKey);
        retained.RememberSelectedThread(GatewayScenario.OtherSessionKey);
        var before = await retained.LoadAsync();
        lifetime.SetClient(null);
        await first.DisconnectAsync();
        using var second = Client();
        lifetime.SetClient(second);
        await second.ConnectAsync();
        await PumpUntil(() => Bound(second));
        Assert.Same(retained, lifetime.Provider);
        Assert.Equal(expectedId, second.AuthenticatedSigningDeviceId);
        Assert.Equal(before.Timelines[GatewayScenario.OtherSessionKey].Entries,
            (await retained.LoadAsync()).Timelines[GatewayScenario.OtherSessionKey].Entries);
        await retained.SendMessageAsync(GatewayScenario.OtherSessionKey, "Fixture stable-profile current send.");
        Assert.Equal("ok", (await server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey).WaitAsync(Deadline)).Outcome);
        Assert.Equal(0, server.SharedCredentialConnects);
        Assert.Equal(0, server.DeviceTokenRejections);
        Assert.Equal(registryBefore, File.ReadAllBytes(registryPath));
        Assert.Equal(identityBefore, File.ReadAllBytes(identityPath));
        output.WriteLine("stable-data producer=v2026.9.8-1; stored-key-signature=valid; identity-regenerated=false; shared-fallbacks=0; retained-reconnect=true");
        await second.DisconnectAsync();

        OpenClawGatewayClient Client() => new(server.Endpoint.AbsoluteUri, record.SharedGatewayToken!, NullLogger.Instance,
            identityPath: identityDirectory, persistHandshakeDeviceTokens: false, requireExistingIdentity: true);
        bool Bound(OpenClawGatewayClient client) => lifetime.Provider is IChatConnectionBoundProvider binding && binding.IsBoundTo(client);
        async Task PumpUntil(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                while (queue.TryDequeue(out var action)) action();
                if (ready()) return;
                await Task.Delay(QueueTurnMs, deadline.Token);
            }
        }
        static byte[] DecodeUrl(string encoded) => Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '='));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory _temp;
        private readonly ConcurrentQueue<Action> _queue = new();
        public FixtureGatewayServer Server { get; }
        public OpenClawGatewayClient Client { get; }
        public ChatProviderConnection Lifetime { get; }
        public GatewayRecord Record { get; }
        public GatewayRegistry Registry { get; }
        public bool IsBound => Lifetime.Provider is IChatConnectionBoundProvider bound && bound.IsBoundTo(Client);
        private Harness(TempDirectory temp, FixtureGatewayServer server, string token)
        {
            _temp = temp;
            Server = server;
            Client = new(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
                identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
            Lifetime = new(action => _queue.Enqueue(action), bridge => new OpenClawChatDataProvider(bridge, null,
                temp.Combine("tools.json"), temp.Combine("attachments.json"), temp.Combine("last-chat.json")));
            var registry = Registry = new GatewayRegistry(temp.Combine("profile"));
            Record = new GatewayRecord { Id = "fixture-security", Url = server.Endpoint.AbsoluteUri };
            registry.AddOrUpdate(Record);
            registry.SetActive(Record.Id);
            Lifetime.ConfigureRegistry(registry);
        }
        public static async Task<Harness> StartAsync(bool legacy = false)
        {
            var temp = new TempDirectory();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: true), token);
            if (legacy) server.RejectUserProfileAsUnsupported();
            var h = new Harness(temp, server, token);
            try
            {
                h.Lifetime.SetClient(h.Client);
                await h.Client.ConnectAsync();
                await h.PumpUntilAsync(() => h.IsBound);
                await h.Lifetime.Provider!.LoadAsync();
                await h.Lifetime.Provider.LoadHistoryAsync(GatewayScenario.OtherSessionKey);
                h.Lifetime.Provider.RememberSelectedThread(GatewayScenario.OtherSessionKey);
                await h.PumpUntilAsync(() => h.Server.Requests.Any(r => r.Method == "agents.list" && r.Outcome == "ok"));
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
        public async Task WaitForQueuedActionAsync(int minimum = 1)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (_queue.Count < minimum) await Task.Delay(QueueTurnMs, deadline.Token);
        }
        public async Task PumpUntilAsync(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                Drain();
                if (condition()) return;
                await Task.Delay(QueueTurnMs, deadline.Token);
            }
        }
        public async Task<int> WaitForAuthorityRequestAsync(string method, int first)
        {
            await WaitWithoutDispatchAsync(() => Server.Requests.Skip(first).Any(r => r.Method == method && r.AuthorityScoped));
            return Server.Requests.Select((request, index) => (request, index))
                .First(pair => pair.index >= first && pair.request.Method == method && pair.request.AuthorityScoped).index;
        }
        public async Task JoinResponseAsync(int index, bool dispatch = true)
        {
            // Success is recorded after the response write. Only then can the
            // subsequent health reply order the receive-loop parser behind it.
            await WaitWithoutDispatchAsync(() => Server.Requests[index].Outcome != "pending");
            Assert.Equal("ok", Server.Requests[index].Outcome);
            await Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
            if (dispatch) Drain();
        }
        public async Task ReleaseAndQueueVerificationCommitAsync(int roster, Action release)
        {
            Drain(); // The profile precheck was posted before this roster was sent.
            Assert.Empty(_queue);
            release();
            await JoinResponseAsync(roster, dispatch: false);
            // A roster response's owner continuation must actually have posted its
            // final commit. Keep it queued so the test can retire its authority.
            await WaitForQueuedActionAsync();
        }
        public async Task ChangeProfileAsync(string profile)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, EventArgs args) => observed.TrySetResult();
            Client.SelfProfileChanged += Changed;
            try
            {
                Server.SetUserProfile(profile);
                await Server.NotifyProfileIdentityChangedAsync();
                await observed.Task.WaitAsync(Deadline);
            }
            finally { Client.SelfProfileChanged -= Changed; }
        }
        private static async Task WaitWithoutDispatchAsync(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (!condition()) await Task.Delay(QueueTurnMs, deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Lifetime.Dispose();
            Client.Dispose();
            await Server.DisposeAsync();
            _temp.Dispose();
        }
    }
}
