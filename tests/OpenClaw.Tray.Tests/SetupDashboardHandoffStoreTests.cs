using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SetupDashboardHandoffStoreTests
{
    private static GatewayRecord Gateway => new() { Id = "gateway-a", Url = "wss://gateway.example/control/" };
    private static GatewayAiSetupCompletion Receipt => new(SetupCompletionIntent.CustodianOnboarding,
        Gateway.Id, GatewayDashboardBinding.Capture(Gateway), "provider/model", "primary", 7,
        IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main");
    private static SetupNativeCompletion Choice => new(Receipt, new(SetupNativeDestination.Chat, "agent:primary:main"));

    [Fact]
    public void ReceiptWithoutOriginalAuthorityCannotBeIssuedOrAdmitted()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        Assert.Throws<SetupNativeOwnershipException>(() =>
            store.Issue(Choice with { Verification = Receipt with { IdentityBinding = null } }));
        var handle = store.Issue(Choice);
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Completion"]!.AsObject().Remove("IdentityBinding");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Fact]
    public void ForgedShapeAndUnknownHandle_AreNotVerificationAuthority()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var forged = "ai-v1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Receipt));
        Assert.Null(SetupDashboardHandoff.ParseHandle(forged));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(forged).Status);
        var real = store.Issue(Choice);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(SetupDashboardHandoff.NativePrefix + new string('0', 64)).Status);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire("../pending.json").Status);
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var publicJson = "openclaw://setup-dashboard?receipt=" + Uri.EscapeDataString(forged);
        var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(new(publicJson, [], null, false)));
        Assert.Null(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        using var valid = store.Acquire(real).Lease;
        Assert.Equal(Receipt, valid!.Completion);
        valid.Consume();
    }

    [Fact]
    public void LocalRecord_ContainsNoHandleOrCredentials_AndNewRunSupersedesOldRun()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var old = store.Issue(Choice);
        var current = store.Issue(Choice with { Verification = Receipt with { Intent = SetupCompletionIntent.Dashboard } });
        Assert.NotEqual(old, current);
        var text = File.ReadAllText(Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json"));
        Assert.DoesNotContain(current, text);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(old).Status);
        using var lease = store.Acquire(current).Lease;
        Assert.Equal(SetupCompletionIntent.Dashboard, lease!.Completion.Intent);
        lease.Consume();
    }

    [Fact]
    public void ExpiryAndBackwardClock_RejectPreviouslyVerifiedReceipt()
    {
        using var directory = new TempDirectory();
        var time = new Clock();
        var store = new SetupDashboardHandoffStore(directory.Path, time);
        var handle = store.Issue(Choice);
        time.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
        handle = store.Issue(Choice);
        time.Now -= TimeSpan.FromSeconds(1);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
    }

    [Fact]
    public void ExclusiveLease_RejectsInflightDuplicateAndReplayAfterConsume()
    {
        using var directory = new TempDirectory();
        var firstProcess = new SetupDashboardHandoffStore(directory.Path);
        var secondProcess = new SetupDashboardHandoffStore(directory.Path);
        var handle = firstProcess.Issue(Choice);
        using (var first = firstProcess.Acquire(handle).Lease)
        {
            Assert.NotNull(first);
            Assert.Equal(SetupHandoffAcquisitionStatus.Busy, secondProcess.Acquire(handle).Status);
            Assert.Equal(SetupHandoffAcquisitionStatus.Busy, secondProcess.Acquire(handle, explicitRetry: true).Status);
            first!.Consume();
        }
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, secondProcess.Acquire(handle).Status);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, secondProcess.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public void InterruptedLease_IsNotReplayableAfterProcessReleasesFile()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var handle = store.Issue(Choice);
        store.Acquire(handle).Lease!.Dispose();
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public void PublicRoute_CannotRequestExplicitRetryOrSupplyAFilePath()
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(new(
            "openclaw://setup-dashboard?handle=..%2Fpending.json&retry=true", [], null, false)));
        Assert.Null(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        Assert.Null(SetupDashboardHandoff.ParseHandle("ai-v3:" + new string('A', 64)));
    }

    [Fact]
    public void CurrentHandshakeFacts_DoNotInventProviderIdentityFromBareSessionDisplayId()
    {
        var known = SetupDashboardLiveFacts.Project(Gateway.Id, "agent:primary:main", "model", "other");
        Assert.False(known.Matches(Receipt));
        var incomplete = SetupDashboardLiveFacts.Project(Gateway.Id, "agent:primary:main", "model");
        Assert.Null(incomplete.ModelRef);
        Assert.True(incomplete.Matches(Receipt));
        Assert.False((incomplete with { AgentId = "other" }).Matches(Receipt));
    }

    [Theory]
    [InlineData("anthropic/claude-sonnet-4", "openrouter", "openrouter/anthropic/claude-sonnet-4")]
    [InlineData("openrouter/anthropic/claude-sonnet-4", "openrouter", "openrouter/anthropic/claude-sonnet-4")]
    [InlineData("model", "provider", "provider/model")]
    [InlineData("anthropic/claude-sonnet-4", null, null)]
    [InlineData("provider/model", "", null)]
    public void CurrentHandshakeFacts_QualifyModelsUsingTheAuthenticatedProvider(
        string model, string? provider, string? expected)
    {
        var facts = SetupDashboardLiveFacts.Project(Gateway.Id, "agent:primary:main", model, provider);
        Assert.Equal(expected, facts.ModelRef);
        if (expected is not null)
            Assert.True(facts.Matches(Receipt with { ModelRef = expected }));
    }

    [Fact]
    public void PublicJsonCannotOverrideIntentModelOrGenerationOfALocalPendingRecord()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var handle = store.Issue(Choice);
        var spoof = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            Receipt with { Intent = SetupCompletionIntent.Dashboard, ModelRef = "other/model", VerifiedGeneration = 999 }));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(new(
            "openclaw://setup-dashboard?handle=" + handle + "&receipt=" + Uri.EscapeDataString(spoof),
            [], null, false)));
        using var lease = store.Acquire(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle).Lease;
        Assert.Equal(Receipt, lease!.Completion);
        lease.Consume();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void FirstAcquisitionStartsOnePersistedExecutionWindow_RetryCannotRenewIt()
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Choice);
        clock.Advance(TimeSpan.FromMinutes(4));
        using (var lease = store.Acquire(handle).Lease)
        {
            Assert.NotNull(lease);
            Assert.Equal(SetupNativeCompletionTiming.Execution, lease.RemainingLifetime);
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.False(lease.IsExpired);
            lease.RetainForExplicitRetry();
        }
        var restarted = new SetupDashboardHandoffStore(directory.Path, clock);
        Assert.Equal(SetupHandoffAcquisitionStatus.RetryRequired, restarted.Acquire(handle).Status);
        using (var retry = restarted.Acquire(handle, explicitRetry: true).Lease)
        {
            Assert.NotNull(retry);
            Assert.Equal(SetupNativeCompletionTiming.Execution - TimeSpan.FromMinutes(6), retry.RemainingLifetime);
            clock.Advance(TimeSpan.FromMinutes(1));
            retry.RetainForExplicitRetry();
        }
        using (var retry = store.Acquire(handle, explicitRetry: true).Lease)
        {
            Assert.NotNull(retry);
            Assert.Equal(SetupNativeCompletionTiming.Execution - TimeSpan.FromMinutes(7), retry.RemainingLifetime);
            clock.Advance(retry.RemainingLifetime);
            Assert.True(retry.IsExpired);
            retry.RetainForExplicitRetry();
        }
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, restarted.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public async Task ConcurrentFirstAcquisitionCannotGrantTwoExecutionWindows()
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var firstStore = new SetupDashboardHandoffStore(directory.Path, clock);
        var secondStore = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = firstStore.Issue(Choice);
        var attempts = await Task.WhenAll(
            Task.Run(() => firstStore.Acquire(handle)),
            Task.Run(() => secondStore.Acquire(handle)));
        using var lease = Assert.Single(attempts, result => result.Lease is not null).Lease!;
        Assert.Single(attempts, result => result.Status == SetupHandoffAcquisitionStatus.Busy);
        Assert.Equal(SetupNativeCompletionTiming.Execution, lease.RemainingLifetime);
        lease.Consume();
    }

    [Theory]
    [InlineData("retry", null)]
    [InlineData("inflight", null)]
    [InlineData("retry", -1)]
    [InlineData("retry", 300)]
    [InlineData("ready", 0)]
    public void InvalidOrLegacyExecutionStateCannotGrantFreshTime(string state, int? startedSeconds)
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Choice);
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");
        var record = JsonSerializer.Deserialize<SetupDashboardHandoffStore.PendingRecord>(File.ReadAllText(path))!;
        record = record with
        {
            State = state,
            ExecutionStartedUtc = startedSeconds is { } seconds ? record.IssuedUtc.AddSeconds(seconds) : null,
            ExecutionExpiresUtc = startedSeconds is { } value
                ? record.IssuedUtc.AddSeconds(value) + SetupNativeCompletionTiming.Execution : null
        };
        File.WriteAllText(path, JsonSerializer.Serialize(record));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public void PersistedRetryExpiresEvenAfterProcessRestart_AndClockCannotMoveBeforeAcquisition()
    {
        using var directory = new TempDirectory();
        var clock = new Clock();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Choice);
        clock.Now += TimeSpan.FromMinutes(1);
        var acquired = clock.Now;
        using (var lease = store.Acquire(handle).Lease!)
            lease.RetainForExplicitRetry();
        clock.Now -= TimeSpan.FromSeconds(1);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid,
            new SetupDashboardHandoffStore(directory.Path, clock).Acquire(handle, explicitRetry: true).Status);
        clock.Now = acquired + SetupNativeCompletionTiming.Execution;
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid,
            new SetupDashboardHandoffStore(directory.Path, clock).Acquire(handle, explicitRetry: true).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(-1)]
    public void MissingOrAlteredExecutionDeadlineCannotAcquireNewTime(int? adjustment)
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Choice);
        using (var lease = store.Acquire(handle).Lease!)
            lease.RetainForExplicitRetry();
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");
        var record = JsonSerializer.Deserialize<SetupDashboardHandoffStore.PendingRecord>(File.ReadAllText(path))!;
        record = record with
        {
            ExecutionExpiresUtc = adjustment is { } seconds ? record.ExecutionExpiresUtc!.Value.AddSeconds(seconds) : null
        };
        File.WriteAllText(path, JsonSerializer.Serialize(record));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle, explicitRetry: true).Status);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(300, false)]
    public void LegacyUnusedRecordKeepsItsOriginalAdmissionDeadline(int elapsed, bool acquired)
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Choice);
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("ExecutionStartedUtc");
        json.Remove("ExecutionExpiresUtc");
        File.WriteAllText(path, json.ToJsonString());
        clock.Advance(TimeSpan.FromSeconds(elapsed));
        var result = store.Acquire(handle);
        using var lease = result.Lease;
        Assert.Equal(acquired ? SetupHandoffAcquisitionStatus.Acquired : SetupHandoffAcquisitionStatus.Invalid, result.Status);
        lease?.Consume();
    }
}
