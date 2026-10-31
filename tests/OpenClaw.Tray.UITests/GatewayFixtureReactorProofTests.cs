using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task RepeatedSessionSwitchingKeepsNativeTimelineResponsive()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var cycle = System.Diagnostics.Stopwatch.StartNew();
                await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
                // Tail placement has its own strict test. A missed final row must
                // not prevent this independent crash/responsiveness exercise.
                await WaitUiAsync(run, () => HasVisibleLongHistory(run), "visible mixed-height long history");
                Assert.False(IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker));
                await ScrollToAsync(run, 0);
                await ScrollToAsync(run, 100);
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker),
                    "other session's visible history after scrolling");
                Assert.False(HasVisibleLongHistory(run));
                output.WriteLine($"Completed session-switch cycle {iteration + 1}/12 ({cycle.Elapsed.TotalSeconds:F3}s).");
            }
            await CaptureIfRequestedAsync(run, "session-switch-responsive.png");
        });
    }

    private static bool HasVisibleLongHistory(GatewayFixtureRun run)
    {
        var timeline = FindTimeline(run);
        if (timeline is null)
            return false;
        var texts = timeline.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        return texts.Cast<AutomationElement>().Any(element =>
        {
            var text = element.Current.Name;
            return (text.StartsWith("Fixture long message ", StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongEarlySentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongMiddleSentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongFinalSentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongHistoryFinalMarker, StringComparison.Ordinal))
                && IsVisibleInTimeline(timeline, element);
        });
    }
}
