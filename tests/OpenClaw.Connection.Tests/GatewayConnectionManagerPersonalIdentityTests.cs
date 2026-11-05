using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayConnectionManagerPersonalIdentityTests
{
    [Fact]
    public async Task PersonalConnectionPublishesOnlyAfterCurrentSocketSelfVerificationAndNeverUsesSavedToken()
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        var self = await peer.ReceiveAsync();
        Assert.Equal("users.self", self.GetProperty("method").GetString());
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        Assert.NotEqual(RoleConnectionState.Connected, harness.Manager.CurrentSnapshot.OperatorState);
        await peer.SelfAsync(self);
        await harness.WaitAsync(RoleConnectionState.Connected);
        var lease = Assert.IsType<PersonalOperatorLease>(harness.Manager.CurrentPersonalOperatorLease);
        Assert.Equal("person-42", lease.IdentityProfileId);
        Assert.Equal("fixture-scope", lease.RecoveryScope);
        var signing = harness.Manager.CreatePersonalBrowserAuthResponseAsync("browser-nonce", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var refresh = await peer.ReceiveAsync();
        Assert.Equal("users.self", refresh.GetProperty("method").GetString());
        await peer.SelfAsync(refresh);
        using var response = JsonDocument.Parse(Assert.IsType<string>(await signing));
        Assert.Empty(response.RootElement.GetProperty("auth").EnumerateObject());
        Assert.Equal("tailscale", response.RootElement.GetProperty("requiredAuthMethod").GetString());
        Assert.Equal(lease.RecoveryScope, response.RootElement.GetProperty("expectedRecoveryScope").GetString());
        Assert.Equal(lease.DeviceId, response.RootElement.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal("saved-shared", harness.Registry.GetById("personal")!.SharedGatewayToken);
        await harness.Manager.DisconnectAsync();
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gateway-owner")]
    public async Task MissingOrSharedOwnerSelfProfileRejectsPersonalConnection(string? profile)
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        var self = await peer.ReceiveAsync();
        await peer.SelfAsync(self, profile);
        await harness.WaitAsync(RoleConnectionState.Error);
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        Assert.Null(harness.Manager.CurrentSnapshot.PersonalIdentityProfileId);
    }

    [Theory]
    [InlineData("gateway-owner")]
    [InlineData("different-person")]
    public async Task ProfileInvalidationWithholdsOldLeaseAndRejectsChangedPerson(string profile)
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        await peer.SelfAsync(await peer.ReceiveAsync());
        await harness.WaitAsync(RoleConnectionState.Connected);
        await peer.ProfileChangedAsync();
        var self = await peer.ReceiveAsync();
        Assert.Equal("users.self", self.GetProperty("method").GetString());
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        await peer.SelfAsync(self, profile);
        await harness.WaitAsync(RoleConnectionState.Error);
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        Assert.Null(harness.Manager.CurrentSnapshot.PersonalIdentityProfileId);
    }

    [Fact]
    public async Task ProfileInvalidationReverifiesSamePersonBeforeReplacingLease()
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        await peer.SelfAsync(await peer.ReceiveAsync());
        await harness.WaitAsync(RoleConnectionState.Connected);
        var previous = harness.Manager.CurrentPersonalOperatorLease;
        var renewed = new TaskCompletionSource<PersonalOperatorLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Manager.StateChanged += (_, _) =>
        {
            if (harness.Manager.CurrentPersonalOperatorLease is { } lease && !ReferenceEquals(lease, previous))
                renewed.TrySetResult(lease);
        };
        await peer.ProfileChangedAsync();
        var self = await peer.ReceiveAsync();
        Assert.Equal("users.self", self.GetProperty("method").GetString());
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        await peer.SelfAsync(self);
        Assert.Equal("person-42", (await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IdentityProfileId);
    }

    [Fact]
    public async Task PendingIdentityReadDoesNotBlockDisconnectOrPublishAnOldLease()
    {
        await using var harness = new Harness();
        var first = await harness.HelloAsync();
        Assert.Equal("users.self", (await first.ReceiveAsync()).GetProperty("method").GetString());
        await harness.Manager.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        var second = await harness.HelloAsync();
        var self = await second.ReceiveAsync();
        await second.SelfAsync(self, "replacement-person");
        await harness.WaitAsync(RoleConnectionState.Connected);
        Assert.Equal("replacement-person", harness.Manager.CurrentPersonalOperatorLease!.IdentityProfileId);
    }

    [Fact]
    public async Task BrowserProfileChangeRetiresOnlyItsCurrentPersonalConnection()
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        await peer.SelfAsync(await peer.ReceiveAsync());
        await harness.WaitAsync(RoleConnectionState.Connected);
        var signing = harness.Manager.CreatePersonalBrowserAuthResponseAsync("browser-nonce", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await peer.SelfAsync(await peer.ReceiveAsync(), "different-person");
        Assert.Null(await signing);
        Assert.Null(harness.Manager.CurrentPersonalOperatorLease);
        Assert.NotEqual(RoleConnectionState.Connected, harness.Manager.CurrentSnapshot.OperatorState);
    }

    [Fact]
    public async Task InvalidBrowserChallengesNeverProducePersonalProof()
    {
        await using var harness = new Harness();
        var peer = await harness.HelloAsync();
        await peer.SelfAsync(await peer.ReceiveAsync());
        await harness.WaitAsync(RoleConnectionState.Connected);
        foreach (var nonce in new[] { "", "a|b", "a\n", "\uFEFFnonce", new string('é', 257) })
            Assert.Null(await harness.Manager.CreatePersonalBrowserAuthResponseAsync(nonce, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.Null(await harness.Manager.CreatePersonalBrowserAuthResponseAsync("expired", 1));
        Assert.NotNull(harness.Manager.CurrentPersonalOperatorLease);
    }

    [Fact]
    public async Task RetiredBrowserReadCannotRevokeReplacementLease()
    {
        await using var harness = new Harness();
        var first = await harness.HelloAsync();
        await first.SelfAsync(await first.ReceiveAsync());
        await harness.WaitAsync(RoleConnectionState.Connected);
        var oldLease = harness.Manager.CurrentPersonalOperatorLease;
        var signing = harness.Manager.CreatePersonalBrowserAuthResponseAsync("browser-nonce", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal("users.self", (await first.ReceiveAsync()).GetProperty("method").GetString());
        await harness.Manager.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = await harness.HelloAsync();
        await replacement.SelfAsync(await replacement.ReceiveAsync(), "replacement-person");
        await harness.WaitAsync(RoleConnectionState.Connected);
        var currentLease = harness.Manager.CurrentPersonalOperatorLease;
        Assert.Null(await signing);
        Assert.NotSame(oldLease, currentLease);
        Assert.Same(currentLease, harness.Manager.CurrentPersonalOperatorLease);
        Assert.Equal("replacement-person", currentLease!.IdentityProfileId);
    }

    private sealed class Harness : IAsyncDisposable, IGatewayClientFactory
    {
        private readonly TempDirectory _directory = new("personal-manager-wire-");
        private readonly List<PersonalGatewayProtocolFixture> _peers = [];
        public GatewayRegistry Registry { get; }
        public GatewayConnectionManager Manager { get; }

        public Harness()
        {
            Registry = new GatewayRegistry(_directory.Path);
            Registry.AddOrUpdate(new GatewayRecord { Id = "personal", Url = PersonalGatewayProtocolFixture.Route,
                OperatorAuthentication = OperatorAuthenticationMode.PersonalTailscale,
                SharedGatewayToken = "saved-shared", BootstrapToken = "saved-bootstrap" });
            Registry.SetActive("personal");
            Manager = new GatewayConnectionManager(new CredentialResolver(new NoOperatorTokenReads()), this, Registry, NullLogger.Instance);
        }

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            Assert.Empty(credential.Token);
            Assert.Equal(OperatorAuthenticationMode.PersonalTailscale, credential.OperatorAuthentication);
            var peer = new PersonalGatewayProtocolFixture();
            _peers.Add(peer);
            return new Lifecycle(peer.CreateClient(identityPath));
        }

        public async Task<PersonalGatewayProtocolFixture> HelloAsync()
        {
            await Manager.ConnectAsync("personal");
            var peer = _peers[^1];
            await peer.ChallengeAsync();
            var connect = await peer.ReceiveAsync();
            Assert.Equal("connect", connect.GetProperty("method").GetString());
            Assert.Empty(connect.GetProperty("params").GetProperty("auth").EnumerateObject());
            await peer.HelloAsync(connect);
            return peer;
        }

        public async Task WaitAsync(RoleConnectionState state)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnState(object? sender, GatewayConnectionSnapshot snapshot)
            {
                if (snapshot.OperatorState == state) changed.TrySetResult();
            }
            Manager.StateChanged += OnState;
            try
            {
                if (Manager.CurrentSnapshot.OperatorState == state) return;
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { Manager.StateChanged -= OnState; }
        }

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            foreach (var peer in _peers) await peer.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class NoOperatorTokenReads : IDeviceIdentityReader
    {
        public string? TryReadStoredDeviceToken(string dataPath) => throw new InvalidOperationException("Personal mode read a saved operator token.");
        public string? TryReadStoredNodeDeviceToken(string dataPath) => null;
    }

    private sealed class Lifecycle(OpenClawGatewayClient client) : IGatewayClientLifecycle
    {
        public OpenClawGatewayClient DataClient => client;
        public event EventHandler<ConnectionStatus>? StatusChanged
        {
            add => client.StatusChanged += value;
            remove => client.StatusChanged -= value;
        }
        public event EventHandler<string>? AuthenticationFailed
        {
            add => client.AuthenticationFailed += value;
            remove => client.AuthenticationFailed -= value;
        }
        public Task ConnectAsync(CancellationToken ct) => client.ConnectAsync();
        public void Dispose() => client.Dispose();
    }
}
