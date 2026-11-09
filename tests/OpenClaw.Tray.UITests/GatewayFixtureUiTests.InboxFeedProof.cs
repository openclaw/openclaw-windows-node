using System.Windows.Automation;
using System.Windows.Automation.Text;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string InboxFeedExpiryTimeoutVariable = "OPENCLAW_GATEWAY_FIXTURE_INBOX_EXPIRY_TIMEOUT_SECONDS";
    // Real 45-second producer lifetime plus UI scheduling margin, inside the unchanged 150-second body budget.
    private static readonly TimeSpan InboxFeedExpiryTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(InboxFeedExpiryTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, 90)) : TimeSpan.FromSeconds(60);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxFeed_LiveApprovalIsRedactedOnPageAndBellRoutesToChatAndExpiresLocally()
    {
        await WithAppAsync(async run =>
        {
            await OpenInboxFeedPageAsync(run, "Approvals");
            Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync("exec.approval.list")).Outcome);
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")?.Current.Name == "Approvals",
                "empty initial approval inventory");
            var sentAt = DateTimeOffset.UtcNow;
            await run.Gateway.PublishInboxFeedApprovalAsync();
            var pageRow = await ExpandInboxFeedRowAsync(run, "Command approval needed,");
            await AssertInboxFeedCommandAsync(run, pageRow, "curl https://example.test/run?token=*** ; echo fixture-end");
            Assert.Equal("Approvals 1", InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")!.Current.Name);
            var firstCountdown = InboxFeedCountdown(pageRow);
            Assert.InRange(firstCountdown, 1, 45);
            await WaitUiAsync(run, () => InboxFeedCountdown(pageRow) is var remaining && remaining > 0 && remaining < firstCountdown,
                "native approval countdown advances without another producer event");
            CaptureInboxFeed(run, "inbox-feed-approval-page.png", pageRow);

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Invoke(await WaitForElementByIdAsync(run, "WorkspaceNotifications", "Inbox bell"));
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsOpenPage") is not null, "Inbox bell open");
            Invoke(InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")!);
            var bellRow = await ExpandInboxFeedRowAsync(run, "Command approval needed,");
            await AssertInboxFeedCommandAsync(run, bellRow, "curl https://example.test/run?token=*** ; echo fixture-end");
            Assert.Equal("Approvals 1", InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")!.Current.Name);
            CaptureInboxFeed(run, "inbox-feed-approval-bell.png", bellRow);

            var historyOccurrence = run.Gateway.Requests.Count(request =>
                request.Method == "chat.history" && request.SessionKey == GatewayScenario.OtherSessionKey) + 1;
            Invoke(await InboxFeedButtonAsync(run, bellRow, "Open chat"));
            await WaitComposerSelectionAsync(run, GatewayScenario.OtherSessionKey);
            Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync("chat.history",
                GatewayScenario.OtherSessionKey, historyOccurrence)).Outcome);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherSentinel), "approval's actual conversation history");
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsOpenPage") is null, "Open chat closes the bell");
            await CaptureIfRequestedAsync(run, "inbox-feed-approval-chat.png");

            await OpenInboxFeedPageAsync(run, "Approvals");
            await ExpandInboxFeedRowAsync(run, "Command approval needed,");
            var listReads = run.Gateway.Requests.Count(request => request.Method == "exec.approval.list");
            // No resolve, new request, list refresh or fake clock ends this request. Its
            // producer timestamps are three hours ahead, but its real lifetime is 45 seconds.
            await WaitUiAsync(run, () => InboxFeedRow(run, "Command approval needed,") is null &&
                InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")?.Current.Name == "Approvals" &&
                InboxFeedVisibleId(run, "WorkspaceNotificationsEmpty") is not null,
                "approval expires on the local clock without another Gateway event", InboxFeedExpiryTimeout);
            Assert.True(DateTimeOffset.UtcNow >= sentAt.AddSeconds(45), "Approval disappeared before its stated lifetime elapsed.");
            output.WriteLine($"Inbox live request sent at {sentAt:O}; local expiry verified at {DateTimeOffset.UtcNow:O}; producer lifetime 45 seconds, clock skew +3 hours.");
            Assert.Equal(listReads, run.Gateway.Requests.Count(request => request.Method == "exec.approval.list"));
            Assert.Equal("exec.approval.requested", Assert.Single(run.Gateway.Events).Event);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method.EndsWith(".approval.resolve", StringComparison.Ordinal));
            CaptureInboxFeed(run, "inbox-feed-approval-expired.png", InboxFeedVisibleId(run, "WorkspaceNotificationsEmpty")!);
        }, scenario: GatewayScenario.CreateInboxFeed());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxFeed_ListHydrationReconnectAndFailureActionsUseAuthenticatedGateway()
    {
        var scenario = GatewayScenario.CreateInboxFeed(seedApproval: true, allowExplainPrompts: true);
        Action? releaseHandshake = null;
        await WithAppAsync(async run =>
        {
            await OpenInboxFeedPageAsync(run, "Approvals");
            var initial = await ExpandInboxFeedRowAsync(run, "Command approval needed, echo fixture-initial");
            await AssertInboxFeedCommandAsync(run, initial, "echo fixture-initial");
            foreach (var method in new[] { "exec.approval.list", "cron.list", "cron.status", "models.authStatus" })
                Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync(method)).Outcome);
            Assert.Empty(run.Gateway.Events); // The first approval came from the list, not an event.
            await WaitUiAsync(run, () => scenario.InboxFeedPagedReadCount > 0, "initial Inbox load uses the bounded cron inventory reader");
            CaptureInboxFeed(run, "inbox-feed-initial-hydration.png", initial);
            Invoke(InboxFeedVisibleId(run, "WorkspaceNotificationsTabAll")!);
            await WaitUiAsync(run, () => InboxFeedRow(run, "Fixture nightly archive, Failed") is not null &&
                InboxFeedRow(run, "Account needs attention: Fixture Cloud,") is not null, "all three initial Gateway sources hydrated");

            var before = run.Gateway.Requests.Count(request => request.Method == "exec.approval.list");
            var pagedBefore = scenario.InboxFeedPagedReadCount;
            run.Gateway.HoldConnectionHandshakes();
            releaseHandshake = run.Gateway.ReleaseConnectionHandshakes;
            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => InboxFeedRow(run, "Command approval needed,") is null &&
                InboxFeedRow(run, "Fixture nightly archive, Failed") is null &&
                InboxFeedRow(run, "Account needs attention: Fixture Cloud,") is null &&
                InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")?.Current.Name == "Approvals",
                "disconnected epoch removes all three Gateway sources");
            Invoke(InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")!);
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsEmpty") is not null, "disconnected approvals are empty");
            CaptureInboxFeed(run, "inbox-feed-disconnected.png", InboxFeedVisibleId(run, "WorkspaceNotificationsEmpty")!);
            scenario.ReplaceInboxApprovalForReconnect();
            releaseHandshake();
            releaseHandshake = null;
            Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync("exec.approval.list", occurrence: before + 1)).Outcome);
            var reconnected = await ExpandInboxFeedRowAsync(run, "Command approval needed, echo fixture-reconnected");
            await AssertInboxFeedCommandAsync(run, reconnected, "echo fixture-reconnected");
            Assert.Null(InboxFeedRow(run, "Command approval needed, echo fixture-initial"));
            Assert.Empty(run.Gateway.Events);
            await WaitUiAsync(run, () => scenario.InboxFeedPagedReadCount > pagedBefore, "new epoch reloads bounded cron inventory");
            Assert.InRange(run.Gateway.Requests.Count(request => request.Method == "exec.approval.list"), 2, 4);
            CaptureInboxFeed(run, "inbox-feed-reconnected-hydration.png", reconnected);

            Invoke(InboxFeedVisibleId(run, "WorkspaceNotificationsTabAutomations")!);
            var automation = await ExpandInboxFeedRowAsync(run, "Fixture nightly archive, Failed");
            await InboxFeedLabelAsync(run, automation, "Fixture nightly archive");
            await InboxFeedLabelAsync(run, automation, "Failed");
            await InboxFeedTextAsync(run, automation, "Synthetic archive destination unavailable.");
            CaptureInboxFeed(run, "inbox-feed-automation-failed.png", automation);
            Invoke(await InboxFeedButtonAsync(run, automation, "Open Automations"));
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "CronPageMarker") is not null &&
                FindById(run, "AutomationExpander:fixture-inbox-cron") is not null,
                "native Automations destination renders the referenced job");
            Assert.All(run.Gateway.Requests.Where(request => request.Method == "cron.list" && request.Outcome != "pending"),
                request => Assert.Equal("ok", request.Outcome));
            await ScrollAutomationIntoViewAsync(run, "AutomationExpander:fixture-inbox-cron");
            Assert.Contains("Fixture nightly archive", FindById(run, "AutomationExpander:fixture-inbox-cron")!.Current.Name);
            await CaptureIfRequestedAsync(run, "inbox-feed-automations-destination.png");

            await OpenInboxFeedPageAsync(run, "System");
            var account = await ExpandInboxFeedRowAsync(run, "Account needs attention: Fixture Cloud,");
            Assert.Contains("Account needs attention · fixture-token", account.Current.Name, StringComparison.Ordinal);
            await InboxFeedLabelAsync(run, account, "Account needs attention: Fixture Cloud");
            await InboxFeedLabelAsync(run, account, "Account needs attention · fixture-token");
            Assert.True(InboxFeedFullyVisible(run, await InboxFeedButtonAsync(run, account, "Ask OpenClaw")));
            var review = await InboxFeedButtonAsync(run, account, "Review account");
            Assert.True(InboxFeedFullyVisible(run, review));
            CaptureInboxFeed(run, "inbox-feed-account-expired.png", account);
            Invoke(review);
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "DefaultModelPageMarker") is not null, "native account-review destination");
            await CaptureIfRequestedAsync(run, "inbox-feed-account-destination.png");

            await OpenInboxFeedPageAsync(run, "Automations");
            automation = await ExpandInboxFeedRowAsync(run, "Fixture nightly archive, Failed");
            const string question = "This automation failed: Fixture nightly archive. Explain why it failed and how to fix it.";
            Invoke(await InboxFeedButtonAsync(run, automation, "Ask OpenClaw"));
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            // The scenario independently rejects any other message/session. A receipt is
            // awaited, not inferred from a click, draft, or optimistic transcript entry.
            Assert.Equal("ok", (await run.Gateway.WaitForCompletedRequestAsync("chat.send", GatewayScenario.MainSessionKey)).Outcome);
            await WaitUiAsync(run, () => InboxFeedChatContains(run, question), "actual explain prompt in the canonical main conversation");
            Assert.Single(run.Gateway.Requests, request => request.Method == "chat.send" && request.Outcome == "ok");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "cron.run" or "cron.add" or "cron.update"
                or "models.authLogin" or "models.authSetApiKey" || request.Method.EndsWith(".approval.resolve", StringComparison.Ordinal));
            await CaptureIfRequestedAsync(run, "inbox-feed-explain-prompt-sent.png");
        }, scenario: scenario, releasePendingResponses: () =>
        {
            var release = releaseHandshake;
            releaseHandshake = null;
            release?.Invoke();
        });
    }

    private static async Task OpenInboxFeedPageAsync(GatewayFixtureRun run, string tab)
    {
        await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
        AutomationElement? readyTab = null;
        // The title can be visible before the header's category peers. Keep the
        // successful page-scoped peer and activate once, outside the existing wait.
        await WaitUiAsync(run, () =>
        {
            readyTab = null;
            foreach (var window in AppWindows(run))
            {
                var windowBounds = window.Current.BoundingRectangle;
                if (!HeaderControlReady(window, windowBounds)) continue;
                var page = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "NotificationsPage"));
                if (!HeaderControlReady(page, windowBounds)) continue;
                var pageBounds = page!.Current.BoundingRectangle;
                var heading = page.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "NotificationsPageMarker"));
                var candidate = page.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, $"WorkspaceNotificationsTab{tab}"));
                if (!HeaderControlReady(heading, pageBounds) || !HeaderControlReady(candidate, pageBounds)) continue;
                readyTab = candidate;
                return true;
            }
            return false;
        }, $"visible enabled Inbox page tab: {tab}");
        Invoke(readyTab!);

        bool HeaderControlReady(AutomationElement? element, System.Windows.Rect viewport)
        {
            if (element is null) return false;
            var info = element.Current;
            var bounds = info.BoundingRectangle;
            return info.ProcessId == run.AppProcessId && !info.IsOffscreen && info.IsEnabled &&
                HasBounds(viewport) && HasBounds(bounds) && viewport.Contains(bounds);
        }

        static bool HasBounds(System.Windows.Rect bounds) =>
            !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;
    }

    private static AutomationElement? InboxFeedVisibleId(GatewayFixtureRun run, string id) =>
        AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)).Cast<AutomationElement>())
            .SingleOrDefault(element => !element.Current.IsOffscreen);

    private static AutomationElement? InboxFeedRow(GatewayFixtureRun run, string prefix) =>
        InboxFeedVisibleId(run, "WorkspaceNotificationsList")?.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)).Cast<AutomationElement>()
            .SingleOrDefault(element => element.Current.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task<AutomationElement> ExpandInboxFeedRowAsync(GatewayFixtureRun run, string prefix)
    {
        AutomationElement? row = null;
        await WaitUiAsync(run, () => (row = InboxFeedRow(run, prefix)) is not null, $"Inbox row {prefix}");
        var pattern = (ExpandCollapsePattern)row!.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        pattern.Expand();
        await WaitUiAsync(run, () => pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded, "Inbox row expanded");
        return row;
    }

    private static async Task<AutomationElement> InboxFeedButtonAsync(GatewayFixtureRun run, AutomationElement row, string name)
    {
        AutomationElement? button = null;
        // Expanded state can precede materialization of the action subtree. Observe
        // the real, enabled button before invoking once; never retry the action itself.
        await WaitUiAsync(run, () =>
        {
            button = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            return button is not null && button.Current.IsEnabled && InboxFeedFullyVisible(run, button);
        }, $"visible enabled Inbox action: {name}");
        return button!;
    }

    private static async Task AssertInboxFeedCommandAsync(GatewayFixtureRun run, AutomationElement row, string expected)
    {
        var command = await InboxFeedTextAsync(run, row, expected);
        var metadata = await InboxFeedTextAsync(run, row, @"Gateway · C:\fixture");
        Assert.True(InboxFeedFullyVisible(run, metadata));
        Assert.True(InboxFeedFullyVisible(run, await InboxFeedButtonAsync(run, row, "Open chat")));
        foreach (var element in row.FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>())
        {
            Assert.DoesNotContain("fixture-secret", element.Current.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", element.Current.HelpText, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", element.Current.ItemStatus, StringComparison.Ordinal);
        }
        Assert.True(command.TryGetCurrentPattern(TextPattern.Pattern, out var pattern), "Complete command must expose native text, not just a collapsed preview.");
        if (expected.EndsWith("fixture-end", StringComparison.Ordinal))
        {
            var document = ((TextPattern)pattern).DocumentRange;
            var full = NormalizeInboxFeedDocument(document, expected);
            var tail = InboxFeedAsciiRange(full, expected, "fixture-end");
            var separator = InboxFeedAsciiRange(full, expected, " ; ");
            // Oracle controls use native ranges, not a clipped app or fabricated
            // UI success. Actual GetVisibleRanges coverage was required above.
            var prefix = full.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, tail, TextPatternRangeEndpoint.Start);
            Assert.False(InboxFeedRangesCover(full, [prefix]), "A visible prefix cannot prove a clipped final marker.");
            Assert.True(InboxFeedRangesCover(full, [tail, prefix]), "Adjacent ranges must cover the text even when returned in reverse order.");
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, separator, TextPatternRangeEndpoint.Start);
            var suffix = full.Clone();
            suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, separator, TextPatternRangeEndpoint.End);
            Assert.False(InboxFeedRangesCover(full, [suffix, prefix]), "Visible endpoints cannot hide a gap inside the disclosure.");
        }
    }

    private static async Task<AutomationElement> InboxFeedTextAsync(GatewayFixtureRun run, AutomationElement row, string expected)
    {
        Assert.False(string.IsNullOrEmpty(expected));
        AutomationElement? found = null;
        await WaitUiAsync(run, () =>
        {
            found = row.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.IsTextPatternAvailableProperty, true)).Cast<AutomationElement>().FirstOrDefault(element =>
            {
                var text = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                var document = text.DocumentRange;
                if (document.GetText(-1) != expected ||
                    !InboxFeedFullyVisible(run, element)) return false;
                var meaningful = NormalizeInboxFeedDocument(document, expected);
                if (!InboxFeedRangesCover(meaningful, text.GetVisibleRanges())) return false;
                var rectangles = meaningful.GetBoundingRectangles();
                var viewport = InboxFeedVisibleId(run, "WorkspaceNotificationsList")!.Current.BoundingRectangle;
                // Reject every invalid/zero-area rectangle. Filtering them could conceal
                // an unrendered part of the command and manufacture a passing subset.
                return InboxFeedFinitePositiveRectangle(viewport) && rectangles.Length > 0 &&
                    rectangles.All(rect => InboxFeedFinitePositiveRectangle(rect) && viewport.Contains(rect));
            });
            return found is not null;
        }, $"complete native visible Inbox text: {expected}");
        return found!;
    }

    private static TextPatternRange NormalizeInboxFeedDocument(TextPatternRange document, string expected)
    {
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.Equal(expected, document.GetText(-1));
        var target = document.Clone();
        AssertInboxFeedRangeBounds(target, document);
        const TextPatternRangeEndpoint first = TextPatternRangeEndpoint.Start;
        const TextPatternRangeEndpoint last = TextPatternRangeEndpoint.End;

        // WinUI document edges can include zero-content structural positions. A real
        // Character round trip reaches insertion boundaries; a zero move cannot do so.
        var before = target.Clone();
        Assert.Equal(1, target.MoveEndpointByUnit(first, TextUnit.Character, 1));
        Assert.Equal(0, target.CompareEndpoints(last, before, last));
        Assert.True(target.CompareEndpoints(first, before, first) > 0);
        AssertInboxFeedRangeBounds(target, document, allowDegenerate: true);
        Assert.Equal(-1, target.MoveEndpointByUnit(first, TextUnit.Character, -1));
        Assert.Equal(0, target.CompareEndpoints(last, before, last));
        Assert.True(target.CompareEndpoints(first, before, first) >= 0, "Start normalization must not move outward.");
        AssertInboxFeedRangeBounds(target, document);
        var discarded = before.Clone();
        discarded.MoveEndpointByRange(last, target, first);
        Assert.True(string.IsNullOrEmpty(discarded.GetText(-1)), "Start normalization must discard zero extracted content, not whitespace.");
        Assert.Equal(expected, target.GetText(-1));

        before = target.Clone();
        Assert.Equal(-1, target.MoveEndpointByUnit(last, TextUnit.Character, -1));
        Assert.Equal(0, target.CompareEndpoints(first, before, first));
        Assert.True(target.CompareEndpoints(last, before, last) < 0);
        AssertInboxFeedRangeBounds(target, document, allowDegenerate: true);
        Assert.Equal(1, target.MoveEndpointByUnit(last, TextUnit.Character, 1));
        Assert.Equal(0, target.CompareEndpoints(first, before, first));
        Assert.True(target.CompareEndpoints(last, before, last) <= 0, "End normalization must not move outward.");
        AssertInboxFeedRangeBounds(target, document);
        discarded = before.Clone();
        discarded.MoveEndpointByRange(first, target, last);
        Assert.True(string.IsNullOrEmpty(discarded.GetText(-1)), "End normalization must discard zero extracted content, not whitespace.");
        Assert.Equal(expected, target.GetText(-1));
        return target;
    }

    private static TextPatternRange InboxFeedAsciiRange(TextPatternRange full, string expected, string part)
    {
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.False(string.IsNullOrEmpty(part));
        Assert.All(expected, character => Assert.InRange(character, ' ', '~'));
        Assert.Equal(expected, full.GetText(-1));
        var start = expected.IndexOf(part, StringComparison.Ordinal);
        Assert.True(start >= 0, "The independent ASCII control text must occur in the expected disclosure.");
        Assert.Equal(start, expected.LastIndexOf(part, StringComparison.Ordinal));
        const TextPatternRangeEndpoint first = TextPatternRangeEndpoint.Start;
        const TextPatternRangeEndpoint last = TextPatternRangeEndpoint.End;
        // ASCII ordinals are used only for these fixed controls, never to normalize
        // document structure or infer character counts from native endpoint signs.
        var range = full.Clone();
        range.MoveEndpointByRange(last, full, first);
        Assert.Equal(start, range.MoveEndpointByUnit(first, TextUnit.Character, start));
        Assert.Equal(0, range.CompareEndpoints(first, range, last));
        AssertInboxFeedRangeBounds(range, full, allowDegenerate: true);
        var anchor = range.Clone();
        Assert.Equal(part.Length, range.MoveEndpointByUnit(last, TextUnit.Character, part.Length));
        Assert.Equal(0, range.CompareEndpoints(first, anchor, first));
        AssertInboxFeedRangeBounds(range, full);
        Assert.Equal(part, range.GetText(-1));
        return range;
    }

    private static void AssertInboxFeedRangeBounds(TextPatternRange range, TextPatternRange document, bool allowDegenerate = false)
    {
        const TextPatternRangeEndpoint first = TextPatternRangeEndpoint.Start;
        const TextPatternRangeEndpoint last = TextPatternRangeEndpoint.End;
        Assert.True(range.CompareEndpoints(first, document, first) >= 0, "Range starts outside its document.");
        Assert.True(range.CompareEndpoints(last, document, last) <= 0, "Range ends outside its document.");
        var order = range.CompareEndpoints(first, range, last);
        // A one-character document can collapse at the intermediate movement only.
        Assert.True(allowDegenerate ? order <= 0 : order < 0, "Range must remain ordered and the completed target nondegenerate.");
    }

    private static bool InboxFeedRangesCover(TextPatternRange target, TextPatternRange[] visible)
    {
        const TextPatternRangeEndpoint first = TextPatternRangeEndpoint.Start;
        const TextPatternRangeEndpoint last = TextPatternRangeEndpoint.End;
        if (target.CompareEndpoints(first, target, last) >= 0 || visible.Length == 0 ||
            visible.Any(range => range is null || range.CompareEndpoints(first, range, last) > 0)) return false;
        var ordered = visible.ToArray();
        Array.Sort(ordered, (left, right) => left.CompareEndpoints(first, right, first));
        var covered = target.Clone();
        covered.MoveEndpointByRange(last, target, first);
        // WinUI can expose separate visual-line ranges. Extend only through overlapping
        // or adjacent ranges; extrema alone would accept a hidden interior or final line.
        foreach (var range in ordered)
        {
            if (range.CompareEndpoints(last, covered, last) <= 0) continue;
            if (range.CompareEndpoints(first, covered, last) > 0) return false;
            covered.MoveEndpointByRange(last, range, last);
            if (covered.CompareEndpoints(last, target, last) >= 0) return true;
        }
        return false;
    }

    private static bool InboxFeedFinitePositiveRectangle(System.Windows.Rect rect) =>
        !rect.IsEmpty && rect.Width > 0 && rect.Height > 0 &&
        double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    private static Task InboxFeedLabelAsync(GatewayFixtureRun run, AutomationElement row, string expected) =>
        WaitUiAsync(run, () => row.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, expected))).Cast<AutomationElement>()
            .Any(element => InboxFeedFullyVisible(run, element)), $"visible Inbox label: {expected}");

    private static bool InboxFeedChatContains(GatewayFixtureRun run, string expected)
    {
        var timeline = FindTimeline(run);
        return timeline is not null && timeline.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)).Cast<AutomationElement>().Any(element =>
        {
            var text = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
            var bounds = element.Current.BoundingRectangle;
            return text.TrimEnd('\r', '\n') == expected && !element.Current.IsOffscreen &&
                bounds.Width > 0 && bounds.Height > 0 && timeline.Current.BoundingRectangle.Contains(bounds);
        });
    }

    private static bool InboxFeedFullyVisible(GatewayFixtureRun run, AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        var list = InboxFeedVisibleId(run, "WorkspaceNotificationsList");
        return !element.Current.IsOffscreen && list is not null && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
            list.Current.BoundingRectangle.Contains(bounds);
    }

    private static int InboxFeedCountdown(AutomationElement row)
    {
        var text = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().FirstOrDefault(element => !element.Current.IsOffscreen &&
                element.Current.Name.StartsWith("Expires in ", StringComparison.Ordinal))?.Current.Name;
        var parts = text?["Expires in ".Length..].Split(':');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds) &&
            minutes >= 0 && seconds is >= 0 and < 60 ? minutes * 60 + seconds : -1;
    }

    private static void CaptureInboxFeed(GatewayFixtureRun run, string file, AutomationElement asserted)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            OwnedWindowCapture.Save(run, AppWindows(run).OrderByDescending(window =>
                window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height).First(), file, asserted);
    }
}
