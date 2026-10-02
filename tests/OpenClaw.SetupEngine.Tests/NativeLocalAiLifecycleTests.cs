using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeLocalAiLifecycleTests
{
    [Fact]
    public void LegacyBindingRetainsAutomaticRecoveryDefault()
    {
        var oldReceipt = JsonSerializer.SerializeToNode(new LocalAiNativeBinding(
            "gateway", "endpoint", "identity", "llamacpp/model", null, "hash", false))!.AsObject();
        oldReceipt.Remove(nameof(LocalAiNativeBinding.AutomaticRecoveryEnabled));
        Assert.True(oldReceipt.Deserialize<LocalAiNativeBinding>()!.AutomaticRecoveryEnabled);
    }

    [Fact]
    public async Task ExplicitStopPersistsAcrossReconnectsAndNewLifecycleUntilExplicitStart()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        var writes = fixture.Rpc.Writes;
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Stopped));
        var reopened = fixture.CreateLifecycle();
        await reopened.ResumeAsync(runtime);
        await reopened.ResumeAsync(runtime);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(2, runtime.WithdrawOnlyCalls);
        Assert.Equal(writes, fixture.Rpc.Writes);
        Assert.False(fixture.Store.Load()!.AutomaticRecoveryEnabled);
        Assert.True(fixture.Store.Exists);
        await reopened.SetAutomaticRecoveryEnabledAsync(true);
        await reopened.ResumeAsync(runtime);
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task HandoffIgnoresStaleCompletedRecoveryAndJoinsTheNextRecovery()
    {
        var install = LocalAiOnboardingTests.Install();
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(install, LocalAiRuntimeState.Stopped));
        Task recovery = Task.CompletedTask;
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = LocalAiGatewayLifecycle.WaitForRecoveryAsync(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), runtime, () =>
            {
                if (!recovery.IsCompleted) joined.TrySetResult();
                return recovery;
            }, default);
        Assert.False(waiting.IsCompleted);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery = next.Task;
        await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Snapshot = LocalAiOnboardingTests.RuntimeSnapshot(install, LocalAiRuntimeState.Healthy);
        Assert.False(waiting.IsCompleted);
        next.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffWaitsForOwnedRuntimeWithoutStartingOrReplayingIt(bool cancel)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        var writes = fixture.Rpc.Writes;
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Stopped));
        var completion = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            Record.Id, GatewayDashboardBinding.Capture(Record), fixture.Model, "main", 1, RequiresManagedLocalAi: true);
        using var cancellation = new CancellationTokenSource();
        var waiting = fixture.Lifecycle.WaitForRuntimeAsync(completion, runtime, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        else
        {
            runtime.Snapshot = LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Healthy);
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(writes, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task HandoffCannotTreatAnotherLoadedModelAsTheOwnedRuntime()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Healthy) with { ModelId = "different" });
        var completion = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            Record.Id, GatewayDashboardBinding.Capture(Record), fixture.Model, "main", 1, RequiresManagedLocalAi: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Lifecycle.WaitForRuntimeAsync(completion, runtime, default));
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Theory]
    [InlineData(LocalAiRuntimeState.Stopped, LocalAiOwnership.CompanionManaged)]
    [InlineData(LocalAiRuntimeState.Healthy, LocalAiOwnership.None)]
    public async Task DetectedSelectionWithPendingBindingDoesNotWaitForManagedRuntime(
        LocalAiRuntimeState state, LocalAiOwnership ownership)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Store.Save(fixture.Store.Load()! with { Pending = true });
        var binding = fixture.Store.Load();
        var writes = fixture.Rpc.Writes;
        fixture.Rpc.OfferDetectedModel = true;
        var client = new GatewayAiSetupClient(fixture.Rpc);
        await client.DetectAsync();
        client.SelectCandidate("existing-model", fixture.Model);
        await client.StartSelectedAsync();
        Assert.True((await client.VerifyAsync()).Ok);
        var proof = client.GetVerifiedCompletion();
        Assert.False(proof.RequiresManagedLocalAi);
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, state) with { Ownership = ownership });
        await fixture.Lifecycle.ResumeAsync(runtime);
        Assert.Equal(0, runtime.Calls);
        using var cancellation = new CancellationTokenSource();
        var waiting = fixture.Lifecycle.WaitForRuntimeAsync(proof, runtime, cancellation.Token);
        var completed = waiting.IsCompletedSuccessfully;
        cancellation.Cancel();
        try { await waiting; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert.True(completed, "Ordinary verified model selection must not join pending managed recovery.");
        var reverified = await SetupNativeCompletionVerifier.VerifyModelAsync(
            new GatewayAiSetupClient(fixture.Rpc, proof.ModelRef, proof.Intent), proof.ModelRef, default);
        SetupNativeVerification.RequireSame(proof, new(reverified, fixture.Rpc.Route.SessionKey!));
        Assert.False(reverified.RequiresManagedLocalAi);
        Assert.Equal(2, fixture.Rpc.Verifications);
        Assert.Equal(binding, fixture.Store.Load());
        Assert.Equal(writes, fixture.Rpc.Writes);
        Assert.Equal(0, runtime.Calls);
        fixture.Rpc.VerificationSucceeds = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyModelAsync(
                new GatewayAiSetupClient(fixture.Rpc, proof.ModelRef, proof.Intent), proof.ModelRef, default));
        fixture.Rpc.VerificationSucceeds = true;
        fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"] = "different/model";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyModelAsync(
                new GatewayAiSetupClient(fixture.Rpc, proof.ModelRef, proof.Intent), proof.ModelRef, default));
        Assert.Throws<SetupNativeOwnershipException>(() => SetupNativeVerification.RequireRoute(
            proof, fixture.Rpc.Route with { GatewayId = "different" }));
        Assert.Throws<SetupNativeOwnershipException>(() => SetupNativeVerification.RequireRoute(
            proof, fixture.Rpc.Route with { IdentityBinding = new string('B', 64) }));
    }

    private static readonly GatewayRecord Record = new()
    {
        Id = "native-local-ai", IsLocal = true, Url = "ws://127.0.0.1:55060",
        NativePackageFamilyName = "OpenClawFoundation.OpenClawGateway_test",
        NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
    };

    [Theory]
    [InlineData("none")]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("port")]
    [InlineData("files")]
    [InlineData("lost-reply")]
    public async Task InstallAndUse_ObservesOnceAndNeverReplaysOrSubstitutesTheReviewedSelection(string change)
    {
        using var fixture = new Fixture();
        var inspections = 0;
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Stopped))
        {
            StartResult = LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Healthy),
            OnStartAsync = async () =>
            {
                var published = await fixture.Lifecycle.CompleteStartAsync(fixture.Install, default);
                if (!published.Success) throw new InvalidOperationException(published.Detail);
                await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(true);
            },
        };
        var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
            [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30,
                DriverVersion: "615.0", CudaMajorVersion: 13, StableId: "GPU-test")], false);
        var host = new SetupLocalAiHost(
            () => throw new InvalidOperationException("Native continuation must not resolve WSL."),
            () => null, () => runtime, _ => Task.FromResult<LocalAiResolvedInstall?>(fixture.Install),
            (_, _) => { inspections++; return Task.FromResult(change != "files"); },
            _ => Task.FromResult(hardware),
            () => throw new InvalidOperationException("Native continuation must not publish through WSL."),
            nativeLifecycle: fixture.Lifecycle);
        host.ConfigureNative(Record, fixture.Rpc, _ => Task.CompletedTask);
        var target = new SetupLocalAiTarget(Record.Id, "", new Uri(Record.Url).Port, null, null,
            true, GatewayDashboardBinding.Capture(Record));
        target = change switch
        {
            "gateway" => target with { GatewayId = "different" },
            "endpoint" => target with { EndpointBinding = "different" },
            _ => target
        };
        var intent = new LocalAiInstallAndUseIntent(target,
            change == "model" ? "different" : fixture.Install.Manifest.ModelCatalogId,
            change == "port" ? 12345 : fixture.Install.Manifest.RequestedPort);
        var use = new LocalAiOnboardingUse(host);
        var stages = new List<LocalAiSetupStage>();
        fixture.Rpc.LoseReply = change == "lost-reply";
        if (change == "none")
        {
            await use.UseInstalledAsync(intent, default, new SynchronousProgress<LocalAiSetupStage>(stages.Add));
            Assert.Equal(intent.Expected, use.Expected);
            Assert.Equal([LocalAiSetupStage.CheckingHardware, LocalAiSetupStage.CheckingFiles,
                LocalAiSetupStage.PreparingGateway, LocalAiSetupStage.StartingRuntime,
                LocalAiSetupStage.PublishingProvider], stages);
        }
        else if (change == "lost-reply")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => use.UseInstalledAsync(intent, default));
            Assert.Equal(intent.Expected, use.Expected);
            Assert.True(fixture.Store.Load()!.Pending);
        }
        else
        {
            await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => use.UseInstalledAsync(intent, default));
            Assert.Null(use.Expected);
        }
        Assert.Equal(1, inspections);
        Assert.Equal(change is "none" or "lost-reply" ? 1 : 0, runtime.Calls);
        Assert.Equal(change is "none" or "lost-reply" ? 1 : 0, fixture.Rpc.Writes);
        Assert.True(intent.IsConsumed);
        await use.DrainAsync();
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => use.UseInstalledAsync(intent, default));
        Assert.Equal(1, inspections);
    }

    [Fact]
    public async Task InstallAndUse_CancellationBeforeAdmissionDoesNotConsumeConsentOrStartAnything()
    {
        using var fixture = new Fixture();
        var host = new SetupLocalAiHost(
            () => throw new InvalidOperationException(), () => null, () => null,
            _ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            nativeLifecycle: fixture.Lifecycle);
        var intent = new LocalAiInstallAndUseIntent(
            new(Record.Id, "", 55060, null, null, true, GatewayDashboardBinding.Capture(Record)),
            fixture.Install.Manifest.ModelCatalogId, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var use = new LocalAiOnboardingUse(host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => use.UseInstalledAsync(intent, cancellation.Token));
        Assert.False(intent.IsConsumed);
        Assert.Null(use.Expected);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task StagedObservationAndReviewNeedNoRegistryPublicationWslOrCredential()
    {
        using var fixture = new Fixture();
        var authorizations = 0;
        var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
            [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30,
                DriverVersion: "615.0", CudaMajorVersion: 13, StableId: "GPU-test")], false);
        var host = new SetupLocalAiHost(
            () => throw new InvalidOperationException("Native observation must not resolve WSL."),
            () => null, () => null, _ => Task.FromResult<LocalAiResolvedInstall?>(null),
            (_, _) => throw new InvalidOperationException("There is no install to inspect."),
            _ => Task.FromResult(hardware),
            () => throw new InvalidOperationException("Observation must not publish."),
            nativeLifecycle: fixture.Lifecycle);
        host.ConfigureNative(Record, fixture.Rpc, _ => { authorizations++; return Task.CompletedTask; });
        var observed = await host.ObserveAsync(default);
        Assert.Equal(LocalAiOnboardingState.SetUp, observed.State);
        Assert.True(observed.Target!.IsNative);
        Assert.Equal(Record.Id, observed.Target.GatewayId);
        host.ReleaseNative(fixture.Rpc);
        Assert.Equal(observed.Target, await host.RevalidateReviewAsync(observed, default));
        Assert.Equal(2, authorizations);
        Assert.False(fixture.Store.Exists);
        Assert.Null(fixture.Lifecycle.GetApiKey());
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task ExplicitSelectionPublishesAuthenticatedProviderAndRestoresOnlyOwnedFields()
    {
        using var fixture = new Fixture();
        var original = fixture.Rpc.Config.DeepClone();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install, LocalAiQuiesceReason.EndpointCycle)).Success);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        Assert.Equal(fixture.Lifecycle.GetApiKey(),
            fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(fixture.Model, fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>());
        Assert.NotNull(fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.True(JsonNode.DeepEquals(original, fixture.Rpc.Config));
        Assert.False(fixture.Store.Load()!.Pending);
        Assert.Equal("cloud/model", fixture.Store.Load()!.PreviousPrimary);
    }

    [Fact]
    public async Task RestartRetainsExactGatewayAndFallbackWithoutRequiringPreviousPageAuthority()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        var resumed = fixture.CreateLifecycle();
        fixture.Rpc.Route = fixture.Rpc.Route with { AuthorityId = "new-page-lifetime" };
        resumed.Register(Record, fixture.Rpc);
        Assert.True((await resumed.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal("cloud/model", fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>());
        Assert.True((await resumed.PublishAsync(fixture.Install)).Success);
    }

    [Fact]
    public async Task EndpointCycleWithdrawsOldPortBeforePublishingReplacement()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install, LocalAiQuiesceReason.EndpointCycle)).Success);
        Assert.Null(fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]);
        Assert.Equal(fixture.Model, fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>());
        var replacement = fixture.Install with { Endpoint = new Uri("http://127.0.0.1:18809/v1") };
        Assert.True((await fixture.Lifecycle.PublishAsync(replacement)).Success);
        Assert.Equal(replacement.Endpoint.AbsoluteUri,
            fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]!["baseUrl"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WithdrawalPreservesUnrelatedEditAndRequiresProofOfRedactedCredential(bool redacted, bool verified)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["userPreference"] = "external-edit";
        if (redacted) fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]!["apiKey"] =
            LocalAiGatewayProviderDefinition.CliRedactedApiKey;
        fixture.Rpc.VerificationSucceeds = verified;
        fixture.Rpc.Revision++;
        var writes = fixture.Rpc.Writes;
        Assert.Equal(!redacted || verified, (await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal(writes + (!redacted || verified ? 1 : 0), fixture.Rpc.Writes);
        Assert.Equal("external-edit", fixture.Rpc.Config["userPreference"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitInferenceReconcilesUnrelatedChangesAndPreservesThem()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["capabilities"] = new JsonObject { ["native"] = true };
        fixture.Rpc.Revision++;
        await fixture.Lifecycle.ReconcileVerifiedAsync(Record, fixture.Rpc, fixture.Install, default);
        Assert.Equal(1, fixture.Rpc.Verifications);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.True(fixture.Rpc.Config["capabilities"]!["native"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrRacingInferenceNeverAdoptsNewRevision(bool race)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        var ownedHash = fixture.Store.Load()!.ConfigHash;
        fixture.Rpc.Revision++;
        fixture.Rpc.VerificationSucceeds = race;
        fixture.Rpc.ChangeDuringVerification = race;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Lifecycle.ReconcileVerifiedAsync(Record, fixture.Rpc, fixture.Install, default));
        Assert.Equal(ownedHash, fixture.Store.Load()!.ConfigHash);
        Assert.Equal(1, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task LostMutationReplyLeavesDurablePendingStateAndNeverReplays()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Rpc.LoseReply = true;
        Assert.False((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        Assert.True(fixture.Store.Load()!.Pending);
        Assert.NotNull(fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]);
        var resumed = fixture.CreateLifecycle();
        resumed.Register(Record, fixture.Rpc);
        var blocked = await resumed.QuiesceAsync(fixture.Install);
        Assert.False(blocked.Success);
        Assert.Contains(Record.Id, blocked.Detail);
        Assert.Contains("cloud/model", blocked.Detail);
        Assert.Contains(fixture.Install.Endpoint!.ToString(), blocked.Detail);
        Assert.Equal(1, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task RejectedAdmissionDoesNotLeaveAnUnsentMutationPending()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Rpc.RejectMutationBeforeDispatch = true;
        Assert.False((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        Assert.False(fixture.Store.Load()!.Pending);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task LostPublicationAfterCrashRestoresOnlySameEndpointAndVerifiesWithoutReplayingWrite()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Rpc.LoseReply = true;
        Assert.False((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        var recovered = fixture.CreateLifecycle();
        recovered.Register(Record, fixture.Rpc);
        await recovered.PrepareAsync(fixture.Install, default);
        Assert.Equal(fixture.Install.Endpoint!.Port, recovered.GetRecoveryPort(fixture.Install));
        Assert.True((await recovered.QuiesceAsync(fixture.Install, LocalAiQuiesceReason.EndpointCycle)).Success);
        Assert.True(fixture.Store.Load()!.Pending);
        Assert.True((await recovered.PublishAsync(fixture.Install)).Success);
        Assert.False(fixture.Store.Load()!.Pending);
        Assert.Equal(1, fixture.Rpc.Writes);
        Assert.Equal(1, fixture.Rpc.Verifications);
        Assert.Null(recovered.GetRecoveryPort(fixture.Install));
    }

    [Fact]
    public async Task FailedInferenceDoesNotAdoptRecoveredPublication()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Rpc.LoseReply = true;
        Assert.False((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        var recovered = fixture.CreateLifecycle();
        recovered.Register(Record, fixture.Rpc);
        await recovered.PrepareAsync(fixture.Install, default);
        fixture.Rpc.VerificationSucceeds = false;
        Assert.False((await recovered.PublishAsync(fixture.Install)).Success);
        Assert.True(fixture.Store.Load()!.Pending);
        Assert.Equal(1, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task LandedWithdrawalWithLostReplyCanBeConfirmedAndReleased()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false);
        fixture.Rpc.LoseReply = true;
        Assert.False((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.True(fixture.Store.Load()!.Pending);
        var recovered = fixture.CreateLifecycle();
        recovered.Register(Record, fixture.Rpc);
        Assert.True((await recovered.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal(2, fixture.Rpc.Writes);
        await recovered.ForgetWithdrawnAsync(Record.Id, default);
        Assert.False(fixture.Store.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedListenerReconcilesUnrelatedEditWithoutReplayingPublication(bool withdrawn)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        if (withdrawn) Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        fixture.Rpc.Config["unrelated"] = "preserved";
        fixture.Rpc.Revision++;
        var writes = fixture.Rpc.Writes;
        await fixture.Lifecycle.PrepareStartAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install, LocalAiQuiesceReason.EndpointCycle)).Success);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        Assert.Equal(writes + (withdrawn ? 1 : 0), fixture.Rpc.Writes);
        Assert.Equal(withdrawn ? 0 : 1, fixture.Rpc.Verifications);
        Assert.Equal("preserved", fixture.Rpc.Config["unrelated"]!.GetValue<string>());
        Assert.False(fixture.Store.Load()!.Pending);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("primary")]
    [InlineData("allowlist")]
    public async Task StoppedRecoveryRejectsOwnedFieldEdits(string field)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        if (field == "provider") fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]!["baseUrl"] = "http://127.0.0.1:9999/v1";
        if (field == "primary") fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"] = "other/model";
        if (field == "allowlist") fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]!["alias"] = "external";
        fixture.Rpc.Revision++;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Lifecycle.PrepareStartAsync(fixture.Install, default));
        Assert.Equal(1, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task FirstUseAdmissionDoesNotAuthorizeAutomaticStartupBeforePublication()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.False(fixture.Store.Load()!.AutomaticRecoveryEnabled);
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Stopped));
        await fixture.Lifecycle.ResumeAsync(runtime);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(1, runtime.WithdrawOnlyCalls);
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.False(fixture.Store.Load()!.AutomaticRecoveryEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyHostUseReconcilesUnderRuntimeOwnerWithoutLatePublication(bool stopAfterStart)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["unrelated"] = true;
        fixture.Rpc.Revision++;
        var runtime = new LocalAiOnboardingTests.FakeRuntime(
            LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Healthy))
        {
            OnStartAsync = async () =>
            {
                await fixture.Lifecycle.PrepareStartAsync(fixture.Install, default);
                Assert.True((await fixture.Lifecycle.CompleteStartAsync(fixture.Install, default)).Success);
                await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(true);
            },
        };
        var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
            [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30,
                DriverVersion: "615.0", CudaMajorVersion: 13, StableId: "GPU-test")], false);
        var host = new SetupLocalAiHost(() => throw new InvalidOperationException(), () => fixture.Registry,
            () => runtime, _ => Task.FromResult<LocalAiResolvedInstall?>(fixture.Install),
            (_, _) => Task.FromResult(true), _ => Task.FromResult(hardware),
            () => throw new InvalidOperationException(), nativeLifecycle: fixture.Lifecycle);
        host.ConfigureNative(Record, fixture.Rpc, _ => Task.CompletedTask);
        var selected = await host.ObserveAsync(default);
        var progress = new SynchronousProgress<LocalAiSetupStage>(stage =>
        {
            if (stage != LocalAiSetupStage.PublishingProvider) return;
            if (!stopAfterStart)
            {
                var evidence = runtime.Snapshot.ModelEvidence;
                runtime.Snapshot = runtime.Snapshot with
                {
                    UpdatedAtUtc = runtime.Snapshot.UpdatedAtUtc.AddSeconds(1),
                    ModelEvidence = new(evidence.State, evidence.ObservedAtUtc.AddSeconds(1),
                        evidence.Sha256, evidence.SizeBytes, evidence.ServerModelId),
                    GatewayRouteRequiresResolution = false,
                };
                return;
            }
            fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false).GetAwaiter().GetResult();
            Assert.True(fixture.Lifecycle.QuiesceAsync(fixture.Install).GetAwaiter().GetResult().Success);
            runtime.Snapshot = LocalAiOnboardingTests.RuntimeSnapshot(fixture.Install, LocalAiRuntimeState.Stopped);
        });
        if (stopAfterStart)
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UseAsync(selected, default, progress));
        else
            await host.UseAsync(selected, default, progress);
        Assert.Equal(stopAfterStart ? 2 : 1, fixture.Rpc.Writes);
        Assert.Equal(1, fixture.Rpc.Verifications);
        Assert.False(fixture.Store.Load()!.Pending);
        Assert.Equal(!stopAfterStart, fixture.Store.Load()!.AutomaticRecoveryEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitReleaseRequiresFreshConfirmedWithdrawal(bool editAfterStop)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default));
        await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false);
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        if (editAfterStop)
        {
            fixture.Rpc.Revision++;
        }
        await fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default);
        Assert.False(fixture.Store.Exists);
    }

    [Fact]
    public async Task TeardownPreservesUserAdoptedAllowlistEntryAndAllowsRelease()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]!["alias"] = "user label";
        fixture.Rpc.Revision++;
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal("user label", fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]!["alias"]!.GetValue<string>());
        Assert.False(fixture.Store.Load()!.AddedAllowlistEntry);
        await fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default);
        Assert.False(fixture.Store.Exists);
    }

    [Fact]
    public async Task SetupRegistrationExcludesExplicitRelease()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lifecycle.ReleaseOwnershipAsync(default));
        Assert.Contains("Close Local AI setup", error.Message);
        Assert.True(fixture.Store.Exists);
    }

    [Fact]
    public async Task UnchangedRevisionClearsPendingWithoutInferringOrReplaying()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Store.Save(fixture.Store.Load()! with { Pending = true });
        var recovered = fixture.CreateLifecycle();
        recovered.Register(Record, fixture.Rpc);
        await recovered.PrepareAsync(fixture.Install, default);
        Assert.False(fixture.Store.Load()!.Pending);
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Fact]
    public async Task ActiveNativeRecordDoesNotRetargetAnExistingWslEndpointCycle()
    {
        using var directory = new TempDirectory();
        var registry = new GatewayRegistry(directory.Path);
        registry.AddOrUpdate(Record);
        registry.SetActive(Record.Id);
        var wsl = new CountingWsl();
        var lifecycle = new LocalAiGatewayLifecycle(new(directory.Path), directory.Path,
            () => registry, () => null, wsl, NullLogger.Instance);
        Assert.True(lifecycle.IsNativeMode);
        Assert.True((await lifecycle.QuiesceAsync(LocalAiOnboardingTests.Install())).Success);
        Assert.True((await lifecycle.PublishAsync(LocalAiOnboardingTests.Install())).Success);
        Assert.Equal(2, wsl.Calls);
        Assert.Null(lifecycle.GetApiKey());
    }

    [Fact]
    public async Task ReplacedIdentityCannotAdoptPersistedOwnership()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        var resumed = fixture.CreateLifecycle();
        fixture.Rpc.Route = fixture.Rpc.Route with { IdentityBinding = new string('B', 64) };
        resumed.Register(Record, fixture.Rpc);
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => resumed.PrepareAsync(fixture.Install, default));
        Assert.False((await resumed.PublishAsync(fixture.Install)).Success);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task ExistingProviderWithoutOwnershipIsNotAdopted()
    {
        using var fixture = new Fixture();
        fixture.Rpc.Config["models"]!["providers"]!["llamacpp"] = new JsonObject();
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() =>
            fixture.Lifecycle.PrepareAsync(fixture.Install, default));
        Assert.False(fixture.Store.Exists);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task OfflineOriginalGatewayLeavesCleanupUnresolvedWithoutWslFallback()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        var binding = fixture.Store.Load();
        fixture.Lifecycle.Release(fixture.Rpc);
        Assert.False((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal(binding, fixture.Store.Load());
        Assert.Equal(1, fixture.Rpc.Writes);
    }

    [Fact]
    public async Task CancellationBeforeDispatchCreatesNoOwnershipOrMutation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Lifecycle.PrepareAsync(fixture.Install, cancellation.Token));
        Assert.False(fixture.Store.Exists);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternallyWithdrawnRouteCleansOnlyOwnedAllowlistAndReleases(bool edited)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["models"]!["providers"]!.AsObject().Remove("llamacpp");
        fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"] = "cloud/model";
        if (edited) fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]!["alias"] = "user";
        fixture.Rpc.Revision++;
        await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false);

        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal(edited ? 1 : 2, fixture.Rpc.Writes);
        if (edited)
            Assert.Equal("user", fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]!["alias"]!.GetValue<string>());
        else
            Assert.Null(fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model]);
        await fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default);
        Assert.False(fixture.Store.Exists);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("authority")]
    [InlineData("lost-reply")]
    public async Task AllowlistOnlyCleanupRetainsJournalAndAuthorityFences(string failure)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Rpc.Config["models"]!["providers"]!.AsObject().Remove("llamacpp");
        fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"] = "cloud/model";
        fixture.Rpc.Revision++;
        await fixture.Lifecycle.SetAutomaticRecoveryEnabledAsync(false);
        fixture.Rpc.RejectMutationBeforeDispatch = failure == "authority";
        fixture.Rpc.ConflictAtDispatch = failure == "conflict";
        fixture.Rpc.LoseReply = failure == "lost-reply";

        Assert.False((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.Equal(failure != "authority", fixture.Store.Load()!.Pending);
        Assert.Equal(failure == "lost-reply" ? 2 : 1, fixture.Rpc.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default));
        Assert.True(fixture.Store.Exists);
        fixture.Rpc.RejectMutationBeforeDispatch = false;
        fixture.Rpc.ConflictAtDispatch = false;
        fixture.Rpc.LoseReply = false;
        Assert.True((await fixture.Lifecycle.QuiesceAsync(fixture.Install)).Success);
        Assert.False(fixture.Store.Load()!.Pending);
        await fixture.Lifecycle.ForgetWithdrawnAsync(Record.Id, default);
        Assert.False(fixture.Store.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipObservationDoesNotAdoptExistingProvider(bool existing)
    {
        using var fixture = new Fixture();
        if (existing) fixture.Rpc.Config["models"]!["providers"]!["llamacpp"] = new JsonObject();
        var files = Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories);
        var observed = await fixture.Lifecycle.ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, default);
        Assert.Equal(existing ? NativeLocalAiOwnershipState.MissingReceipt : NativeLocalAiOwnershipState.Unselected, observed);
        Assert.Equal(files, Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories));
        Assert.False(fixture.Store.Exists);
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnershipObservationFindsDurableRecoveryWithoutSettlingIt(bool pending, bool landed)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        if (landed) Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        fixture.Store.Save(fixture.Store.Load()! with { Pending = pending });
        var before = fixture.Store.Load();
        var files = Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories);
        var writes = fixture.Rpc.Writes;
        var observed = await fixture.CreateLifecycle().ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, default);
        Assert.Equal(pending ? NativeLocalAiOwnershipState.RecoveryRequired : NativeLocalAiOwnershipState.SameOwner, observed);
        Assert.Equal(before, fixture.Store.Load());
        Assert.Equal(files, Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories));
        Assert.Equal(writes, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("identity")]
    [InlineData("model")]
    [InlineData("damaged")]
    [InlineData("allowlist")]
    public async Task OwnershipObservationRejectsInvalidEvidenceWithoutMutation(string change)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        var binding = fixture.Store.Load()!;
        fixture.Store.Save(change switch
        {
            "gateway" => binding with { GatewayId = "another" },
            "endpoint" => binding with { EndpointBinding = "another" },
            "identity" => binding with { IdentityBinding = new string('B', 64) },
            "model" => binding with { ModelRef = "llamacpp/another" },
            _ => binding
        });
        if (change == "damaged")
            File.WriteAllText(fixture.BindingPath, "{");
        if (change == "allowlist")
            fixture.Rpc.Config["agents"]!["defaults"]!["models"]![fixture.Model] = new JsonObject { ["alias"] = "user" };
        var before = File.ReadAllText(fixture.BindingPath);
        var observed = await fixture.Lifecycle.ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, default);
        Assert.Equal(change is "damaged" or "allowlist" ? NativeLocalAiOwnershipState.InvalidReceipt :
            NativeLocalAiOwnershipState.DifferentOwner, observed);
        Assert.Equal(before, File.ReadAllText(fixture.BindingPath));
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Fact]
    public async Task OwnershipObservationDetectsRevisionDriftAndCancellationWithoutSettling()
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        fixture.Rpc.Revision++;
        var before = fixture.Store.Load();
        Assert.Equal(NativeLocalAiOwnershipState.RecoveryRequired,
            await fixture.Lifecycle.ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Lifecycle.ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, cancellation.Token));
        Assert.Equal(before, fixture.Store.Load());
        Assert.Equal(0, fixture.Rpc.Verifications);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostObservationProjectsDurableOwnershipBeforeOfferingManagement(bool owned)
    {
        using var fixture = new Fixture();
        if (owned)
        {
            await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
            fixture.Store.Save(fixture.Store.Load()! with { Pending = true });
        }
        else fixture.Rpc.Config["models"]!["providers"]!["llamacpp"] = new JsonObject();
        var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
            [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30,
                DriverVersion: "615.0", CudaMajorVersion: 13, StableId: "GPU-test")], false);
        var host = new SetupLocalAiHost(
            () => throw new InvalidOperationException("No WSL inspection."),
            () => null, () => null, _ => Task.FromResult<LocalAiResolvedInstall?>(fixture.Install),
            (_, _) => Task.FromResult(true), _ => Task.FromResult(hardware),
            () => throw new InvalidOperationException("No publication."),
            nativeLifecycle: fixture.Lifecycle);
        host.ConfigureNative(Record, fixture.Rpc, _ => Task.CompletedTask);
        var result = await host.ObserveAsync(default);
        Assert.Equal(owned ? LocalAiOnboardingState.Reconcile : LocalAiOnboardingState.ManagementBlocked, result.State);
        Assert.Equal(owned, result.CanUse);
        Assert.False(result.ReplacesDetectedChoice);
        if (!owned)
            await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.UseAsync(result, default));
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("primary")]
    [InlineData("unrelated")]
    [InlineData("pending")]
    public async Task UnconfirmedRecoveryPreservesIndependentDetectedModel(string change)
    {
        using var fixture = new Fixture();
        await fixture.Lifecycle.PrepareAsync(fixture.Install, default);
        Assert.True((await fixture.Lifecycle.PublishAsync(fixture.Install)).Success);
        if (change == "provider")
            fixture.Rpc.Config["models"]!["providers"]!["llamacpp"]!["baseUrl"] = "http://127.0.0.1:9999/v1";
        if (change == "primary")
            fixture.Rpc.Config["agents"]!["defaults"]!["model"]!["primary"] = "other/model";
        if (change == "pending")
            fixture.Store.Save(fixture.Store.Load()! with { Pending = true });
        else fixture.Rpc.Revision++;
        var binding = fixture.Store.Load();
        var host = fixture.CreateObservationHost();
        var observed = await host.ObserveAsync(default);
        Assert.Equal(LocalAiOnboardingState.Reconcile, observed.State);
        Assert.True(observed.CanUse);
        Assert.False(observed.ReplacesDetectedChoice);
        var view = AiSetupPresentationModel.Create(new()
        {
            Candidates = [new("existing-model", "Existing model", "", fixture.Model, true)],
            ManualProviders = [], Workspace = "workspace", SetupComplete = true
        }, Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet(), gatewayId: Record.Id,
            localGatewayId: observed.ReplacesDetectedChoice ? observed.Target?.GatewayId : null,
            localModelRef: observed.ReplacesDetectedChoice ? observed.ModelRef : null);
        Assert.Single(view.Candidates);
        Assert.Equal(binding, fixture.Store.Load());
        Assert.Equal(1, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
        if (change is "provider" or "primary")
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Lifecycle.PrepareAsync(fixture.Install, default));
    }

    [Fact]
    public async Task ConnectionLostDuringObservationRetainsNativeTargetAndCanRefresh()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rpc.ReadConfig = _ => { started.SetResult(); return reply.Task; };
        await using var observation = new LocalAiOnboardingObservation(fixture.CreateObservationHost());
        var refresh = observation.RefreshAsync();
        await started.Task;
        reply.SetException(new GatewayConnectionLostException(null, null));
        await refresh;
        Assert.Equal(LocalAiOnboardingState.ManagementBlocked, observation.Snapshot.State);
        Assert.True(observation.Snapshot.Target!.IsNative);
        Assert.Equal(Record.Id, observation.Snapshot.Target.GatewayId);
        Assert.Equal("LocalOwnershipUnavailable", observation.Snapshot.ReasonKey);
        Assert.True(observation.Snapshot.CanRefresh);
        Assert.False(observation.Snapshot.ReplacesDetectedChoice);
        fixture.Rpc.ReadConfig = null;
        await observation.RefreshAsync();
        Assert.Equal(NativeLocalAiOwnershipState.Unselected, observation.Snapshot.NativeOwnership);
        Assert.Equal(Record.Id, observation.Snapshot.Target!.GatewayId);
        Assert.False(fixture.Store.Exists);
        Assert.Equal(0, fixture.Rpc.Writes);
        Assert.Equal(0, fixture.Rpc.Verifications);
    }

    [Fact]
    public async Task CallerCancellationDuringConnectionLossStillPropagates()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Rpc.ReadConfig = _ =>
        {
            cancellation.Cancel();
            return Task.FromException<JsonElement>(new GatewayConnectionLostException(null, null));
        };
        await Assert.ThrowsAsync<GatewayConnectionLostException>(() =>
            fixture.Lifecycle.ObserveOwnershipAsync(Record, fixture.Rpc, fixture.Install, cancellation.Token));
        Assert.False(fixture.Store.Exists);
        Assert.Equal(0, fixture.Rpc.Writes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _directory = new();
        public string DirectoryPath => _directory.Path;
        public string BindingPath => Path.Combine(_directory.Path, "LocalAI", "gateway-binding.json");
        public LocalAiResolvedInstall Install { get; } = LocalAiOnboardingTests.Install();
        public string Model => LocalAiGatewayProviderDefinition.BuildPrimaryModel(Install);
        public RpcTransport Rpc { get; } = new();
        public LocalAiNativeBindingStore Store { get; }
        public LocalAiGatewayLifecycle Lifecycle { get; }
        public GatewayRegistry Registry { get; }
        public Fixture()
        {
            Registry = new(_directory.Path);
            Registry.AddOrUpdate(Record);
            Registry.SetActive(Record.Id);
            Store = new(new(_directory.Path));
            Lifecycle = CreateLifecycle();
            Lifecycle.Register(Record, Rpc);
        }
        public LocalAiGatewayLifecycle CreateLifecycle() => new(new(_directory.Path), _directory.Path,
            () => Registry, () => null, new ForbiddenWsl(), NullLogger.Instance);
        public SetupLocalAiHost CreateObservationHost()
        {
            var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
                [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30,
                    DriverVersion: "615.0", CudaMajorVersion: 13, StableId: "GPU-test")], false);
            var host = new SetupLocalAiHost(
                () => throw new InvalidOperationException("No WSL inspection."),
                () => null, () => null, _ => Task.FromResult<LocalAiResolvedInstall?>(Install),
                (_, _) => Task.FromResult(true), _ => Task.FromResult(hardware),
                () => throw new InvalidOperationException("No publication."),
                nativeLifecycle: Lifecycle);
            host.ConfigureNative(Record, Rpc, _ => Task.CompletedTask);
            return host;
        }
        public void Dispose() => _directory.Dispose();
    }

    private sealed class ForbiddenWsl : ILocalAiEndpointLifecycle
    {
        public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(LocalAiResolvedInstall install,
            LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native flow must never call WSL.");
        public Task<LocalAiEndpointLifecycleResult> PublishAsync(LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Native flow must never call WSL.");
    }

    private sealed class CountingWsl : ILocalAiEndpointLifecycle
    {
        public int Calls { get; private set; }
        public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(LocalAiResolvedInstall install,
            LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(LocalAiEndpointLifecycleResult.Ok()); }
        public Task<LocalAiEndpointLifecycleResult> PublishAsync(LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(LocalAiEndpointLifecycleResult.Ok()); }
    }

    private sealed class RpcTransport : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; set; } = new(Record.Id, "main", "page-authority",
            GatewayDashboardBinding.Capture(Record), new string('A', 64), "agent:main:main");
        public long Generation => 1;
        public bool IsConnected => true;
        public bool OfferDetectedModel { get; set; }
        public IReadOnlyCollection<string> Methods => OfferDetectedModel
            ? ["config.get", "config.patch", "openclaw.setup.verify", "openclaw.setup.detect", "openclaw.setup.activate"]
            : ["config.get", "config.patch", "openclaw.setup.verify"];
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public JsonObject Config { get; } = JsonNode.Parse("""
            {"models":{"providers":{}},"agents":{"defaults":{"model":{"primary":"cloud/model"},"models":{"cloud/model":{"alias":"keep"}}}}}
            """)!.AsObject();
        public int Revision { get; set; }
        public int Writes { get; private set; }
        public bool LoseReply { get; set; }
        public bool VerificationSucceeds { get; set; } = true;
        public bool ChangeDuringVerification { get; set; }
        public int Verifications { get; private set; }
        public bool RejectMutationBeforeDispatch { get; set; }
        public bool ConflictAtDispatch { get; set; }
        public Func<CancellationToken, Task<JsonElement>>? ReadConfig { get; set; }
        public Task<JsonElement> RequestMutationAsync(string method, object parameters, int timeoutMs,
            CancellationToken ct, Action? beforeDispatch = null)
        {
            ct.ThrowIfCancellationRequested();
            if (RejectMutationBeforeDispatch) throw new InvalidOperationException("Admission failed before dispatch.");
            beforeDispatch?.Invoke();
            return RequestAsync(method, parameters, timeoutMs, CancellationToken.None);
        }
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == "config.get" && ReadConfig is { } readConfig) return readConfig(ct);
            if (method == "openclaw.setup.detect" && OfferDetectedModel)
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    candidates = new[] { new { kind = "existing-model", label = "Existing Local AI",
                        modelRef = Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>() } },
                    manualProviders = Array.Empty<object>(), workspace = "test", setupComplete = true,
                    configuredModel = Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>(),
                }));
            if (method == "openclaw.setup.activate" && OfferDetectedModel)
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    modelRef = Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>(),
                    gatewayRestartRequired = false,
                }));
            if (method == "openclaw.setup.verify")
            {
                Verifications++;
                if (ChangeDuringVerification) Revision++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = VerificationSucceeds,
                    modelRef = Config["agents"]!["defaults"]!["model"]!["primary"]!.GetValue<string>(),
                    latencyMs = 1, status = "unavailable", error = "Simulated inference failure.",
                }));
            }
            if (method == "config.patch")
            {
                var payload = JsonSerializer.SerializeToElement(parameters);
                if (ConflictAtDispatch) Revision++;
                if (payload.GetProperty("baseHash").GetString() != Revision.ToString())
                    throw new InvalidOperationException("Configuration hash conflict.");
                Assert.Equal(Revision.ToString(), payload.GetProperty("baseHash").GetString());
                Merge(Config, JsonNode.Parse(payload.GetProperty("raw").GetString()!)!.AsObject());
                Revision++;
                Writes++;
                if (LoseReply) throw new IOException("Simulated lost reply after persistence.");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { hash = Revision.ToString(), config = Config, valid = true }));
        }
        private static void Merge(JsonObject target, JsonObject patch)
        {
            foreach (var pair in patch)
            {
                if (pair.Value is null) target.Remove(pair.Key);
                else if (pair.Value is JsonObject child && target[pair.Key] is JsonObject existing) Merge(existing, child);
                else target[pair.Key] = pair.Value.DeepClone();
            }
        }
    }
}
