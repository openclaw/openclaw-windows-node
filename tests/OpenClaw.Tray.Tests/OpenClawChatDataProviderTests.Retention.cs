using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenClaw.Connection;
using OpenClaw.TestSupport.Gateway;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public partial class OpenClawChatDataProviderTests
{
    [Fact]
    public async Task RetiredClientKeepsLoadedConversationReadOnlyAndRejectsLateHistory()
    {
        using var temp = new TempDirectory();
        const string research = "agent:research:retained";
        var first = new FakeBridge
        {
            IsConnected = true, CurrentStatus = ConnectionStatus.Connected,
            MainSessionKey = "main", HasHandshakeSnapshot = true,
            Sessions = [MainSession(), new SessionInfo { Key = research, DisplayName = "Research" }],
            HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key!, Messages = [new ChatMessageInfo { Role = "assistant", Text = "Retained research transcript.", Ts = 1 }]
            }),
        };
        using var connection = new RetainedChatGatewayBridge();
        connection.Attach(first);
        await using var provider = new OpenClawChatDataProvider(connection, null,
            Path.Combine(temp.DirectoryPath, "tools.json"), Path.Combine(temp.DirectoryPath, "attachments.json"),
            Path.Combine(temp.DirectoryPath, "last-chat.json"));
        await provider.LoadAsync();
        await provider.LoadHistoryAsync(research);
        provider.RememberSelectedThread(research);
        var before = await provider.LoadAsync();
        var pending = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.HistoryBehavior = _ => pending.Task;
        var late = provider.LoadHistoryAsync(research, force: true);

        connection.Attach(null);
        var offline = await provider.LoadAsync();
        Assert.Equal("Disconnected", offline.ConnectionStatus);
        Assert.False(offline.ComposeTarget.IsReady);
        Assert.Equal(research, offline.DefaultThreadId);
        Assert.Contains(offline.Threads, thread => thread.Id == research);
        Assert.Equal("Retained research transcript.", Assert.Single(offline.Timelines[research].Entries).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendChatMessageAsync("Offline send", research, null));
        Assert.Empty(first.SentMessages);
        Assert.True(first.IsDisposed);
        pending.SetResult(new ChatHistoryInfo
        {
            SessionKey = research, Messages = [new ChatMessageInfo { Role = "assistant", Text = "Obsolete reply", Ts = 2 }]
        });
        await late;
        Assert.Equal(before.Timelines[research].Entries, (await provider.LoadAsync()).Timelines[research].Entries);

        var second = new FakeBridge
        {
            IsConnected = true, CurrentStatus = ConnectionStatus.Connected,
            MainSessionKey = "main", HasHandshakeSnapshot = true, Sessions = first.Sessions,
        };
        connection.Attach(second);
        var recovered = await provider.LoadAsync();
        Assert.Equal("Connected", recovered.ConnectionStatus);
        Assert.Equal(research, recovered.DefaultThreadId);
        Assert.Equal(before.Timelines[research].Entries, recovered.Timelines[research].Entries);
        await connection.SendChatMessageAsync("Recovered send", research, null);
        Assert.Equal(research, Assert.Single(second.SentSessionKeys));
        Assert.Empty(first.SentMessages);
    }

    [Fact]
    public async Task ProviderLifetimeReusesOnlyFreshlyConfirmedIdentityAndDiscardsRevokedHistory()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(allowChatSend: true), token);
        var dispatch = new ConcurrentQueue<Action>();
        using var lifetime = new ChatProviderConnection(action => dispatch.Enqueue(action), bridge =>
            new OpenClawChatDataProvider(bridge, null, Path.Combine(temp.DirectoryPath, "tools.json"),
                Path.Combine(temp.DirectoryPath, "attachments.json"), Path.Combine(temp.DirectoryPath, "last-chat.json")));
        var registry = new GatewayRegistry(Path.Combine(temp.DirectoryPath, "profile"));
        var record = new GatewayRecord { Id = "retention", Url = server.Endpoint.AbsoluteUri };
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        lifetime.ConfigureRegistry(registry);
        using var first = Client("identity-a");
        lifetime.SetClient(first);
        await first.ConnectAsync();
        await PumpUntil(() => lifetime.Provider is not null);
        var retained = lifetime.Provider!;
        await retained.LoadAsync();
        await retained.LoadHistoryAsync(GatewayScenario.OtherSessionKey);
        retained.RememberSelectedThread(GatewayScenario.OtherSessionKey);
        var before = await retained.LoadAsync();
        Assert.Contains(before.Timelines[GatewayScenario.OtherSessionKey].Entries, entry => entry.Text.Contains(GatewayScenario.OtherSentinel));

        lifetime.SetClient(null);
        Assert.Same(retained, lifetime.Provider);
        var offline = await retained.LoadAsync();
        Assert.Equal("Disconnected", offline.ConnectionStatus);
        Assert.Equal(GatewayScenario.OtherSessionKey, offline.DefaultThreadId);
        Assert.False(offline.ComposeTarget.IsReady);
        await first.DisconnectAsync();

        using var second = Client("identity-a");
        lifetime.SetClient(second);
        await second.ConnectAsync();
        await PumpUntil(() => lifetime.Provider is IChatConnectionBoundProvider bound && bound.IsBoundTo(second));
        Assert.Same(retained, lifetime.Provider);
        var recovered = await retained.LoadAsync();
        Assert.Equal(GatewayScenario.OtherSessionKey, recovered.DefaultThreadId);
        Assert.Equal(before.Timelines[GatewayScenario.OtherSessionKey].Entries, recovered.Timelines[GatewayScenario.OtherSessionKey].Entries);
        await retained.SendMessageAsync(GatewayScenario.OtherSessionKey, "Send after explicit client replacement.");
        var sent = await server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey);
        Assert.Equal("ok", sent.Outcome);

        lifetime.SetClient(null);
        await second.DisconnectAsync();
        using var changedIdentity = Client("identity-b");
        lifetime.SetClient(changedIdentity);
        await changedIdentity.ConnectAsync();
        await PumpUntil(() => lifetime.Provider is IChatConnectionBoundProvider bound && bound.IsBoundTo(changedIdentity));
        Assert.NotSame(retained, lifetime.Provider);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.LoadAsync());
        var fresh = lifetime.Provider!;
        var freshSnapshot = await fresh.LoadAsync();
        Assert.DoesNotContain(freshSnapshot.Timelines.Values.SelectMany(timeline => timeline.Entries),
            entry => entry.Text.Contains(GatewayScenario.OtherSentinel));

        lifetime.ObserveConnection(new GatewayConnectionSnapshot { GatewayId = record.Id, OperatorErrorKind = GatewayErrorKind.Auth });
        await PumpUntil(() => lifetime.Provider is null);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fresh.LoadAsync());
        await changedIdentity.DisconnectAsync();

        OpenClawGatewayClient Client(string identity) => new(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: Path.Combine(temp.DirectoryPath, identity), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        Task PumpUntil(Func<bool> ready) => WaitForConditionAsync(() =>
        {
            while (dispatch.TryDequeue(out var action)) action();
            return ready();
        }, attempts: 500);
    }

    [Fact]
    public async Task AuthoritativeRemovalOrChangedSessionIdentityCannotReuseLoadedHistory()
    {
        using var temp = new TempDirectory();
        var (bridge, provider, _, _) = CreateProvider([MainSession()],
            toolMetaCachePath: Path.Combine(temp.DirectoryPath, "tools.json"),
            attachmentMetaCachePath: Path.Combine(temp.DirectoryPath, "attachments.json"),
            lastChatStatePath: Path.Combine(temp.DirectoryPath, "last-chat.json"));
        await using var ownedProvider = provider;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key!, SessionId = "session-a",
            Messages = [new ChatMessageInfo { Role = "assistant", Text = "Private retained text.", Ts = 1 }]
        });
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.True(provider.CanRetainHistory([new SessionInfo { Key = "main", SessionId = "session-a" }]));
        Assert.False(provider.CanRetainHistory([]));
        Assert.False(provider.CanRetainHistory([new SessionInfo { Key = "main", SessionId = "session-b" }]));
        Assert.False(provider.CanRetainHistory([new SessionInfo { Key = "main" }]));
    }

    [Fact]
    public async Task LiveTranscriptBeforeHistoryCompletesStillRequiresTheSameSessionIdentity()
    {
        using var temp = new TempDirectory();
        var session = MainSession();
        session.SessionId = "session-a";
        var (bridge, provider, _, _) = CreateProvider([session],
            toolMetaCachePath: Path.Combine(temp.DirectoryPath, "tools.json"),
            attachmentMetaCachePath: Path.Combine(temp.DirectoryPath, "attachments.json"),
            lastChatStatePath: Path.Combine(temp.DirectoryPath, "last-chat.json"));
        await using var ownedProvider = provider;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.LoadAsync();
        bridge.RaiseSessions([session]);
        var pending = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = _ => pending.Task;
        var loading = provider.LoadHistoryAsync("main");
        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "main", Role = "assistant", Text = "Live transcript before history.", Ts = 1 });
        var live = await provider.LoadAsync();
        Assert.False(live.Timelines["main"].HistoryLoaded);
        Assert.Contains(live.Timelines["main"].Entries, entry => entry.Text == "Live transcript before history.");
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        try
        {
            Assert.False(provider.CanRetainHistory([new SessionInfo { Key = "main", SessionId = "session-b" }]));
            Assert.False(provider.CanRetainHistory([new SessionInfo { Key = "main" }]));
            Assert.True(provider.CanRetainHistory([new SessionInfo { Key = "main", SessionId = "session-a" }]));
        }
        finally
        {
            pending.TrySetResult(new ChatHistoryInfo { SessionKey = "main", SessionId = "session-a" });
            await loading;
        }
    }

    [Fact]
    public void RetentionRequiresTheSameConfirmedEndpointDeviceAndProfile()
    {
        var original = new ChatConnectionAuthority("gateway-a", "endpoint-a", "device-a", "profile-a");
        Assert.True(original.CanRetain(original));
        Assert.False(original.CanRetain(null));
        Assert.False(original.CanRetain(original with { GatewayId = "gateway-b" }));
        Assert.False(original.CanRetain(original with { EndpointBinding = "endpoint-b" }));
        Assert.False(original.CanRetain(original with { SigningDeviceId = "device-b" }));
        Assert.False(original.CanRetain(original with { ProfileId = "profile-b" }));
        Assert.False(original.CanRetain(original with { ProfileId = "" }));
    }
}
