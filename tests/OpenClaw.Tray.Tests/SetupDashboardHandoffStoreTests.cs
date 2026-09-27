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
        Assert.Null(store.Acquire(handle));
    }

    [Fact]
    public void ForgedShapeAndUnknownHandle_AreNotVerificationAuthority()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var forged = "ai-v1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Receipt));
        Assert.Null(SetupDashboardHandoff.ParseHandle(forged));
        Assert.Null(store.Acquire(forged));
        var real = store.Issue(Choice);
        Assert.Null(store.Acquire(SetupDashboardHandoff.NativePrefix + new string('0', 64)));
        Assert.Null(store.Acquire("../pending.json"));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var publicJson = "openclaw://setup-dashboard?receipt=" + Uri.EscapeDataString(forged);
        var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(new(publicJson, [], null, false)));
        Assert.Null(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        using var valid = store.Acquire(real);
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
        Assert.Null(store.Acquire(old));
        using var lease = store.Acquire(current);
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
        Assert.Null(store.Acquire(handle));
        handle = store.Issue(Choice);
        time.Now -= TimeSpan.FromSeconds(1);
        Assert.Null(store.Acquire(handle));
    }

    [Fact]
    public void ExclusiveLease_RejectsInflightDuplicateAndReplayAfterConsume()
    {
        using var directory = new TempDirectory();
        var firstProcess = new SetupDashboardHandoffStore(directory.Path);
        var secondProcess = new SetupDashboardHandoffStore(directory.Path);
        var handle = firstProcess.Issue(Choice);
        using (var first = firstProcess.Acquire(handle))
        {
            Assert.NotNull(first);
            Assert.Null(secondProcess.Acquire(handle));
            Assert.Null(secondProcess.Acquire(handle, explicitRetry: true));
            first!.Consume();
        }
        Assert.Null(secondProcess.Acquire(handle));
        Assert.Null(secondProcess.Acquire(handle, explicitRetry: true));
    }

    [Fact]
    public void InterruptedLease_IsNotReplayableAfterProcessReleasesFile()
    {
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var handle = store.Issue(Choice);
        store.Acquire(handle)!.Dispose();
        Assert.Null(store.Acquire(handle));
        Assert.Null(store.Acquire(handle, explicitRetry: true));
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
        using var lease = store.Acquire(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        Assert.Equal(Receipt, lease!.Completion);
        lease.Consume();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
