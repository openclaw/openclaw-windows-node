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
        Assert.Null(store.Acquire(handle));
        using var retry = store.Acquire(handle, explicitRetry: true);
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
        var handle = store.Issue(choice);
        Assert.NotNull(SetupDashboardHandoff.ParseHandle(handle));
        Assert.StartsWith("ai-v3:", handle);
        Assert.Null(store.Acquire("ai-v2:" + handle[6..]));
        using var lease = store.Acquire(handle);
        Assert.Equal(choice.Target, lease!.NativeTarget);
        lease.Consume();
        Assert.Null(store.Acquire(handle));
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
        Assert.Equal(["verify", "open", "failure", "failure", "verify", "open"], calls);
        Assert.Null(store.Acquire(handle, explicitRetry: true));
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
        Assert.Null(store.Acquire(handle, explicitRetry: true));
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
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(Proof, Choice.Target.SessionKey)),
            (_, _) => { launches++; return presented.Task; }, _ => { });
        var first = launcher.OpenAsync(store, handle);
        Assert.False(await launcher.OpenAsync(new(temp.Path), handle));
        presented.SetResult();
        Assert.True(await first);
        Assert.Equal(1, launches);
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
        Assert.Null(store.Acquire(handle, explicitRetry: true));
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
    }
}
