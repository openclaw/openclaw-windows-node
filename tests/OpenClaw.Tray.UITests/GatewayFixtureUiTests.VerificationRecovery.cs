using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ConnectedVerificationTimeoutHasExplicitRetryWithoutDiscardingTheLoadedDraft()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherSentinel), "loaded conversation before verification timeout");
            const string draft = "Unsent draft survives chat verification timeout.";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            var inputIdentity = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            await CaptureIfRequestedAsync(run, "verification-before.png");
            await ToggleConnectionAsync(run, ToggleState.On);
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() == "Idle", "explicit manager disconnect before withholding roster");
            var firstRequest = run.Gateway.Requests.Count;
            run.Gateway.HoldSessionListings();
            var rosterHeld = true;
            try
            {
                await ToggleConnectionAsync(run, ToggleState.Off);
                await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Skip(firstRequest)
                    .Any(r => r.Method == "sessions.list" && r.AuthorityScoped && r.Outcome == "pending")), "held authoritative verification roster");
                output.WriteLine("Observed authority-scoped sessions.list pending on the reconnected client; using the production verification deadline.");
                await run.InvokeAsync("app.navigate", new { page = "chat" });
                await WaitUiAsync(run, () => FindById(run, "ChatComposerRetryVerification") is { Current.IsOffscreen: false }, "explicit verification retry in the retained composer");
                Assert.Equal("Connected", (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString());
                Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
                Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                Assert.False(FindById(run, "ChatComposerInput")!.Current.IsEnabled);
                Assert.False(FindById(run, "ChatComposerPrimaryAction")!.Current.IsEnabled);
                Assert.True(IsVisibleInTimeline(run, GatewayScenario.OtherSentinel));
                Assert.Contains("could not be verified", FindById(run, "ChatComposerConnectionStatus")!.Current.Name);
                await CaptureIfRequestedAsync(run, "verification-failed-retained.png");
                run.Gateway.ReleaseSessionListings();
                rosterHeld = false;
                Invoke(FindById(run, "ChatComposerRetryVerification")!);
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: true }, "same-client explicit verification retry");
                Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
                Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                await CaptureIfRequestedAsync(run, "verification-recovered-retained.png");
            }
            finally { if (rosterHeld) run.Gateway.ReleaseSessionListings(); }

            run.Gateway.HoldUserProfiles();
            var profileHeld = true;
            try
            {
                run.Gateway.SetUserProfile("fixture-ui-new-profile");
                await run.Gateway.NotifyProfileIdentityChangedAsync();
                await WaitUiAsync(run, () => FindById(run, "ChatRetryVerification") is { Current.IsOffscreen: false }, "fresh-profile verification failure retry");
                Assert.Equal("Connected", (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString());
                Assert.Contains("could not be verified", FindById(run, "ChatVerificationStatus")!.Current.Name);
                await CaptureIfRequestedAsync(run, "verification-failed-fresh-profile.png");
                run.Gateway.ReleaseUserProfiles();
                profileHeld = false;
                Invoke(FindById(run, "ChatRetryVerification")!);
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: true }, "fresh verified profile after explicit retry");
                await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
                ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Verified current-profile ordinary Send.");
                await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is { Current.IsEnabled: true }, "ordinary Send after explicit retry");
                Invoke(FindById(run, "ChatComposerPrimaryAction")!);
                Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey)).Outcome);
                await CaptureIfRequestedAsync(run, "verification-retry-sent.png");
            }
            finally { if (profileHeld) run.Gateway.ReleaseUserProfiles(); }
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }
}
