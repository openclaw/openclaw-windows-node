using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SetupNativeHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyInFlightPresentationSurvivesDuplicateBusyWithoutAReadinessProof(bool failVerification)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var ownership = new SetupHandoffPresentationOwnership();
        var verification = new TaskCompletionSource<SetupVerifiedNativeRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifies = 0;
        var navigations = 0;
        var shellOpen = true;
        var failures = new List<SetupNativeLaunchFailure>();
        SetupNativeHandoffLauncher Launcher() => new(() => Gateway,
            (_, _) => { verifies++; return verification.Task; },
            (completion, _) =>
            {
                Assert.Equal(Choice.Target, completion.Target);
                navigations++;
                return Task.CompletedTask;
            },
            failures.Add,
            showPreparing: (_, _) => throw new Exception("A legacy destination cannot become a preparation receipt."),
            acquisitionDeferred: status =>
            {
                var action = SetupDeferredPresentationPolicy.Project(status, hasUnboundStartupShell: shellOpen,
                    hasInFlightPresentation: ownership.IsActive);
                if (action == SetupDeferredPresentation.CloseUnboundShell) shellOpen = false;
            },
            acquirePresentation: ownership.Acquire);
        var first = Launcher().OpenAsync(store, handle, restartRecovery: recovery);
        try
        {
            Assert.True(ownership.IsActive);
            Assert.False(first.IsCompleted);
            Assert.False(await Launcher().OpenAsync(store, handle, restartRecovery: recovery));
            Assert.True(shellOpen);
            Assert.True(ownership.IsActive);
            Assert.Equal(1, verifies);
            Assert.Equal(0, navigations);
            Assert.Empty(failures);
            Assert.Equal(handle, recovery.Read());
        }
        finally
        {
            if (failVerification) verification.TrySetException(new IOException("Synthetic verification unavailable"));
            else verification.TrySetResult(new(Proof, Choice.Target.SessionKey));
        }
        Assert.Equal(!failVerification, await first);
        Assert.False(ownership.IsActive);
        Assert.Equal(failVerification ? 0 : 1, navigations);
        Assert.Equal(1, verifies);
        Assert.Equal(failVerification ? handle : null, recovery.Read());
        Assert.Equal(failVerification ? SetupHandoffAcquisitionStatus.RetryRequired : SetupHandoffAcquisitionStatus.Invalid,
            store.Acquire(handle).Status);
    }

    [Fact]
    public async Task GenuinelyUnboundBusyShellStillClosesWithoutDisturbingTheReceipt()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        using var otherLease = store.Acquire(handle).Lease;
        Assert.NotNull(otherLease);
        var path = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        var before = File.ReadAllBytes(path);
        var ownership = new SetupHandoffPresentationOwnership();
        var shellOpen = true;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => throw new Exception("Busy cannot verify"),
            (_, _) => throw new Exception("Busy cannot navigate"),
            _ => throw new Exception("Busy cannot report a new failure"),
            acquisitionDeferred: status =>
            {
                Assert.Equal(SetupDeferredPresentation.CloseUnboundShell,
                    SetupDeferredPresentationPolicy.Project(status, shellOpen, ownership.IsActive));
                shellOpen = false;
            },
            acquirePresentation: ownership.Acquire);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.False(shellOpen);
        Assert.False(ownership.IsActive);
        Assert.Equal(before, File.ReadAllBytes(path));
        otherLease.RetainForExplicitRetry();
    }

    [Fact]
    public async Task PersistedRetrySettlesEarlyStartupWithoutVerificationOrRenewingReceipt()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        using (var lease = store.Acquire(handle).Lease) lease!.RetainForExplicitRetry();
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var path = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        var before = File.ReadAllBytes(path);
        var presentation = SetupDeferredPresentation.None;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => throw new Exception("Non-explicit startup cannot verify a retry receipt"),
            (_, _) => throw new Exception("Must not open"),
            _ => throw new Exception("Legacy failure path must not be replayed"),
            acquisitionDeferred: status => presentation = SetupDeferredPresentationPolicy.Project(status, true));
        Assert.False(await launcher.OpenAsync(store, recovery.Read(), restartRecovery: recovery));
        Assert.Equal(SetupDeferredPresentation.OfferRetry, presentation);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(handle, recovery.Read());
        Assert.Equal(SetupDeferredPresentation.None,
            SetupDeferredPresentationPolicy.Project(SetupHandoffAcquisitionStatus.RetryRequired, false));
    }

    [Fact]
    public async Task DuplicateBusyActivationCannotCloseTheAdmittedPresentation()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var verify = new TaskCompletionSource<SetupVerifiedNativeRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bound = false;
        var deferred = SetupDeferredPresentation.None;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway, (_, _) => verify.Task,
            (_, _) => throw new Exception("No destination"), _ => throw new Exception("No failure expected"),
            showPreparing: (_, _) => { bound = true; return Task.CompletedTask; },
            showReady: (_, _) => Task.CompletedTask,
            acquisitionDeferred: status => deferred = SetupDeferredPresentationPolicy.Project(status, !bound));
        var first = launcher.OpenAsync(store, handle);
        Assert.True(bound);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupDeferredPresentation.None, deferred);
        Assert.False(first.IsCompleted);
        verify.SetResult(new(Proof, Proof.SessionKey!));
        Assert.True(await first);
        Assert.Equal(SetupDeferredPresentation.CloseUnboundShell,
            SetupDeferredPresentationPolicy.Project(SetupHandoffAcquisitionStatus.Busy, true));
    }

    [Fact]
    public async Task MissedRevisionEventBeforeSubscriptionCannotMountOrConsumePreparation()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var transport = new ReadinessTransport(Proof);
        var observation = new ReadinessSubscription();
        var mounted = false;
        var consumed = false;
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            async (_, ct) =>
            {
                var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, ct);
                return new(binding.Proof, binding.Proof.SessionKey!, binding);
            }, (_, _) => throw new Exception("No destination"), failures.Add,
            showPreparing: (_, _) => Task.CompletedTask,
            showReady: async (route, ct) =>
            {
                using var subscription = await route.ReadyBinding!.ObserveAndCheckAsync(_ =>
                {
                    transport.Hash = "changed-without-event";
                    return observation;
                }, ct);
                mounted = true;
            },
            readyConsumed: () => consumed = true,
            confirmStableAuthority: (proof, ct) => SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, proof, ct));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.False(mounted);
        Assert.False(consumed);
        Assert.Equal(1, observation.Disposals);
        Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, store.Acquire(handle).Status);
    }

    private sealed class ReadinessSubscription : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("disconnect")]
    [InlineData("revision")]
    public async Task ConfirmedStableFreshnessLossRetainsOriginalLeaseForExplicitReverification(string stale)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        recovery.Save(handle);
        var transport = new ReadinessTransport(Proof);
        var first = true;
        var confirmations = 0;
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            async (_, ct) =>
            {
                var binding = await SetupNativeReadyBinding.VerifyAsync(transport, Proof, ct);
                if (first) clock.Advance(TimeSpan.FromMinutes(2));
                return new(binding.Proof, binding.Proof.SessionKey!, binding);
            }, (_, _) => throw new Exception("No destination was requested"), failures.Add, clock,
            showPreparing: (_, _) => Task.CompletedTask,
            showReady: async (route, ct) =>
            {
                if (first)
                {
                    first = false;
                    if (stale == "generation") transport.Generation++;
                    if (stale == "disconnect") transport.IsConnected = false;
                    if (stale == "revision") transport.Hash = "unrelated-revision";
                }
                await route.ReadyBinding!.RequireCurrentAsync(ct);
            },
            confirmStableAuthority: async (proof, ct) =>
            {
                confirmations++;
                transport.IsConnected = true; // Synthetic normal-owner reconnect, not setup recovery.
                await SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, proof, ct);
            });
        Assert.False(await launcher.OpenAsync(store, handle, restartRecovery: recovery));
        Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
        Assert.Equal(handle, recovery.Read());
        Assert.Equal(1, confirmations);
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, store.Acquire(handle).Status);
        var path = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(SetupNativeCompletionTiming.Execution,
            record.RootElement.GetProperty("ExecutionExpiresUtc").GetDateTimeOffset() -
            record.RootElement.GetProperty("ExecutionStartedUtc").GetDateTimeOffset());
        clock.Advance(TimeSpan.FromMinutes(5)); // Beyond admission, but still inside the original execution lease.
        Assert.True(await launcher.OpenAsync(store, handle, explicitRetry: true, restartRecovery: recovery));
        Assert.Null(recovery.Read());
        Assert.Equal(2, transport.Verifications);
        Assert.Equal(1, transport.Discoveries);
    }

    [Theory]
    [InlineData("identity", "Changed")]
    [InlineData("endpoint", "Changed")]
    [InlineData("agent", "Changed")]
    [InlineData("session", "Changed")]
    [InlineData("model", "Changed")]
    [InlineData("unknown", "Invalid")]
    [InlineData("expired", "Invalid")]
    public async Task LostFreshnessCannotRetainReceiptWhenStableAuthorityIsChangedOrUnconfirmed(
        string change, string expected)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var transport = new ReadinessTransport(Proof);
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromException<SetupVerifiedNativeRoute>(new SetupNativeReadinessExpiredException()),
            (_, _) => throw new Exception("Must not open"), failures.Add, clock,
            showPreparing: (_, _) => Task.CompletedTask,
            confirmStableAuthority: async (proof, ct) =>
            {
                transport.Route = change switch
                {
                    "identity" => transport.Route with { IdentityBinding = new string('C', 64) },
                    "endpoint" => transport.Route with { EndpointBinding = new string('C', 64) },
                    "agent" => transport.Route with { AgentId = "another", SessionKey = "agent:another:main" },
                    "session" => transport.Route with { SessionKey = "agent:primary:another" },
                    _ => transport.Route,
                };
                if (change == "model") transport.Model = "other/model";
                if (change == "unknown") throw new IOException("No current authority evidence");
                if (change == "expired") clock.Advance(SetupNativeCompletionTiming.Execution);
                await SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, proof, ct);
            });
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(expected, Assert.Single(failures).ToString());
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
        Assert.Equal(0, transport.Verifications);
    }

    private sealed class ReadinessTransport(GatewayAiSetupCompletion proof) : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; set; } = new(proof.GatewayId, proof.AgentId, "fixture",
            proof.EndpointBinding, proof.IdentityBinding, proof.SessionKey);
        public long Generation { get; set; } = proof.VerifiedGeneration;
        public bool IsConnected { get; set; } = true;
        public string Model { get; set; } = proof.ModelRef;
        public string Hash { get; set; } = "original-revision";
        public int Verifications { get; private set; }
        public int Discoveries { get; private set; }
        public IReadOnlyCollection<string> Methods => ["openclaw.setup.detect", "openclaw.setup.verify", "openclaw.setup.activate"];
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public Task<System.Text.Json.JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == "openclaw.setup.verify") Verifications++;
            if (method == "openclaw.setup.detect") Discoveries++;
            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement<object>(method switch
            {
                "config.get" => new { hash = Hash, valid = true },
                "openclaw.setup.verify" => new { ok = true, modelRef = Model, latencyMs = 1 },
                "openclaw.setup.detect" => new { candidates = Array.Empty<object>(), manualProviders = Array.Empty<object>(),
                    workspace = "fixture", setupComplete = true, configuredModel = Model },
                _ => throw new Exception("No setup mutations are permitted"),
            }));
        }
    }

    [Fact]
    public void ReadyObservationRequiresTheVerifiedModelEvenIfAnUnrelatedRuntimeWasCapturedAfterVerification()
    {
        var healthy = LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:9999"), DateTimeOffset.UnixEpoch) with
        {
            State = LocalAiRuntimeState.Healthy, Ownership = LocalAiOwnership.CompanionManaged,
            ModelId = "selected", ProcessId = 7, ProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
        };
        Assert.True(SetupReadyObservation.IsSameManagedRuntime("llamacpp/selected", healthy, healthy));
        var other = healthy with { ModelId = "other" };
        Assert.False(SetupReadyObservation.IsSameManagedRuntime("llamacpp/selected", other, other));
        Assert.False(SetupReadyObservation.IsSameManagedRuntime("llamacpp/selected", healthy, healthy with { ProcessId = 8 }));
        Assert.False(SetupReadyObservation.IsSameManagedRuntime("llamacpp/selected", healthy,
            healthy with { State = LocalAiRuntimeState.Stopped }));
    }

    [Fact]
    public async Task PreparationConsumesOnlyAfterFreshReadyMountAndNeverOpensAFabricatedDestination()
    {
        Assert.Null(Gateway.NativePackageFamilyName); // Existing/remote receipts use the same destination-free contract.
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mounting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => { calls.Add("verify"); return Task.FromResult(new SetupVerifiedNativeRoute(Proof with { VerifiedGeneration = 25 }, Proof.SessionKey!)); },
            (_, _) => throw new Exception("No destination was chosen"),
            _ => throw new Exception("Must succeed"),
            showPreparing: (_, _) => { calls.Add("progress"); return Task.CompletedTask; },
            showReady: async (route, _) =>
            {
                Assert.Equal(25, route.Verification.VerifiedGeneration);
                calls.Add("ready");
                mounting.SetResult();
                await mounted.Task;
            });
        var launch = launcher.OpenAsync(store, handle);
        await mounting.Task;
        Assert.Equal(SetupHandoffAcquisitionStatus.Busy, store.Acquire(handle).Status);
        mounted.SetResult();
        Assert.True(await launch);
        Assert.Equal(["progress", "verify", "ready"], calls);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Fact]
    public void PreparationAndDestinationExplicitlySupersedeOnePendingRecord()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var old = store.Issue(Choice);
        var preparation = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(old).Status);
        using (var lease = store.Acquire(preparation).Lease)
        {
            Assert.NotNull(lease);
            Assert.True(lease.IsPreparation);
            Assert.Null(lease.NativeTarget);
            Assert.Equal(Proof.SessionKey, lease.SessionKey);
            lease.RetainForExplicitRetry();
        }
        var replacement = store.Issue(Choice);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(preparation, explicitRetry: true).Status);
        using var destination = store.Acquire(replacement).Lease;
        Assert.NotNull(destination);
        Assert.False(destination.IsPreparation);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("role")]
    public void PreparationRequiresFullSessionAuthorityWithoutADestination(string invalid)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var proof = Proof with
        {
            IdentityBinding = invalid == "identity" ? null : Proof.IdentityBinding,
            VerifiedGeneration = invalid == "generation" ? 0 : Proof.VerifiedGeneration,
            ModelTarget = invalid == "role" ? "utility" : null,
        };
        Assert.Throws<SetupNativeOwnershipException>(() => store.IssuePreparation(new(proof,
            invalid == "session" ? "agent:primary:other" : Proof.SessionKey!)));
    }

    [Fact]
    public async Task PreparationFailureRetriesWithinOriginalLeaseWithoutFinalization()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var failures = new List<SetupNativeLaunchFailure>();
        var fail = true;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => fail ? Task.FromException<SetupVerifiedNativeRoute>(new IOException("Model unavailable")) :
                Task.FromResult(new SetupVerifiedNativeRoute(Proof, Proof.SessionKey!)),
            (_, _) => throw new Exception("No destination"),
            failures.Add, clock, showPreparing: (_, _) => Task.CompletedTask,
            showReady: (_, _) => Task.CompletedTask);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, store.Acquire(handle).Status);
        clock.Advance(TimeSpan.FromMinutes(6));
        fail = false;
        Assert.True(await launcher.OpenAsync(store, handle, explicitRetry: true));
        Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
    }

    [Fact]
    public void CrashedPreparationRemainsNonReplayableAndUnknownKindFailsClosed()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        store.Acquire(handle).Lease!.Dispose();
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
        handle = store.IssuePreparation(new(Proof, Proof.SessionKey!));
        var path = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("preparation-v1", "preparation-v99", StringComparison.Ordinal));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCallbackReturningSuccess_RetainsReceiptAndRecoveryForExplicitRetry(bool duringVerify)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        using var cancellation = new CancellationTokenSource();
        var failures = new List<SetupNativeLaunchFailure>();
        var opens = 0;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) =>
            {
                if (duringVerify) cancellation.Cancel();
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey));
            },
            (_, token) =>
            {
                opens++;
                cancellation.Cancel();
                Assert.True(token.IsCancellationRequested);
                return Task.CompletedTask;
            }, failures.Add);

        Assert.False(await launcher.OpenAsync(store, handle, ct: cancellation.Token, restartRecovery: recovery));
        Assert.Equal(duringVerify ? 0 : 1, opens);
        Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
        Assert.Equal(handle, recovery.Read());
        Assert.Null(store.Acquire(handle).Lease);

        var retry = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => Task.CompletedTask, failures.Add);
        Assert.True(await retry.OpenAsync(store, handle, explicitRetry: true, restartRecovery: recovery));
        Assert.Null(recovery.Read());
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Fact]
    public void ReadyChatBinding_RejectsCancellationAndOldSameSessionRebind()
    {
        var request = new SetupNativeNavigationRequest(Choice with
            { Target = new(SetupNativeDestination.Chat, Proof.SessionKey!) });
        var binding = new SetupNativeChatBinding();
        binding.Bind(request);
        binding.RequireCurrent(request, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => binding.RequireCurrent(request, cancelled.Token));

        var replacement = request with { };
        Assert.Equal(request, replacement);
        binding.Bind(replacement);
        Assert.Throws<SetupNativeOwnershipException>(() => binding.RequireCurrent(request, CancellationToken.None));
        binding.RequireCurrent(replacement, CancellationToken.None);
        binding.Invalidate();
        binding.RetainForDestination(replacement.WorkspaceDestination!);
        Assert.Throws<SetupNativeOwnershipException>(() => binding.RequireCurrent(replacement, CancellationToken.None));
        binding.Bind(replacement);
        binding.RequireCurrent(replacement, CancellationToken.None);
    }

    [Fact]
    public async Task AdmittedDelayedSessionCreation_InvalidatesReadinessBeforeDestinationChanges()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var choice = Choice with { Target = new(SetupNativeDestination.Chat, Proof.SessionKey!) };
        var handle = store.Issue(choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var binding = new SetupNativeChatBinding();
        var history = new WorkspaceNavigationHistory();
        var mounted = new TaskCompletionSource<SetupNativeNavigationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkReadiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, choice.Target.SessionKey)),
            async (completion, ct) =>
            {
                var request = new SetupNativeNavigationRequest(completion);
                binding.Bind(request);
                history.Navigate(request.WorkspaceDestination!);
                mounted.SetResult(request);
                await checkReadiness.Task;
                binding.RequireCurrent(request, ct);
            }, failures.Add);
        var launch = launcher.OpenAsync(store, handle, restartRecovery: recovery);
        var request = await mounted.Task;

        async Task CreateSessionAsync()
        {
            binding.Invalidate();
            var key = await created.Task;
            history.Navigate(new(WorkspacePageId.Home, key));
        }

        var creation = CreateSessionAsync();
        Assert.False(creation.IsCompleted);
        Assert.Equal(request.WorkspaceDestination, history.Current);
        Assert.Throws<SetupNativeOwnershipException>(() => binding.RequireCurrent(request, CancellationToken.None));
        checkReadiness.SetResult();
        Assert.False(await launch);
        Assert.Equal([SetupNativeLaunchFailure.Changed], failures);
        Assert.Null(recovery.Read());
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
        created.SetResult("agent:primary:new");
        await creation;
        Assert.Equal("agent:primary:new", history.Current.SessionKey);
    }

    [Theory]
    [InlineData("contract", false)]
    [InlineData("response", false)]
    [InlineData("json", false)]
    [InlineData("identity", false)]
    [InlineData("contract", true)]
    [InlineData("response", true)]
    [InlineData("identity", true)]
    public async Task ExpectedPostLeaseFailuresAreSettledAndReported(string kind, bool duringOpen)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        Exception error = kind switch
        {
            "identity" => new DeviceIdentityLoadException("synthetic", new IOException()),
            "response" => new InvalidDataException("Empty verification response"),
            "json" => new System.Text.Json.JsonException("Bad verification response"),
            _ => new NotSupportedException("Missing verify API"),
        };
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => duringOpen ? Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey))
                : Task.FromException<SetupVerifiedNativeRoute>(error),
            (_, _) => Task.FromException(error), failures.Add);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal([kind == "identity" ? SetupNativeLaunchFailure.Changed : SetupNativeLaunchFailure.Unavailable], failures);
        Assert.Null(store.Acquire(handle).Lease);
        using var retry = store.Acquire(handle, explicitRetry: true).Lease;
        if (kind == "identity") Assert.Null(retry);
        else { Assert.NotNull(retry); retry!.Consume(); }
    }

    [Fact]
    public async Task OversizedPendingRecord_ReportsInvalidWithoutOpeningOrAutomaticRetry()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        File.WriteAllText(Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json"), new string('x', 17000));
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => throw new InvalidOperationException("Must not verify"),
            (_, _) => throw new InvalidOperationException("Must not open"), failures.Add);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal([SetupNativeLaunchFailure.Invalid], failures);
    }

    private static GatewayRecord Gateway => new() { Id = "a", Url = "wss://gateway.example/control/" };
    private static GatewayAiSetupCompletion Proof => new(SetupCompletionIntent.CustodianOnboarding,
        Gateway.Id, GatewayDashboardBinding.Capture(Gateway), "provider/model", "primary", 4,
        IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main");
    private static SetupNativeCompletion Choice => new(Proof, new(SetupNativeDestination.Telegram, "agent:primary:main"));

    [Theory]
    [InlineData(SetupNativeDestination.Chat, 0, "chat")]
    [InlineData(SetupNativeDestination.WhatsApp, 1, "channels")]
    [InlineData(SetupNativeDestination.Telegram, 2, "channels")]
    [InlineData(SetupNativeDestination.Channels, 3, "channels")]
    [InlineData(SetupNativeDestination.Skills, 4, "skills")]
    public void NativeRecordIsVersionedAndCannotBecomeALegacyBrowserReceipt(SetupNativeDestination destination, int persistedValue, string page)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var choice = Choice with { Target = Choice.Target with { Destination = destination } };
        Assert.Equal(persistedValue, (int)destination);
        Assert.Equal(page, new SetupNativeNavigationRequest(choice).PageTag);
        var request = new SetupNativeNavigationRequest(choice);
        if (destination == SetupNativeDestination.Chat)
        {
            Assert.Equal(new WorkspaceDestination(WorkspacePageId.Home, choice.Target.SessionKey), request.WorkspaceDestination);
            request.RequireWorkspaceDestination(request.WorkspaceDestination!);
        }
        else
        {
            Assert.Null(request.WorkspaceDestination);
            Assert.Throws<SetupNativeOwnershipException>(() =>
                request.RequireWorkspaceDestination(new(WorkspacePageId.Home, choice.Target.SessionKey)));
        }
        var handle = store.Issue(choice);
        Assert.NotNull(SetupDashboardHandoff.ParseHandle(handle));
        Assert.StartsWith("ai-v3:", handle);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire("ai-v2:" + handle[6..]).Status);
        using var lease = store.Acquire(handle).Lease;
        Assert.Equal(choice.Target, lease!.NativeTarget);
        lease.Consume();
        Assert.Equal(SetupHandoffAcquisitionStatus.Busy, store.Acquire(handle).Status);
        lease.Dispose();
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeChatNavigation_RejectsDestinationOrSessionDrift(bool differentSession)
    {
        var request = new SetupNativeNavigationRequest(Choice with
            { Target = new(SetupNativeDestination.Chat, "agent:primary:main") });
        var history = new WorkspaceNavigationHistory();
        history.Navigate(request.WorkspaceDestination!);
        request.RequireWorkspaceDestination(history.Current);
        history.Navigate(differentSession
            ? new(WorkspacePageId.Home, "agent:primary:other")
            : new(WorkspacePageId.Notifications));
        Assert.Throws<SetupNativeOwnershipException>(() => request.RequireWorkspaceDestination(history.Current));
        Assert.True(history.GoBack());
        request.RequireWorkspaceDestination(history.Current);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("agent")]
    [InlineData("endpoint")]
    [InlineData("disconnected")]
    public void SkillsBindingRejectsDrift(string changed)
    {
        var request = new SetupNativeNavigationRequest(Choice with
            { Target = new(SetupNativeDestination.Skills, "agent:primary:main") });
        var gateway = Gateway with
        {
            Id = changed == "gateway" ? "other" : Gateway.Id,
            Url = changed == "endpoint" ? "wss://other.example/" : Gateway.Url,
        };
        Assert.ThrowsAny<InvalidOperationException>(() => request.RequireCurrent(gateway, "a",
            changed == "agent" ? "agent:other:main" : "agent:primary:main", changed != "disconnected"));
    }

    [Fact]
    public async Task NativeOpenVerifiesBeforeNavigationAndKeepsFailedLaunchForExplicitRetryOnly()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var calls = new List<string>();
        var attempts = 0;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => { calls.Add("verify"); return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)); },
            (choice, _) =>
            {
                Assert.Equal(Choice, choice);
                calls.Add("open");
                return ++attempts == 1 ? Task.FromException(new IOException("Synthetic failure")) : Task.CompletedTask;
            }, _ => calls.Add("failure"));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(1, attempts);
        Assert.True(await launcher.OpenAsync(store, handle, explicitRetry: true));
        Assert.Equal(["verify", "open", "failure", "verify", "open"], calls);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("agent")]
    [InlineData("session")]
    [InlineData("model")]
    [InlineData("device")]
    [InlineData("same-agent-session")]
    public async Task NativeDriftCannotOpenOrRemainRetryable(string drift)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var current = Proof with { AgentId = drift == "agent" ? "other" : Proof.AgentId,
            ModelRef = drift == "model" ? "other/model" : Proof.ModelRef,
            IdentityBinding = drift == "device" ? new string('C', 64) : Proof.IdentityBinding,
            SessionKey = drift == "same-agent-session" ? "agent:primary:alternate" : Proof.SessionKey };
        var launcher = new SetupNativeHandoffLauncher(() => drift == "gateway" ? new() { Id = "b", Url = Gateway.Url } : Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(current,
                drift == "session" ? "agent:primary:other" : current.SessionKey!)),
            (_, _) => throw new InvalidOperationException("Must not open"),
            failure => Assert.Equal(SetupNativeLaunchFailure.Changed, failure));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public async Task ConnectionTimeoutRetainsExplicitRetryInsteadOfLeavingAnInflightRecord()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var calls = 0;
        var opened = 0;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => ++calls == 1
                ? Task.FromException<SetupVerifiedNativeRoute>(new TimeoutException("Synthetic connection timeout"))
                : Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => { opened++; return Task.CompletedTask; },
            failure => Assert.Equal(SetupNativeLaunchFailure.Unavailable, failure));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(0, opened);
        Assert.True(await launcher.OpenAsync(store, handle, explicitRetry: true));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task ConcurrentNativeActivation_DoesNotQueueAnotherLaunch()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var notifications = new AppNotificationService();
        var navigations = new List<string>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => { launches++; return presented.Task; },
            failure =>
            {
                notifications.Show(new() { Id = SetupNativeHandoffLauncher.FailureNotificationId, Message = failure.ToString() });
                navigations.Add("connection");
            });
        var first = launcher.OpenAsync(store, handle, restartRecovery: recovery);
        Assert.False(await launcher.OpenAsync(new(temp.Path), handle, restartRecovery: recovery));
        Assert.Empty(notifications.Snapshot.ActiveNotifications);
        Assert.Empty(navigations);
        Assert.Equal(handle, recovery.Read());
        Assert.False(first.IsCompleted);
        presented.SetResult();
        Assert.True(await first);
        Assert.Equal(1, launches);
        Assert.Null(recovery.Read());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreAcquisitionIoFailureRetainsReadyAndRestartHandleForWorkingExplicitRetry(bool lockPathUnavailable)
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var lockPath = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.lock");
        var pendingPath = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        FileStream? blocked = null;
        if (lockPathUnavailable)
        {
            File.Delete(lockPath);
            Directory.CreateDirectory(lockPath);
        }
        else
            blocked = new FileStream(pendingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var opened = 0;
        var failures = new List<SetupNativeLaunchFailure>();
        Func<Task<bool>>? retryAction = null;
        SetupNativeHandoffLauncher launcher = null!;
        launcher = new(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => { opened++; return Task.CompletedTask; },
            failure =>
            {
                failures.Add(failure);
                if (failure == SetupNativeLaunchFailure.Unavailable)
                    retryAction = () => launcher.OpenAsync(store, handle, explicitRetry: true, restartRecovery: recovery);
            });
        try
        {
            Assert.False(await launcher.OpenAsync(store, handle, restartRecovery: recovery));
            Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
            Assert.Equal(0, opened);
            Assert.Equal(handle, recovery.Read());
        }
        finally
        {
            blocked?.Dispose();
            if (lockPathUnavailable) Directory.Delete(lockPath);
        }
        using (var pending = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pendingPath)))
            Assert.Equal("ready", pending.RootElement.GetProperty("State").GetString());
        Assert.NotNull(retryAction);
        Assert.True(await retryAction());
        Assert.Equal(1, opened);
        Assert.Null(recovery.Read());
        Assert.False(File.Exists(pendingPath));
    }

    [Fact]
    public async Task PostAdmissionFailureRetainsRetryAndRestartUntilSuccessfulPresentation()
    {
        using var temp = new TempDirectory();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(Choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var attempts = 0;
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => ++attempts == 1 ? Task.FromException(new IOException("Synthetic page failure")) : Task.CompletedTask,
            failures.Add);
        Assert.False(await launcher.OpenAsync(store, handle, restartRecovery: recovery));
        Assert.Equal(handle, recovery.Read());
        Assert.False(await launcher.OpenAsync(store, handle, restartRecovery: recovery));
        Assert.Equal(1, attempts);
        Assert.Equal([SetupNativeLaunchFailure.Unavailable], failures);
        Assert.Equal(handle, recovery.Read());
        Assert.True(await launcher.OpenAsync(store, handle, explicitRetry: true, restartRecovery: recovery));
        Assert.Equal(2, attempts);
        Assert.Null(recovery.Read());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("forged")]
    [InlineData("inflight")]
    public async Task InvalidAcquisitionNeverVerifiesOrNavigatesAndCannotEraseADifferentRestart(string state)
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var recovery = new NativeRestartRecoveryStore(temp.Path);
        recovery.Save(handle);
        var pending = Path.Combine(temp.Path, "setup-dashboard-handoff", "pending.json");
        switch (state)
        {
            case "missing": File.Delete(pending); break;
            case "malformed": File.WriteAllText(pending, "{"); break;
            case "expired": clock.Now += TimeSpan.FromMinutes(6); break;
            case "inflight": store.Acquire(handle).Lease!.Dispose(); break;
        }
        var supplied = state == "forged" ? SetupDashboardHandoff.NativePrefix + new string('0', 64) : handle;
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => throw new InvalidOperationException("Must not verify"),
            (_, _) => throw new InvalidOperationException("Must not navigate"), failures.Add);
        Assert.False(await launcher.OpenAsync(store, supplied, explicitRetry: true, restartRecovery: recovery));
        Assert.Equal([SetupNativeLaunchFailure.Invalid], failures);
        Assert.Equal(state == "forged" ? handle : null, recovery.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryOrEndpointDriftDuringVerification_CannotLaunchOrRetry(bool expiry)
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var gateway = Gateway;
        var launcher = new SetupNativeHandoffLauncher(() => gateway,
            (_, _) =>
            {
                if (expiry) clock.Now += SetupNativeCompletionTiming.Execution;
                else gateway = gateway with { Url = "wss://other.example/" };
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey));
            },
            (_, _) => throw new InvalidOperationException("Must not open"),
            failure => Assert.Equal(expiry ? SetupNativeLaunchFailure.Invalid : SetupNativeLaunchFailure.Changed, failure));
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public void FocusPolicyDoesNotTreatFallbackOrConfiguredOtherChannelsAsAuthoritativeAbsence()
    {
        Assert.Equal(SetupChannelAvailability.Unconfirmed,
            SetupChannelFocusPolicy.GetAvailability(new(), "whatsapp"));
        Assert.Equal(SetupChannelAvailability.Unconfirmed,
            SetupChannelFocusPolicy.GetAvailability(new() { Channels = new Dictionary<string, System.Text.Json.JsonElement>
                { ["telegram"] = default } }, "whatsapp"));
        Assert.Equal(SetupChannelAvailability.NotOffered,
            SetupChannelFocusPolicy.GetAvailability(new() { ChannelOrder = ["telegram"] }, "whatsapp"));
        Assert.Equal(SetupChannelAvailability.Offered,
            SetupChannelFocusPolicy.GetAvailability(new() { ChannelOrder = ["whatsapp"] }, "whatsapp"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public TimeSpan? Deadline { get; private set; }
        public Action? Expire { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Deadline = dueTime;
            Expire = () => callback(state);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task ColdStartupAndRealInferenceHaveTimeToFinishBeforeOpening()
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var opened = false;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, ct) =>
            {
                Assert.Equal(SetupNativeCompletionTiming.Execution, clock.Deadline);
                clock.Now += TimeSpan.FromSeconds(60 + 120);
                Assert.False(ct.IsCancellationRequested);
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey));
            },
            (_, _) => { opened = true; return Task.CompletedTask; },
            _ => Assert.Fail("Cold startup should not produce a premature dialog."), clock);
        Assert.True(await launcher.OpenAsync(store, handle));
        Assert.True(opened);
    }

    [Fact]
    public async Task DeadlineStopsVerificationAndAutomaticActivationDoesNotRepeatTheDialog()
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var failures = new List<SetupNativeLaunchFailure>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Verification must be cancelled.");
            },
            (_, _) => throw new InvalidOperationException("Must not open before verification."),
            failures.Add, clock);
        var attempt = launcher.OpenAsync(store, handle);
        await entered.Task;
        clock.Expire!();
        Assert.False(await attempt);
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, store.Acquire(handle).Status);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Single(failures);
        using var retry = store.Acquire(handle, explicitRetry: true).Lease;
        Assert.NotNull(retry);
        retry.Consume();
    }

    [Fact]
    public async Task UnacquiredReceiptStillExpiresAfterFiveMinutes()
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        clock.Now += TimeSpan.FromMinutes(5);
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => throw new InvalidOperationException("An expired receipt must not verify."),
            (_, _) => throw new InvalidOperationException("Must not open."),
            failure => Assert.Equal(SetupNativeLaunchFailure.Invalid, failure), clock);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPublishedPhasesOpenExactlyOnceWithinTheExecutionLease(bool recoverModel)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var opens = 0;
        var verifications = 0;
        var failures = new List<SetupNativeLaunchFailure>();
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            async (_, ct) =>
            {
                verifications++;
                var phases = recoverModel ? new[] { 200, 200, 200, 145 } : new[] { 175, 100 };
                foreach (var seconds in phases)
                {
                    clock.Advance(TimeSpan.FromSeconds(seconds));
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                }
                return new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey);
            },
            (_, ct) =>
            {
                clock.Advance(TimeSpan.FromSeconds(25));
                ct.ThrowIfCancellationRequested();
                opens++;
                return Task.CompletedTask;
            }, failures.Add, clock);
        Assert.True(await launcher.OpenAsync(store, handle));
        Assert.Empty(failures);
        Assert.False(await launcher.OpenAsync(store, handle, explicitRetry: true));
        Assert.Equal(1, verifications);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task ExpiredExecutionCannotNavigateAfterAnUncooperativeVerificationReturns()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var result = new TaskCompletionSource<SetupVerifiedNativeRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken verificationToken = default;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, ct) => { verificationToken = ct; return result.Task; },
            (_, _) => throw new InvalidOperationException("Late verification cannot navigate."),
            failure => Assert.Equal(SetupNativeLaunchFailure.Invalid, failure), clock);
        var opening = launcher.OpenAsync(store, handle);
        clock.Advance(SetupNativeCompletionTiming.Execution);
        Assert.True(verificationToken.IsCancellationRequested);
        result.SetResult(new(Proof, Choice.Target.SessionKey));
        Assert.False(await opening);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public async Task NavigationDeadlineCancelsLatePresentationWithoutConsumingOrRenewingReceipt()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken navigationToken = default;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, ct) => { navigationToken = ct; return opened.Task; },
            failure => Assert.Equal(SetupNativeLaunchFailure.Unavailable, failure), clock);
        var opening = launcher.OpenAsync(store, handle);
        clock.Advance(SetupNativeCompletionTiming.Navigation);
        Assert.True(navigationToken.IsCancellationRequested);
        opened.SetResult();
        Assert.False(await opening);
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, store.Acquire(handle).Status);
        using var retry = store.Acquire(handle, explicitRetry: true).Lease!;
        Assert.Equal(SetupNativeCompletionTiming.Execution - SetupNativeCompletionTiming.Navigation, retry.RemainingLifetime);
        retry.Consume();
    }

    [Fact]
    public async Task BackwardClockDuringVerificationInvalidatesReceiptWithoutOfferingRetry()
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) =>
            {
                clock.Now -= TimeSpan.FromSeconds(1);
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey));
            },
            (_, _) => throw new InvalidOperationException("Invalid lease must not navigate."),
            failure => Assert.Equal(SetupNativeLaunchFailure.Invalid, failure), clock);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }
}
