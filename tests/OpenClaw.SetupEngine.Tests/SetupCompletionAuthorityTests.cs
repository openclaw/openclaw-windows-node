using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupCompletionAuthorityTests
{
    [Theory]
    [InlineData("rotated")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public void PersistedIdentityCannotRelabelAnAlreadyVerifiedClient(string change)
    {
        using var directory = new TempDirectory();
        var identity = new DeviceIdentity(directory.Path);
        identity.Initialize();
        var captured = SetupCompletionAuthority.CaptureIdentity(directory.Path, identity.DeviceId);
        SetupCompletionAuthority.RequirePersistedIdentity(directory.Path, captured);
        var file = Path.Combine(directory.Path, "device-key-ed25519.json");
        if (change == "rotated")
        {
            using var replacement = new TempDirectory();
            new DeviceIdentity(replacement.Path).Initialize();
            File.Copy(Path.Combine(replacement.Path, "device-key-ed25519.json"), file, true);
        }
        else if (change == "missing") File.Delete(file);
        else File.WriteAllText(file, "{invalid");
        var before = File.Exists(file) ? File.ReadAllText(file) : null;
        Assert.Throws<SetupNativeOwnershipException>(() =>
            SetupCompletionAuthority.RequirePersistedIdentity(directory.Path, captured));
        Assert.Equal(captured, SetupCompletionAuthority.CaptureIdentity(directory.Path, identity.DeviceId));
        Assert.Equal(before, File.Exists(file) ? File.ReadAllText(file) : null);
    }

    [Fact]
    public void VerifiedReconnectNeverGeneratesAMissingIdentity()
    {
        using var directory = new TempDirectory();
        Assert.Throws<DeviceIdentityLoadException>(() => new OpenClawGatewayClient(
            "wss://not-contacted.invalid", "synthetic", identityPath: directory.Path, requireExistingIdentity: true));
        Assert.False(File.Exists(Path.Combine(directory.Path, "device-key-ed25519.json")));
    }

    [Fact]
    public void NoAuthenticatedSigningIdentityCannotProduceValidAuthority()
    {
        var record = new GatewayRecord { Id = "gateway", Url = "wss://not-contacted.invalid" };
        var binding = new SetupGatewaySessionBinding(record);
        var route = binding.GetRoute(record, "identity", "agent:primary:main", null);
        Assert.Null(route.IdentityBinding);
        Assert.False(SetupCompletionAuthority.IsValid(route.IdentityBinding, route.SessionKey, route.AgentId));
    }

    private static GatewayAiSetupCompletion Proof => new(SetupCompletionIntent.CustodianOnboarding,
        "gateway", new string('A', 64), "provider/model", "primary", 1,
        IdentityBinding: SetupCompletionAuthority.CaptureIdentity("identity", "device-a"),
        SessionKey: "agent:primary:main");

    [Theory]
    [InlineData("device")]
    [InlineData("identity")]
    [InlineData("session")]
    [InlineData("missing")]
    [InlineData("managed-use")]
    public void CrossSessionVerification_RejectsChangedOrMissingStableAuthority(string change)
    {
        var current = Proof with
        {
            VerifiedGeneration = 7,
            IdentityBinding = change switch
            {
                "device" => SetupCompletionAuthority.CaptureIdentity("identity", "device-b"),
                "identity" => SetupCompletionAuthority.CaptureIdentity("other", "device-a"),
                "missing" => null,
                _ => Proof.IdentityBinding,
            },
            SessionKey = change == "session" ? "agent:primary:alternate" : Proof.SessionKey,
            RequiresManagedLocalAi = change == "managed-use",
        };
        Assert.Throws<SetupNativeOwnershipException>(() => SetupNativeVerification.RequireSame(
            Proof, new(current, current.SessionKey!)));
    }

    [Fact]
    public void FreshGenerationWithSameIdentityAndFullSession_IsAcceptedWithoutLeakingIdentityPath()
    {
        SetupNativeVerification.RequireSame(Proof, new(Proof with { VerifiedGeneration = 19 }, Proof.SessionKey!));
        var json = System.Text.Json.JsonSerializer.Serialize(Proof);
        Assert.DoesNotContain(Path.GetFullPath("identity"), json);
        Assert.DoesNotContain("device-a", json);
        Assert.NotEqual(SetupCompletionAuthority.CaptureIdentity("a|b", "c"),
            SetupCompletionAuthority.CaptureIdentity("a", "b|c"));
    }

    [Fact]
    public async Task PersistedPortDrift_IsRejectedBeforeFreshSessionCanConnect()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var record = registry.AddOrUpdate(new()
        {
            Id = "gateway", Url = "wss://not-contacted.invalid",
            SshTunnel = new("user", "not-contacted.invalid", 18789, 19001),
        });
        registry.SetActive(record.Id);
        registry.Save();
        var path = registry.GetIdentityDirectory(record.Id);
        var device = new DeviceIdentity(path);
        device.Initialize();
        var proof = Proof with { EndpointBinding = GatewayDashboardBinding.Capture(record),
            IdentityBinding = SetupCompletionAuthority.CaptureIdentity(path, device.DeviceId) };
        SetupGatewaySession.RequireCompletionGateway(temp.Path, proof);
        registry.Update(record.Id, value => value with { SshTunnel = value.SshTunnel! with { LocalPort = 19002 } });
        registry.Save();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyAsync(temp.Path, proof, timeout.Token));
    }

    [Fact]
    public async Task ClosingDuringAdmittedDrainRetainsOwnershipUntilCleanupEnds()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var returnDrain = new ManualResetEventSlim();
        using var owner = new SetupCompletionPreparation(Proof, _ =>
        {
            entered.SetResult();
            Assert.True(returnDrain.Wait(TimeSpan.FromSeconds(10)));
            return drain.Task;
        },
            (_, _) => throw new InvalidOperationException("Must not verify"),
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"));
        var completion = owner.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            owner.Dispose();
            returnDrain.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
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
    public async Task UncertainFinalizationCannotReplayGatewayWrite()
    {
        var writes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => { writes++; throw new IOException("Unknown write outcome"); },
            (_, _) => throw new InvalidOperationException("Must not publish"));
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.False(owner.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task LaunchedRestartCannotBePublishedAgainAfterShutdownSchedulingFails()
    {
        var launches = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                launches++;
                throw new SetupPreparationCommittedException();
            });

        await Assert.ThrowsAsync<SetupPreparationCommittedException>(owner.StartAsync);
        Assert.False(owner.CanRetry);
        Assert.False(owner.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task CancellationAfterPublicationCannotLaunchASecondRestart()
    {
        var launches = 0;
        SetupCompletionPreparation? owner = null;
        using (owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                launches++;
                owner!.Dispose();
                return Task.CompletedTask;
            }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(owner.StartAsync);
            Assert.False(owner.CanRetry);
            Assert.Equal(1, launches);
            await owner.CleanupCompleted;
        }
    }

    [Fact]
    public async Task RetryAfterVerificationFailurePublishesWithoutRepeatingFinalization()
    {
        var verifies = 0;
        var finalizes = 0;
        var publishes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => ++verifies == 1
                ? Task.FromException<SetupVerifiedNativeRoute>(new IOException("Transient"))
                : Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => { finalizes++; return Task.CompletedTask; },
            (_, _) => ++publishes == 1
                ? Task.FromException(new IOException("Publish failed"))
                : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        await Assert.ThrowsAsync<IOException>(owner.RetryAsync);
        await owner.RetryAsync();
        Assert.Equal(2, verifies);
        Assert.Equal(1, finalizes);
        Assert.Equal(2, publishes);
        Assert.True(owner.IsCompleted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalizationAndPublicationWaitsAreFiniteButRetainAdmittedWork(bool publishing, bool cancel)
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizes = 0;
        var publishes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            async (_, _) =>
            {
                finalizes++;
                if (!publishing)
                {
                    entered.SetResult();
                    await release.Task; // Simulates an admitted write that cannot be withdrawn.
                }
            },
            async (_, _) =>
            {
                publishes++;
                entered.SetResult();
                await release.Task;
            }, clock);
        var completion = owner.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) owner.Dispose();
            else clock.Advance(TimeSpan.FromMinutes(27));
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(
                    () => completion.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(SetupNativeCompletionPhase.Execution, error.Phase);
                Assert.Equal("Onboarding_Ready_Timeout",
                    AiSetupReadinessPresentation.CompletionFailureKey(error, "unused"));
            }
            Assert.True(owner.HasPendingWork);
            Assert.False(owner.CanRetry);
            await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
            owner.Dispose();
            Assert.False(owner.CleanupCompleted.IsCompleted);
        }
        finally
        {
            owner.Dispose();
            release.TrySetResult();
            await owner.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(owner.HasPendingWork);
        Assert.False(owner.IsCompleted);
        Assert.Equal(1, finalizes);
        Assert.Equal(publishing ? 1 : 0, publishes);
    }

    [Fact]
    public async Task FailedAttemptCannotRenewExactExecutionDeadlineOnRetry()
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifies = 0;
        var finalizes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) =>
            {
                if (++verifies == 1)
                {
                    clock.Advance(TimeSpan.FromMinutes(20));
                    throw new IOException("Transient verification failure");
                }
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!));
            },
            async (_, _) =>
            {
                finalizes++;
                entered.SetResult();
                await release.Task;
            },
            (_, _) => throw new InvalidOperationException("Expired finalization must not publish"), clock);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.True(owner.CanRetry);
        var retry = owner.RetryAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromTicks(1));
            Assert.False(retry.IsCompleted);
            clock.Advance(TimeSpan.FromTicks(1));
            var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(
                () => retry.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SetupNativeCompletionPhase.Execution, error.Phase);
            Assert.False(owner.CanRetry);
        }
        finally
        {
            release.TrySetResult();
            owner.Dispose();
            await owner.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, verifies);
        Assert.Equal(1, finalizes);
    }

    [Fact]
    public async Task LateCommittedPublicationFailureCannotLaunchAgain()
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => Task.CompletedTask,
            async (_, _) =>
            {
                publishes++;
                entered.SetResult();
                await release.Task;
                throw new SetupPreparationCommittedException();
            }, clock);
        var completion = owner.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(27));
            await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(
                () => completion.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            owner.Dispose();
            await owner.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(owner.HasPendingWork);
        Assert.False(owner.CanRetry);
        Assert.False(owner.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
        Assert.Equal(1, publishes);
    }

    [Fact]
    public async Task IdleRetryExpiresAtOriginalDeadlineWithoutAdmittingMoreWork()
    {
        var clock = new ManualTimeProvider();
        var verifies = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => { verifies++; throw new IOException("Not ready"); },
            (_, _) => throw new InvalidOperationException("Must not finalize"),
            (_, _) => throw new InvalidOperationException("Must not publish"), clock);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        clock.Advance(TimeSpan.FromMinutes(27) - TimeSpan.FromTicks(1));
        Assert.True(owner.CanRetry);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(owner.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(owner.RetryAsync);
        Assert.Equal(1, verifies);
    }

    [Fact]
    public async Task DeadlineCancellationStillJoinsCooperativeFinalizersActualCleanup()
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            async (_, ct) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    cleaning.SetResult();
                    await release.Task;
                }
            },
            (_, _) => throw new InvalidOperationException("Cancelled finalization must not publish"), clock);
        var completion = owner.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(27));
            await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(
                () => completion.WaitAsync(TimeSpan.FromSeconds(5)));
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owner.HasPendingWork);
            Assert.False(owner.CanRetry);
            owner.Dispose();
            Assert.False(owner.CleanupCompleted.IsCompleted);
        }
        finally
        {
            owner.Dispose();
            release.TrySetResult();
            await owner.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(owner.HasPendingWork);
        Assert.False(owner.CanRetry);
    }

    [Fact]
    public async Task ConfirmedCommittedFinalizationResumesWithoutRepeatingVerificationOrPublication()
    {
        var committed = false;
        var verifies = 0;
        var mutations = 0;
        var publishes = 0;
        using var owner = new SetupCompletionPreparation(Proof, _ => Task.CompletedTask,
            (_, _) =>
            {
                verifies++;
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!));
            },
            (_, _) =>
            {
                if (!committed)
                {
                    mutations++;
                    committed = true;
                    throw new IOException("Startup registration not applied yet");
                }
                return Task.CompletedTask;
            },
            (_, _) => { publishes++; return Task.CompletedTask; },
            canResumeCommittedFinalization: () => committed);
        Assert.False(owner.CanRetry);
        await Assert.ThrowsAsync<IOException>(owner.StartAsync);
        Assert.True(owner.CanRetry);
        await owner.RetryAsync();
        Assert.True(owner.IsCompleted);
        Assert.Equal(1, verifies);
        Assert.Equal(1, mutations);
        Assert.Equal(1, publishes);
    }
}
