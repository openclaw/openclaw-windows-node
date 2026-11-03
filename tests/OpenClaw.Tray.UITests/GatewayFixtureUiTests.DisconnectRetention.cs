using System.Windows.Automation;
using System.Text.Json;
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
            ReaderAnchor? anchor = null;
            await WaitUiAsync(run, () => (anchor = CaptureVisibleReaderAnchor(run)) is not null,
                "actually visible mid-history reader anchor");
            var reader = anchor!;
            var renderedBefore = RenderedHistoryRevision(run, GatewayScenario.LongSessionKey);
            var historyRepliesBefore = CompletedReaderHistoryReplies(run);
            var inputIdentity = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "disconnect-reader-anchor.json"),
                JsonSerializer.Serialize(new { reader, renderedBefore, historyRepliesBefore }));
            await CaptureIfRequestedAsync(run, "explicit-disconnect-before.png", FindReaderAnchor(run, reader)!);
            await WriteReaderBoundaryAsync(run, reader, "before-disconnect");

            await ToggleConnectionAsync(run, ToggleState.On);
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() == "Idle", "explicit manager disconnect");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: false }, "retained offline composer");
            Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongTitle));
            await WriteReaderBoundaryAsync(run, reader, "offline-mounted");
            try
            {
                await WaitUiAsync(run, () => ReaderAnchorIsPositioned(run, reader), "offline visible reader anchor and viewport offset");
            }
            catch
            {
                // Preserve the original assertion failure. Emit only fixture-owned
                // geometry and whitelisted state, never a UI tree or chat contents.
                await WriteReaderBoundaryAsync(run, reader, "offline-anchor-failure", includeProviderSnapshot: true);
                throw;
            }
            Assert.InRange(TimelineScroll(run).Current.VerticalScrollPercent, beforeScroll - 5, beforeScroll + 5);
            Assert.False(FindById(run, "ChatComposerPrimaryAction")?.Current.IsEnabled == true);
            var offline = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.LongSessionKey });
            Assert.Contains(offline.GetProperty("selectedTimeline").GetProperty("entries").EnumerateArray(),
                entry => entry.GetProperty("Text").GetString()?.Contains(GatewayScenario.LongHistoryFinalMarker, StringComparison.Ordinal) == true);
            await CaptureIfRequestedAsync(run, "explicit-disconnect-offline.png", FindReaderAnchor(run, reader)!);

            await ToggleConnectionAsync(run, ToggleState.Off);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await run.WaitForAsync(() => Task.FromResult(CompletedReaderHistoryReplies(run) > historyRepliesBefore),
                "new reconnect history response, not the initial load");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsEnabled: true } &&
                RenderConsumedHistory(run, GatewayScenario.LongSessionKey, afterRevision: renderedBefore),
                "fresh reconnect history revision consumed by the mounted composer");
            await WaitUiAsync(run, () => PositioningConsumedFreshHistory(run, renderedBefore)
                && ReaderAnchorIsPositioned(run, reader),
                "fresh history's native positioning/scroll completion and rendered reader anchor");
            Assert.Equal(inputIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongTitle));
            Assert.InRange(TimelineScroll(run).Current.VerticalScrollPercent, beforeScroll - 5, beforeScroll + 5);
            var renderedAfter = RenderedHistoryRevision(run, GatewayScenario.LongSessionKey);
            Assert.True(renderedAfter > renderedBefore);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "disconnect-reader-anchor.json"),
                JsonSerializer.Serialize(new { reader, renderedBefore, renderedAfter,
                    historyRepliesBefore, historyRepliesAfter = CompletedReaderHistoryReplies(run),
                    recoveredOffset = FindReaderAnchor(run, reader)!.Current.BoundingRectangle.Top - FindTimeline(run)!.Current.BoundingRectangle.Top }));
            await CaptureIfRequestedAsync(run, "explicit-disconnect-recovered.png", FindReaderAnchor(run, reader)!);
            await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is { Current.IsEnabled: true }, "normal Send after recovery");
            Invoke(FindById(run, "ChatComposerPrimaryAction")!);
            var sent = await run.Gateway.WaitForCompletedRequestAsync("chat.send", GatewayScenario.LongSessionKey);
            Assert.Equal("ok", sent.Outcome);
            await CaptureIfRequestedAsync(run, "explicit-disconnect-sent.png");
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }

    private const double ReaderAnchorTolerancePixels = 4;
    private sealed record ReaderAnchor(string Text, double TopOffset, double LeftOffset);

    private static ReaderAnchor? CaptureVisibleReaderAnchor(GatewayFixtureRun run)
    {
        var timeline = FindTimeline(run);
        if (timeline is null) return null;
        var viewport = timeline.Current.BoundingRectangle;
        var message = timeline.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Where(element => element.Current.Name.StartsWith("Fixture long message ", StringComparison.Ordinal)
                && IsVisibleInTimeline(timeline, element))
            .OrderBy(element => Math.Abs(element.Current.BoundingRectangle.Top - (viewport.Top + viewport.Height / 2)))
            .FirstOrDefault();
        if (message is null) return null;
        var bounds = message.Current.BoundingRectangle;
        return new(message.Current.Name, bounds.Top - viewport.Top, bounds.Left - viewport.Left);
    }

    private static AutomationElement? FindReaderAnchor(GatewayFixtureRun run, ReaderAnchor anchor) =>
        FindTimeline(run)?.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, anchor.Text)));

    private static bool ReaderAnchorIsPositioned(GatewayFixtureRun run, ReaderAnchor anchor)
    {
        var timeline = FindTimeline(run);
        var message = FindReaderAnchor(run, anchor);
        if (timeline is null || message is null || !IsVisibleInTimeline(timeline, message)) return false;
        var bounds = message.Current.BoundingRectangle;
        var viewport = timeline.Current.BoundingRectangle;
        return Math.Abs(bounds.Top - viewport.Top - anchor.TopOffset) <= ReaderAnchorTolerancePixels
            && Math.Abs(bounds.Left - viewport.Left - anchor.LeftOffset) <= ReaderAnchorTolerancePixels;
    }

    private async Task WriteReaderBoundaryAsync(GatewayFixtureRun run, ReaderAnchor anchor, string stage,
        bool includeProviderSnapshot = false)
    {
        try
        {
            // The ordinal identifies known synthetic fixture data without exporting
            // arbitrary UI text. The expected offsets are the original captured anchor.
            const string prefix = "Fixture long message ";
            var fixtureOrdinal = anchor.Text.StartsWith(prefix, StringComparison.Ordinal)
                && anchor.Text.Length >= prefix.Length + 3
                && int.TryParse(anchor.Text.AsSpan(prefix.Length, 3), out var ordinal)
                && ordinal is >= 1 and <= 240 ? (int?)ordinal : null;
            var timeline = FindTimeline(run);
            var message = FindReaderAnchor(run, anchor);
            var input = FindById(run, "ChatComposerInput");
            var viewport = timeline?.Current.BoundingRectangle;
            var bounds = message?.Current.BoundingRectangle;
            var intersection = viewport is { } v && bounds is { } b
                ? System.Windows.Rect.Intersect(v, b) : System.Windows.Rect.Empty;
            var scrollElement = timeline?.TryGetCurrentPattern(ScrollPattern.Pattern, out var direct) == true
                ? direct : timeline?.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
                    ?.GetCurrentPattern(ScrollPattern.Pattern);
            var scroll = (scrollElement as ScrollPattern)?.Current;
            using var rendered = string.IsNullOrEmpty(input?.Current.ItemStatus)
                ? null : JsonDocument.Parse(input.Current.ItemStatus);
            using var positioned = string.IsNullOrEmpty(timeline?.Current.ItemStatus)
                ? null : JsonDocument.Parse(timeline.Current.ItemStatus);
            var selected = rendered?.RootElement.GetProperty("selectedThreadId").GetString();
            var revision = rendered?.RootElement.GetProperty("historyRevisions");
            long? historyRevision = revision is { } revisions
                && revisions.TryGetProperty(GatewayScenario.LongSessionKey, out var value)
                && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
            var requestKey = positioned?.RootElement.GetProperty("requestKey").GetString();
            var completedKey = positioned?.RootElement.GetProperty("completedRequestKey").GetString();
            output.WriteLine("SYNTHETIC disconnect-reader " + JsonSerializer.Serialize(new
            {
                stage, fixtureOrdinal, expectedY = anchor.TopOffset, expectedX = anchor.LeftOffset,
                viewport = ReaderRectangle(viewport), anchorBounds = ReaderRectangle(bounds),
                intersection = ReaderRectangle(intersection),
                relativeY = bounds is { IsEmpty: false } a && viewport is { IsEmpty: false } t ? (double?)(a.Top - t.Top) : null,
                relativeX = bounds is { IsEmpty: false } c && viewport is { IsEmpty: false } w ? (double?)(c.Left - w.Left) : null,
                timelineFound = timeline is not null, anchorFound = message is not null,
                timelineOffscreen = timeline?.Current.IsOffscreen, anchorOffscreen = message?.Current.IsOffscreen,
                timelineRuntimeId = timeline?.GetRuntimeId(), anchorRuntimeId = message?.GetRuntimeId(),
                visible = timeline is not null && message is not null && IsVisibleInTimeline(timeline, message),
                scrollPercent = scroll?.VerticalScrollPercent, viewSize = scroll?.VerticalViewSize,
                selectedFixtureReader = selected == GatewayScenario.LongSessionKey, historyRevision,
                nativeHistoryRevision = positioned?.RootElement.GetProperty("historyRevision").GetInt64(),
                nativeSettled = positioned?.RootElement.GetProperty("settled").GetBoolean(),
                nativeRequestFingerprint = requestKey is null ? null : Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestKey)))[..16],
                nativeRequestCompleted = requestKey is not null && requestKey == completedKey,
                historyReplies = CompletedReaderHistoryReplies(run)
            }));
            if (!includeProviderSnapshot) return;
            // This existing supporting read runs only after the failed assertion,
            // so it cannot refresh history and rescue the boundary under test.
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.LongSessionKey });
            var available = snapshot.TryGetProperty("composeTarget", out var target);
            output.WriteLine("SYNTHETIC disconnect-reader-provider " + JsonSerializer.Serialize(new
            {
                stage, snapshotAvailable = available,
                connected = snapshot.TryGetProperty("connectionStatus", out var status)
                    && string.Equals(status.GetString(), "connected", StringComparison.OrdinalIgnoreCase),
                composeTargetReady = available && target.GetProperty("isReady").GetBoolean(),
                composeTargetMatchesReader = available && target.GetProperty("sessionKey").GetString() == GatewayScenario.LongSessionKey,
                historyLoaded = snapshot.TryGetProperty("selectedTimeline", out var history)
                    && history.ValueKind == JsonValueKind.Object && history.GetProperty("historyLoaded").GetBoolean()
            }));
        }
        catch (Exception diagnosticError)
        {
            // No exception message: UIA/MCP errors can contain arbitrary content.
            output.WriteLine($"SYNTHETIC disconnect-reader stage={stage}; diagnostic-unavailable={diagnosticError.GetType().Name}");
        }
    }

    private static double[]? ReaderRectangle(System.Windows.Rect? bounds) =>
        bounds is { IsEmpty: false } value ? [value.X, value.Y, value.Width, value.Height] : null;

    private static bool PositioningConsumedFreshHistory(GatewayFixtureRun run, long previousRevision)
    {
        var timeline = FindTimeline(run);
        if (timeline is null || string.IsNullOrEmpty(timeline.Current.ItemStatus)) return false;
        using var receipt = JsonDocument.Parse(timeline.Current.ItemStatus);
        return receipt.RootElement.GetProperty("historyRevision").GetInt64() > previousRevision
            && receipt.RootElement.GetProperty("settled").GetBoolean()
            && receipt.RootElement.GetProperty("requestKey").GetString() ==
                receipt.RootElement.GetProperty("completedRequestKey").GetString();
    }

    private static long RenderedHistoryRevision(GatewayFixtureRun run, string key)
    {
        using var receipt = JsonDocument.Parse(FindById(run, "ChatComposerInput")!.Current.ItemStatus);
        Assert.Equal(key, receipt.RootElement.GetProperty("selectedThreadId").GetString());
        return receipt.RootElement.GetProperty("historyRevisions").GetProperty(key).GetInt64();
    }

    private static int CompletedReaderHistoryReplies(GatewayFixtureRun run) =>
        run.Gateway.Requests.Count(request => request.Method == "chat.history"
            && request.SessionKey == GatewayScenario.LongSessionKey && request.Outcome == "ok");

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
