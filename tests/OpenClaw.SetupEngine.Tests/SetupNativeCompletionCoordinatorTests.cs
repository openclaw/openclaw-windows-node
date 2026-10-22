using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupNativeCompletionCoordinatorTests
{
    private static GatewayAiSetupCompletion Proof => new(SetupCompletionIntent.CustodianOnboarding,
        "gateway-a", new string('A', 64), "provider/model", "primary", 2,
        IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main");
    private static SetupVerifiedNativeRoute Route => new(Proof, "agent:primary:main");

    [Fact]
    public async Task SlowDrainAuthorityChecksAndModelProofHaveIndependentBudgets()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        using var owner = new SetupNativeCompletionCoordinator(Proof,
            ct => { clock.Advance(TimeSpan.FromSeconds(25)); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            (_, ct) =>
            {
                // Fresh authority subprocesses followed by the observed 22.5-second model RPC.
                clock.Advance(TimeSpan.FromSeconds(20));
                clock.Advance(TimeSpan.FromSeconds(22.5));
                ct.ThrowIfCancellationRequested();
                calls.Add("verified");
                return Task.FromResult(Route);
            },
            (_, ct) =>
            {
                clock.Advance(TimeSpan.FromSeconds(136));
                ct.ThrowIfCancellationRequested();
                calls.Add("finalized");
                return Task.CompletedTask;
            },
            (_, ct) =>
            {
                clock.Advance(TimeSpan.FromSeconds(136));
                ct.ThrowIfCancellationRequested();
                calls.Add("published");
                return Task.CompletedTask;
            }, clock);

        await owner.SelectAsync(SetupNativeDestination.Chat);
        Assert.Equal(["verified", "finalized", "published"], calls);
        Assert.True(owner.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SelectAsync(SetupNativeDestination.Chat));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadOnlyPhaseTimeoutNamesPhaseAndNeverFinalizesOrPublishes(bool drain)
    {
        var clock = new ManualTimeProvider();
        using var owner = new SetupNativeCompletionCoordinator(Proof,
            ct =>
            {
                if (drain) clock.Advance(SetupNativeCompletionCoordinator.DrainTimeout);
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            (_, ct) =>
            {
                clock.Advance(SetupNativeCompletionCoordinator.VerificationTimeout);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Route);
            },
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"), clock);
        var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(() => owner.SelectAsync(SetupNativeDestination.Chat));
        Assert.Contains(drain ? "closing the previous AI setup page" : "checking AI setup readiness", error.Message);
        Assert.Equal(drain ? SetupNativeCompletionPhase.PageDrain : SetupNativeCompletionPhase.Verification, error.Phase);
        Assert.False(owner.IsBusy);
        Assert.False(owner.IsCompleted);
    }

    [Theory]
    [InlineData(SetupNativeDestination.Chat)]
    [InlineData(SetupNativeDestination.Channels)]
    [InlineData(SetupNativeDestination.Skills)]
    [InlineData(SetupNativeDestination.WhatsApp)]
    [InlineData(SetupNativeDestination.Telegram)]
    public async Task ShowingChooserDoesNothing_ExplicitChoiceVerifiesThenFinalizesAndPublishes(SetupNativeDestination destination)
    {
        var calls = new List<string>();
        using var owner = new SetupNativeCompletionCoordinator(Proof,
            _ => Task.CompletedTask,
            (_, _) => { calls.Add("verify"); return Task.FromResult(Route); },
            (_, _) => { calls.Add("finalize"); return Task.CompletedTask; },
            (choice, _) => { calls.Add(choice.Target.Destination.ToString()); return Task.CompletedTask; });
        Assert.Empty(calls);
        Assert.False(owner.IsCompleted);
        await owner.SelectAsync(destination);
        Assert.Equal(["verify", "finalize", destination.ToString()], calls);
        Assert.True(owner.IsCompleted);
    }

    [Fact]
    public async Task DoubleClickIsGatedBeforeAwait_AndPublicationRetryDoesNotRepeatFinalization()
    {
        var pending = new TaskCompletionSource<SetupVerifiedNativeRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalized = 0;
        var launched = 0;
        using var owner = new SetupNativeCompletionCoordinator(Proof, _ => Task.CompletedTask,
            (_, _) => pending.Task, (_, _) => { finalized++; return Task.CompletedTask; },
            (_, _) => ++launched == 1 ? Task.FromException(new IOException("Synthetic launch failure")) : Task.CompletedTask);
        var first = owner.SelectAsync(SetupNativeDestination.Chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SelectAsync(SetupNativeDestination.Telegram));
        pending.SetResult(Route);
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.False(owner.IsCompleted);
        await owner.SelectAsync(SetupNativeDestination.Chat);
        Assert.Equal(1, finalized);
        Assert.Equal(2, launched);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("agent")]
    [InlineData("model")]
    [InlineData("role")]
    [InlineData("session")]
    public async Task ChangedProofCannotFinalizeOrPublish(string changed)
    {
        var proof = Proof with
        {
            GatewayId = changed == "gateway" ? "other" : Proof.GatewayId,
            EndpointBinding = changed == "endpoint" ? new string('B', 64) : Proof.EndpointBinding,
            AgentId = changed == "agent" ? "other" : Proof.AgentId,
            ModelRef = changed == "model" ? "other/model" : Proof.ModelRef,
            ModelTarget = changed == "role" ? "utility" : null,
        };
        using var owner = new SetupNativeCompletionCoordinator(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(proof,
                changed == "session" ? "agent:other:main" : Route.SessionKey)),
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"));
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() => owner.SelectAsync(SetupNativeDestination.Chat));
        Assert.False(owner.IsCompleted);
    }

    [Fact]
    public async Task SameAgentDifferentSession_CannotFinalizeOriginalVerification()
    {
        var finalized = false;
        using var owner = new SetupNativeCompletionCoordinator(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, "agent:primary:alternate")),
            (_, _) => { finalized = true; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() => owner.SelectAsync(SetupNativeDestination.Chat));
        Assert.False(finalized);
    }

    [Fact]
    public void UnavailableVerificationIsRetryableRatherThanAnOwnershipChange()
    {
        Assert.Throws<InvalidOperationException>(() => SetupNativeCompletionVerifier.RequireAvailable(
            new() { Ok = false, Status = "unavailable", Error = "Synthetic model not ready" }));
        SetupNativeCompletionVerifier.RequireAvailable(new() { Ok = true });
    }

    [Fact]
    public async Task ClosingDuringVerificationCancelsAndNeverFinalizesOrPublishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SetupNativeCompletionCoordinator(Proof, _ => Task.CompletedTask,
            async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Route;
            },
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"));
        var task = owner.SelectAsync(SetupNativeDestination.Chat);
        await entered.Task;
        owner.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(owner.IsCompleted);
        Assert.False(owner.IsBusy);
    }
}
