using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewaySetupTests
{
    private const string Family = "OpenClaw.Gateway_123456789abcd";

    [Fact]
    public async Task PublishedSession_CannotRestartWizardButCanRetryCompletion()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkWizardCompleted();
        var published = await session.CompleteAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => session.BeginWizard());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestartAsync(CancellationToken.None));
        Assert.Equal(published, await session.CompleteAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityCompletion_AppliesSelectionsBeforeValidationAndHealth_PreservingConfiguration(bool deferred)
    {
        using var fixture = new Fixture();
        await using (var initial = await fixture.PrepareAsync()) { }
        var config = fixture.Config();
        config["gateway"]!["reload"]!["mode"] = "hybrid";
        config["models"] = JsonNode.Parse("""{"providers":{"custom":{"baseUrl":"https://provider.example","apiKey":"saved-provider-key"}}}""");
        config["gateway"]!["nodes"] = JsonNode.Parse(
            """{"commands":{"allow":["camera.snap","obsolete.command"],"deny":["system.run"],"custom":true},"other":"preserved"}""");
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        await using var session = await fixture.PrepareAsync();
        var capabilities = new CapabilitiesConfig
        {
            System = true, Canvas = false, Screen = true, Camera = false,
            Location = false, Browser = false, Device = true, Tts = true, Stt = false,
        };
        var expected = config.DeepClone();
        expected["gateway"]!["nodes"]!["commands"]!["allow"] =
            JsonSerializer.SerializeToNode(capabilities.GetEnabledCommandIds());
        fixture.Events.Clear();
        fixture.Host.Validate = () =>
        {
            Assert.Equal(["stop", "validate"], fixture.Events);
            Assert.True(JsonNode.DeepEquals(expected, fixture.Config()));
            Assert.Empty(fixture.Registry.GetAll());
        };
        fixture.Host.Health = () =>
        {
            Assert.True(JsonNode.DeepEquals(expected, fixture.Config()));
            Assert.Empty(fixture.Registry.GetAll());
        };
        if (deferred)
            session.MarkOptionalSetupDeferred();
        else
            session.MarkWizardCompleted();

        await session.CompleteAsync(CancellationToken.None, capabilities);

        Assert.Equal(["stop", "validate", "start", "inspect", "health", "stop", "stop"], fixture.Events);
        var allow = fixture.Config()["gateway"]!["nodes"]!["commands"]!["allow"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(capabilities.GetEnabledCommandIds(), allow);
        Assert.DoesNotContain(allow, command => command.StartsWith("camera.", StringComparison.Ordinal));
        Assert.Null(fixture.Config()["gateway"]!["nodes"]!["allowCommands"]);
        Assert.Null(fixture.Host.ApprovedId);
        Assert.Equal(session.Record.Id, fixture.Registry.ActiveGatewayId);
    }

    [Fact]
    public async Task CapabilityCompletion_FailedValidationDoesNotPublish_AndRetryAppliesNewSelection()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkWizardCompleted();
        var credential = session.Record.SharedGatewayToken;
        var capabilities = new CapabilitiesConfig { Camera = false };
        fixture.Host.Validate = () => throw new InvalidOperationException("invalid provider config");
        fixture.Events.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CompleteAsync(CancellationToken.None, capabilities));

        Assert.Equal(["stop", "validate", "stop"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(capabilities.GetEnabledCommandIds()),
            fixture.Config()["gateway"]!["nodes"]!["commands"]!["allow"]));
        fixture.Host.Validate = () => { };
        capabilities.Camera = true;
        fixture.Events.Clear();

        var record = await session.CompleteAsync(CancellationToken.None, capabilities);

        Assert.Equal(["stop", "validate", "start", "inspect", "health", "stop", "stop"], fixture.Events);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(capabilities.GetEnabledCommandIds()),
            fixture.Config()["gateway"]!["nodes"]!["commands"]!["allow"]));
        Assert.Equal(credential, record.SharedGatewayToken);
        Assert.Equal(record.Id, fixture.Registry.ActiveGatewayId);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"gateway":null}""")]
    [InlineData("""{"gateway":[]}""")]
    [InlineData("""{"gateway":{"nodes":null}}""")]
    [InlineData("""{"gateway":{"nodes":[]}}""")]
    [InlineData("""{"gateway":{"nodes":{"commands":null}}}""")]
    [InlineData("""{"gateway":{"nodes":{"commands":[]}}}""")]
    public async Task CapabilityCompletion_RejectsInvalidConfigurationShapeWithoutPublishing(string invalidConfig)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkWizardCompleted();
        // Restore reload first so this isolates the capability edit on an unchanged invalid file.
        fixture.Host.Validate = () => throw new InvalidOperationException("retry finalization");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        fixture.Host.Validate = () => { };
        var original = File.ReadAllText(fixture.ConfigPath);
        File.WriteAllText(fixture.ConfigPath, invalidConfig);
        fixture.Events.Clear();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                session.CompleteAsync(CancellationToken.None, new CapabilitiesConfig()));

            Assert.Contains("Native Gateway configuration", error.Message);
            Assert.Equal(invalidConfig, File.ReadAllText(fixture.ConfigPath));
            Assert.Equal(["stop", "stop"], fixture.Events);
            Assert.Empty(fixture.Registry.GetAll());
        }
        finally
        {
            File.WriteAllText(fixture.ConfigPath, original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityCompletion_CancelledOrUnfinishedWizardDoesNotWriteSelections(bool cancelled)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var original = File.ReadAllText(fixture.ConfigPath);
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
        {
            session.MarkWizardCompleted();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.CompleteAsync(cancellation.Token, new CapabilitiesConfig { Camera = false }));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.CompleteAsync(cancellation.Token, new CapabilitiesConfig { Camera = false }));
        }

        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath));
        Assert.Null(fixture.Config()["gateway"]!["nodes"]);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.DoesNotContain("health", fixture.Events);
    }

    [Fact]
    public async Task CapabilityCompletion_CancelledDuringStopDoesNotWriteSelections()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var original = File.ReadAllText(fixture.ConfigPath);
        using var cancellation = new CancellationTokenSource();
        fixture.Runtime.Stop = cancellation.Cancel;
        fixture.Events.Clear();
        session.MarkWizardCompleted();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.CompleteAsync(cancellation.Token, new CapabilitiesConfig { Camera = false }));

        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath));
        Assert.Equal(["stop", "stop"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task CapabilityCompletion_NullSelectionPreservesExistingNodePolicy()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var config = fixture.Config();
        var nodes = JsonNode.Parse("""{"commands":{"allow":["camera.snap"],"deny":["system.run"]}}""")!;
        config["gateway"]!["nodes"] = nodes.DeepClone();
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        session.MarkWizardCompleted();

        await session.CompleteAsync(CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(nodes, fixture.Config()["gateway"]!["nodes"]));
    }

    [Fact]
    public async Task DeferredOptionalSetup_StillRequiresConfigAndHealthBeforePublication()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkOptionalSetupDeferred();
        fixture.Events.Clear();
        await session.CompleteAsync(CancellationToken.None);
        Assert.Equal(["stop", "validate", "start", "inspect", "health", "stop", "stop"], fixture.Events);
        Assert.Equal(session.Record.Id, fixture.Registry.ActiveGatewayId);
    }

    [Fact]
    public async Task DeferredOptionalSetup_FailedHealthCannotPublish()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkOptionalSetupDeferred();
        fixture.Host.Health = () => throw new InvalidOperationException("unhealthy");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewWizard_CannotReusePriorOptionalSetupHandoff(bool restart)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkOptionalSetupDeferred();
        if (restart)
            await session.RestartAsync(CancellationToken.None);
        else
            session.BeginWizard();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task WizardPairing_ApprovesOnlyItsOwnRequestOnTheVerifiedProfile()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session);
        fixture.Events.Clear();
        await session.ApproveWizardPairingAsync("request-1", CancellationToken.None);
        Assert.Equal(["start", "inspect", "list-pairing", "start", "inspect", "approve-pairing"], fixture.Events);
        Assert.Equal("request-1", fixture.Host.ApprovedId);
        Assert.Equal(fixture.ConfigPath, fixture.Host.PairingEnvironment!["OPENCLAW_CONFIG_PATH"]);
        Assert.Equal(new Uri(session.Record.Url).Port.ToString(), fixture.Host.PairingEnvironment["OPENCLAW_GATEWAY_PORT"]);
        Assert.Equal("", fixture.Host.PairingEnvironment["OPENCLAW_GATEWAY_URL"]);
        Assert.Equal(session.Record.SharedGatewayToken, fixture.Host.PairingEnvironment["OPENCLAW_GATEWAY_TOKEN"]);
        Assert.Empty(fixture.Registry.GetAll());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("different-device")]
    [InlineData("different-key")]
    [InlineData("different-request")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task WizardPairing_RejectsUnrelatedOrAmbiguousRequests(string mismatch)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session, mismatch);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ApproveWizardPairingAsync("request-1", CancellationToken.None));
        Assert.Null(fixture.Host.ApprovedId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("request; exit")]
    public async Task WizardPairing_RejectsUnsafeIdsBeforeAnyCommand(string? requestId)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ApproveWizardPairingAsync(requestId, CancellationToken.None));
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WizardPairing_RejectsUnownedOrReplacedEndpoints(bool replaceAfterListing)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session);
        if (replaceAfterListing)
            fixture.Host.OnList = () => fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        else
            fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ApproveWizardPairingAsync("request-1", CancellationToken.None));
        Assert.Null(fixture.Host.ApprovedId);
    }

    [Fact]
    public async Task WizardPairing_CancellationAfterListingPreventsApproval()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Host.PendingRequests = PairingRequests(session);
        fixture.Host.OnList = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ApproveWizardPairingAsync("request-1", cancellation.Token));
        Assert.Null(fixture.Host.ApprovedId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WizardPairing_RejectsConfigurationRedirects(bool changeAfterListing)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session);
        void Redirect()
        {
            var config = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!;
            config["gateway"]!["mode"] = "remote";
            File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        }
        if (changeAfterListing)
            fixture.Host.OnList = Redirect;
        else
            Redirect();
        fixture.Events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ApproveWizardPairingAsync("request-1", CancellationToken.None));
        Assert.Null(fixture.Host.ApprovedId);
        if (!changeAfterListing)
            Assert.DoesNotContain("list-pairing", fixture.Events);
    }

    [Fact]
    public async Task WizardPairing_RequiresTheExactApprovalAcknowledgement()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session);
        fixture.Host.ApprovalReplyId = "another-request";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ApproveWizardPairingAsync("request-1", CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
    }

    private static string PairingRequests(NativeGatewaySetupSession session, string? mismatch = null)
    {
        var identity = new DeviceIdentity(session.IdentityDirectory);
        identity.Initialize();
        var request = new
        {
            requestId = mismatch == "different-request" ? "request-2" : "request-1",
            deviceId = mismatch == "different-device" ? "another-device" : identity.DeviceId,
            publicKey = mismatch == "different-key" ? "another-key" : identity.PublicKeyBase64Url,
        };
        return JsonSerializer.Serialize(new
        {
            pending = mismatch == "missing" ? [] : mismatch == "duplicate" ? new[] { request, request } : [request]
        });
    }

    [Theory]
    [InlineData("f0082bc4-c57a-41f1-b9fe-7f59f02b8ec5", true)]
    [InlineData(null, false)]
    [InlineData("request; exit", false)]
    public void PairingGuidance_UsesOnlyAnExplicitSafeRequest(string? requestId, bool safe)
    {
        var guidance = NativeGatewaySetupSession.GetPairingGuidance(requestId);
        Assert.DoesNotContain("--latest", guidance);
        Assert.Contains("this setup profile", guidance);
        Assert.Contains("Retry", guidance);
        if (safe)
            Assert.Contains($"openclaw devices approve {requestId}", guidance);
        else
        {
            Assert.Contains("openclaw devices list", guidance);
            if (requestId is not null) Assert.DoesNotContain(requestId, guidance);
        }
    }

    [Fact]
    public async Task Draft_ResumesAfterClosingSetup_WithoutReplacingCredentials()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Service.CreateDraftAsync(CancellationToken.None);
        string? token;
        await using (var session = await fixture.Service.PrepareAsync(draft, CancellationToken.None))
            token = session.Record.SharedGatewayToken;
        var reopened = new NativeGatewaySetupService(new GatewayRegistry(fixture.Temp.Path),
            fixture.Resolver, fixture.Host, () => fixture.Runtime);
        Assert.Equal(draft, await reopened.CreateDraftAsync(CancellationToken.None));
        await using var resumed = await reopened.PrepareAsync(draft, CancellationToken.None);
        Assert.Equal(token, resumed.Record.SharedGatewayToken);
        resumed.MarkWizardCompleted();
        await resumed.CompleteAsync(CancellationToken.None);
        var next = await reopened.CreateDraftAsync(CancellationToken.None);
        Assert.NotEqual(draft.GatewayId, next.GatewayId);
    }

    [Fact]
    public async Task Prepare_UsesStagedProfile_AndPublishesOnlyAfterWizardValidationHealthAndStop()
    {
        using var fixture = new Fixture();
        fixture.Registry.AddOrUpdate(new GatewayRecord { Id = "old", Url = "wss://existing.example" });
        fixture.Registry.SetActive("old");
        fixture.Registry.Save();
        await using var session = await fixture.PrepareAsync();
        Assert.Equal("old", fixture.Registry.ActiveGatewayId);
        Assert.Equal(["prepare", "validate", "start", "inspect"], fixture.Events);
        Assert.Equal([NativeGatewaySetupStage.StartingGateway, NativeGatewaySetupStage.VerifyingEndpoint],
            fixture.Host.Progress);
        Assert.Equal("off", fixture.Config()["gateway"]!["reload"]!["mode"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Equal("old", fixture.Registry.ActiveGatewayId);
        fixture.Events.Clear();
        session.MarkWizardCompleted();
        var record = await session.CompleteAsync(CancellationToken.None);
        Assert.Equal(["stop", "validate", "start", "inspect", "health", "stop", "stop"], fixture.Events);
        Assert.Equal(Family, record.NativePackageFamilyName);
        Assert.Null(record.SetupManagedDistroName);
        Assert.Null(record.BootstrapToken);
        Assert.Equal(64, record.SharedGatewayToken!.Length);
        Assert.True(record.RequiresV2Signature);
        Assert.Equal(record.Id, fixture.Registry.ActiveGatewayId);
        Assert.NotNull(fixture.Registry.GetById("old"));
        Assert.False(File.Exists(fixture.Temp.Combine("settings.json")));
        Assert.Null(fixture.Config()["gateway"]!["reload"]!["mode"]);
    }

    [Fact]
    public async Task Prepare_UntrustedListener_NeverSendsCredentialOrRegisters()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.PrepareAsync);
        Assert.Equal(["prepare", "validate", "start", "inspect", "dispose"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Null(fixture.Config()["gateway"]!["reload"]!["mode"]);
    }

    [Fact]
    public async Task EveryCredentialHandoff_RechecksOwnership_EvenWithExistingIdentity()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthorizeAsync(CancellationToken.None));
        Assert.DoesNotContain("health", fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task FailedHealth_RetainsConfigurationAndAllowsFinalizationRetry()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.Health = () => throw new InvalidOperationException("health failed");
        session.MarkWizardCompleted();
        var credential = session.Record.SharedGatewayToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Null(fixture.Config()["gateway"]!["reload"]!["mode"]);
        fixture.Host.Health = () => { };
        var record = await session.CompleteAsync(CancellationToken.None);
        Assert.Equal(credential, record.SharedGatewayToken);
    }

    [Fact]
    public async Task FailedValidation_NeverCallsHealthOrPublishes()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.Validate = () => throw new InvalidOperationException("invalid provider config");
        session.MarkWizardCompleted();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.DoesNotContain("health", fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Equal("stop", fixture.Events.Last());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("hybrid")]
    [InlineData("restart")]
    public async Task Cancel_StopsRuntime_RestoresOriginalReload_AndRetainsProfileForRetry(string originalMode)
    {
        using var fixture = new Fixture();
        await using (var first = await fixture.PrepareAsync()) { }
        var config = fixture.Config();
        config["gateway"]!["reload"]!["mode"] = originalMode;
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        var credential = config["gateway"]!["auth"]!["token"]!.GetValue<string>();
        var session = await fixture.PrepareAsync();
        Assert.Equal("off", fixture.Config()["gateway"]!["reload"]!["mode"]!.GetValue<string>());
        await session.RestartAsync(CancellationToken.None);
        await session.DisposeAsync();
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        Assert.Equal(originalMode, fixture.Config()["gateway"]!["reload"]!["mode"]!.GetValue<string>());
        Assert.Equal(credential, fixture.Config()["gateway"]!["auth"]!["token"]!.GetValue<string>());
        Assert.Empty(fixture.Registry.GetAll());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AuthorizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Restart_UsesSameProfile_AndCannotCompleteUntilNewWizardFinishes()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var token = session.Record.SharedGatewayToken;
        session.MarkWizardCompleted();
        await session.RestartAsync(CancellationToken.None);
        Assert.Equal(token, session.Record.SharedGatewayToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task Restart_RejectsChangedEndpointBeforeStartingOrSendingCredentials()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var config = fixture.Config();
        config["gateway"]!["bind"] = "lan";
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        fixture.Events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestartAsync(CancellationToken.None));
        Assert.Equal(["stop"], fixture.Events);
    }

    [Fact]
    public async Task NewWizardAfterFinalizationFailure_SuspendsReloadAgain()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkWizardCompleted();
        fixture.Host.Health = () => throw new InvalidOperationException("health failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        await session.PrepareWizardAsync(CancellationToken.None);
        Assert.Equal("off", fixture.Config()["gateway"]!["reload"]!["mode"]!.GetValue<string>());
        session.BeginWizard();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Publication_ReloadsRegistryAndPreservesOtherGatewayChanges()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var otherWriter = new GatewayRegistry(fixture.Temp.Path);
        otherWriter.AddOrUpdate(new GatewayRecord { Id = "other", Url = "wss://other.example" });
        otherWriter.Save();
        session.MarkWizardCompleted();
        await session.CompleteAsync(CancellationToken.None);
        var reloaded = new GatewayRegistry(fixture.Temp.Path);
        reloaded.Load();
        Assert.NotNull(reloaded.GetById("other"));
        Assert.Equal(session.Record.Id, reloaded.ActiveGatewayId);
    }

    [Fact]
    public async Task CancelledPreparation_DoesNotStartOrPersist()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Host.Prepare = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PrepareAsync(
            fixture.Draft, cancellation.Token));
        Assert.Equal(["prepare"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task ChangedPackage_IsRejectedBeforeCreatingConfig()
    {
        using var fixture = new Fixture();
        fixture.Resolver.FamilyName = "OpenClaw.Gateway_different1234";
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.PrepareAsync);
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Theory]
    [InlineData("remote", "loopback", "token", 18789)]
    [InlineData("local", "lan", "token", 18789)]
    [InlineData("local", "loopback", "none", 18789)]
    [InlineData("local", "loopback", "token", 18800)]
    public void ConfiguredProfile_RejectsUnsafeOrChangedEndpoint(string mode, string bind, string authMode, int port)
    {
        var json = JsonSerializer.Serialize(new
        {
            gateway = new { mode, bind, port, auth = new { mode = authMode, token = "fixture" } },
        });
        Assert.Throws<InvalidOperationException>(() => NativeGatewaySetupService.ReadConfiguredRecord(
            new("0123456789abcdef", 18789, Family), json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"gateway":null}""")]
    [InlineData("""{"gateway":{"mode":"local","bind":"loopback","port":18789,"auth":{"mode":"token","token":""}}}""")]
    public void ConfiguredProfile_RejectsMissingConfiguration(string json) =>
        Assert.Throws<InvalidOperationException>(() => NativeGatewaySetupService.ReadConfiguredRecord(
            new("0123456789abcdef", 18789, Family), json));

    [Fact]
    public void Environment_MapsCrossPackageStatePaths()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var physicalRoot = temp.Combine("physical");
        var environment = NativeGatewaySetupService.BuildEnvironment(registry, "0123456789abcdef",
            new Resolver { Map = path => Path.Combine(physicalRoot, Path.GetRelativePath(temp.Path, path)) });
        Assert.StartsWith(physicalRoot, environment["OPENCLAW_STATE_DIR"]);
        Assert.StartsWith(physicalRoot, environment["OPENCLAW_CONFIG_PATH"]);
        Assert.DoesNotContain("TOKEN", string.Join(",", environment.Keys));
    }

    private sealed class Fixture : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        public List<string> Events { get; } = [];
        public GatewayRegistry Registry { get; }
        public Resolver Resolver { get; } = new();
        public Host Host { get; }
        public Runtime Runtime { get; }
        public NativeGatewaySetupService Service { get; }
        public NativeGatewaySetupDraft Draft { get; } = new("0123456789abcdef", 18789, Family);
        public string ConfigPath => NativeGatewayPaths.GetConfigPath(Registry, Draft.GatewayId);
        public JsonObject Config() => JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        public Fixture()
        {
            Registry = new GatewayRegistry(Temp.Path);
            Host = new Host(Events);
            Runtime = new Runtime(Events);
            Service = new NativeGatewaySetupService(Registry, Resolver, Host, () => Runtime);
        }
        public Task<NativeGatewaySetupSession> PrepareAsync() => Service.PrepareAsync(Draft, CancellationToken.None);
        public void Dispose() => Temp.Dispose();
    }

    private sealed class Resolver : INativeGatewayPackageResolver
    {
        public string FamilyName { get; set; } = Family;
        public Func<string, string> Map { get; set; } = path => path;
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NativeGatewayPackage(FamilyName, "0.0.0.1", @"C:\package\openclaw.exe", @"C:\package\clawctl.exe"));
        public string ResolveDataPath(string path) => Map(path);
    }

    private sealed class Host(List<string> events) : INativeGatewaySetupHost
    {
        public string PendingRequests { get; set; } = "{\"pending\":[]}";
        public Action OnList { get; set; } = () => { };
        public string? ApprovedId { get; private set; }
        public string? ApprovalReplyId { get; set; }
        public IReadOnlyDictionary<string, string>? PairingEnvironment { get; private set; }
        public Task<string> ListDevicePairingRequestsAsync(NativeGatewayPackage package,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("list-pairing");
            PairingEnvironment = environment;
            OnList();
            return Task.FromResult(PendingRequests);
        }
        public Task<string> ApproveDevicePairingAsync(NativeGatewayPackage package, string requestId,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("approve-pairing");
            ApprovedId = requestId;
            return Task.FromResult(JsonSerializer.Serialize(new { requestId = ApprovalReplyId ?? requestId }));
        }

        public List<NativeGatewaySetupStage> Progress { get; } = [];
        public void ReportProgress(NativeGatewaySetupStage stage) => Progress.Add(stage);
        public Action Prepare { get; set; } = () => { };
        public Action Health { get; set; } = () => { };
        public Action Validate { get; set; } = () => { };
        public IDisposable OpenRecoveryTerminal(NativeGatewayPackage package,
            IReadOnlyDictionary<string, string> environment) => throw new NotSupportedException();
        public Task PreparePackageAsync(NativeGatewayPackage package, IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            events.Add("prepare");
            Prepare();
            return Task.CompletedTask;
        }
        public Task ValidateConfigurationAsync(NativeGatewayPackage package, IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            events.Add("validate");
            Validate();
            return Task.CompletedTask;
        }
        public Task VerifyHealthAsync(NativeGatewayPackage package, IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            events.Add("health");
            Health();
            return Task.CompletedTask;
        }
    }

    private sealed class Runtime(List<string> events) : INativeGatewayRuntime
    {
        public GatewayEndpointProvenanceKind Provenance { get; set; } = GatewayEndpointProvenanceKind.ExpectedManagedGateway;
        public Action Stop { get; set; } = () => { };
        public Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
        {
            events.Add("start");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add("stop");
            Stop();
            return Task.CompletedTask;
        }
        public Task<GatewayEndpointProvenance> InspectAsync(GatewayRecord record, CancellationToken cancellationToken)
        {
            events.Add("inspect");
            return Task.FromResult(new GatewayEndpointProvenance(Provenance, new Uri(record.Url).Port));
        }
        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
