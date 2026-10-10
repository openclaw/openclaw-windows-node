using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupReadinessTests
{
    private static GatewayAiSetupCompletion Proof => new(SetupCompletionIntent.Dashboard,
        "gateway", new string('A', 64), "demo/model", "main", 2,
        IdentityBinding: new string('B', 64), SessionKey: "agent:main:main");

    [Fact]
    public async Task CompletionYieldsThenDrainsFinalizesAndPublishesWithoutADestination()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        using var owner = new SetupCompletionPreparation(Proof, _ => drain.Task,
            (_, _) => { calls.Add("verify"); return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)); },
            (_, _) => { calls.Add("finalize"); return Task.CompletedTask; },
            (preparation, _) =>
            {
                Assert.True(preparation.IsValid);
                Assert.Equal(Proof.SessionKey, preparation.SessionKey);
                calls.Add("publish");
                return Task.CompletedTask;
            });
        var completion = owner.StartAsync();
        Assert.Empty(calls);
        Assert.Same(completion, owner.StartAsync());
        drain.SetResult();
        await completion;
        Assert.Equal(["verify", "finalize", "publish"], calls);
        Assert.True(owner.IsCompleted);
    }

    [Fact]
    public async Task CloseDuringDrainRetainsOwnershipUntilAdmittedCleanupEnds()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var returnDrain = new ManualResetEventSlim();
        using var owner = new SetupCompletionPreparation(Proof, _ =>
        {
            entered.SetResult();
            Assert.True(returnDrain.Wait(TimeSpan.FromSeconds(10)), "The admitted drain must be allowed to return.");
            return drain.Task;
        },
            (_, _) => throw new InvalidOperationException("Must not verify"),
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"));
        var task = owner.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            owner.Dispose();
            returnDrain.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(owner.CleanupCompleted.IsCompleted);
        }
        finally
        {
            returnDrain.Set();
            drain.TrySetResult();
        }
        await owner.CleanupCompleted;
    }

    [Fact]
    public async Task UncertainFinalizationCannotBeReplayedByDuplicateStart()
    {
        var writes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => { writes++; throw new IOException("Unknown write outcome"); },
            (_, _) => throw new InvalidOperationException("Must not publish"));
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData(SetupNativeDestination.Chat)]
    [InlineData(SetupNativeDestination.Channels)]
    [InlineData(SetupNativeDestination.Skills)]
    public async Task UnchangedReadyChoiceNavigatesWithoutInferenceOrSetup(SetupNativeDestination destination)
    {
        var transport = new Transport { Generation = 11 };
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        Assert.Equal(11, binding.Proof.VerifiedGeneration);
        var callsBefore = transport.Calls.Count;
        using var chooser = new SetupReadyCoordinator(binding, (choice, _) =>
        {
            Assert.Equal(destination, choice.Target.Destination);
            Assert.Equal(11, choice.Verification.VerifiedGeneration);
            return Task.CompletedTask;
        });
        await chooser.SelectAsync(destination);
        Assert.True(chooser.IsCompleted);
        Assert.Equal(["config.get"], transport.Calls.Skip(callsBefore));
        Assert.Equal(1, transport.Calls.Count(call => call == "openclaw.setup.verify"));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("disconnect")]
    [InlineData("identity")]
    [InlineData("session")]
    [InlineData("endpoint")]
    [InlineData("config")]
    [InlineData("invalidate")]
    public async Task DriftCannotNavigateOrSilentlyReverify(string drift)
    {
        var transport = new Transport();
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        switch (drift)
        {
            case "generation": transport.Generation++; break;
            case "disconnect": transport.IsConnected = false; break;
            case "identity": transport.Route = transport.Route with { IdentityBinding = new string('C', 64) }; break;
            case "session": transport.Route = transport.Route with { SessionKey = "agent:main:other" }; break;
            case "endpoint": transport.Route = transport.Route with { EndpointBinding = new string('C', 64) }; break;
            case "config": transport.Hash = "changed"; break;
            default: binding.Invalidate(); break;
        }
        using var chooser = new SetupReadyCoordinator(binding, (_, _) => throw new Exception("Must not navigate"));
        if (drift is "generation" or "disconnect" or "config" or "invalidate")
            await Assert.ThrowsAsync<SetupNativeReadinessExpiredException>(() => chooser.SelectAsync(SetupNativeDestination.Chat));
        else
            await Assert.ThrowsAsync<SetupNativeOwnershipException>(() => chooser.SelectAsync(SetupNativeDestination.Chat));
        Assert.Equal(1, transport.Calls.Count(call => call == "openclaw.setup.verify"));
    }

    [Fact]
    public async Task FailedNavigationCanRetryWithoutAnotherInference()
    {
        var transport = new Transport();
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        var opens = 0;
        using var chooser = new SetupReadyCoordinator(binding, (_, _) =>
            ++opens == 1 ? Task.FromException(new IOException("Mount failed")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => chooser.SelectAsync(SetupNativeDestination.Chat));
        await chooser.SelectAsync(SetupNativeDestination.Chat);
        Assert.Equal(2, opens);
        Assert.Equal(1, transport.Calls.Count(call => call == "openclaw.setup.verify"));
    }

    [Fact]
    public async Task PreparationTransfersOnlyOnceWithoutReconnectingOrRediscovering()
    {
        var transport = new Transport();
        var owner = new Owner();
        await using var preparation = await GatewayAiPreparation.PrepareAsync(transport, owner, default);
        var transferred = preparation.Take();
        Assert.Same(transport, transferred.Transport);
        Assert.NotNull(transferred.Client.Detection);
        Assert.Throws<SetupNativeOwnershipException>(() => preparation.Take());
        await preparation.DisposeAsync();
        Assert.Equal(0, owner.Disposals);
        await transferred.Owner.DisposeAsync();
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(["openclaw.setup.detect"], transport.Calls);
    }

    [Fact]
    public async Task PreparationDriftAndFailureDisposeOriginalOwner()
    {
        var transport = new Transport();
        var owner = new Owner();
        await using (var preparation = await GatewayAiPreparation.PrepareAsync(transport, owner, default))
        {
            transport.Generation++;
            Assert.Throws<SetupNativeOwnershipException>(() => preparation.Take());
        }
        Assert.Equal(1, owner.Disposals);
        transport = new Transport { Failure = new UnauthorizedAccessException("Not authorized") };
        owner = new Owner();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GatewayAiPreparation.PrepareAsync(transport, owner, default));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ConfigChangingDuringInferenceNeverPublishesReady()
    {
        var transport = new Transport { ChangeDuringVerify = true };
        await Assert.ThrowsAsync<SetupNativeReadinessExpiredException>(() =>
            SetupNativeReadyBinding.VerifyAsync(transport, Proof, default));
    }

    [Fact]
    public async Task CloseDuringFinalizationJoinsAdmittedWriteWithoutPublishing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            async (_, _) => { entered.SetResult(); await finished.Task; },
            (_, _) => throw new Exception("Must not publish"));
        var completion = owner.StartAsync();
        await entered.Task;
        owner.Dispose();
        Assert.False(completion.IsCompleted);
        finished.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(owner.IsCompleted);
    }

    [Fact]
    public async Task DuplicateDestinationCannotOpenASecondSurface()
    {
        var binding = await SetupNativeReadyBinding.VerifyAsync(new Transport(), Proof, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var chooser = new SetupReadyCoordinator(binding, async (_, _) =>
        {
            entered.SetResult();
            await mounted.Task;
        });
        var selecting = chooser.SelectAsync(SetupNativeDestination.Chat);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => chooser.SelectAsync(SetupNativeDestination.Skills));
        mounted.SetResult();
        await selecting;
    }

    [Fact]
    public async Task SameConfigObservationPreservesReadyButChangedRevisionCannotBeRestored()
    {
        var transport = new Transport();
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        Assert.True(binding.ObserveConfiguration(JsonSerializer.SerializeToElement(new { hash = transport.Hash })));
        Assert.False(binding.ObserveConfiguration(JsonSerializer.SerializeToElement(new { hash = "different" })));
        Assert.False(binding.ObserveConfiguration(JsonSerializer.SerializeToElement(new { hash = transport.Hash })));
        await Assert.ThrowsAsync<SetupNativeReadinessExpiredException>(() => binding.RequireCurrentAsync(default));
    }

    [Fact]
    public async Task ReconnectedDiscoveryStopsBeingActionable()
    {
        var transport = new Transport();
        await using var preparation = await GatewayAiPreparation.PrepareAsync(transport, new Owner(), default);
        Assert.True(preparation.Client.HasCurrentDiscovery);
        transport.Generation++;
        Assert.False(preparation.Client.HasCurrentDiscovery);
    }

    [Fact]
    public async Task PresentationDrainDeadlineDoesNotReleaseUnderlyingOwnership()
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new SetupCompletionPreparation(Proof,
            _ => { entered.SetResult(); return drain.Task; },
            (_, _) => throw new Exception("Must not verify"),
            (_, _) => throw new Exception("Must not finalize"),
            (_, _) => throw new Exception("Must not publish"), clock);
        var waiting = owner.StartAsync();
        await entered.Task;
        // Admission creates the deadline before waiting on the already-owned drain.
        clock.Advance(SetupNativeCompletionCoordinator.DrainTimeout);
        var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(() => waiting);
        Assert.Equal(SetupNativeCompletionPhase.PageDrain, error.Phase);
        Assert.False(owner.CanRetry);
        owner.Dispose();
        Assert.False(owner.CleanupCompleted.IsCompleted);
        drain.SetResult();
        await owner.CleanupCompleted;
    }

    [Fact]
    public async Task ExplicitRetryAfterVerificationFailurePublishesFreshProofOnlyOnce()
    {
        var verifies = 0;
        var finalizes = 0;
        var publishes = 0;
        var stages = new List<SetupNativeCompletionStage>();
        var current = Proof with { VerifiedGeneration = 44 };
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => ++verifies == 1 ? Task.FromException<SetupVerifiedNativeRoute>(new IOException("Transient")) :
                Task.FromResult(new SetupVerifiedNativeRoute(current, current.SessionKey!)),
            (_, _) => { finalizes++; return Task.CompletedTask; },
            (preparation, _) =>
            {
                Assert.Equal(current, preparation.Verification);
                return ++publishes == 1 ? Task.FromException(new IOException("Restart failed")) : Task.CompletedTask;
            });
        owner.StateChanged += () => stages.Add(owner.Stage);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.True(owner.CanRetry);
        await Assert.ThrowsAsync<IOException>(owner.RetryAsync);
        Assert.True(owner.CanRetry);
        await owner.RetryAsync();
        Assert.Equal(2, verifies);
        Assert.Equal(1, finalizes);
        Assert.Equal(2, publishes);
        Assert.Contains(SetupNativeCompletionStage.Draining, stages);
        Assert.Contains(SetupNativeCompletionStage.Finalizing, stages);
        owner.Dispose();
        await owner.CleanupCompleted;
        await Assert.ThrowsAsync<ObjectDisposedException>(owner.StartAsync);
    }

    [Fact]
    public async Task UncertainFinalizationHasNoExplicitRetryEdge()
    {
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => Task.FromException(new IOException("Uncertain write")),
            (_, _) => throw new Exception("Must not publish"));
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.False(owner.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
    }

    [Fact]
    public async Task CommittedContextCheckpointResumesSettingsWithoutReopeningOrReplayingGatewayFinalization()
    {
        var checkpoint = false;
        var verifies = 0;
        var gatewayWrites = 0;
        var settingsAttempts = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => { verifies++; return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)); },
            (_, _) =>
            {
                if (!checkpoint) { gatewayWrites++; checkpoint = true; }
                return ++settingsAttempts == 1 ? Task.FromException(new IOException("Settings unavailable")) : Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask, canResumeCommittedFinalization: () => checkpoint);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.True(owner.CanRetry);
        await owner.RetryAsync();
        Assert.Equal(1, verifies);
        Assert.Equal(1, gatewayWrites);
        Assert.Equal(2, settingsAttempts);
        Assert.True(owner.IsCompleted);
    }

    [Fact]
    public void ReadyRequiresSuccessfulCurrentMountAndConsumption_InEitherOrder()
    {
        foreach (var consumeFirst in new[] { false, true })
        {
            var state = new SetupReadyPresentation();
            var generation = state.BeginMount();
            Assert.False(state.IsReady);
            if (consumeFirst) state.ConsumeReceipt();
            Assert.False(state.IsReady);
            Assert.True(state.CompleteMount(generation));
            if (!consumeFirst) Assert.False(state.IsReady);
            state.ConsumeReceipt();
            Assert.True(state.IsReady);
            state.Fail();
            Assert.False(state.IsReady);
            Assert.True(state.ReceiptConsumed);
            Assert.False(state.CompleteMount(generation));
            Assert.True(state.CompleteMount(state.BeginMount()));
            Assert.True(state.IsReady);
        }
    }

    [Fact]
    public void InvalidationBeforeConsumptionCannotRevealReadyOrLoseRecoveryState()
    {
        var state = new SetupReadyPresentation();
        var generation = state.BeginMount();
        state.Fail();
        Assert.False(state.CompleteMount(generation));
        state.ConsumeReceipt();
        Assert.True(state.ReceiptConsumed);
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task ConfigChecksAndInferenceShareExistingModelVerificationBudget()
    {
        var clock = new ManualTimeProvider();
        var reads = 0;
        var transport = new Transport
        {
            BeforeReply = method => clock.Advance(TimeSpan.FromSeconds(
                method == "config.get" ? ++reads == 1 ? 10 : 41 : 100))
        };
        var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(() =>
            SetupNativeReadyBinding.VerifyAsync(transport, Proof, default, clock));
        Assert.Equal(SetupNativeCompletionPhase.ModelVerification, error.Phase);
        Assert.Equal(TimeSpan.FromSeconds(150), SetupNativeCompletionTiming.ModelVerification);
    }

    [Fact]
    public async Task ConfigCheckAndMountShareExistingNavigationBudget()
    {
        var transport = new Transport();
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        var clock = new ManualTimeProvider();
        transport.BeforeReply = _ => clock.Advance(TimeSpan.FromSeconds(20));
        using var choice = new SetupReadyCoordinator(binding, (_, ct) =>
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, timeProvider: clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => choice.SelectAsync(SetupNativeDestination.Chat));
        Assert.False(choice.IsCompleted);
    }

    [Fact]
    public async Task DiscoveryDisplayDoesNotReadAuthorityButActionsStillDo()
    {
        var transport = new Transport();
        await using var preparation = await GatewayAiPreparation.PrepareAsync(transport, new Owner(), default);
        transport.FailRouteRead = true;
        Assert.True(preparation.Client.HasCurrentDiscovery);
        await Assert.ThrowsAsync<IOException>(() => preparation.Client.DetectAsync());
    }

    [Theory]
    [InlineData(true, false, false, false, true, "VerificationFailedTitle")]
    [InlineData(true, false, false, true, false, "VerifyingTitle")]
    [InlineData(false, true, false, true, false, "LocalInstalling")]
    [InlineData(false, false, false, true, false, "Preparing")]
    [InlineData(false, false, true, false, false, "Title.Text")]
    public void PinnedVerificationHasAnHonestHeading(bool pinned, bool local, bool choices, bool busy, bool failed, string key) =>
        Assert.Equal(key, AiSetupReadinessPresentation.TitleKey(pinned, local, choices, busy, failed));

    [Theory]
    [InlineData(LocalAiOnboardingState.Checking)]
    [InlineData(LocalAiOnboardingState.Unknown)]
    public void AdmittedNativeLocalAiRemainsVisibleWhileAvailabilityIsPending(LocalAiOnboardingState state)
    {
        var snapshot = new LocalAiOnboardingSnapshot(state);
        Assert.True(AiSetupReadinessPresentation.ShowLocalChoice(true, true, snapshot));
        Assert.False(snapshot.CanUse);
        Assert.False(snapshot.CanReview);
        Assert.False(AiSetupReadinessPresentation.ShowLocalChoice(true, false, snapshot));
    }

    [Fact]
    public void FailedProviderDiscoveryPreservesIndependentLocalReviewButNotPendingOrReplacedAuthority()
    {
        var snapshot = new LocalAiOnboardingSnapshot(LocalAiOnboardingState.SetUp,
            Target: new("gateway", "fixture-distro", 18789, null, null));
        Assert.True(snapshot.CanReview);
        Assert.True(AiSetupReadinessPresentation.ShowLocalRecovery(false, false, snapshot));
        Assert.False(AiSetupReadinessPresentation.ShowLocalRecovery(true, false, snapshot));
        Assert.False(AiSetupReadinessPresentation.ShowLocalRecovery(false, true, snapshot));
        Assert.False(AiSetupReadinessPresentation.ShowLocalRecovery(false, false, null));
        Assert.False(AiSetupReadinessPresentation.ShowLocalRecovery(false, false, new(LocalAiOnboardingState.Checking)));
    }

    [Fact]
    public void FailedUnconsumedLocalAdmissionShowsExplicitChoiceNotPreparationOrReadiness()
    {
        Assert.Equal("Title.Text", AiSetupReadinessPresentation.TitleKey(
            pinnedModel: false, localOperation: false, choicesPrepared: false, busy: false, failed: true,
            localAdmissionFailed: true));
        Assert.Equal("Preparing", AiSetupReadinessPresentation.TitleKey(
            pinnedModel: false, localOperation: false, choicesPrepared: false, busy: true, failed: false,
            localAdmissionFailed: true));
        Assert.Equal("Preparing", AiSetupReadinessPresentation.TitleKey(
            pinnedModel: false, localOperation: false, choicesPrepared: false, busy: false, failed: true));
        Assert.Equal("VerificationFailedTitle", AiSetupReadinessPresentation.TitleKey(
            pinnedModel: true, localOperation: false, choicesPrepared: false, busy: false, failed: true,
            localAdmissionFailed: true));
    }

    [Fact]
    public async Task DiscoveryHintRecoversButNeverReusesPreviousChoicesDuringOrAfterFailedDetection()
    {
        var transport = new Transport();
        var client = new GatewayAiSetupClient(transport);
        await client.DetectAsync();
        Assert.True(client.HasCurrentDiscovery);
        transport.IsConnected = false;
        Assert.False(client.HasCurrentDiscovery);
        transport.IsConnected = true;
        Assert.True(client.HasCurrentDiscovery);
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PendingDetection = result.Task;
        var detection = client.DetectAsync();
        Assert.False(client.HasCurrentDiscovery);
        result.SetResult(JsonSerializer.SerializeToElement(new
        {
            candidates = Array.Empty<object>(), manualProviders = Array.Empty<object>(),
            workspace = "fixture", setupComplete = true, configuredModel = "demo/model",
        }));
        await detection;
        Assert.True(client.HasCurrentDiscovery);
        transport.PendingDetection = null;
        transport.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => client.DetectAsync());
        Assert.False(client.HasCurrentDiscovery);
        Assert.All(transport.Calls, method => Assert.Equal("openclaw.setup.detect", method));
    }

    [Theory]
    [InlineData(GatewayAiSetupPhase.Running)]
    [InlineData(GatewayAiSetupPhase.Uncertain)]
    [InlineData(GatewayAiSetupPhase.VerificationRequired)]
    [InlineData(GatewayAiSetupPhase.Prepared)]
    public void ReplacedPendingAuthorityRequiresBlockedRecoveryNotMutationReplay(GatewayAiSetupPhase phase)
    {
        Assert.True(AiSetupReadinessPresentation.IsManagerRecoveryBlocked(true, phase));
        Assert.False(AiSetupReadinessPresentation.IsManagerRecoveryBlocked(false, phase));
        Assert.False(AiSetupReadinessPresentation.IsManagerRecoveryBlocked(true, GatewayAiSetupPhase.Choosing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageDrainBoundsPureReadsButRetainsAdmittedMutationOwnership(bool mutation)
    {
        var clock = new ManualTimeProvider();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SetupPageRequestDrain();
        var reported = new List<bool>();
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = owner.DrainAsync(pending.Task, mutation, retained =>
        {
            reported.Add(retained);
            delayed.SetResult();
        }, clock);
        clock.Advance(SetupPageRequestDrain.PresentationTimeout);
        await delayed.Task;
        if (!mutation)
        {
            await draining;
            Assert.False(owner.ObservationCompleted.IsCompleted);
        }
        else
            Assert.False(draining.IsCompleted);
        pending.SetResult();
        await draining;
        await owner.ObservationCompleted;
        Assert.Equal([mutation], reported);
    }

    [Theory]
    [InlineData(GatewayAiSetupPhase.Idle, false)]
    [InlineData(GatewayAiSetupPhase.Choosing, false)]
    [InlineData(GatewayAiSetupPhase.Rejected, false)]
    [InlineData(GatewayAiSetupPhase.Running, true)]
    [InlineData(GatewayAiSetupPhase.Uncertain, true)]
    [InlineData(GatewayAiSetupPhase.VerificationRequired, true)]
    [InlineData(GatewayAiSetupPhase.Prepared, true)]
    public void PageDrainClassificationNeverTreatsPendingEffectsAsPureReads(GatewayAiSetupPhase phase, bool retained)
    {
        Assert.Equal(retained, SetupPageRequestDrain.RequiresOwnership(phase, localMutation: false));
        Assert.True(SetupPageRequestDrain.RequiresOwnership(phase, localMutation: true));
        Assert.True(SetupPageRequestDrain.RequiresOwnership(null, localMutation: false));
    }

    [Theory]
    [InlineData("timeout", GatewayAiDiscoveryFailure.Unavailable)]
    [InlineData("io", GatewayAiDiscoveryFailure.Unavailable)]
    [InlineData("data", GatewayAiDiscoveryFailure.InvalidResponse)]
    [InlineData("json", GatewayAiDiscoveryFailure.InvalidResponse)]
    public async Task ClassifiedDiscoveryFailureTransfersAuthenticatedOwnerWithoutAutomaticRedetection(
        string failure, GatewayAiDiscoveryFailure expected)
    {
        var transport = new Transport { Failure = failure switch
        {
            "timeout" => new TimeoutException(), "io" => new IOException(),
            "data" => new InvalidDataException(), _ => new JsonException()
        }};
        var owner = new Owner();
        await using var preparation = await GatewayAiPreparation.PrepareAsync(transport, owner, default);
        Assert.Equal(expected, preparation.DiscoveryFailure);
        var result = preparation.Take();
        Assert.Equal(expected, result.DiscoveryFailure);
        Assert.Null(result.Client.Detection);
        Assert.False(result.Client.HasCurrentDiscovery);
        await preparation.DisposeAsync();
        Assert.Equal(0, owner.Disposals);
        Assert.Equal(["openclaw.setup.detect"], transport.Calls);
        await result.Owner.DisposeAsync();
        Assert.Equal(1, owner.Disposals);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("disconnect")]
    [InlineData("cancel")]
    public async Task FailedDiscoveryCannotTransferAfterAuthorityLossOrCancellation(string drift)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport { Failure = new TimeoutException() };
        transport.BeforeReply = _ =>
        {
            if (drift == "identity") transport.Route = transport.Route with { IdentityBinding = new string('C', 64) };
            if (drift == "generation") transport.Generation++;
            if (drift == "disconnect") transport.IsConnected = false;
            if (drift == "cancel") cancellation.Cancel();
        };
        var owner = new Owner();
        if (drift == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GatewayAiPreparation.PrepareAsync(transport, owner, cancellation.Token));
        else
            await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
                GatewayAiPreparation.PrepareAsync(transport, owner, cancellation.Token));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task UnclassifiedDiscoveryFailureIsNotConvertedIntoPreparedRecovery()
    {
        var owner = new Owner();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatewayAiPreparation.PrepareAsync(new Transport { Failure = new InvalidOperationException("Unknown failure") },
                owner, default));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task GatewayConnectionLostDuringDiscoveryDisposesOwnerWithoutCancellingPageToken()
    {
        using var page = new CancellationTokenSource();
        var lost = new OpenClaw.Shared.GatewayConnectionLostException(1006, "Synthetic connection loss");
        var transport = new Transport { Failure = lost };
        transport.BeforeReply = _ => transport.IsConnected = false;
        var owner = new Owner();
        var error = await Assert.ThrowsAsync<OpenClaw.Shared.GatewayConnectionLostException>(() =>
            GatewayAiPreparation.PrepareAsync(transport, owner, page.Token));
        Assert.Same(lost, error);
        Assert.False(page.IsCancellationRequested);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(["openclaw.setup.detect"], transport.Calls);
    }

    [Fact]
    public async Task ObservationIsInstalledBeforeFinalRevisionCheckAndDisposedOnMissedEvent()
    {
        var transport = new Transport();
        var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, default);
        var observation = new Subscription();
        var calls = transport.Calls.Count;
        await Assert.ThrowsAsync<SetupNativeReadinessExpiredException>(() => binding.ObserveAndCheckAsync(_ =>
        {
            // Change occurs just before observation is installed, with no delivered event.
            transport.Hash = "changed-before-subscribe";
            return observation;
        }, default));
        Assert.Equal(["config.get"], transport.Calls.Skip(calls));
        Assert.Equal(1, observation.Disposals);
        Assert.Equal(1, transport.Calls.Count(method => method == "openclaw.setup.verify"));
    }

    [Fact]
    public async Task StableFreshnessConfirmationChecksSelectedModelWithoutInferenceOrMutation()
    {
        var transport = new Transport { Generation = 99, Hash = "unrelated-new-revision" };
        await SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, Proof, default);
        Assert.Equal(["config.get", "openclaw.setup.detect", "config.get"], transport.Calls);
        transport.ConfiguredModel = "other/model";
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
            SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, Proof, default));
    }

    private sealed class Subscription : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    private sealed class Owner : IAsyncDisposable
    {
        public int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Transport : IGatewayAiSetupTransport
    {
        private GatewayAiSetupRoute _route = new("gateway", "main", "authority",
            Proof.EndpointBinding, Proof.IdentityBinding, Proof.SessionKey);
        public GatewayAiSetupRoute Route
        {
            get => FailRouteRead ? throw new IOException("Identity read failed") : _route;
            set => _route = value;
        }
        public bool FailRouteRead { get; set; }
        public Action<string>? BeforeReply { get; set; }
        public Task<JsonElement>? PendingDetection { get; set; }
        public long Generation { get; set; } = 2;
        public bool IsConnected { get; set; } = true;
        public string Hash { get; set; } = "revision-1";
        public bool Fail { get; set; }
        public Exception? Failure { get; set; }
        public string ConfiguredModel { get; set; } = "demo/model";
        public bool ChangeDuringVerify { get; set; }
        public IReadOnlyCollection<string> Methods => ["openclaw.setup.verify", "openclaw.setup.detect", "openclaw.setup.activate"];
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public List<string> Calls { get; } = [];
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(method);
            BeforeReply?.Invoke(method);
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "openclaw.setup.detect" && PendingDetection is not null) return PendingDetection;
            if (Failure is not null) throw Failure;
            if (Fail) throw new IOException("Discovery failed");
            if (method == "openclaw.setup.verify" && ChangeDuringVerify) Hash = "revision-2";
            return Task.FromResult(JsonDocument.Parse(method switch
            {
                "config.get" => $$"""{"hash":"{{Hash}}","valid":true}""",
                "openclaw.setup.verify" => """{"ok":true,"modelRef":"demo/model","latencyMs":12}""",
                "openclaw.setup.detect" => $$"""{"candidates":[],"manualProviders":[],"workspace":"fixture","setupComplete":true,"configuredModel":"{{ConfiguredModel}}"}""",
                _ => throw new InvalidOperationException("Unexpected mutation"),
            }).RootElement.Clone());
        }
    }
}
