using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewaySetupTests
{
    private const string Family = "OpenClaw.Gateway_123456789abcd";

    // Session events from one verified credential handoff: pairing configuration check,
    // explicit start request, then fresh ownership inspection.
    private static readonly string[] s_authorize = ["agent-check", "start", "inspect"];

    [Fact]
    public async Task DevelopmentToStorePackage_RequiresConsentAndPreservesOldProfile()
    {
        using var fixture = new Fixture();
        var previous = await fixture.Service.CreateDraftAsync(default);
        var draftPath = NativeGatewaySetupService.GetDraftPath(fixture.Registry);
        var savedDraft = File.ReadAllBytes(draftPath);
        var stateDirectory = NativeGatewayPaths.GetStateDirectory(fixture.Registry, previous.GatewayId);
        Directory.CreateDirectory(stateDirectory);
        var markerPath = Path.Combine(stateDirectory, "existing-profile.txt");
        File.WriteAllText(markerPath, "preserved profile data");
        fixture.Resolver.FamilyName = "OpenClawFoundation.OpenClawGateway_rfcbke2p71se2";

        await Assert.ThrowsAsync<NativeGatewaySetupService.NativeGatewayDraftRecoveryRequiredException>(
            () => fixture.Service.CreateDraftAsync(default));
        Assert.Equal(savedDraft, File.ReadAllBytes(draftPath));
        Assert.Equal("preserved profile data", File.ReadAllText(markerPath));
        Assert.Empty(fixture.Events);

        await fixture.Service.DiscardIncompatibleDraftAsync(default);
        var refreshed = await fixture.Service.CreateDraftAsync(default);

        Assert.NotEqual(previous.GatewayId, refreshed.GatewayId);
        Assert.Equal(fixture.Resolver.FamilyName, refreshed.PackageFamilyName);
        Assert.Equal(NativeGatewayContract.IsolatedSessionV1, refreshed.Contract);
        Assert.Equal("preserved profile data", File.ReadAllText(markerPath));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Theory]
    // Written by the retired same-user runtime: Contract = Legacy (1), with or without a port change.
    [InlineData("""{"Contract":1}""")]
    [InlineData("""{"Contract":1,"PreviousPort":18790}""")]
    // Written before runtime contracts were introduced: Contract omitted.
    [InlineData("""{}""")]
    [InlineData("""{"Contract":0}""")]
    public async Task RetiredSameUserDraft_RequiresExplicitDiscardBeforeCreatingIsolatedProfile(string legacyFields)
    {
        using var fixture = new Fixture();
        var legacy = new NativeGatewaySetupDraft("0123456789abcdef", 18789, Family);
        var stateDirectory = NativeGatewayPaths.GetStateDirectory(fixture.Registry, legacy.GatewayId);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, "credential-marker.txt"), "legacy-credential");
        var draftPath = NativeGatewaySetupService.GetDraftPath(fixture.Registry);
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        var draft = JsonSerializer.SerializeToNode(new { legacy.GatewayId, legacy.Port, legacy.PackageFamilyName })!.AsObject();
        foreach (var (name, value) in System.Text.Json.Nodes.JsonNode.Parse(legacyFields)!.AsObject())
            draft[name] = value?.DeepClone();
        var legacyJson = draft.ToJsonString();
        File.WriteAllText(draftPath, legacyJson);

        await Assert.ThrowsAsync<NativeGatewaySetupService.NativeGatewayDraftRecoveryRequiredException>(
            () => fixture.Service.CreateDraftAsync(default));
        Assert.Equal(legacyJson, File.ReadAllText(draftPath));
        var saved = JsonSerializer.Deserialize<NativeGatewaySetupDraft>(legacyJson)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(saved, default));
        Assert.Empty(fixture.Events);

        await fixture.Service.DiscardIncompatibleDraftAsync(default);
        var isolated = await fixture.Service.CreateDraftAsync(default);

        Assert.NotEqual(legacy.GatewayId, isolated.GatewayId);
        Assert.Equal(NativeGatewayContract.IsolatedSessionV1, isolated.Contract);
        Assert.Equal("legacy-credential", File.ReadAllText(Path.Combine(stateDirectory, "credential-marker.txt")));
        Assert.Equal(isolated, JsonSerializer.Deserialize<NativeGatewaySetupDraft>(File.ReadAllText(draftPath)));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public void IsolatedDraftContractKeepsItsPersistedValue()
    {
        // Saved drafts store the numeric enum value, so removing a contract must not renumber this one.
        Assert.Equal(2, (int)NativeGatewayContract.IsolatedSessionV1);
        var draft = new NativeGatewaySetupDraft("0123456789abcdef", 18789, Family)
        {
            Contract = NativeGatewayContract.IsolatedSessionV1
        };
        Assert.Contains("\"Contract\":2", JsonSerializer.Serialize(draft), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftRecovery_PreservesProfileDataAfterRemovingPublishedRegistration(bool published)
    {
        using var fixture = new Fixture();
        var draft = await fixture.Service.CreateDraftAsync(default);
        await using (var session = await fixture.Service.PrepareAsync(draft, default))
        {
            if (published)
            {
                session.MarkWizardCompleted();
                await session.CompleteAsync(default);
            }
        }
        var identityDirectory = fixture.Registry.GetIdentityDirectory(draft.GatewayId);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        var before = Directory.GetFiles(identityDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        fixture.Registry.Remove(draft.GatewayId);
        fixture.Registry.Save();

        // Descriptors written before runtime contracts were introduced omit Contract.
        File.WriteAllText(NativeGatewaySetupService.GetDraftPath(fixture.Registry),
            JsonSerializer.Serialize(new { draft.GatewayId, draft.Port, draft.PackageFamilyName }));
        await Assert.ThrowsAsync<NativeGatewaySetupService.NativeGatewayDraftRecoveryRequiredException>(
            () => fixture.Service.CreateDraftAsync(default));

        await fixture.Service.DiscardIncompatibleDraftAsync(default);
        var replacement = await fixture.Service.CreateDraftAsync(default);

        Assert.NotEqual(draft.GatewayId, replacement.GatewayId);
        Assert.Equal(NativeGatewayContract.IsolatedSessionV1, replacement.Contract);
        Assert.Equal(before.Count, Directory.GetFiles(identityDirectory, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task PublishedNativeProfileCannotBeDiscardedByDraftRecovery()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Service.CreateDraftAsync(default);
        var stateDirectory = NativeGatewayPaths.GetStateDirectory(fixture.Registry, draft.GatewayId);
        Directory.CreateDirectory(stateDirectory);
        var credentialPath = Path.Combine(stateDirectory, "credential-marker.txt");
        File.WriteAllText(credentialPath, "preserved-credential");
        fixture.Registry.AddOrUpdate(new GatewayRecord
        {
            Id = draft.GatewayId,
            Url = $"ws://127.0.0.1:{draft.Port}",
            IsLocal = true,
            NativePackageFamilyName = draft.PackageFamilyName
        });
        fixture.Registry.Save();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.DiscardIncompatibleDraftAsync(default));

        Assert.True(File.Exists(credentialPath));
        Assert.True(File.Exists(NativeGatewaySetupService.GetDraftPath(fixture.Registry)));
        Assert.NotNull(fixture.Registry.GetById(draft.GatewayId));
    }

    [Fact]
    public void PortSelection_PrefersConventionalPortWhenAvailable()
    {
        Assert.Equal(18789, NativeGatewaySetupService.PreferredPort);
        int free;
        using (var probe = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true })
        {
            probe.Start();
            free = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Assert.Equal(free, NativeGatewaySetupService.SelectAvailablePort(free));
    }

    [Fact]
    public void PortSelection_FallsBackWhenConventionalPortIsOccupied()
    {
        using var occupant = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        occupant.Start();
        var occupied = ((IPEndPoint)occupant.LocalEndpoint).Port;

        var selected = NativeGatewaySetupService.SelectAvailablePort(occupied);

        Assert.NotEqual(occupied, selected);
        Assert.InRange(selected, 1, 65535);
    }

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
    public async Task CapabilityCompletion_AppliesSelectionsBeforeValidationAndHealth(bool deferred)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var capabilities = new CapabilitiesConfig
        {
            System = true, Canvas = false, Screen = true, Camera = false,
            Location = false, Browser = false, Device = true, Tts = true, Stt = false,
        };
        fixture.Events.Clear();
        fixture.Host.Validate = () =>
        {
            Assert.Equal(["stop", "agent-capabilities", "agent-check", "validate"], fixture.Events);
            Assert.Equal(capabilities.GetEnabledCommandIds(), fixture.Host.AppliedCommands);
            Assert.Empty(fixture.Registry.GetAll());
        };
        fixture.Host.Health = () => Assert.Empty(fixture.Registry.GetAll());
        if (deferred)
            session.MarkOptionalSetupDeferred();
        else
            session.MarkWizardCompleted();

        await session.CompleteAsync(CancellationToken.None, capabilities);

        Assert.Equal(["stop", "agent-capabilities", "agent-check", "validate", .. s_authorize, "health", "stop", "stop"],
            fixture.Events);
        Assert.DoesNotContain(fixture.Host.AppliedCommands!, command => command.StartsWith("camera.", StringComparison.Ordinal));
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

        Assert.Equal(["stop", "agent-capabilities", "agent-check", "validate", "stop"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Equal(capabilities.GetEnabledCommandIds(), fixture.Host.AppliedCommands);
        fixture.Host.Validate = () => { };
        capabilities.Camera = true;
        fixture.Events.Clear();

        var record = await session.CompleteAsync(CancellationToken.None, capabilities);

        Assert.Equal(["stop", "agent-capabilities", "agent-check", "validate", .. s_authorize, "health", "stop", "stop"],
            fixture.Events);
        Assert.Equal(capabilities.GetEnabledCommandIds(), fixture.Host.AppliedCommands);
        Assert.Equal(credential, record.SharedGatewayToken);
        Assert.Equal(record.Id, fixture.Registry.ActiveGatewayId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityCompletion_CancelledOrUnfinishedWizardDoesNotWriteSelections(bool cancelled)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
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

        Assert.Null(fixture.Host.AppliedCommands);
        Assert.DoesNotContain("agent-capabilities", fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.DoesNotContain("health", fixture.Events);
    }

    [Fact]
    public async Task CapabilityCompletion_CancelledDuringStopDoesNotWriteSelections()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Runtime.Stop = cancellation.Cancel;
        fixture.Events.Clear();
        session.MarkWizardCompleted();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.CompleteAsync(cancellation.Token, new CapabilitiesConfig { Camera = false }));

        Assert.Null(fixture.Host.AppliedCommands);
        Assert.Equal(["stop", "stop"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task CapabilityCompletion_NullSelectionPreservesExistingNodePolicy()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkWizardCompleted();

        await session.CompleteAsync(CancellationToken.None);

        Assert.Null(fixture.Host.AppliedCommands);
        Assert.DoesNotContain("agent-capabilities", fixture.Events);
        Assert.Equal(session.Record.Id, fixture.Registry.ActiveGatewayId);
    }

    [Fact]
    public async Task DeferredOptionalSetup_StillRequiresConfigAndHealthBeforePublication()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        session.MarkOptionalSetupDeferred();
        fixture.Events.Clear();
        await session.CompleteAsync(CancellationToken.None);
        Assert.Equal(["stop", "agent-check", "validate", .. s_authorize, "health", "stop", "stop"], fixture.Events);
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
        Assert.Equal(
            [.. s_authorize, "agent-check", "list-pairing", .. s_authorize, "agent-check", "approve-pairing"],
            fixture.Events);
        Assert.Equal("request-1", fixture.Host.ApprovedId);
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WizardPairing_RejectsChangedPackagePortOrToken(bool changeAfterListing, bool changePort)
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.PendingRequests = PairingRequests(session);
        void Redirect() => fixture.Host.IsolatedConfiguration = changePort
            ? fixture.Host.IsolatedConfiguration with { Port = fixture.Host.IsolatedConfiguration.Port + 1 }
            : fixture.Host.IsolatedConfiguration with { Token = "replaced-agent-token" };
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
        fixture.Host.IsolatedConfiguration = new(draft.Port, "agent-token");
        string? token;
        await using (var session = await fixture.Service.PrepareAsync(draft, CancellationToken.None))
            token = session.Record.SharedGatewayToken;
        var reopened = new NativeGatewaySetupService(new GatewayRegistry(fixture.Temp.Path),
            fixture.Resolver, fixture.Host, () => fixture.Runtime);
        Assert.Equal(draft, await reopened.CreateDraftAsync(CancellationToken.None));
        await using var resumed = await reopened.PrepareAsync(draft, CancellationToken.None);
        Assert.Equal("agent-token", token);
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
        Assert.Equal(["prepare", "agent-config", "validate", .. s_authorize], fixture.Events);
        Assert.Equal([NativeGatewaySetupStage.StartingGateway, NativeGatewaySetupStage.VerifyingEndpoint],
            fixture.Host.Progress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Equal("old", fixture.Registry.ActiveGatewayId);
        fixture.Events.Clear();
        session.MarkWizardCompleted();
        var record = await session.CompleteAsync(CancellationToken.None);
        Assert.Equal(["stop", "agent-check", "validate", .. s_authorize, "health", "stop", "stop"], fixture.Events);
        Assert.Equal(Family, record.NativePackageFamilyName);
        Assert.Equal(NativeGatewayPackageClient.IsolatedContract, record.NativeRuntimeContract);
        Assert.Null(record.SetupManagedDistroName);
        Assert.Null(record.BootstrapToken);
        Assert.Equal(fixture.Host.IsolatedConfiguration.Token, record.SharedGatewayToken);
        Assert.True(record.RequiresV2Signature);
        Assert.Equal(record.Id, fixture.Registry.ActiveGatewayId);
        Assert.NotNull(fixture.Registry.GetById("old"));
        Assert.False(File.Exists(fixture.Temp.Combine("settings.json")));
    }

    [Fact]
    public async Task Prepare_UntrustedListener_NeverSendsCredentialOrRegisters()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.PrepareAsync);
        Assert.Equal(["prepare", "agent-config", "validate", .. s_authorize, "dispose"], fixture.Events);
        Assert.Empty(fixture.Registry.GetAll());
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

    [Fact]
    public async Task Cancel_DisposesRuntimeAndRetainsProfileForRetry()
    {
        using var fixture = new Fixture();
        var session = await fixture.PrepareAsync();
        var credential = session.Record.SharedGatewayToken;
        await session.RestartAsync(CancellationToken.None);
        await session.DisposeAsync();
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        Assert.Equal("dispose", fixture.Events.Last());
        Assert.Empty(fixture.Registry.GetAll());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AuthorizeAsync(CancellationToken.None));

        await using var retry = await fixture.PrepareAsync();
        Assert.Equal(credential, retry.Record.SharedGatewayToken);
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
    public async Task Restart_RejectsChangedPackageCredentialBeforeVerifyingOrSendingCredentials()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        fixture.Host.IsolatedConfiguration = fixture.Host.IsolatedConfiguration with { Token = "replaced-agent-token" };
        fixture.Events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestartAsync(CancellationToken.None));
        Assert.Equal(["validate", "restart", "agent-check"], fixture.Events);
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
    public async Task Publication_DoesNotOverwriteAConcurrentlyPublishedDraftId()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.PrepareAsync();
        var other = new GatewayRegistry(fixture.Temp.Path);
        other.AddOrUpdate(new GatewayRecord { Id = session.Record.Id, Url = "wss://other.example" });
        other.Save();
        session.MarkWizardCompleted();
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() => session.CompleteAsync(default));
        other.Load();
        Assert.Equal("wss://other.example", other.GetById(session.Record.Id)!.Url);
    }

    [Fact]
    public async Task Cancel_DrainsOwnedRuntimeAndRetainsPairedIdentityAndDraft()
    {
        using var fixture = new Fixture();
        var draftPath = NativeGatewaySetupService.GetDraftPath(fixture.Registry);
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        var draftJson = JsonSerializer.Serialize(fixture.Draft);
        File.WriteAllText(draftPath, draftJson);
        var session = await fixture.PrepareAsync();
        var identity = new DeviceIdentity(session.IdentityDirectory);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "retained-node-token");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.DisposeRuntime = () => { entered.SetResult(); return finish.Task; };
        var closing = session.DisposeAsync().AsTask();
        await entered.Task;
        Assert.False(closing.IsCompleted);
        finish.SetResult();
        await closing;
        identity.LoadExisting();
        Assert.Equal("retained-node-token", identity.NodeDeviceToken);
        Assert.Equal(draftJson, File.ReadAllText(draftPath));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task CancelledPreparation_DoesNotConfigureStartOrPersist()
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
    public async Task ChangedPackage_IsRejectedBeforePreparingThePackage()
    {
        using var fixture = new Fixture();
        fixture.Resolver.FamilyName = "OpenClaw.Gateway_different1234";
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.PrepareAsync);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task IsolatedSetupUsesAgentConfigurationAndPublishesOnlyAfterHealthyWizard()
    {
        using var fixture = new Fixture();
        NativeGatewaySetupDraft draft = await fixture.Service.CreateDraftAsync(CancellationToken.None);
        fixture.Host.IsolatedConfiguration = new(20123, "preserved-agent-token");

        await using var session = await fixture.Service.PrepareAsync(draft, CancellationToken.None);

        Assert.Equal("ws://127.0.0.1:20123", session.Record.Url);
        Assert.Equal("preserved-agent-token", session.Record.SharedGatewayToken);
        Assert.Equal(NativeGatewayPackageClient.IsolatedContract, session.Record.NativeRuntimeContract);
        Assert.False(Directory.Exists(NativeGatewayPaths.GetStateDirectory(fixture.Registry, draft.GatewayId)));
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Equal(20123, JsonSerializer.Deserialize<NativeGatewaySetupDraft>(
            File.ReadAllText(NativeGatewaySetupService.GetDraftPath(fixture.Registry)))!.Port);

        var capabilities = new CapabilitiesConfig { System = true, Screen = true };
        session.MarkWizardCompleted();
        GatewayRecord published = await session.CompleteAsync(CancellationToken.None, capabilities);

        Assert.Equal(capabilities.GetEnabledCommandIds(), fixture.Host.AppliedCommands);
        Assert.Equal(published, fixture.Registry.GetById(draft.GatewayId));
        Assert.Contains("health", fixture.Events);
        Assert.False(Directory.Exists(NativeGatewayPaths.GetStateDirectory(fixture.Registry, draft.GatewayId)));
    }

    [Fact]
    public async Task IsolatedSetupRetriesCancelledDraftWithoutReplacingAgentCredentials()
    {
        using var fixture = new Fixture();
        NativeGatewaySetupDraft draft = await fixture.Service.CreateDraftAsync(CancellationToken.None);
        fixture.Host.IsolatedConfiguration = new(draft.Port, "agent-token");
        await using (var first = await fixture.Service.PrepareAsync(draft, CancellationToken.None)) { }

        NativeGatewaySetupDraft resumed = await fixture.Service.CreateDraftAsync(CancellationToken.None);
        await using var retry = await fixture.Service.PrepareAsync(resumed, CancellationToken.None);

        Assert.Equal(draft, resumed);
        Assert.Equal("agent-token", retry.Record.SharedGatewayToken);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task IsolatedSetupRejectsChangedPortAndTokenBeforePublicationWithoutRepairingConfiguration()
    {
        using var fixture = new Fixture();
        NativeGatewaySetupDraft draft = await fixture.Service.CreateDraftAsync(CancellationToken.None);
        fixture.Host.IsolatedConfiguration = new(draft.Port, "first-token");
        await using var session = await fixture.Service.PrepareAsync(draft, CancellationToken.None);
        session.MarkWizardCompleted();
        fixture.Events.Clear();
        fixture.Host.IsolatedConfiguration = new(draft.Port + 1, "different-token");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CompleteAsync(CancellationToken.None));
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Contains("agent-check", fixture.Events);
        Assert.DoesNotContain("agent-config", fixture.Events);
        Assert.Equal(new IsolatedGatewayConfiguration(draft.Port + 1, "different-token"),
            fixture.Host.IsolatedConfiguration);
    }

    [Fact]
    public async Task IsolatedWizardPreparationDoesNotRestartUntilExplicitlyRequested()
    {
        using var fixture = new Fixture();
        var draft = await fixture.Service.CreateDraftAsync(default);
        await using var session = await fixture.Service.PrepareAsync(draft, default);
        fixture.Events.Clear();

        await session.PrepareWizardAsync(default);
        Assert.DoesNotContain("restart", fixture.Events);
        Assert.DoesNotContain("stop", fixture.Events);

        fixture.Events.Clear();
        await session.RestartAsync(default);
        Assert.Equal(["validate", "restart", .. s_authorize], fixture.Events);
    }

    internal sealed class Fixture : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        public List<string> Events { get; } = [];
        public GatewayRegistry Registry { get; }
        public Resolver Resolver { get; } = new();
        public Host Host { get; }
        public Runtime Runtime { get; }
        public NativeGatewaySetupService Service { get; }
        public NativeGatewaySetupDraft Draft { get; }
        public Fixture(int port = 18789)
        {
            Draft = new("0123456789abcdef", port, Family) { Contract = NativeGatewayContract.IsolatedSessionV1 };
            Registry = new GatewayRegistry(Temp.Path);
            Host = new Host(Events) { IsolatedConfiguration = new(port, "fixture-agent-token") };
            Runtime = new Runtime(Events);
            Service = new NativeGatewaySetupService(Registry, Resolver, Host, () => Runtime);
        }
        public Task<NativeGatewaySetupSession> PrepareAsync() => Service.PrepareAsync(Draft, CancellationToken.None);
        public void Dispose() => Temp.Dispose();
    }

    internal sealed class Resolver : INativeGatewayPackageResolver
    {
        public string FamilyName { get; set; } = Family;
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NativeGatewayPackage(FamilyName, "2026.9.5.5", @"C:\package\openclaw.exe", @"C:\package\clawctl.exe"));
    }

    internal sealed class Host(List<string> events) : INativeGatewaySetupHost
    {
        public NativeGatewayContract Contract { get; set; } = NativeGatewayContract.IsolatedSessionV1;
        public IsolatedGatewayConfiguration IsolatedConfiguration { get; set; } =
            new(18789, "fixture-agent-token");
        public IReadOnlyList<string>? AppliedCommands { get; private set; }
        public Task<NativeGatewayContract> DetectContractAsync(
            NativeGatewayPackage package, CancellationToken cancellationToken) =>
            Task.FromResult(Contract);
        public Task<IsolatedGatewayConfiguration> PrepareIsolatedConfigurationAsync(
            NativeGatewayPackage package, int port, CancellationToken cancellationToken)
        {
            events.Add("agent-config");
            return Task.FromResult(IsolatedConfiguration);
        }
        public Task ApplyIsolatedCapabilitiesAsync(
            NativeGatewayPackage package, IReadOnlyList<string> commandIds, CancellationToken cancellationToken)
        {
            events.Add("agent-capabilities");
            AppliedCommands = commandIds;
            return Task.CompletedTask;
        }

        public Task<IsolatedGatewayConfiguration> CheckIsolatedPairingConfigurationAsync(
            NativeGatewayPackage package, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("agent-check");
            return Task.FromResult(IsolatedConfiguration);
        }

        public string PendingRequests { get; set; } = "{\"pending\":[]}";
        public Action OnList { get; set; } = () => { };
        public string? ApprovedId { get; private set; }
        public string? ApprovalReplyId { get; set; }
        public Task<string> ListDevicePairingRequestsAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("list-pairing");
            OnList();
            return Task.FromResult(PendingRequests);
        }
        public Task<string> ApproveDevicePairingAsync(NativeGatewayPackage package, string requestId,
            CancellationToken cancellationToken)
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
        public IDisposable OpenRecoveryTerminal(NativeGatewayPackage package) => throw new NotSupportedException();
        public Task PreparePackageAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
        {
            events.Add("prepare");
            Prepare();
            return Task.CompletedTask;
        }
        public Task ValidateConfigurationAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
        {
            events.Add("validate");
            Validate();
            return Task.CompletedTask;
        }
        public Task VerifyHealthAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
        {
            events.Add("health");
            Health();
            return Task.CompletedTask;
        }
    }

    internal sealed class Runtime(List<string> events) : INativeGatewayRuntime
    {
        public Task RestartAsync(GatewayRecord record, CancellationToken cancellationToken)
        {
            events.Add("restart");
            return StopConnections();
        }

        public GatewayEndpointProvenance Inspect(GatewayRecord record) =>
            new(Provenance, new Uri(record.Url).Port);

        public GatewayEndpointProvenanceKind Provenance { get; set; } = GatewayEndpointProvenanceKind.ExpectedManagedGateway;
        public Action Stop { get; set; } = () => { };
        public Func<Task> StopConnections { get; set; } = () => Task.CompletedTask;
        public Func<Task> DisposeRuntime { get; set; } = () => Task.CompletedTask;
        public Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken)
        {
            events.Add("start");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add("stop");
            Stop();
            return StopConnections();
        }
        public Action Release { get; set; } = () => { };
        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            events.Add("release");
            Release();
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
            return new(DisposeRuntime());
        }
    }
}
