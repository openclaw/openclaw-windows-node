using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayConnectionValidatorTests
{
    [Fact]
    public void StagedIdentity_CopyUsesProtectedSensitiveFileAcl()
    {
        using var saved = new TempDirectory();
        new DeviceIdentity(saved.Path).Initialize();
        using var copy = new GatewayValidationIdentity(saved.Path);
        var file = new FileInfo(Path.Combine(copy.DirectoryPath, "device-key-ed25519.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(saved.Path, "device-key-ed25519.json")), File.ReadAllText(file.FullName));
        if (OperatingSystem.IsWindows())
        {
            var acl = System.IO.FileSystemAclExtensions.GetAccessControl(file);
            Assert.True(acl.AreAccessRulesProtected);
            var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
                Assert.False(rule.IsInherited);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RevokedDeviceToken_RecoversOnceInCopyAndPreservesKeypairThroughCheckAndNext(bool bootstrap, bool ssh)
    {
        using var saved = new TempDirectory();
        var original = new DeviceIdentity(saved.Path);
        original.Initialize();
        original.StoreDeviceTokenForRole("operator", "revoked");
        original.StoreDeviceTokenForRole("node", "node-keep");
        var file = Path.Combine(saved.Path, "device-key-ed25519.json");
        var before = File.ReadAllBytes(file);
        using var copy = new GatewayValidationIdentity(saved.Path);
        var credentials = new List<GatewayCredential>();
        var ownedTunnel = ssh ? new FakeTunnel() : null;
        var validator = CreateValidator(async (record, credential, tunnel, config, generation, ct) =>
        {
            credentials.Add(credential);
            return tunnel is null ? ReconnectAuthorizationResult.AllowedResult :
                await GatewayConnectionManager.AuthorizeValidationTunnelHandshakeAsync(tunnel, config!, generation!.Value, ct);
        }, (client, ct) =>
        {
            if (client.ConnectAuthToken == "revoked")
                return Task.FromResult(GatewayConnectionValidator.AuthenticationFailure("AUTH_DEVICE_TOKEN_MISMATCH: rejected"));
            var received = typeof(OpenClawGatewayClient).GetField("DeviceTokenReceived",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.IsType<EventHandler<DeviceTokenReceivedEventArgs>>(received.GetValue(client))(
                client, new("replacement-device", ["operator.read"], "operator"));
            return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        }, ownedTunnel);
        var candidate = new GatewayRecord
        {
            Url = "wss://gateway.example",
            SharedGatewayToken = bootstrap ? null : "shared",
            BootstrapToken = "bootstrap",
            SshTunnel = ssh ? new("user", "host.example", 18789, 45678) : null,
        };
        Assert.Equal(SetupCodeOutcome.Success,
            (await validator.ValidateAsync(candidate, copy, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.True(copy.OperatorTokenRecoveryAttempted);
        var copied = new DeviceIdentity(copy.DirectoryPath);
        copied.Initialize();
        Assert.Equal(original.DeviceId, copied.DeviceId);
        Assert.Equal("node-keep", DeviceIdentity.TryReadStoredDeviceTokenForRole(copy.DirectoryPath, "node"));
        Assert.Equal(SetupCodeOutcome.Success,
            (await validator.ValidateAsync(candidate, copy, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Equal("replacement-device", credentials[^1].Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, credentials[^1].Source);
        Assert.Contains(credentials, value => value.Source == (bootstrap
            ? CredentialResolver.SourceBootstrapToken : CredentialResolver.SourceSharedGatewayToken));
        if (!bootstrap) Assert.DoesNotContain(credentials, value => value.IsBootstrapToken);
        copy.ReplaceExisting(saved.Path);
        var committed = new DeviceIdentity(saved.Path);
        committed.Initialize();
        Assert.Equal(original.DeviceId, committed.DeviceId);
        Assert.Equal("replacement-device", DeviceIdentity.TryReadStoredDeviceToken(saved.Path));
        Assert.Equal("node-keep", DeviceIdentity.TryReadStoredDeviceTokenForRole(saved.Path, "node"));
    }

    [Theory]
    [InlineData("wrong-shared")]
    [InlineData("no-fallback")]
    [InlineData("unsafe")]
    [InlineData("conflict")]
    [InlineData("repeated")]
    [InlineData("rejected-fallback")]
    [InlineData("cancel")]
    [InlineData("cancel-fallback")]
    public async Task RecoveryRejectsUnsafeOrUnrelatedFailuresWithoutChangingSavedIdentity(string scenario)
    {
        using var saved = new TempDirectory();
        var identity = new DeviceIdentity(saved.Path);
        identity.Initialize();
        if (scenario != "wrong-shared") identity.StoreDeviceTokenForRole("operator", "revoked");
        var file = Path.Combine(saved.Path, "device-key-ed25519.json");
        var before = File.ReadAllBytes(file);
        using var copy = new GatewayValidationIdentity(saved.Path);
        using var cancellation = new CancellationTokenSource();
        var handshakes = 0;
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            if (scenario == "cancel" && credential.Source != CredentialResolver.SourceDeviceToken)
                cancellation.Cancel();
            return Task.FromResult(scenario == "conflict" && credential.Source != CredentialResolver.SourceDeviceToken
                ? new ReconnectAuthorizationResult(false, GatewayErrorKind.LocalPortConflict, "unowned listener")
                : ReconnectAuthorizationResult.AllowedResult);
        }, (_, _) =>
        {
            handshakes++;
            if (scenario == "cancel-fallback" && handshakes == 2)
            {
                cancellation.Cancel();
                return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
            }
            return Task.FromResult(GatewayConnectionValidator.AuthenticationFailure(
                scenario == "wrong-shared" || scenario == "rejected-fallback" && handshakes == 2
                    ? "AUTH_TOKEN_MISMATCH: wrong shared token" : "AUTH_DEVICE_TOKEN_MISMATCH: revoked"));
        });
        var record = new GatewayRecord
        {
            Url = scenario == "unsafe" ? "ws://remote.example" :
                scenario == "conflict" ? "ws://127.0.0.1:18789" : "wss://gateway.example",
            IsLocal = scenario == "conflict",
            SharedGatewayToken = scenario == "no-fallback" ? null : "shared",
            BootstrapToken = scenario == "rejected-fallback" ? "must-not-fall-back-again" : null,
        };
        if (scenario is "cancel" or "cancel-fallback")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                validator.ValidateAsync(record, copy, new HashSet<int>(), cancellation.Token));
        else
            Assert.Equal(SetupCodeOutcome.ConnectionFailed,
                (await validator.ValidateAsync(record, copy, new HashSet<int>(), cancellation.Token)).Outcome);
        var recovered = scenario is "repeated" or "rejected-fallback" or "cancel-fallback";
        Assert.Equal(recovered ? 2 : 1, handshakes);
        Assert.Equal(before, File.ReadAllBytes(file));
        if (!recovered) Assert.Equal(before, File.ReadAllBytes(Path.Combine(copy.DirectoryPath, "device-key-ed25519.json")));
    }

    [Fact]
    public void AuthenticationFailure_PreservesOnlyStructuredDeviceMismatchForRecovery()
    {
        Assert.Equal(GatewayErrorKind.DeviceTokenMismatch,
            GatewayConnectionValidator.AuthenticationFailure("AUTH_DEVICE_TOKEN_MISMATCH: rejected").ErrorKind);
        Assert.NotEqual(GatewayErrorKind.DeviceTokenMismatch,
            GatewayConnectionValidator.AuthenticationFailure("token mismatch").ErrorKind);
        Assert.NotEqual(GatewayErrorKind.DeviceTokenMismatch,
            GatewayConnectionValidator.AuthenticationFailure("device token invalid").ErrorKind);
    }

    [Fact]
    public async Task Validate_UsesCopiedDeviceIdentityWithoutWritingSavedFiles()
    {
        using var saved = new TempDirectory();
        var original = new DeviceIdentity(saved.Path);
        original.Initialize();
        original.StoreDeviceTokenForRole("operator", "device-token");
        var before = File.ReadAllBytes(Path.Combine(saved.Path, "device-key-ed25519.json"));
        using var copy = new GatewayValidationIdentity(saved.Path);
        var copiedIdentity = new DeviceIdentity(copy.DirectoryPath);
        copiedIdentity.Initialize();
        Assert.Equal(original.DeviceId, copiedIdentity.DeviceId);
        GatewayCredential? authorized = null;
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            authorized = credential;
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        }, async (client, ct) =>
        {
            Assert.False((await client.ReconnectAuthorizationAsync!(ct)).Allowed);
            Assert.True((await client.HandshakeAuthorizationAsync!(ct)).Allowed);
            return new(SetupCodeOutcome.Success);
        });
        var result = await validator.ValidateAsync(new()
        {
            Url = "wss://gateway.example", SharedGatewayToken = "shared", BootstrapToken = "bootstrap"
        }, copy, new HashSet<int>(), CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.Success, result.Outcome);
        Assert.Equal(CredentialResolver.SourceDeviceToken, authorized!.Source);
        Assert.Equal("device-token", authorized.Token);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(saved.Path, "device-key-ed25519.json")));
    }

    [Fact]
    public async Task Validate_BootstrapIsNotTreatedAsSharedCredential()
    {
        using var identity = new GatewayValidationIdentity();
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            Assert.True(credential.IsBootstrapToken);
            Assert.Equal(CredentialResolver.SourceBootstrapToken, credential.Source);
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        });
        var result = await validator.ValidateAsync(new()
        {
            Url = "wss://gateway.example", BootstrapToken = "bootstrap"
        }, identity, new HashSet<int>(), CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Validate_DeniedProvenanceNeverConstructsHandshakeClient()
    {
        using var identity = new GatewayValidationIdentity();
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
            Task.FromResult(new ReconnectAuthorizationResult(false, GatewayErrorKind.LocalPortConflict, "unowned")),
            (_, _) => throw new InvalidOperationException("Must not contact the gateway."));
        var result = await validator.ValidateAsync(new()
        {
            Url = "ws://127.0.0.1:18789", IsLocal = true, SharedGatewayToken = "shared"
        }, identity, new HashSet<int>(), CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal("unowned", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(identity.DirectoryPath));
    }

    [Fact]
    public async Task Validate_SshUsesOwnedTemporaryPortAndPinnedGeneration()
    {
        using var identity = new GatewayValidationIdentity();
        var tunnel = new FakeTunnel();
        var ssh = new SshTunnelConfig("user", "host.example", 18789, 45678, true, 2222);
        var validator = CreateValidator(async (record, credential, manager, config, generation, ct) =>
        {
            Assert.Same(tunnel, manager);
            Assert.NotNull(config);
            Assert.NotEqual(ssh.LocalPort, config.LocalPort);
            Assert.False(config.IncludeBrowserProxyForward);
            Assert.Equal(2222, config.SshPort);
            return await GatewayConnectionManager.AuthorizeValidationTunnelHandshakeAsync(
                manager!, config, generation!.Value, ct);
        }, async (client, ct) =>
        {
            tunnel.Generation++;
            var permission = await client.HandshakeAuthorizationAsync!(ct);
            Assert.False(permission.Allowed);
            return new(SetupCodeOutcome.ConnectionFailed, permission.Detail);
        }, tunnel);
        var result = await validator.ValidateAsync(new()
        {
            Url = "ws://127.0.0.1:18789", SharedGatewayToken = "shared", SshTunnel = ssh
        }, identity, new HashSet<int> { 45678, 45680 }, CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.ConnectionFailed, result.Outcome);
        Assert.Contains("shared token was not sent", result.ErrorMessage);
        Assert.True(tunnel.Stopped);
        Assert.True(tunnel.Disposed);
    }

    [Fact]
    public async Task Validate_CancellationDrainsOwnedTunnelAndDoesNotReportSuccess()
    {
        using var identity = new GatewayValidationIdentity();
        using var cancellation = new CancellationTokenSource();
        var tunnel = new FakeTunnel();
        var validator = CreateValidator((record, credential, manager, config, generation, ct) =>
            Task.FromResult(ReconnectAuthorizationResult.AllowedResult),
            async (_, ct) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new(SetupCodeOutcome.Success);
            }, tunnel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.ValidateAsync(new()
        {
            Url = "ws://127.0.0.1:18789", SharedGatewayToken = "shared",
            SshTunnel = new("user", "host.example", 18789, 45678)
        }, identity, new HashSet<int>(), cancellation.Token));
        Assert.True(tunnel.Stopped);
        Assert.True(tunnel.Disposed);
    }

    [Fact]
    public void ValidationClient_DisablesHandshakeTokenPersistence()
    {
        using var identity = new GatewayValidationIdentity();
        using var client = GatewayConnectionValidator.CreateClient(
            "wss://gateway.example", new("shared", false, CredentialResolver.SourceSharedGatewayToken),
            identity.DirectoryPath, new(), NullLogger.Instance,
            _ => Task.FromResult(ReconnectAuthorizationResult.AllowedResult));
        var persist = typeof(OpenClawGatewayClient).GetField("_persistHandshakeDeviceTokens",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.False(Assert.IsType<bool>(persist!.GetValue(client)));
    }

    [Fact]
    public async Task Validate_RetainsIssuedBootstrapHandoffOnlyInMemoryUntilCommit()
    {
        using var identity = new GatewayValidationIdentity();
        using var committed = new TempDirectory();
        var requests = new List<GatewayCredential>();
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            requests.Add(credential);
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        }, (client, ct) =>
        {
            var tokenEvent = typeof(OpenClawGatewayClient).GetField("DeviceTokenReceived",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var handler = Assert.IsType<EventHandler<DeviceTokenReceivedEventArgs>>(tokenEvent!.GetValue(client));
            handler(client, new("issued-device-token", ["operator.read"], "operator"));
            return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        });
        var record = new GatewayRecord { Url = "wss://gateway.example", BootstrapToken = "single-use-bootstrap" };
        Assert.Equal(SetupCodeOutcome.Success,
            (await validator.ValidateAsync(record, identity, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identity.DirectoryPath));
        Assert.Equal(SetupCodeOutcome.Success,
            (await validator.ValidateAsync(record, identity, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Equal(CredentialResolver.SourceBootstrapToken, requests[0].Source);
        Assert.Equal(CredentialResolver.SourceDeviceToken, requests[1].Source);
        Assert.Equal("issued-device-token", requests[1].Token);
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identity.DirectoryPath));
        identity.CopyTo(committed.Path);
        Assert.Equal("issued-device-token", DeviceIdentity.TryReadStoredDeviceToken(committed.Path));
    }

    private static GatewayConnectionValidator CreateValidator(
        ValidationHandshakeAuthorization authorize,
        Func<OpenClawGatewayClient, CancellationToken, Task<SetupCodeResult>>? handshake = null,
        ISshTunnelManager? tunnel = null) =>
        new(new CredentialResolver(DeviceIdentityFileReader.Instance),
            () => tunnel ?? throw new InvalidOperationException("SSH was not expected."),
            authorize, NullLogger.Instance, handshake ?? ((_, _) => Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success))));

    [Fact]
    public void StagedIdentity_SameLogicalGatewayPromotionAndRollbackPreserveOriginalKey()
    {
        using var saved = new TempDirectory();
        var original = new DeviceIdentity(saved.Path);
        original.Initialize();
        original.StoreDeviceTokenForRole("operator", "old-token");
        var file = Path.Combine(saved.Path, "device-key-ed25519.json");
        var before = File.ReadAllBytes(file);
        using var staged = new GatewayValidationIdentity(saved.Path);
        staged.CaptureToken(new("new-token", ["operator.read"], "operator"));
        var transaction = staged.ReplaceExisting(saved.Path);
        Assert.Equal("new-token", DeviceIdentity.TryReadStoredDeviceToken(saved.Path));
        var reloaded = new DeviceIdentity(saved.Path);
        reloaded.Initialize();
        Assert.Equal(original.DeviceId, reloaded.DeviceId);
        Assert.Equal(DeviceTokenRestoreOutcome.Restored, DeviceIdentity.RestoreValidatedIdentity(transaction).Outcome);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void StagedIdentity_ConcurrentSavedChangeIsNotOverwritten()
    {
        using var saved = new TempDirectory();
        var identity = new DeviceIdentity(saved.Path);
        identity.Initialize();
        using var staged = new GatewayValidationIdentity(saved.Path);
        identity.StoreDeviceTokenForRole("operator", "newer-writer");
        Assert.Throws<InvalidOperationException>(() => staged.ReplaceExisting(saved.Path));
        Assert.Equal("newer-writer", DeviceIdentity.TryReadStoredDeviceToken(saved.Path));
    }

    [Fact]
    public void StagedIdentity_RollbackDoesNotClobberNewerCredentials()
    {
        using var saved = new TempDirectory();
        new DeviceIdentity(saved.Path).Initialize();
        using var staged = new GatewayValidationIdentity(saved.Path);
        var transaction = staged.ReplaceExisting(saved.Path);
        var newer = new DeviceIdentity(saved.Path);
        newer.Initialize();
        newer.StoreDeviceTokenForRole("operator", "newer-writer");
        Assert.Equal(DeviceTokenRestoreOutcome.Superseded, DeviceIdentity.RestoreValidatedIdentity(transaction).Outcome);
        Assert.Equal("newer-writer", DeviceIdentity.TryReadStoredDeviceToken(saved.Path));
    }

    [Fact]
    public void StagedIdentity_PreviouslyAbsentKeyIsRemovedOnRollbackWithoutDeletingSidecars()
    {
        using var saved = new TempDirectory();
        var sidecar = Path.Combine(saved.Path, "sidecar.txt");
        File.WriteAllText(sidecar, "retained");
        using var staged = new GatewayValidationIdentity(saved.Path);
        new DeviceIdentity(staged.DirectoryPath).Initialize();
        var transaction = staged.ReplaceExisting(saved.Path);
        Assert.Equal(DeviceTokenRestoreOutcome.Restored, DeviceIdentity.RestoreValidatedIdentity(transaction).Outcome);
        Assert.False(File.Exists(Path.Combine(saved.Path, "device-key-ed25519.json")));
        Assert.Equal("retained", File.ReadAllText(sidecar));
    }

    [Fact]
    public async Task Validate_ExactBoundedBootstrapFallbackGetsOneFreshAuthorizedAttempt()
    {
        using var identity = new GatewayValidationIdentity();
        var authorizations = 0;
        var attempts = 0;
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            authorizations++;
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        }, async (client, ct) =>
        {
            attempts++;
            Assert.False((await client.ReconnectAuthorizationAsync!(ct)).Allowed);
            if (attempts == 1)
            {
                using var error = System.Text.Json.JsonDocument.Parse(
                    """{"error":{"code":"AUTH_BOOTSTRAP_TOKEN_INVALID","message":"bootstrap token invalid"}}""");
                var handle = typeof(OpenClawGatewayClient).GetMethod("HandleRequestError",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                handle!.Invoke(client, ["connect", error.RootElement, 0L]);
                Assert.True(client.UsesBoundedBootstrapScopes);
                return new(SetupCodeOutcome.ConnectionFailed, "full profile rejected");
            }
            Assert.True(client.UsesBoundedBootstrapScopes);
            return new(SetupCodeOutcome.Success);
        });
        var result = await validator.ValidateAsync(new()
        {
            Url = "wss://gateway.example", BootstrapToken = "bootstrap"
        }, identity, new HashSet<int>(), CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.Success, result.Outcome);
        Assert.Equal(2, attempts);
        Assert.Equal(2, authorizations);
    }

    [Fact]
    public async Task Validate_SignatureFallbackIsBoundedAndReauthorizesEndpoint()
    {
        using var identity = new GatewayValidationIdentity();
        var authorizations = 0;
        var attempts = 0;
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            authorizations++;
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        }, (client, ct) =>
        {
            attempts++;
            if (attempts == 1)
                client.UseV2Signature = true;
            else
                Assert.True(client.UseV2Signature);
            return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, "rejected"));
        });
        var result = await validator.ValidateAsync(new()
        {
            Url = "wss://gateway.example", SharedGatewayToken = "shared"
        }, identity, new HashSet<int>(), CancellationToken.None);
        Assert.Equal(SetupCodeOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal("rejected", result.ErrorMessage);
        Assert.Equal(2, attempts);
        Assert.Equal(2, authorizations);
    }

    [Fact]
    public async Task Validate_DisconnectFailureRetainsAuthenticatedBootstrapUpgradeForRetry()
    {
        using var identity = new GatewayValidationIdentity();
        var credentials = new List<GatewayCredential>();
        var attempts = 0;
        var validator = CreateValidator((record, credential, tunnel, config, generation, ct) =>
        {
            credentials.Add(credential);
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        }, (client, ct) =>
        {
            if (++attempts == 1)
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var tokenHandler = Assert.IsType<EventHandler<DeviceTokenReceivedEventArgs>>(
                    typeof(OpenClawGatewayClient).GetField("DeviceTokenReceived", flags)!.GetValue(client));
                tokenHandler(client, new("issued-before-close", ["operator.read"], "operator"));
                var handshake = Assert.IsType<EventHandler>(
                    typeof(OpenClawGatewayClient).GetField("HandshakeSucceeded", flags)!.GetValue(client));
                handshake(client, EventArgs.Empty);
                throw new TimeoutException("graceful disconnect timed out");
            }
            return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        });
        var record = new GatewayRecord { Url = "wss://gateway.example", BootstrapToken = "single-use" };
        Assert.Equal(SetupCodeOutcome.ConnectionFailed,
            (await validator.ValidateAsync(record, identity, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identity.DirectoryPath));
        Assert.Equal(SetupCodeOutcome.Success,
            (await validator.ValidateAsync(record, identity, new HashSet<int>(), CancellationToken.None)).Outcome);
        Assert.Equal("issued-before-close", credentials[1].Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, credentials[1].Source);
    }

    private sealed class FakeTunnel : ISshTunnelManager
    {
        public long Generation = 1;
        public bool Stopped;
        public bool Disposed;
        public bool IsActive => ActiveConfig is not null && !Stopped;
        public long OwnershipGeneration => Generation;
        public SshTunnelConfig? ActiveConfig { get; private set; }
        public string? LocalTunnelUrl => ActiveConfig is null ? null : $"ws://localhost:{ActiveConfig.LocalPort}";
        public bool IsRestartPending(SshTunnelExit exit) => false;
        public Task<bool> IsOwnedListenerReadyAsync(SshTunnelConfig config, int port, CancellationToken ct) =>
            Task.FromResult(config == ActiveConfig && port == config.LocalPort);
        public async Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct) =>
            (await StartOwnedAsync(config, ct)).Url;
        public Task<SshTunnelStartResult> StartOwnedAsync(SshTunnelConfig config, CancellationToken ct)
        {
            ActiveConfig = config;
            return Task.FromResult(new SshTunnelStartResult(LocalTunnelUrl!, config, Generation));
        }
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        public Task<bool> StopIfOwnedAsync(SshTunnelConfig config, long generation, CancellationToken ct) =>
            Task.FromResult(config == ActiveConfig && generation == Generation);
        public void Dispose() => Disposed = true;
    }
}
