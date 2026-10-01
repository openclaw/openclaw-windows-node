using OpenClaw.Connection;
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
                if (expiry) clock.Now += TimeSpan.FromMinutes(6);
                else gateway = gateway with { Url = "wss://other.example/" };
                return Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey));
            },
            (_, _) => throw new InvalidOperationException("Must not open"),
            failure => Assert.Equal(SetupNativeLaunchFailure.Changed, failure));
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
                Assert.Equal(TimeSpan.FromMinutes(4.5), clock.Deadline);
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
    public async Task DelayedAcquisitionCannotExtendTheOriginalReceiptLifetime()
    {
        using var temp = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(temp.Path, clock);
        var handle = store.Issue(Choice);
        clock.Now += TimeSpan.FromMinutes(4);
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, ct) =>
            {
                Assert.Equal(TimeSpan.FromMinutes(1), clock.Deadline);
                clock.Now += TimeSpan.FromMinutes(1);
                clock.Expire!();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("An expired receipt must not verify.");
            }, (_, _) => throw new InvalidOperationException("Must not open."),
            failure => Assert.Equal(SetupNativeLaunchFailure.Unavailable, failure), clock);
        Assert.False(await launcher.OpenAsync(store, handle));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }
}
