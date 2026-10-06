using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClawTray.Services;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class SetupDashboardHandoffTests
{
    private static GatewayRecord Gateway => new()
    {
        Id = "gateway-a", Url = "wss://gateway.example/control/",
    };

    private static GatewayAiSetupCompletion Receipt(SetupCompletionIntent intent = SetupCompletionIntent.CustodianOnboarding) =>
        new(intent, Gateway.Id, GatewayDashboardBinding.Capture(Gateway), "provider/model", "agent-a", 7,
            IdentityBinding: new string('B', 64), SessionKey: "agent:agent-a:main");

    private static SetupNativeCompletion Native(GatewayAiSetupCompletion receipt) =>
        new(receipt, new(SetupNativeDestination.Chat, "agent:agent-a:main"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStartup_DoesNotWaitForUpdatePromptOrExitForInstaller(bool acceptInstaller)
    {
        using var directory = new TempDirectory();
        var clock = new StartupClock();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Native(Receipt()));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var input = new LaunchActivationInput(null, [], handle, false);
        var heldPrompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCalls = 0;
        var startup = router.CheckOrdinaryStartupUpdateAsync(input, () =>
        {
            updateCalls++;
            clock.Now += TimeSpan.FromMinutes(6);
            return acceptInstaller ? Task.FromResult(false) : heldPrompt.Task;
        });
        Assert.True(await startup.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, updateCalls);
        using var lease = store.Acquire(handle).Lease;
        Assert.NotNull(lease);
        Assert.False(lease!.IsExpired);
        Assert.Equal(handle, Assert.IsType<ActivationRoute.CompleteAiSetup>(
            Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(input)).Route).Handle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("chat")]
    [InlineData("ai-v3:invalid")]
    public async Task OrdinaryStartup_StillRunsUpdateAndHonorsInstallerExit(string? target)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var updates = 0;
        Assert.False(await router.CheckOrdinaryStartupUpdateAsync(new(null, [], target, false),
            () => { updates++; return Task.FromResult(false); }));
        Assert.Equal(1, updates);
    }

    [Theory]
    [InlineData(SetupCompletionIntent.Dashboard)]
    [InlineData(SetupCompletionIntent.CustodianOnboarding)]
    public void RestartAndForwardedActivation_PreserveTypedReceipt(SetupCompletionIntent intent)
    {
        var receipt = Receipt(intent);
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var encoded = store.Issue(Native(receipt));
        Assert.Equal(encoded, SetupDashboardHandoff.ParseHandle(encoded));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        foreach (var input in new[]
        {
            new LaunchActivationInput(null, [], encoded, false),
            new LaunchActivationInput(
                "openclaw://setup-dashboard?handle=" + Uri.EscapeDataString(encoded), [], null, false),
        })
        {
            var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(input));
            Assert.Equal(encoded, Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        }
        using var lease = store.Acquire(encoded).Lease;
        Assert.Equal(receipt, lease!.Completion);
        lease.Consume();
    }

    [Theory]
    [InlineData("chat", "chat")]
    [InlineData("settings", "settings")]
    [InlineData("connection", "connection")]
    public void OldRestartArguments_KeepTheirRoutes(string value, string page)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(
            router.PlanLaunch(new(null, [], value, false)));
        Assert.Equal(page, Assert.IsType<ActivationRoute.OpenHub>(plan.Route).Page);
    }

    [Theory]
    [InlineData("ai-v1:invalid")]
    [InlineData("ai-v2:0000000000000000000000000000000000000000000000000000000000000000")]
    public void RetiredOrInvalidHandoff_IsVisibleFailureRoute_NotGlobalDashboardFallback(string handle)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(
            router.PlanLaunch(new(null, [], handle, false)));
        Assert.Null(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
        Assert.Throws<SetupNativeOwnershipException>(() => store.Issue(Native(Receipt() with { ModelTarget = "utility" })));
        Assert.Throws<InvalidOperationException>(() => store.Issue(Native(Receipt() with { VerifiedGeneration = 0 })));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("channels", "/channels")]
    public async Task OrdinaryDashboard_UsesRequestedPathAndNormalCredentials(string? path, string suffix)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "synthetic shared", false, CredentialResolver.SourceSharedGatewayToken),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));
        Assert.True(await launcher.OpenAsync(path));
        Assert.Equal("https://gateway.example/control" + suffix + "#token=synthetic%20shared", launched);
        Assert.DoesNotContain("custodian", launched);
    }

    [Theory]
    [InlineData(CredentialResolver.SourceDeviceToken, false)]
    [InlineData(CredentialResolver.SourceBootstrapToken, true)]
    public async Task DeviceAndBootstrapTokens_NeverEnterBrowserUrl(string source, bool bootstrap)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "do-not-export", bootstrap, source),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));
        Assert.True(await launcher.OpenAsync());
        Assert.Equal("https://gateway.example/control", launched);
    }

    [Theory]
    [InlineData("tunnel")]
    [InlineData("credential")]
    [InlineData("browser")]
    [InlineData("exception")]
    public async Task Failure_IsReportedOnceWithoutAutomaticRetry(string failure)
    {
        var launches = 0;
        var failures = 0;
        var launcher = new GatewayDashboardLauncher(() => failure != "tunnel",
            () => failure == "credential" ? null : new(Gateway.Url, "shared", false, CredentialResolver.SourceSharedGatewayToken),
            _ =>
            {
                launches++;
                if (failure == "exception") throw new InvalidOperationException("synthetic-secret-url");
                return Task.FromResult(false);
            }, () => failures++);
        Assert.False(await launcher.OpenAsync());
        Assert.Equal(failure is "browser" or "exception" ? 1 : 0, launches);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void EndpointBinding_ExcludesRotatingTokensButIncludesSshRealm()
    {
        var original = Gateway with { SshTunnel = new("user", "ssh.example", 18789, 19001) };
        var binding = GatewayDashboardBinding.Capture(original);
        Assert.Equal(binding, GatewayDashboardBinding.Capture(original with { SharedGatewayToken = "rotated" }));
        Assert.NotEqual(binding, GatewayDashboardBinding.Capture(
            original with { SshTunnel = original.SshTunnel! with { Host = "other.example" } }));
    }

    [Fact]
    public async Task ExplicitDashboardRetry_KeepsRequestedPathAndClearsFailureOnlyAfterOpen()
    {
        var urls = new List<string>();
        var failures = 0;
        var opened = 0;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "synthetic", false, CredentialResolver.SourceSharedGatewayToken),
            url => { urls.Add(url); return Task.FromResult(urls.Count == 2); },
            () => failures++, () => opened++);
        Assert.False(await launcher.OpenAsync("channels"));
        Assert.Equal(0, opened);
        Assert.Single(urls);
        Assert.True(await launcher.OpenAsync("channels"));
        Assert.Equal(urls[0], urls[1]);
        Assert.Equal(1, failures);
        Assert.Equal(1, opened);
    }

    private sealed class StartupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
