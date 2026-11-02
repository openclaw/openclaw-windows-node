using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string UnavailableWarning =
        "The Gateway verified during setup is not connected. Check Connection, or check again when it is available.";
    private const string AuthorityWarning =
        "The Gateway and agent verified during setup could not be confirmed. Open Connection to check them.";

    [Fact]
    public void FixtureGuardAndActualSetupResolverUseTheSameEffectiveLocalDirectory()
    {
        using var profile = new GatewayFixtureProfile(new Uri("ws://127.0.0.1:49231/"), "synthetic");
        using var environment = new OpenClaw.TestSupport.EnvironmentScope()
            .Set("OPENCLAW_GATEWAY_FIXTURE", "1")
            .Set("OPENCLAW_TRAY_DATA_DIR", profile.DataDirectory)
            .Set("OPENCLAW_TRAY_APPDATA_DIR", profile.RoamingRoot)
            .Set("OPENCLAW_TRAY_LOCALAPPDATA_DIR", profile.LocalRoot)
            .Set("OPENCLAW_TRAY_LOCAL_DATA_DIR", profile.SetupDirectory);
        Assert.Equal(SetupContext.ResolveLocalDataDir(), GatewayFixtureIsolation.Get().LocalDataDirectory);
        Assert.Equal(profile.SetupDirectory, SetupContext.ResolveLocalDataDir());
        environment.Set("OPENCLAW_TRAY_LOCAL_DATA_DIR", Path.Combine(profile.RunDirectory, "wrong"));
        Assert.Throws<InvalidOperationException>(() => GatewayFixtureIsolation.Get());
        Assert.Equal(profile.SetupDirectory, SetupContext.ResolveLocalDataDir());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task VerifiedSetupWarningRecoversAndRejectsChangedAuthorityInFullApp()
    {
        using var releaseVerification = new ManualResetEventSlim();
        var verificationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = GatewayScenario.CreateNativeSetup((method, _) =>
        {
            if (method != "openclaw.setup.verify")
                throw new InvalidOperationException($"Unexpected synthetic setup request: {method}");
            verificationRequested.TrySetResult();
            if (!releaseVerification.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("The startup proof did not release synthetic model verification.");
            return new { ok = true, modelRef = "fixture/browse", latencyMs = 1 };
        });
        await WithAppAsync(async run =>
        {
            await verificationRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUiAsync(run, () => FindText(run, "Finishing setup") is { Current.IsOffscreen: false },
                "restart progress while synthetic model verification is held");
            var progressWindow = Assert.Single(AppWindows(run), window =>
                window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar))
                    is { Current.IsOffscreen: false });
            Assert.Null(FindById(run, "ChatComposerInput"));
            Assert.Null(FindText(run, "Welcome to OpenClaw"));
            OwnedWindowCapture.Save(run, progressWindow, "00-restart-verification-progress.png",
                FindText(run, "Finishing setup")!,
                progressWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar)));
            releaseVerification.Set();
            await WaitVerifiedChatAsync(run);
            await WaitUiAsync(run, () => !AppWindows(run).Any(window => window.Current.Name == "Finishing setup"),
                "progress window closed after verified native navigation");
            var workspace = Assert.Single(AppWindows(run), window =>
                window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNavHome")) is not null);
            Assert.DoesNotContain(AppWindows(run), window =>
                window.Current.Name.Contains("OpenClaw Settings", StringComparison.Ordinal) ||
                window.Current.Name.Contains("OpenClaw Setup", StringComparison.Ordinal));
            Assert.Contains(run.Gateway.Requests, request => request.Method == "openclaw.setup.verify" && request.Outcome == "ok");
            await run.WaitForAsync(() => Task.FromResult(
                !File.Exists(Path.Combine(run.Profile.DataDirectory, "setup-dashboard-handoff", "pending.json"))),
                "production launcher consuming verified handoff");
            var initial = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.MainSessionKey });
            var connectionCount = run.Gateway.ConnectionCount;
            const string draft = "Keep this unsent draft through setup chat recovery";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            CaptureWarningState(run, "01-bound-verified-chat.png", null);

            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => FindText(run, UnavailableWarning) is { Current.IsOffscreen: false },
                "actual connection loss warning");
            CaptureWarningState(run, "02-connection-unavailable.png", UnavailableWarning);
            await WaitVerifiedChatAsync(run);
            Assert.True(run.Gateway.ConnectionCount > connectionCount);
            Assert.Equal(draft,
                ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            CaptureWarningState(run, "03-automatic-recovery.png", null);
            var recovered = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.MainSessionKey });

            // Change only the disposable persisted signing identity, not the connected
            // client or its in-memory receipt. The production authority guard must reject it.
            var registry = new GatewayRegistry(run.Profile.DataDirectory);
            var identityPath = registry.GetIdentityDirectory(run.Profile.GatewayId);
            File.Delete(Path.Combine(identityPath, "device-key-ed25519.json"));
            new DeviceIdentity(identityPath).Initialize();
            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => FindText(run, AuthorityWarning) is { Current.IsOffscreen: false },
                "genuine persisted identity mismatch after reconnect");
            Assert.Null(FindById(run, "ChatComposerInput"));
            CaptureWarningState(run, "04-authority-blocked.png", AuthorityWarning);
            Invoke(FindById(run, "ChatNativeSetupCheckAgain")!);
            await Task.Delay(350);
            Assert.NotNull(FindText(run, AuthorityWarning));
            Assert.Null(FindById(run, "ChatComposerInput"));
            CaptureWarningState(run, "05-recheck-remains-blocked.png", AuthorityWarning);

            await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false } &&
                FindById(run, "ChatNativeSetupError") is null, "ordinary navigation clearing obsolete bound warning");
            CaptureWarningState(run, "06-ordinary-navigation.png", null);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "warning-proof.json"),
                JsonSerializer.Serialize(new
                {
                    scenario = scenario.Name, scenario.Sha256,
                    modelVerification = "synthetic fixture response, not real Gateway or inference proof",
                    activation = "--post-setup-restart --post-setup-launch; actual SetupDashboardHandoffStore receipt and production verifier",
                    visibleProgressBeforeVerification = true,
                    workspaceHwnd = workspace.Current.NativeWindowHandle,
                    progressWindowClosedAfterVerification = true,
                    verifiedSession = GatewayScenario.MainSessionKey, initial, recovered,
                    automaticRecovery = true, authorityMismatchRemainedBlocked = true,
                    unsentDraftPreserved = true,
                    checkAgainInvoked = true, ordinaryNavigationClearedWarning = true,
                    setupVerificationRequests = run.Gateway.Requests.Count(request => request.Method == "openclaw.setup.verify"),
                    run.Gateway.ConnectionCount
                }, new JsonSerializerOptions { WriteIndented = true }));
        }, scenario: scenario, prepareSetupHandoff: IssueSyntheticHandoff, postSetupRestart: true,
            releasePendingResponses: releaseVerification.Set);
    }

    private static string IssueSyntheticHandoff(GatewayFixtureProfile profile) =>
        IssueSyntheticHandoff(profile, SetupNativeDestination.Chat);

    private static string IssueSyntheticHandoff(GatewayFixtureProfile profile, SetupNativeDestination destination)
    {
        var registry = new GatewayRegistry(profile.DataDirectory);
        registry.Load();
        var record = registry.GetActive()!;
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var verification = new GatewayAiSetupCompletion(SetupCompletionIntent.Dashboard,
            record.Id, GatewayDashboardBinding.Capture(record), "fixture/browse", "main", 1,
            IdentityBinding: SetupCompletionAuthority.CaptureIdentity(identityPath, identity.DeviceId),
            SessionKey: GatewayScenario.MainSessionKey);
        return new SetupDashboardHandoffStore(profile.DataDirectory).Issue(new(verification,
            new(destination, GatewayScenario.MainSessionKey)));
    }

    private static Task WaitVerifiedChatAsync(GatewayFixtureRun run) =>
        WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false } &&
            FindById(run, "ChatNativeSetupError") is null &&
            RenderConsumedHistory(run, GatewayScenario.MainSessionKey) &&
            IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "verified main session visible with no warning");

    private static void CaptureWarningState(GatewayFixtureRun run, string name, string? warning)
    {
        var window = AppWindows(run).Single(candidate => candidate.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNavHome")) is not null);
        if (warning is null)
        {
            Assert.Null(FindById(run, "ChatNativeSetupError"));
            OwnedWindowCapture.Save(run, window, name, FindById(run, "ChatComposerInput")!);
            Assert.Null(FindById(run, "ChatNativeSetupError"));
        }
        else
        {
            OwnedWindowCapture.Save(run, window, name, FindText(run, warning)!,
                FindById(run, "ChatNativeSetupCheckAgain")!);
            Assert.NotNull(FindText(run, warning));
        }
    }
}
