using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiOnboardingTests
{
    private static readonly SetupLocalAiTarget Target = new("gateway", "Managed", 18789, null, null);
    private static readonly HostHardwareInfo Hardware = new(Architecture.X64, 128L << 30, 100L << 30,
        [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30, DriverVersion: "615.0",
            CudaMajorVersion: 13, StableId: "GPU-test")], false);

    [Theory]
    [InlineData(false, false, false, LocalAiRuntimeState.Stopped, false, LocalAiOnboardingState.SetUp)]
    [InlineData(true, true, false, LocalAiRuntimeState.Stopped, false, LocalAiOnboardingState.StartAndUse)]
    [InlineData(true, true, false, LocalAiRuntimeState.Healthy, false, LocalAiOnboardingState.Use)]
    [InlineData(true, false, false, LocalAiRuntimeState.Stopped, false, LocalAiOnboardingState.Repair)]
    [InlineData(false, false, true, LocalAiRuntimeState.Stopped, false, LocalAiOnboardingState.Repair)]
    [InlineData(true, true, false, LocalAiRuntimeState.Failed, false, LocalAiOnboardingState.Repair)]
    [InlineData(true, true, false, LocalAiRuntimeState.Conflict, false, LocalAiOnboardingState.Repair)]
    [InlineData(true, true, false, LocalAiRuntimeState.Starting, false, LocalAiOnboardingState.Working)]
    [InlineData(true, true, false, LocalAiRuntimeState.Stopping, false, LocalAiOnboardingState.Working)]
    [InlineData(false, false, false, LocalAiRuntimeState.Stopped, true, LocalAiOnboardingState.BusyGpu)]
    public void Projection_DistinguishesEvidenceStates(bool installed, bool verified, bool damaged,
        LocalAiRuntimeState runtimeState, bool busy, LocalAiOnboardingState expected)
    {
        var install = Install();
        var eligibility = LocalInferenceEligibility.Evaluate(Hardware, install.Manifest.ModelCatalogId);
        if (busy) eligibility = eligibility with { Status = LocalInferenceEligibilityStatus.EligibleButBusy };
        var snapshot = LocalAiOnboardingSnapshot.Project(Target, eligibility, installed ? install : null,
            verified, damaged, RuntimeSnapshot(install, runtimeState));
        Assert.Equal(expected, snapshot.State);
        Assert.True(snapshot.ShowLocalChoice);
    }

    [Theory]
    [InlineData(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, LocalInferenceSelectionFailureCode.NoNvidiaGpu, false)]
    [InlineData(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, LocalInferenceSelectionFailureCode.None, false)]
    [InlineData(LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow, LocalInferenceSelectionFailureCode.None, false)]
    [InlineData(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, LocalInferenceSelectionFailureCode.None, true)]
    [InlineData(LocalInferenceEligibilityFailureCode.DriverTooOld, LocalInferenceSelectionFailureCode.None, true)]
    [InlineData(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, LocalInferenceSelectionFailureCode.RuntimeUnavailable, true)]
    [InlineData(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, LocalInferenceSelectionFailureCode.UnknownModel, true)]
    [InlineData(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, LocalInferenceSelectionFailureCode.None, true)]
    [InlineData(LocalInferenceEligibilityFailureCode.None, LocalInferenceSelectionFailureCode.None, true)]
    public void FreshDevice_HidesOnlyConclusiveHardwareIncompatibility(
        LocalInferenceEligibilityFailureCode failure, LocalInferenceSelectionFailureCode selectionFailure, bool show)
    {
        var eligibility = LocalInferenceEligibility.Evaluate(Hardware) with
        {
            Status = LocalInferenceEligibilityStatus.Unsupported,
            FailureCode = failure, SelectionFailureCode = selectionFailure,
        };
        var fresh = LocalAiOnboardingSnapshot.Project(Target, eligibility, null, false, false, null);
        Assert.False(fresh.HasInstallationEvidence);
        Assert.Equal(show, fresh.ShowLocalChoice);
        Assert.False(fresh.CanReview);
        Assert.False(fresh.CanUse);

        foreach (var existing in new[]
        {
            LocalAiOnboardingSnapshot.Project(Target, eligibility, Install(), false, false, null),
            LocalAiOnboardingSnapshot.Project(Target, eligibility, null, false, true, null),
            LocalAiOnboardingSnapshot.Project(Target, eligibility, null, false, false, null, "known-receipt"),
            LocalAiOnboardingSnapshot.Project(Target, eligibility, null, false, false, null, installationKnown: true),
            LocalAiOnboardingSnapshot.Project(Target with { ModelCatalogId = "pinned" }, eligibility, null, false, false, null),
            LocalAiOnboardingSnapshot.Project(Target, eligibility, null, false, false,
                RuntimeSnapshot(Install(), LocalAiRuntimeState.Failed)),
        })
        {
            Assert.True(existing.HasInstallationEvidence);
            Assert.True(existing.ShowLocalChoice);
            Assert.Equal(fresh.State, existing.State);
            Assert.False(existing.CanUse);
            Assert.False(existing.CanReview);
        }
    }

    [Fact]
    public void MissingFactsAndRemoteGateway_KeepAttentionWithoutPromotingAChoice()
    {
        var unsupported = LocalInferenceEligibility.Evaluate(Hardware with { Gpus = [] });
        foreach (var snapshot in new[]
        {
            new LocalAiOnboardingSnapshot(LocalAiOnboardingState.Checking),
            new LocalAiOnboardingSnapshot(LocalAiOnboardingState.Unsupported),
            LocalAiOnboardingSnapshot.Project(Target, null, null, false, false, null),
            LocalAiOnboardingSnapshot.Project(Target, null, null, false, true, null),
            LocalAiOnboardingSnapshot.Project(null, unsupported, null, false, false, null),
        })
        {
            Assert.True(snapshot.ShowLocalChoice);
            Assert.False(snapshot.CanReview);
            Assert.False(snapshot.CanUse);
        }
        Assert.True(LocalAiOnboardingSnapshot.Project(Target, null, Install(), false, false, null).HasInstallationEvidence);
    }

    [Fact]
    public void UnsupportedFreshDevice_DoesNotHideIndependentGatewayCandidates()
    {
        var local = LocalAiOnboardingSnapshot.Project(Target,
            LocalInferenceEligibility.Evaluate(Hardware with { Gpus = [] }), null, false, false, null);
        var view = AiSetupPresentationModel.Create(new()
        {
            Candidates = [new("existing-model", "Gateway model", "Ready", "provider/model", true)],
            ManualProviders = [], Workspace = "workspace", SetupComplete = true,
        }, Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet(), hasLocalChoice: local.ShowLocalChoice);
        Assert.False(local.ShowLocalChoice);
        Assert.Single(view.Candidates);
        Assert.True(view.HasChoices);
    }

    [Fact]
    public void UnknownUnsupportedAndRemote_AreNotSynonyms()
    {
        var eligible = LocalInferenceEligibility.Evaluate(Hardware);
        Assert.True(eligible.CanInstall);
        Assert.Equal(LocalAiOnboardingState.Unknown,
            LocalAiOnboardingSnapshot.Project(Target, null, null, false, false, null).State);
        Assert.Equal(LocalAiOnboardingState.Unknown,
            LocalAiOnboardingSnapshot.Project(Target, eligible with
            { FailureCode = LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete }, null, false, false, null).State);
        Assert.Equal(LocalAiOnboardingState.Unsupported,
            LocalAiOnboardingSnapshot.Project(Target,
                LocalInferenceEligibility.Evaluate(Hardware with { Gpus = [] }), null, false, false, null).State);
        Assert.Equal(LocalAiOnboardingState.UnsupportedGateway,
            LocalAiOnboardingSnapshot.Project(null, eligible, null, false, false, null).State);
    }

    [Fact]
    public void Healthy_WrongModelOrUnownedEndpoint_IsNotUse()
    {
        var install = Install();
        var eligibility = LocalInferenceEligibility.Evaluate(Hardware, install.Manifest.ModelCatalogId);
        foreach (var runtime in new[]
        {
            RuntimeSnapshot(install, LocalAiRuntimeState.Healthy) with { ModelId = "other" },
            RuntimeSnapshot(install, LocalAiRuntimeState.Healthy) with { Ownership = LocalAiOwnership.None },
            RuntimeSnapshot(install, LocalAiRuntimeState.Healthy) with { Endpoint = new("http://127.0.0.1:19999/v1") },
        })
            Assert.NotEqual(LocalAiOnboardingState.Use,
                LocalAiOnboardingSnapshot.Project(Target, eligibility, install, true, false, runtime).State);
    }

    [Fact]
    public void OwnLoadedModel_DoesNotMistakeItsGpuAllocationForAnotherBusyApplication()
    {
        var install = Install();
        var eligibility = LocalInferenceEligibility.Evaluate(Hardware, install.Manifest.ModelCatalogId) with
        { Status = LocalInferenceEligibilityStatus.EligibleButBusy };
        var runtime = RuntimeSnapshot(install, LocalAiRuntimeState.Healthy) with
        {
            ModelEvidence = new(LocalAiModelAvailabilityState.Loaded, DateTimeOffset.UtcNow,
                new string('a', 64), 1, install.Manifest.ModelAlias),
        };
        Assert.Equal(LocalAiOnboardingState.Use,
            LocalAiOnboardingSnapshot.Project(Target, eligibility, install, true, false, runtime).State);
    }

    [Fact]
    public async Task Observation_CancelsRefreshAndDiscardsStaleCallbacks()
    {
        var first = new TaskCompletionSource<LocalAiOnboardingSnapshot>();
        var second = new TaskCompletionSource<LocalAiOnboardingSnapshot>();
        var host = new ObservationHost(first.Task, second.Task);
        await using var observation = new LocalAiOnboardingObservation(host);
        var stages = new List<LocalAiSetupStage>();
        var progress = new SynchronousProgress<LocalAiSetupStage>(stages.Add);
        var firstRequest = observation.RefreshAsync(progress);
        host.Progress[0]!.Report(LocalAiSetupStage.CheckingHardware);
        var secondRequest = observation.RefreshAsync(progress);
        Assert.True(host.Tokens[0].IsCancellationRequested);
        host.Progress[0]!.Report(LocalAiSetupStage.CheckingFiles);
        host.Progress[1]!.Report(LocalAiSetupStage.CheckingHardware);
        second.SetResult(new(LocalAiOnboardingState.SetUp, Target));
        await secondRequest;
        host.Progress[1]!.Report(LocalAiSetupStage.CheckingFiles);
        first.SetResult(new(LocalAiOnboardingState.Repair, Target));
        await firstRequest;
        Assert.Equal(LocalAiOnboardingState.SetUp, observation.Snapshot.State);
        Assert.Equal(0, host.Mutations);
        Assert.Equal([LocalAiSetupStage.CheckingHardware, LocalAiSetupStage.CheckingHardware], stages);
    }

    [Fact]
    public async Task ClosingObservation_FencesCallbacksAndNeverMutates()
    {
        var pending = new TaskCompletionSource<LocalAiOnboardingSnapshot>();
        var host = new ObservationHost(pending.Task);
        var observation = new LocalAiOnboardingObservation(host);
        var notifications = 0;
        observation.Changed += () => notifications++;
        var request = observation.RefreshAsync();
        var closing = observation.DisposeAsync().AsTask();
        Assert.True(host.Tokens[0].IsCancellationRequested);
        pending.SetResult(new(LocalAiOnboardingState.Use, Target));
        await Task.WhenAll(request, closing);
        Assert.Equal(1, notifications);
        Assert.Equal(0, host.Mutations);
        await observation.DisposeAsync();
    }

    [Fact]
    public async Task FailedObservation_IsUnknown_AndRetryDoesNotBlockGatewayWork()
    {
        var host = new ObservationHost(Task.FromException<LocalAiOnboardingSnapshot>(new IOException()),
            Task.FromResult(new LocalAiOnboardingSnapshot(LocalAiOnboardingState.SetUp, Target)));
        await using var observation = new LocalAiOnboardingObservation(host);
        await observation.RefreshAsync();
        Assert.Equal(LocalAiOnboardingState.Unknown, observation.Snapshot.State);
        await observation.RefreshAsync();
        Assert.Equal(LocalAiOnboardingState.SetUp, observation.Snapshot.State);
        Assert.Equal(0, host.Mutations);
    }

    [Theory]
    [InlineData("gateway", "provider/model", 1)]
    [InlineData("different", "provider/model", 2)]
    [InlineData("gateway", "provider/other-case", 2)]
    [InlineData(null, "provider/model", 2)]
    public void DuplicateSuppression_RequiresExactGatewayAndModel(string? localGateway, string model, int count)
    {
        var detection = new GatewayAiSetupDetection
        {
            Candidates = [new("existing-model", "Managed", "", "provider/model", true),
                new("saved-auth:other", "Other", "", "provider/other", false)],
            ManualProviders = [], Workspace = "workspace", SetupComplete = true,
        };
        var view = AiSetupPresentationModel.Create(detection, Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet(),
            gatewayId: "gateway", localGatewayId: localGateway, localModelRef: model);
        Assert.Equal(count, view.Candidates.Count);
        Assert.Equal(2, detection.Candidates.Length);
    }

    [Fact]
    public void UtilityChoices_AreNotMainChoices_ButExplanationIsVisible()
    {
        var view = AiSetupPresentationModel.Create(new()
        {
            Candidates = [new("existing-model", "Utility", "", "provider/model", true, ModelTarget: "utility")],
            ManualProviders = [new("key", "Utility key", ModelTarget: "utility")],
            AuthOptions = [new("auth", "Utility login", Featured: true, ModelTarget: "utility")],
            PrepareOptions = [new("prepare", "Utility preparation", ModelTarget: "utility")],
            Workspace = "workspace", SetupComplete = false, UtilityModel = "provider/model",
        }, Enum.GetValues<GatewayAiSetupChoiceKind>().ToHashSet());
        Assert.True(view.HasUtilityChoices);
        Assert.False(view.HasChoices);
        Assert.Empty(view.Candidates);
    }

    [Fact]
    public async Task Host_FirstInstallWithoutReceipt_AdmitsSameGatewayWithoutRuntimeMutation()
    {
        using var directory = new TempDirectory();
        var registry = new GatewayRegistry(directory.Path);
        registry.Load();
        var lifecycleResolver = new LocalAiGatewayDistroResolver(registry);
        var runtime = new FakeRuntime(RuntimeSnapshot(Install(), LocalAiRuntimeState.Stopped));
        var resolver = new LocalAiSetupRouteResolver(() => registry, directory.Path, directory.Path, "Managed",
            (distro, expectedId) =>
            {
                var disk = new GatewayRegistry(directory.Path);
                disk.Load();
                var owner = disk.GetActive();
                Assert.Equal("Managed", distro);
                return new(owner is not null, owner?.Id, owner?.Url, true, true, true, distro, true, 0, []);
            });
        var host = new SetupLocalAiHost(() => resolver.ResolveAsync(), () => registry, () => runtime,
            _ => Task.FromResult<LocalAiResolvedInstall?>(null), (_, _) => Task.FromResult(true),
            _ => Task.FromResult(Hardware), () => throw new InvalidOperationException("No mutation during discovery."));
        host.BeginGatewaySetup();
        // PairOperator owns a separate registry during the installation pipeline.
        var pairingRegistry = Registry(directory.Path);
        pairingRegistry.Save();
        var savedBytes = File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json"));
        Assert.Equal(LocalAiOnboardingState.UnsupportedGateway, (await host.ObserveAsync(default)).State);
        Assert.False(lifecycleResolver.Resolve().Success);
        var registryEvents = 0;
        registry.Changed += (_, _) => registryEvents++;
        await host.ReconcileGatewaySetupAsync(pairingRegistry.GetSnapshot(), "gateway");
        var observation = await host.ObserveAsync(CancellationToken.None);
        Assert.Equal(LocalAiOnboardingState.SetUp, observation.State);
        var target = await host.RevalidateReviewAsync(observation, CancellationToken.None);
        Assert.Equal("gateway", target.GatewayId);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal("gateway", registry.GetActive()?.Id);
        Assert.True(lifecycleResolver.Resolve().Success);
        Assert.Equal("Managed", lifecycleResolver.Resolve().DistroName);
        var commands = new ReadOnlyGateway(Install(), false);
        var canonicalLifecycle = new LocalAiGatewayProviderCoordinator(commands, lifecycleResolver, NullLogger.Instance);
        Assert.True((await canonicalLifecycle.ValidatePublicationAsync(Install())).Success);
        Assert.Equal(2, commands.Reads);
        Assert.Equal(1, registryEvents);
        Assert.Equal(savedBytes, File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json")));
        registry.Remove("gateway");
        registry.AddOrUpdate(new GatewayRecord
        { Id = "replacement", Url = "ws://127.0.0.1:18789", IsLocal = true, SetupManagedDistroName = "Other" });
        Assert.False(lifecycleResolver.Resolve().Success);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("url")]
    [InlineData("token")]
    [InlineData("ssh")]
    [InlineData("addition")]
    [InlineData("removal")]
    [InlineData("other-config")]
    public void CompletedSetup_AdoptsOnlyTheOwningPipelineOutput(string drift)
    {
        using var directory = new TempDirectory();
        var canonical = Registry(directory.Path);
        canonical.Save();
        var baseline = canonical.CapturePersistedSnapshot();
        var logger = new SetupLogger(filePath: null);
        var context = new SetupContext(new SetupConfig(), logger, new TransactionJournal(filePath: null),
            new CommandRunner(logger), CancellationToken.None, directory.Path, directory.Path)
        { ExpectedGatewayRegistry = baseline };
        var writer = context.LoadSetupRegistry();
        writer.AddOrUpdate(new GatewayRecord { Id = "installed", Url = "wss://installed.example", SharedGatewayToken = "owned-output" });
        writer.SetActive("installed");
        context.SaveSetupRegistry(writer);
        var expected = context.ExpectedGatewayRegistry!;
        var external = new GatewayRegistry(directory.Path);
        external.Load();
        switch (drift)
        {
            case "url": external.Update("installed", record => record with { Url = "wss://changed.example" }); break;
            case "token": external.Update("installed", record => record with { SharedGatewayToken = "external" }); break;
            case "ssh": external.Update("installed", record => record with { SshTunnel = new("other", "ssh.example", 18789, 19001) }); break;
            case "addition": external.AddOrUpdate(new() { Id = "external", Url = "wss://external.example" }); break;
            case "removal": external.Remove("gateway"); break;
            case "other-config": external.Update("gateway", record => record with { FriendlyName = "external" }); break;
        }
        external.Save();
        var saved = File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json"));
        if (drift == "none")
        {
            Assert.Equal("installed", canonical.ReconcileCompletedSetup(baseline, expected, "installed").ActiveId);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => canonical.ReconcileCompletedSetup(baseline, expected, "installed"));
            Assert.True(GatewayRegistry.HasSameSetupAuthority(baseline, canonical.GetSnapshot()));
            Assert.Throws<InvalidOperationException>(() => context.LoadSetupRegistry());
            Assert.Throws<InvalidOperationException>(() => context.SaveSetupRegistry(writer));
        }
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json")));
    }

    [Fact]
    public void CompletedSetup_ReconciliationDoesNotDiscardUnsavedCanonicalEdits()
    {
        using var directory = new TempDirectory();
        var registry = new GatewayRegistry(directory.Path);
        var baseline = registry.GetSnapshot();
        Registry(directory.Path).Save();
        registry.AddOrUpdate(new GatewayRecord { Id = "unsaved", Url = "wss://remote.example" });
        Assert.Throws<InvalidOperationException>(() => registry.ReconcileCompletedSetup(baseline, baseline, "gateway"));
        Assert.Equal("unsaved", Assert.Single(registry.GetAll()).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedRecovery_PreservesReconnectBookkeepingWithoutRejectingSuccess(bool saved)
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        registry.Save();
        var baseline = registry.CapturePersistedSnapshot();
        var connected = new DateTime(2026, 9, 24, 2, 0, 0, DateTimeKind.Utc);
        if (saved)
            registry.UpdateAndSave("gateway", record => record with { LastConnected = connected });
        else
            registry.Update("gateway", record => record with { LastConnected = connected });
        var savedBytes = File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json"));
        var events = 0;
        registry.Changed += (_, _) => events++;

        var result = registry.ReconcileCompletedSetup(baseline, baseline, "gateway");

        Assert.Equal("gateway", result.ActiveId);
        Assert.Equal(connected, Assert.Single(result.Records).LastConnected);
        Assert.Equal(connected, registry.GetActive()!.LastConnected);
        Assert.Equal(0, events);
        Assert.Equal(savedBytes, File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json")));
    }

    [Fact]
    public void CompletedRecovery_StillRejectsAuthorityChangesAlongsideReconnectBookkeeping()
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        registry.Save();
        var baseline = registry.CapturePersistedSnapshot();
        registry.UpdateAndSave("gateway", record => record with
        {
            LastConnected = DateTime.UtcNow,
            Url = "ws://127.0.0.1:19999"
        });
        Assert.Throws<InvalidOperationException>(() => registry.ReconcileCompletedSetup(baseline, baseline, "gateway"));
        Assert.Equal("ws://127.0.0.1:19999", registry.GetActive()!.Url);
    }

    [Fact]
    public void GatewaySetupCannotCaptureAnAlreadyUnsavedBaseline()
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        registry.Save();
        var savedBytes = File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json"));
        registry.Update("gateway", record => record with { FriendlyName = "Unsaved edit" });
        var host = Host(registry, new FakeRuntime(RuntimeSnapshot(Install(), LocalAiRuntimeState.Stopped)), () => null);
        Assert.Throws<InvalidOperationException>(host.BeginGatewaySetup);
        Assert.Equal("Unsaved edit", registry.GetActive()?.FriendlyName);
        Assert.Equal(savedBytes, File.ReadAllBytes(Path.Combine(directory.Path, "gateways.json")));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"activeId\":\"other\",\"gateways\":[]}")]
    public void CompletedSetup_InvalidDiskNeverAdoptsStaleState(string json)
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        var baseline = registry.GetSnapshot();
        File.WriteAllText(Path.Combine(directory.Path, "gateways.json"), json);
        Assert.ThrowsAny<Exception>(() => registry.ReconcileCompletedSetup(baseline, baseline, "gateway"));
        Assert.Equal(baseline, registry.GetSnapshot() with { Records = baseline.Records });
        Assert.Equal("gateway", Assert.Single(registry.GetAll()).Id);
    }

    [Theory]
    [InlineData(LocalAiRuntimeState.Failed, false)]
    [InlineData(LocalAiRuntimeState.Conflict, false)]
    [InlineData(LocalAiRuntimeState.Failed, true)]
    public async Task Host_StartFailureClearsBindingOnlyWithRuntimeCleanupEvidence(
        LocalAiRuntimeState failure, bool unresolved)
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install, LocalAiRuntimeState.Stopped))
        {
            StartResult = RuntimeSnapshot(install, failure) with
            { GatewayRouteRequiresResolution = unresolved, Detail = "Synthetic startup failure." },
        };
        var commands = new ReadOnlyGateway(install, false);
        var host = Host(registry, runtime, () => install,
            () => new(commands, new LocalAiGatewayDistroResolver(registry), NullLogger.Instance));
        var selected = await host.ObserveAsync(default);
        var use = new LocalAiOnboardingUse(host);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => use.UseAsync(selected, default));
        Assert.Equal(!unresolved, error is LocalAiStartFailedException);
        Assert.Equal(unresolved, use.Expected is not null);
        Assert.Equal(LocalAiOnboardingState.Repair, (await host.ObserveAsync(default)).State);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(2, commands.Reads); // Admission only, no second publication after failure.
    }

    [Fact]
    public async Task Host_SwitchedGatewayOrModel_RejectsBeforeStarting()
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install, LocalAiRuntimeState.Stopped));
        var host = Host(registry, runtime, () => install);
        var selected = await host.ObserveAsync(CancellationToken.None);
        Assert.Equal(LocalAiOnboardingState.StartAndUse, selected.State);
        install = install with { Manifest = install.Manifest with { RequestedPort = 18808 } };
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.UseAsync(selected, CancellationToken.None));
        registry.AddOrUpdate(new GatewayRecord { Id = "remote", Url = "wss://gateway.example" });
        registry.SetActive("remote");
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.UseAsync(selected, CancellationToken.None));
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Host_GatewaySwitchDuringAdmissionRejectsWithoutMutationOrUncertainBinding()
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        registry.AddOrUpdate(new GatewayRecord { Id = "remote", Url = "wss://other.example" });
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install, LocalAiRuntimeState.Stopped));
        var admissionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ReadOnlyGateway(install, false)
        {
            AfterRead = async reads =>
            {
                if (reads != 2) return;
                admissionRead.SetResult();
                await releaseAdmission.Task;
            },
        };
        var host = Host(registry, runtime, () => install,
            () => new(commands, new LocalAiGatewayDistroResolver(registry), NullLogger.Instance));
        var selected = await host.ObserveAsync(default);
        var use = new LocalAiOnboardingUse(host);
        var pending = use.UseAsync(selected, default);
        try
        {
            await admissionRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registry.SetActive("remote");
        }
        finally { releaseAdmission.TrySetResult(); }
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => pending);
        Assert.Null(use.Expected);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(2, commands.Reads);
        Assert.Equal(LocalAiOnboardingState.UnsupportedGateway, (await host.ObserveAsync(default)).State);
    }

    [Fact]
    public async Task Host_GatewaySwitchAfterStartRetainsUncertainExactBinding()
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        registry.AddOrUpdate(new GatewayRecord { Id = "remote", Url = "wss://other.example" });
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install, LocalAiRuntimeState.Healthy))
        { OnStart = () => registry.SetActive("remote") };
        var commands = new ReadOnlyGateway(install, false);
        var host = Host(registry, runtime, () => install,
            () => new(commands, new LocalAiGatewayDistroResolver(registry), NullLogger.Instance));
        var selected = await host.ObserveAsync(default);
        var use = new LocalAiOnboardingUse(host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => use.UseAsync(selected, default));
        Assert.Equal(new SetupLocalAiUseResult("gateway", selected.ModelRef!), use.Expected);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(2, commands.Reads);
    }

    [Fact]
    public void RecoveryDraft_PreservesCapabilitiesAndExternalRouteWithoutTailscaleInstallation()
    {
        var config = new SetupConfig { LocalAiRecoveryGatewayId = "gateway" };
        var draft = new SetupAccessDraft(config);
        draft.SelectRoute(SetupGatewayRoute.Existing, true);
        draft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
        config.LocalAi.Enabled = true;
        config.Tailscale.Enabled = true;
        draft.LocalAiReady = true;
        Assert.True(draft.CanInstall(localAiRecovery: true));
        Assert.False(draft.CanInstall());
        Assert.Equal(SetupCapabilityProfile.ReadOnly, draft.Profile);
        Assert.Equal(SetupGatewayRoute.Existing, draft.Route);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_ExplicitUseRequiresPublicationAdmissionAndReturnsExactIdentity(bool drifted)
    {
        using var directory = new TempDirectory();
        var registry = Registry(directory.Path);
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install, LocalAiRuntimeState.Healthy));
        var commands = new ReadOnlyGateway(install, drifted);
        var host = Host(registry, runtime, () => install,
            () => new(commands, new LocalAiGatewayDistroResolver(registry), NullLogger.Instance));
        var selected = await host.ObserveAsync(CancellationToken.None);
        Assert.Equal(0, commands.Reads);
        var stages = new List<LocalAiSetupStage>();
        var progress = new SynchronousProgress<LocalAiSetupStage>(stages.Add);
        if (drifted)
        {
            await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.UseAsync(selected, CancellationToken.None, progress));
            Assert.Equal(0, runtime.Calls);
            Assert.Equal([LocalAiSetupStage.CheckingHardware, LocalAiSetupStage.CheckingFiles,
                LocalAiSetupStage.PreparingGateway], stages);
        }
        else
        {
            var use = new LocalAiOnboardingUse(host);
            await use.UseAsync(selected, CancellationToken.None, progress);
            var result = use.Expected!;
            Assert.Equal("gateway", result.GatewayId);
            Assert.Equal(selected.ModelRef, result.ModelRef);
            Assert.Equal(1, runtime.Calls);
            Assert.Equal(4, commands.Reads);
            Assert.Equal([LocalAiSetupStage.CheckingHardware, LocalAiSetupStage.CheckingFiles,
                LocalAiSetupStage.PreparingGateway, LocalAiSetupStage.StartingRuntime,
                LocalAiSetupStage.PublishingProvider], stages);
        }
    }

    private static SetupLocalAiHost Host(GatewayRegistry registry, FakeRuntime runtime,
        Func<LocalAiResolvedInstall?> install, Func<LocalAiGatewayProviderCoordinator>? provider = null) =>
        new(() => Task.FromResult(new LocalAiSetupResolution(LocalAiSetupRoute.Recovery,
                new("gateway", "Managed", 18789, install()?.Manifest.ModelCatalogId, install()?.Manifest.RequestedPort))),
            () => registry, () => runtime, _ => Task.FromResult(install()), (_, _) => Task.FromResult(true),
            _ => Task.FromResult(Hardware), provider ?? (() => throw new InvalidOperationException("No route mutation expected.")));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReopenedOwnedFailureOffersRecoveryNotArtifactRepair(bool verified)
    {
        var install = Install();
        var runtime = RuntimeSnapshot(install, LocalAiRuntimeState.Failed) with
        { Ownership = LocalAiOwnership.CompanionManaged, GatewayRouteRequiresResolution = true };
        var result = LocalAiOnboardingSnapshot.Project(Target with { IsNative = true },
            LocalInferenceEligibility.Evaluate(Hardware, install.Manifest.ModelCatalogId),
            install, verified, false, runtime);
        Assert.Equal(verified ? LocalAiOnboardingState.Reconcile : LocalAiOnboardingState.Repair, result.State);
        Assert.Equal(verified, result.CanUse);
        Assert.Equal(!verified, result.CanReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WslUseAndRepairRejectNativeOwnershipBeforeAdmission(bool repair)
    {
        using var temp = new TempDirectory();
        var registry = Registry(temp.Path);
        var install = Install();
        var runtime = new FakeRuntime(RuntimeSnapshot(install,
            repair ? LocalAiRuntimeState.Failed : LocalAiRuntimeState.Healthy));
        var paths = new LocalAiPaths(temp.Path);
        new LocalAiNativeBindingStore(paths).Save(new("native", "endpoint", "identity",
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), null, "hash", false));
        var lifecycle = new LocalAiGatewayLifecycle(paths, temp.Path, () => registry, () => null,
            new LocalAiGatewayProviderCoordinator(new ReadOnlyGateway(install, false),
                new LocalAiGatewayDistroResolver(registry), NullLogger.Instance), NullLogger.Instance);
        var host = new SetupLocalAiHost(
            () => Task.FromResult(new LocalAiSetupResolution(LocalAiSetupRoute.Recovery,
                new("gateway", "Managed", 18789, install.Manifest.ModelCatalogId, install.Manifest.RequestedPort))),
            () => registry, () => runtime, _ => Task.FromResult<LocalAiResolvedInstall?>(install),
            (_, _) => Task.FromResult(true), _ => Task.FromResult(Hardware),
            () => throw new InvalidOperationException("WSL admission must not run."),
            nativeLifecycle: lifecycle);
        var selected = await host.ObserveAsync(default);
        var error = repair
            ? await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.RevalidateReviewAsync(selected, default))
            : await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() => host.UseAsync(selected, default));
        Assert.Contains("release", error.Message);
        Assert.Equal(0, runtime.Calls);
    }

    private sealed class ReadOnlyGateway(LocalAiResolvedInstall install, bool drifted) : IWslCommandRunner
    {
        public int Reads { get; private set; }
        public Func<int, Task>? AfterRead { get; init; }
        public async Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command,
            CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null)
        {
            Assert.Equal("Managed", name);
            Assert.Contains("get", command);
            Assert.Null(standardInput);
            Reads++;
            if (AfterRead is not null) await AfterRead(Reads);
            return new WslCommandResult(0,
                command.Contains(LocalAiGatewayProviderDefinition.ProviderPath)
                    ? LocalAiGatewayProviderDefinition.BuildProviderJson(install)
                    : System.Text.Json.JsonSerializer.Serialize(drifted
                        ? "other/model" : LocalAiGatewayProviderDefinition.BuildPrimaryModel(install)), "");
        }
        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => throw new InvalidOperationException();
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private static GatewayRegistry Registry(string directory)
    {
        var registry = new GatewayRegistry(directory);
        registry.AddOrUpdate(new GatewayRecord
        { Id = "gateway", Url = "ws://127.0.0.1:18789", IsLocal = true, SetupManagedDistroName = "Managed" });
        registry.SetActive("gateway");
        return registry;
    }

    internal static LocalAiResolvedInstall Install()
    {
        var model = LocalModelCatalog.FindInstalled(LocalModelCatalog.Qwen35BModelId)!;
        var endpoint = new Uri("http://127.0.0.1:18803/v1");
        return new(new LocalAiInstallManifest
        {
            EngineVersion = "test", Architecture = "x64", RuntimeId = "test", ModelCatalogId = model.Id,
            SelectedGpuId = "GPU-test", ExecutablePath = "engines\\llama-server.exe",
            RuntimeAssets = ImmutableArray<LocalAiAssetReceipt>.Empty, ModelPath = "models\\test.gguf",
            ModelId = "test/source", ModelAlias = model.Id, ContextLength = LocalModelCatalog.NativeContextTokens,
            ModelAsset = new() { FileName = "test.gguf", SourceUrl = "https://example.com/test",
                SizeBytes = 1, Sha256 = new string('a', 64) },
            Endpoint = endpoint.AbsoluteUri,
        }, "llama-server.exe", "test.gguf", endpoint);
    }

    internal static LocalAiRuntimeSnapshot RuntimeSnapshot(LocalAiResolvedInstall install, LocalAiRuntimeState state) =>
        new(state, state == LocalAiRuntimeState.Healthy ? LocalAiOwnership.CompanionManaged : LocalAiOwnership.None,
            install.Endpoint!, "test", install.Manifest.ModelCatalogId,
            new(LocalAiModelAvailabilityState.Verified, DateTimeOffset.UtcNow, new string('a', 64), 1),
            null, null, null, DateTimeOffset.UtcNow)
        { GatewayRouteRequiresResolution = state != LocalAiRuntimeState.Healthy };

    internal sealed class FakeRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public int Calls { get; private set; }
        public int WithdrawOnlyCalls { get; private set; }
        public Action? OnStart { get; init; }
        public Func<Task>? OnStartAsync { get; init; }
        public LocalAiRuntimeSnapshot? StartResult { get; init; }
        public LocalAiRuntimeSnapshot Snapshot { get; set; } = snapshot;
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) =>
            EnsureStartedAsync(cancellationToken);
        public Task<LocalAiRuntimeSnapshot> ReconcileStoppedAsync(CancellationToken cancellationToken = default)
        { WithdrawOnlyCalls++; return Task.FromResult(Snapshot); }
        public async Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            OnStart?.Invoke();
            Snapshot = StartResult ?? Snapshot;
            if (OnStartAsync is not null) await OnStartAsync();
            return Snapshot;
        }
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Observation must not stop a runtime.");
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Observation must not restart a runtime.");
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Runtime refresh can mutate a Gateway route.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservationHost(params Task<LocalAiOnboardingSnapshot>[] results) : ISetupLocalAiHost
    {
        public GatewayRegistrySnapshot BeginGatewaySetup() => throw new InvalidOperationException();
        public Task ReconcileGatewaySetupAsync(GatewayRegistrySnapshot expectedOutput, string? completedGatewayId) => throw new InvalidOperationException();
        private readonly Queue<Task<LocalAiOnboardingSnapshot>> _results = new(results);
        public List<CancellationToken> Tokens { get; } = [];
        public List<IProgress<LocalAiSetupStage>?> Progress { get; } = [];
        public int Mutations { get; private set; }
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct)
        { Tokens.Add(ct); return _results.Dequeue(); }
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct, IProgress<LocalAiSetupStage>? progress)
        { Progress.Add(progress); return ObserveAsync(ct); }
        public Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
        { Mutations++; throw new InvalidOperationException(); }
        public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
        { Mutations++; throw new InvalidOperationException(); }
    }
}
