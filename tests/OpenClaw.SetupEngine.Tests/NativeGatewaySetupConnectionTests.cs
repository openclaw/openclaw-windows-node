using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewaySetupConnectionTests
{
    private const string Token = "native-loopback-fixture-token-not-a-production-credential";
    private const string Model = "fixture/native-model";
    private const string StaleDeviceToken = "stale-operator-device-token-not-a-production-credential";

    /// <summary>
    /// A gateway that no longer honours the stored operator device token must not wedge
    /// onboarding: the rejected credential is retired and the shared setup token reconnects.
    /// </summary>
    [Fact]
    public async Task RejectedOperatorDeviceToken_IsRetiredSoSetupReconnectsWithTheSharedCredential()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(owner.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken(StaleDeviceToken);
        Assert.Equal(StaleDeviceToken, DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));

        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);

        Assert.True(connection.IsConnected);
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));
        var reloaded = new DeviceIdentity(owner.IdentityDirectory);
        reloaded.Initialize();
        Assert.Equal(identity.DeviceId, reloaded.DeviceId);
    }

    /// <summary>
    /// A device token the gateway still honours must survive the connect unchanged.
    /// </summary>
    [Fact]
    public async Task AcceptedOperatorDeviceToken_IsPreservedAcrossSetupConnect()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        server.AcceptedDeviceToken = StaleDeviceToken;
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(owner.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken(StaleDeviceToken);

        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);

        Assert.True(connection.IsConnected);
        Assert.Equal(StaleDeviceToken, DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));
    }

    /// <summary>
    /// Recovery must fail closed when the listener is no longer the verified managed Gateway.
    /// Driven through the production connect path: the gateway rejects the stored token, the
    /// endpoint's ownership changes before recovery re-verifies it, and nothing is retired,
    /// disclosed, or paired on the word of a process whose ownership cannot be proven.
    /// </summary>
    [Fact]
    public async Task Recovery_IsRefusedThroughConnectWhenGatewayOwnershipFailsVerification()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(owner.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken(StaleDeviceToken);

        // Ownership changes only once the gateway has rejected the stored token, so the
        // staged authorization still succeeds and recovery is genuinely reached.
        server.DeviceTokenRejected = () =>
            fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NativeGatewaySetupConnection.ConnectAsync(owner));

        Assert.Contains("ownership could not be verified", error.Message);
        Assert.Equal(1, server.DeviceTokenRejections);
        Assert.Equal(StaleDeviceToken, DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));
        Assert.Equal(0, server.SharedCredentialConnects);
        Assert.Null(fixture.Host.ApprovedId);
    }

    /// <summary>
    /// Recovery must fail closed when the isolated setup credential or port drifted, since the
    /// shared token it would fall back to is no longer the one this session was staged against.
    /// Driven through the production connect path.
    /// </summary>
    [Fact]
    public async Task Recovery_IsRefusedThroughConnectWhenSetupCredentialChanged()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(owner.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken(StaleDeviceToken);

        server.DeviceTokenRejected = () => fixture.Host.IsolatedConfiguration =
            new(server.Endpoint.Port, "rotated-setup-token-not-a-production-credential");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NativeGatewaySetupConnection.ConnectAsync(owner));

        Assert.Contains("port or token changed", error.Message);
        Assert.Equal(1, server.DeviceTokenRejections);
        Assert.Equal(StaleDeviceToken, DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));
        Assert.Equal(0, server.SharedCredentialConnects);
        Assert.Null(fixture.Host.ApprovedId);
    }

    /// <summary>
    /// Recovery is budgeted once per staged session so a gateway that keeps rejecting cannot
    /// drive repeated credential deletion.
    /// </summary>
    [Fact]
    public async Task Recovery_IsBudgetedOncePerStagedSession()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(owner.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken(StaleDeviceToken);

        Assert.True(await owner.RecoverRejectedOperatorTokenAsync(StaleDeviceToken, default));
        identity.StoreDeviceToken(StaleDeviceToken);

        Assert.False(await owner.RecoverRejectedOperatorTokenAsync(StaleDeviceToken, default));
        Assert.Equal(StaleDeviceToken, DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationRequest_DrainsDispatchedRpcWhenCallerCancels(bool staged)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        object Respond(string method, JsonElement parameters)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The mutation test did not release its fixture response.");
            return Reply(method, parameters);
        }
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Respond), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        IGatewayAiSetupTransport transport = staged ? connection :
            new GatewayAiSetupTransport(connection.Client, () => connection.Route, owner.AuthorizeAsync);
        using var cancellation = new CancellationTokenSource();
        var request = transport.RequestMutationAsync("openclaw.setup.activate.start",
            new { sessionId = "mutation-drain-test" }, 15_000, cancellation.Token);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            cancellation.Cancel();
            await Task.Delay(50);
            Assert.False(request.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await request).GetProperty("done").GetBoolean());
    }

    private static object Reply(string method, JsonElement parameters) => method switch
    {
        "openclaw.setup.detect" => new
        {
            candidates = new[] { new { kind = "existing-model", label = "Fixture", detail = "Synthetic", modelRef = Model, recommended = true } },
            manualProviders = Array.Empty<object>(), workspace = "fixture-workspace", setupComplete = true, configuredModel = Model,
        },
        "openclaw.setup.verify" => new { ok = true, modelRef = Model, latencyMs = 1 },
        "openclaw.setup.activate.start" => new
        {
            sessionId = parameters.GetProperty("sessionId").GetString(), done = true, status = "done",
            modelActivation = new { modelRef = Model, gatewayRestartRequired = false },
        },
        "wizard.cancel" => new { status = "cancelled" },
        _ => throw new InvalidOperationException("Unexpected fixture setup method: " + method),
    };

    private static NativeGatewaySetupTests.Fixture CreateFixture(FixtureGatewayServer server, bool isolated = false)
    {
        var fixture = new NativeGatewaySetupTests.Fixture(server.Endpoint.Port,
            isolated ? NativeGatewayContract.IsolatedSessionV1 : NativeGatewayContract.Legacy);
        fixture.Host.IsolatedConfiguration = new(server.Endpoint.Port, Token);
        if (!isolated)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConfigPath)!);
            File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(new
            {
                gateway = new { mode = "local", bind = "loopback", port = server.Endpoint.Port,
                    auth = new { mode = "token", token = Token }, reload = new { mode = "hybrid" } },
                models = new { sentinel = "preserved" },
            }));
        }
        fixture.Runtime.StopConnections = () => server.CloseConnectionsAsync();
        return fixture;
    }

    [Theory]
    [InlineData(SetupNativeDestination.Chat, false)]
    [InlineData(SetupNativeDestination.Channels, false)]
    [InlineData(SetupNativeDestination.Skills, false)]
    [InlineData(SetupNativeDestination.Chat, true)]
    [InlineData(SetupNativeDestination.Channels, true)]
    [InlineData(SetupNativeDestination.Skills, true)]
    public async Task FocusedAi_AllDestinationsReverifyAfterNativeRestartBeforePublication(SetupNativeDestination destination, bool isolated)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated);
        await using var owner = await fixture.PrepareAsync();
        var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        var client = new GatewayAiSetupClient(connection);
        await client.DetectAsync();
        client.SelectCandidate("existing-model", Model);
        await client.StartSelectedAsync();
        Assert.True((await client.VerifyAsync()).Ok);
        var proof = client.GetVerifiedCompletion();
        Assert.Empty(fixture.Registry.GetAll());
        SetupNativeCompletion? published = null;
        using var chooser = new SetupNativeCompletionCoordinator(proof,
            _ => connection.DisposeAsync().AsTask(), owner.VerifyAsync,
            async (verified, ct) => { await owner.CompleteVerifiedAsync(verified, new CapabilitiesConfig { Camera = false }, ct); },
            (completion, _) =>
            {
                Assert.Equal(owner.Record.Id, fixture.Registry.ActiveGatewayId);
                if (isolated)
                {
                    Assert.False(File.Exists(fixture.ConfigPath));
                    Assert.Equal(NativeGatewayPackageClient.IsolatedContract, owner.Record.NativeRuntimeContract);
                    Assert.Contains("restart", fixture.Events);
                    Assert.True(fixture.Events.IndexOf("restart") < fixture.Events.IndexOf("stop"),
                        "An isolated verified restart must retain its original detach/stop ownership.");
                    Assert.Equal(new CapabilitiesConfig { Camera = false }.GetEnabledCommandIds(), fixture.Host.AppliedCommands);
                }
                else
                {
                    Assert.Equal("hybrid", fixture.Config()["gateway"]!["reload"]!["mode"]!.GetValue<string>());
                    Assert.Equal("preserved", fixture.Config()["models"]!["sentinel"]!.GetValue<string>());
                }
                Assert.True(server.Requests.Count(request => request.Method == "openclaw.setup.verify") >= 3);
                published = completion;
                return Task.CompletedTask;
            });
        Assert.Empty(fixture.Registry.GetAll());
        await chooser.SelectAsync(destination).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(destination, Assert.IsType<SetupNativeCompletion>(published).Target.Destination);
        Assert.Equal(proof.IdentityBinding, published!.Verification.IdentityBinding);
        Assert.Equal(proof.SessionKey, published.Verification.SessionKey);
        Assert.DoesNotContain(server.Requests, request => request.Method == "wizard.start");
        Assert.False(File.Exists(fixture.ConfigPath + ".setup-reload.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignListener_DeniesCredentialsEvenWithStoredDeviceToken(bool paired)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server);
        await using var owner = await fixture.PrepareAsync();
        if (paired)
        {
            var identity = new DeviceIdentity(owner.IdentityDirectory);
            identity.Initialize();
            identity.StoreDeviceToken(Token);
        }
        fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeGatewaySetupConnection.ConnectAsync(owner));
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryRequestAndReconnect_RechecksNativeOwnership(bool isolated)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated);
        await using var owner = await fixture.PrepareAsync();
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        var count = server.Requests.Count;
        fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.RequestAsync("openclaw.setup.verify", new { agentId = "main" }, 5000, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.RequestAsync("logs.tail", new { limit = 1, maxBytes = 1 }, 5000, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Client.HandshakeAuthorizationAsync!(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Client.ReconnectAuthorizationAsync!(default));
        Assert.Equal(count, server.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedConfigurationDriftRejectsRequestsAndReconnectsWithoutHostConfig(bool changedPort)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated: true);
        await using var owner = await fixture.PrepareAsync();
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        var count = server.Requests.Count;
        fixture.Host.IsolatedConfiguration = changedPort
            ? new(server.Endpoint.Port == 65535 ? 65534 : server.Endpoint.Port + 1, Token)
            : new(server.Endpoint.Port, "other-agent-token");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.RequestAsync("openclaw.setup.verify", new { agentId = "main" }, 5000, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Client.HandshakeAuthorizationAsync!(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Client.ReconnectAuthorizationAsync!(default));
        Assert.Equal(count, server.Requests.Count);
        Assert.False(File.Exists(fixture.ConfigPath));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public void NativeRuntimeContractIsPartOfRetainedCompletionAuthority()
    {
        var legacy = new GatewayRecord { Id = "native", Url = "ws://127.0.0.1:18789",
            NativePackageFamilyName = "OpenClaw.Gateway_123456789abcd" };
        var isolated = legacy with { NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract };
        Assert.NotEqual(GatewayDashboardBinding.Capture(legacy), GatewayDashboardBinding.Capture(isolated));
        Assert.NotEqual(GatewayDashboardBinding.Capture(isolated),
            GatewayDashboardBinding.Capture(isolated with { NativeRuntimeContract = "unsupported" }));
    }

    [Fact]
    public async Task LostPairedIdentity_IsNotRecreatedWhenReopeningTheSameNativeSetup()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server);
        await using var owner = await fixture.PrepareAsync();
        await using (var connection = await NativeGatewaySetupConnection.ConnectAsync(owner)) { }
        var connections = server.ConnectionCount;
        var identityPath = Path.Combine(owner.IdentityDirectory, "device-key-ed25519.json");
        File.Delete(identityPath);
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() => NativeGatewaySetupConnection.ConnectAsync(owner));
        Assert.False(File.Exists(identityPath));
        Assert.Equal(connections, server.ConnectionCount);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("identity")]
    [InlineData("session")]
    [InlineData("agent")]
    [InlineData("model")]
    public async Task ChangedCompletionAuthority_CannotPublish(string changed)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server);
        await using var owner = await fixture.PrepareAsync();
        GatewayAiSetupCompletion proof;
        await using (var connection = await NativeGatewaySetupConnection.ConnectAsync(owner))
        {
            var client = new GatewayAiSetupClient(connection, Model);
            await client.VerifyConfiguredAsync(Model);
            proof = client.GetVerifiedCompletion();
        }
        proof = changed switch
        {
            "gateway" => proof with { GatewayId = "another-gateway" },
            "identity" => proof with { IdentityBinding = new string('F', 64) },
            "session" => proof with { SessionKey = "agent:main:other" },
            "agent" => proof with { AgentId = "other", SessionKey = "agent:other:main" },
            _ => proof with { ModelRef = "fixture/other-model" },
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            owner.CompleteVerifiedAsync(proof, new CapabilitiesConfig(), default));
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Equal("preserved", fixture.Config()["models"]!["sentinel"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedHandshake_OffersOnlyExplicitClassicFallbackWithoutMutation()
    {
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateNativeSetup(Reply, advertiseSetup: false), Token);
        using var fixture = CreateFixture(server);
        await using var owner = await fixture.PrepareAsync();
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        var client = new GatewayAiSetupClient(connection);
        Assert.Null(await client.DetectAsync());
        Assert.Equal(GatewayAiSetupPhase.ClassicWizardRequired, client.Phase);
        Assert.Throws<InvalidOperationException>(() => client.GetVerifiedCompletion());
        Assert.DoesNotContain(server.Requests, request => request.Method.StartsWith("openclaw.setup", StringComparison.Ordinal));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task OwnedRestart_ChangesHandshakeGenerationWithoutChangingIdentityOrProfile()
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server);
        await using var owner = await fixture.PrepareAsync();
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(owner);
        var route = connection.Route;
        var generation = connection.Generation;
        await connection.RestartAsync(default);
        Assert.True(connection.Generation > generation);
        Assert.Equal(route, connection.Route);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Equal("preserved", fixture.Config()["models"]!["sentinel"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostRestartVerification_BorrowsNormalOwnerAndRejectsUnownedOrReplacedAuthority(bool isolated)
    {
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Reply), Token);
        using var fixture = CreateFixture(server, isolated);
        GatewayAiSetupCompletion proof;
        await using (var owner = await fixture.PrepareAsync())
        {
            await using (var connection = await NativeGatewaySetupConnection.ConnectAsync(owner))
            {
                var client = new GatewayAiSetupClient(connection, Model);
                await client.VerifyConfiguredAsync(Model);
                proof = client.GetVerifiedCompletion();
            }
            await owner.CompleteVerifiedAsync(proof, new CapabilitiesConfig(), default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyAsync(fixture.Temp.Path, proof, default));
        var normalRuntime = new NativeGatewaySetupTests.Runtime(fixture.Events)
        {
            StopConnections = () => server.CloseConnectionsAsync(),
        };
        await using var manager = new GatewayConnectionManager(
            new CredentialResolver(DeviceIdentityFileReader.Instance), new GatewayClientFactory(),
            fixture.Registry, NullLogger.Instance, nativeGatewayRuntime: normalRuntime);
        await manager.ConnectAsync(proof.GatewayId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var verified = await SetupNativeCompletionVerifier.VerifyAsync(fixture.Temp.Path, proof, deadline.Token, manager);
        SetupNativeVerification.RequireSame(proof, verified);
        var borrowed = manager.OperatorClient;
        var connections = server.ConnectionCount;
        normalRuntime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyAsync(fixture.Temp.Path, proof, deadline.Token, manager));
        Assert.Same(borrowed, manager.OperatorClient);
        Assert.Equal(connections, server.ConnectionCount);
        normalRuntime.Provenance = GatewayEndpointProvenanceKind.ExpectedManagedGateway;
        var changed = fixture.Registry.GetActive()! with { NativePackageFamilyName = "OpenClaw.Gateway_otherfamily" };
        fixture.Registry.AddOrUpdate(changed);
        fixture.Registry.Save();
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
            SetupNativeCompletionVerifier.VerifyAsync(fixture.Temp.Path, proof, deadline.Token, manager));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedVerifierEnforcesRecoveryAndModelPhaseTokens(bool blockModel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseModel = new ManualResetEventSlim();
        var block = false;
        object Respond(string method, JsonElement parameters)
        {
            if (block && blockModel && method == "openclaw.setup.verify")
            {
                entered.TrySetResult();
                if (!releaseModel.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("The test did not release model verification.");
            }
            return Reply(method, parameters);
        }
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup(Respond), Token);
        using var fixture = CreateFixture(server, isolated: true);
        GatewayAiSetupCompletion proof;
        await using (var owner = await fixture.PrepareAsync())
        {
            await using (var connection = await NativeGatewaySetupConnection.ConnectAsync(owner))
            {
                var client = new GatewayAiSetupClient(connection, Model);
                await client.VerifyConfiguredAsync(Model);
                proof = client.GetVerifiedCompletion();
            }
            await owner.CompleteVerifiedAsync(proof, new CapabilitiesConfig(), default);
        }
        var runtime = new NativeGatewaySetupTests.Runtime(fixture.Events)
        {
            StopConnections = () => server.CloseConnectionsAsync()
        };
        await using var manager = new GatewayConnectionManager(
            new CredentialResolver(DeviceIdentityFileReader.Instance), new GatewayClientFactory(),
            fixture.Registry, NullLogger.Instance, nativeGatewayRuntime: runtime);
        await manager.ConnectAsync(proof.GatewayId);
        var clock = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        CancellationToken recoveryToken = default;
        async Task Recover(GatewayAiSetupCompletion _, CancellationToken ct)
        {
            recoveryToken = ct;
            if (blockModel) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        block = true;
        var verifying = SetupNativeCompletionVerifier.VerifyAsync(
            fixture.Temp.Path, proof, caller.Token, manager, Recover, clock);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            clock.Advance(blockModel ? SetupNativeCompletionTiming.ModelVerification : SetupNativeCompletionTiming.ModelRecovery);
            var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(() => verifying.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(blockModel ? "selected AI model" : "Local AI recovery", error.Message);
            Assert.False(caller.IsCancellationRequested);
            if (!blockModel) Assert.True(recoveryToken.IsCancellationRequested);
        }
        finally { block = false; releaseModel.Set(); caller.Cancel(); }
        using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var verified = await SetupNativeCompletionVerifier.VerifyAsync(
            fixture.Temp.Path, proof, retryDeadline.Token, manager, timeProvider: clock);
        SetupNativeVerification.RequireSame(proof, verified);
    }
}
