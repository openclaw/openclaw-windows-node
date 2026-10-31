using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ExplicitDisconnectRetainsLoadedTranscriptDraftAndScrollAcrossRecovery()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongTitle, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.LongSessionKey), "loaded conversation before explicit Disconnect");
            const string draft = "Unsent draft retained across explicit Disconnect.";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker) &&
                TimelineScroll(run).Current.VerticalScrollPercent >= 99, "settled visible initial tail before reader scrolling");
            await MoveReaderAwayFromTailAsync(run);
            var beforeScroll = TimelineScroll(run).Current.VerticalScrollPercent;
            Assert.InRange(beforeScroll, 5, 95);
            var inputIdentity = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            await CaptureIfRequestedAsync(run, "explicit-disconnect-before.png");

            await ToggleConnectionAsync(run, ToggleState.On);
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() == "Idle", "explicit manager disconnect");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: false }, "retained offline composer");
            Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongTitle));
            Assert.InRange(TimelineScroll(run).Current.VerticalScrollPercent, beforeScroll - 5, beforeScroll + 5);
            Assert.False(FindById(run, "ChatComposerPrimaryAction")?.Current.IsEnabled == true);
            var offline = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.LongSessionKey });
            Assert.Contains(offline.GetProperty("selectedTimeline").GetProperty("entries").EnumerateArray(),
                entry => entry.GetProperty("Text").GetString()?.Contains(GatewayScenario.LongHistoryFinalMarker, StringComparison.Ordinal) == true);
            await CaptureIfRequestedAsync(run, "explicit-disconnect-offline.png");

            await ToggleConnectionAsync(run, ToggleState.Off);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: true } &&
                RenderConsumedHistory(run, GatewayScenario.LongSessionKey), "same retained conversation after verified reconnect");
            Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongTitle));
            Assert.InRange(TimelineScroll(run).Current.VerticalScrollPercent, beforeScroll - 5, beforeScroll + 5);
            await CaptureIfRequestedAsync(run, "explicit-disconnect-recovered.png");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is { Current.IsEnabled: true }, "normal Send after recovery");
            Invoke(FindById(run, "ChatComposerPrimaryAction")!);
            var sent = await run.Gateway.WaitForCompletedRequestAsync("chat.send", GatewayScenario.LongSessionKey);
            Assert.Equal("ok", sent.Outcome);
            await CaptureIfRequestedAsync(run, "explicit-disconnect-sent.png");
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }

    private static async Task MoveReaderAwayFromTailAsync(GatewayFixtureRun run)
    {
        const int maxReaderPages = 30;
        const double readerTargetPercent = 80;
        // UIA SetScrollPercent over a still-realizing virtualized extent reported
        // 50% -> 0% in the original CI run. Drive ordinary page-up semantics only
        // after the actual tail is visible, and await each native scroll effect.
        for (var page = 0; page < maxReaderPages; page++)
        {
            var before = TimelineScroll(run).Current.VerticalScrollPercent;
            if (before <= readerTargetPercent) return;
            TimelineScroll(run).Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
            await WaitUiAsync(run, () =>
            {
                var current = TimelineScroll(run).Current.VerticalScrollPercent;
                return current >= 0 && current < before;
            }, "native reader page-up scroll effect");
        }
        Assert.InRange(TimelineScroll(run).Current.VerticalScrollPercent, 5, readerTargetPercent);
    }

    private static ScrollPattern TimelineScroll(GatewayFixtureRun run)
    {
        var timeline = FindTimeline(run) ?? throw new InvalidOperationException("The retained timeline is not mounted.");
        if (timeline.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)) return (ScrollPattern)pattern;
        var scroller = timeline.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
            ?? throw new InvalidOperationException("The retained timeline has no native scroll provider.");
        return (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
    }

    private static async Task ToggleConnectionAsync(GatewayFixtureRun run, ToggleState expected)
    {
        await run.InvokeAsync("app.navigate", new { page = "connection" });
        await WaitUiAsync(run, () => FindById(run, "ConnectionToggle") is { Current.IsEnabled: true, Current.IsOffscreen: false }, "connection toggle");
        var toggle = (TogglePattern)FindById(run, "ConnectionToggle")!.GetCurrentPattern(TogglePattern.Pattern);
        Assert.Equal(expected, toggle.Current.ToggleState);
        toggle.Toggle();
    }
}
