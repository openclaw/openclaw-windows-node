using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task TranscriptSearch_ShowsVisibleMatchesAndOpensTheMatchingConversation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindById(run, "SessionsTranscriptQuery") is not null, "transcript search field");
            var query = FindById(run, "SessionsTranscriptQuery")!;
            var input = query.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)) ?? query;
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("FIXTURE RESEARCH:");
            var searchWindow = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SessionsTranscriptQuery")) is not null);
            Invoke(FindById(run, "SessionsTranscriptSubmit")!);
            await run.Gateway.WaitForCompletedRequestAsync("sessions.search");
            await WaitUiAsync(run, () => FindById(run, "SessionsTranscriptSearchResults") is { Current.IsOffscreen: false },
                "visible transcript search results");
            var results = FindById(run, "SessionsTranscriptSearchResults")!;
            var snippets = results.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty,
                    "FIXTURE RESEARCH: the violet compass points north."));
            Assert.Equal(2, snippets.Count);
            Assert.All(snippets.Cast<AutomationElement>(), snippet => Assert.False(snippet.Current.IsOffscreen));
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, searchWindow, "sessions-transcript-search-results.png");
            var open = results.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Fixture: research conversation")))!;
            Invoke(open);
            await WaitHistoryAsync(run, GatewayScenario.OtherSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherSentinel),
                "matched conversation transcript");
            await CaptureIfRequestedAsync(run, "sessions-search-opened-matching-conversation.png");
        });
    }
}
