// Mounted Inbox consent proof, through canonical plugin events and real UIA actions.
// Run GatewayFixtureUiTests.InboxPluginReview_* with the existing fixture app/desktop opt-in.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using ComUia = global::Interop.UIAutomationClient;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string InboxDenyOnlyNote = "This approval can't be shown in full, so only Deny is available.";
    private const string InboxTitleSuffix = "TITLE REVIEW END";
    private const string InboxCriticalSuffix = "CRITICAL END: delete archived invoices.";

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxPluginReview_PageAndFlyoutExposeFullDisclosureBeforeAllow()
    {
        await WithAppAsync(async run =>
        {
            var title = "Fixture plugin export: " + string.Join(" ",
                Enumerable.Repeat("review every selected invoice", 8)) + " " + InboxTitleSuffix;
            var detail = "<review>Literal plugin operation, not executable markup.</review>\n" +
                string.Join("\n", Enumerable.Range(1, 12).Select(index =>
                    $"Line {index:00}: export the fixture invoice summary after review.")) +
                "\n" + InboxCriticalSuffix;
            var expectedDisclosure = title + "\n\n" + detail;

            foreach (var context in new[] { "page", "flyout" })
            {
                var id = $"fixture-inbox-plugin-{context}";
                await OpenInboxReviewAsync(run, flyout: context == "flyout");
                await run.Gateway.PublishPluginApprovalRequestedAsync(id, title, detail,
                    "This synthetic request changes only fixture state.");
                await ExpandInboxReviewAsync(run, title);

                // Read the expanded selectable TextBlock, not the ellipsized header, row model,
                // tooltip or MCP snapshot. Equality protects every title/detail character.
                await WaitUiAsync(run, () => FindInboxDisclosure(run, expectedDisclosure) is not null,
                    $"complete mounted {context} plugin disclosure");
                await RevealInboxTextRangeAsync(run, expectedDisclosure, InboxTitleSuffix);
                await CaptureIfRequestedAsync(run, $"inbox-plugin-{context}-full-title.png");
                await RevealInboxTextRangeAsync(run, expectedDisclosure, InboxCriticalSuffix);
                await CaptureIfRequestedAsync(run, $"inbox-plugin-{context}-critical-suffix.png");

                await RevealInboxDecisionAsync(run, "InboxApprovalAllowOnce");
                Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                Invoke(FindInboxReviewAction(run, "InboxApprovalAllowOnce")!);
                await AssertInboxDecisionAsync(run, id, "allow-once", title);
            }

            Assert.Equal(2, run.Gateway.Requests.Count(request => request.Method == "plugin.approval.resolve"));
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "exec.approval.resolve");
        }, scenario: GatewayScenario.CreateApprovalReview());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxPluginReview_TruncatedOrConcealedDetailAndWarningOfferOnlyDeny()
    {
        await WithAppAsync(async run =>
        {
            await OpenInboxReviewAsync(run, flyout: false);
            var truncated = new string('x', 16 * 1024 + 1);
            var cases = new[]
            {
                (Name: "truncated-detail", Detail: truncated, Warning: (string?)null, Concealed: (string?)null),
                (Name: "truncated-warning", Detail: "Review this fixture operation.", Warning: (string?)truncated, Concealed: (string?)null),
                (Name: "concealed-detail", Detail: "Authorization: Bearer synthetic-hidden-detail", Warning: (string?)null, Concealed: (string?)"synthetic-hidden-detail"),
                (Name: "concealed-warning", Detail: "Review this fixture operation.", Warning: (string?)"Authorization: Bearer synthetic-hidden-warning", Concealed: (string?)"synthetic-hidden-warning"),
            };
            foreach (var item in cases)
            {
                var id = $"fixture-inbox-plugin-{item.Name}";
                var title = $"Fixture plugin {item.Name}";
                // The wire request offers Allow once. Only the real product disclosure gate
                // may remove it; the fixture does not calculate reviewability for the app.
                await run.Gateway.PublishPluginApprovalRequestedAsync(id, title, item.Detail, item.Warning);
                await ExpandInboxReviewAsync(run, title);
                await WaitUiAsync(run, () => InboxReviewTextElements(run).Any(text =>
                    ReadInboxReviewText(text) == InboxDenyOnlyNote), "mounted deny-only explanation");
                Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowOnce"));
                Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                var rendered = string.Join("\n", InboxReviewTextElements(run).Select(ReadInboxReviewText));
                if (item.Concealed is { } concealed)
                    Assert.DoesNotContain(concealed, rendered);
                else
                    Assert.Contains("…[truncated]", rendered);

                await RevealInboxDecisionAsync(run, "InboxApprovalDeny");
                Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowOnce"));
                Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                await CaptureIfRequestedAsync(run, $"inbox-plugin-{item.Name}-deny-only.png");
                Invoke(FindInboxReviewAction(run, "InboxApprovalDeny")!);
                await AssertInboxDecisionAsync(run, id, "deny", title);
            }
            var decisions = run.Gateway.Requests.Where(request => request.Method == "plugin.approval.resolve").ToArray();
            Assert.Equal(4, decisions.Length);
            Assert.All(decisions, request => Assert.Equal("deny", request.Decision));
        }, scenario: GatewayScenario.CreateApprovalReview());
    }

    private static async Task OpenInboxReviewAsync(GatewayFixtureRun run, bool flyout)
    {
        if (flyout)
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false }, "chat before bell");
            Invoke(await WaitForElementByIdAsync(run, "WorkspaceNotifications", "Workspace bell"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNotificationsOpenPage") is { Current.IsOffscreen: false }, "Inbox bell flyout");
        }
        else
        {
            await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is { Current.IsOffscreen: false }, "full Inbox page");
        }
        // An empty Inbox collapses its list. Wait for the list only after publication.
    }

    private static AutomationElement? FindInboxReviewList(GatewayFixtureRun run) =>
        AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNotificationsList")).Cast<AutomationElement>())
            .SingleOrDefault(element => !element.Current.IsOffscreen);

    private static AutomationElement? FindInboxReviewExpander(GatewayFixtureRun run, string title) =>
        FindInboxReviewList(run)?.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true))
            .Cast<AutomationElement>().FirstOrDefault(element => element.Current.Name.Contains(title, StringComparison.Ordinal));

    private static async Task ExpandInboxReviewAsync(GatewayFixtureRun run, string title)
    {
        AutomationElement? expander = null;
        await WaitUiAsync(run, () => (expander = FindInboxReviewExpander(run, title)) is not null, "canonical plugin event in Inbox");
        ((ExpandCollapsePattern)expander!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUiAsync(run, () => FindInboxReviewAction(run, "InboxApprovalDeny") is not null, "expanded Inbox decision controls");
    }

    private static IEnumerable<AutomationElement> InboxReviewTextElements(GatewayFixtureRun run) =>
        FindInboxReviewList(run)?.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>() ?? [];

    private static string ReadInboxReviewText(AutomationElement element) =>
        (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
            ? ((TextPattern)pattern).DocumentRange.GetText(-1) : element.Current.Name)
        .Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    private static AutomationElement? FindInboxDisclosure(GatewayFixtureRun run, string expected) =>
        InboxReviewTextElements(run).SingleOrDefault(element => ReadInboxReviewText(element) == expected);

    private const int InboxAutomationNodeLimit = 4096;

    private static Task RevealInboxTextRangeAsync(GatewayFixtureRun run, string disclosure, string suffix)
    {
        var text = FindInboxDisclosure(run, disclosure)!;
        return RevealInboxTextRangeAsync(run, BindNativeInboxText(run, text.GetRuntimeId()), disclosure, suffix);
    }

    private static async Task RevealInboxTextRangeAsync(GatewayFixtureRun run,
        (ComUia.IUIAutomation Client, ComUia.IUIAutomationElement Element, ComUia.IUIAutomationTextPattern Pattern) binding,
        string disclosure, string suffix)
    {
        var textPattern = binding.Pattern;
        Assert.NotEqual(ComUia.SupportedTextSelection.SupportedTextSelection_None, textPattern.SupportedTextSelection);
        var document = textPattern.DocumentRange;
        var documentText = document.GetText(-1);
        Assert.Equal(disclosure, documentText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n'));
        Assert.False(string.IsNullOrEmpty(suffix));
        var start = documentText.IndexOf(suffix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        const ComUia.TextPatternRangeEndpoint first = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
        const ComUia.TextPatternRangeEndpoint last = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;
        // WinUI does not implement FindText. Keep checked Character movement for these
        // fixture strings, and verify exact text rather than assuming UTF-16/provider units agree.
        var range = document.Clone();
        range.MoveEndpointByRange(last, document, first);
        Assert.Equal(start, range.MoveEndpointByUnit(first, ComUia.TextUnit.TextUnit_Character, start));
        Assert.Equal(suffix.Length, range.MoveEndpointByUnit(last, ComUia.TextUnit.TextUnit_Character, suffix.Length));
        Assert.Equal(suffix, range.GetText(-1));
        if (start == 0)
        {
            // Zero movement leaves reserved document positions intact. A checked native
            // Character round trip may discard only a strictly zero-content outer prefix.
            var before = range.Clone();
            Assert.Equal(1, range.MoveEndpointByUnit(first, ComUia.TextUnit.TextUnit_Character, 1));
            Assert.Equal(-1, range.MoveEndpointByUnit(first, ComUia.TextUnit.TextUnit_Character, -1));
            Assert.True(range.CompareEndpoints(first, before, first) >= 0);
            Assert.Equal(0, range.CompareEndpoints(last, before, last));
            var discarded = before.Clone();
            discarded.MoveEndpointByRange(last, range, first);
            Assert.True(string.IsNullOrEmpty(discarded.GetText(-1)), "Normalization must not discard any text, including whitespace.");
            Assert.Equal(suffix, range.GetText(-1));
        }
        Assert.True(range.CompareEndpoints(first, document, first) >= 0);
        Assert.True(range.CompareEndpoints(last, document, last) <= 0);
        Assert.True(range.CompareEndpoints(first, range, last) < 0);
        range.Select();
        await WaitUiAsync(run, () =>
        {
            var selections = textPattern.GetSelection();
            if (selections is null) return false;
            for (var i = 0; i < selections.Length; i++)
            {
                var selected = selections.GetElement(i);
                if (selected is not null && selected.CompareEndpoints(first, range, first) == 0 &&
                    selected.CompareEndpoints(last, range, last) == 0)
                    return true;
            }
            return false;
        }, $"selected expanded approval text: {suffix}");
        range.ScrollIntoView(0);
        try
        {
            await WaitUiAsync(run, () => InboxRangeIsVisible(run, textPattern, range), $"reachable expanded approval text: {suffix}");
        }
        catch (TimeoutException failure)
        {
            try { RecordInboxVisibilityFailure(run, textPattern, range); }
            catch (Exception diagnosticFailure)
            {
                throw new AggregateException("Native visibility and its diagnostic capture both failed.", failure, diagnosticFailure);
            }
            throw;
        }
        GC.KeepAlive(binding); // Retain the COM client and owning element across the asynchronous checks.
    }

    private static (ComUia.IUIAutomation Client, ComUia.IUIAutomationElement Element, ComUia.IUIAutomationTextPattern Pattern)
        BindNativeInboxText(GatewayFixtureRun run, int[] runtimeId)
    {
        // The WPF range wrapper failed on returned selections on the proof host. Bind the same
        // mounted element through UIA3 COM, not a replacement control or weaker oracle.
        var client = (ComUia.IUIAutomation)new ComUia.CUIAutomation8();
        var walker = client.RawViewWalker;
        var visited = 0;
        foreach (var window in AppWindows(run))
        {
            var stack = new Stack<ComUia.IUIAutomationElement>();
            stack.Push(client.ElementFromHandle(new IntPtr(window.Current.NativeWindowHandle)));
            while (stack.TryPop(out var element))
            {
                if (element.CurrentProcessId != run.AppProcessId) continue;
                if (element.GetRuntimeId().SequenceEqual(runtimeId))
                {
                    var cache = client.CreateCacheRequest();
                    cache.AddPattern(TextPattern.Pattern.Id);
                    var cached = element.BuildUpdatedCache(cache);
                    return (client, cached, (ComUia.IUIAutomationTextPattern)cached.GetCachedPattern(TextPattern.Pattern.Id));
                }
                for (var child = walker.GetFirstChildElement(element); child is not null; child = walker.GetNextSiblingElement(child))
                {
                    if (++visited > InboxAutomationNodeLimit)
                        throw new InvalidOperationException("The owned Inbox automation tree exceeded its traversal limit.");
                    stack.Push(child);
                }
            }
        }
        throw new InvalidOperationException("Native UIA could not locate the mounted Inbox disclosure.");
    }

    private static bool InboxRangeIsVisible(GatewayFixtureRun run, ComUia.IUIAutomationTextPattern text, ComUia.IUIAutomationTextRange range)
    {
        // Complete document text alone is not visible proof. Require full native range containment
        // and positive rectangles entirely inside the visible Inbox list viewport.
        const ComUia.TextPatternRangeEndpoint first = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
        const ComUia.TextPatternRangeEndpoint last = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;
        var visibleRanges = text.GetVisibleRanges();
        if (visibleRanges is null || visibleRanges.Length == 0 || range.CompareEndpoints(first, range, last) >= 0)
            return false;
        var ordered = new List<ComUia.IUIAutomationTextRange>();
        for (var i = 0; i < visibleRanges.Length; i++)
        {
            var visible = visibleRanges.GetElement(i);
            if (visible is null) return false;
            ordered.Add(visible);
        }
        // WinUI can return one range per wrapped text rectangle. Require a gap-free union,
        // not one enclosing span or extrema that could hide an invisible interior section.
        ordered.Sort((left, right) => left.CompareEndpoints(first, right, first));
        var covered = range.Clone();
        covered.MoveEndpointByRange(last, range, first);
        var contained = false;
        foreach (var visible in ordered)
        {
            if (visible.CompareEndpoints(last, covered, last) <= 0) continue;
            if (visible.CompareEndpoints(first, covered, last) > 0) return false;
            covered.MoveEndpointByRange(last, visible, last);
            if (covered.CompareEndpoints(last, range, last) >= 0)
            {
                contained = true;
                break;
            }
        }
        if (!contained) return false;
        var viewport = FindInboxReviewList(run)!.Current.BoundingRectangle;
        var rectangles = range.GetBoundingRectangles();
        const int coordinatesPerRectangle = 4;
        if (rectangles is null || rectangles.Length == 0 || rectangles.Length % coordinatesPerRectangle != 0 || viewport.IsEmpty ||
            !double.IsFinite(viewport.Left) || !double.IsFinite(viewport.Top) ||
            !double.IsFinite(viewport.Right) || !double.IsFinite(viewport.Bottom) ||
            rectangles.Any(coordinate => !double.IsFinite(coordinate))) return false;
        for (var i = 0; i < rectangles.Length; i += coordinatesPerRectangle)
            if (rectangles[i + 2] <= 0 || rectangles[i + 3] <= 0 ||
                rectangles[i] < viewport.Left - 1 || rectangles[i + 1] < viewport.Top - 1 ||
                rectangles[i] + rectangles[i + 2] > viewport.Right + 1 ||
                rectangles[i + 1] + rectangles[i + 3] > viewport.Bottom + 1)
                return false;
        return true;
    }

    private const int InboxVisibilityDiagnosticLimit = 64;

    private static void RecordInboxVisibilityFailure(GatewayFixtureRun run, ComUia.IUIAutomationTextPattern text,
        ComUia.IUIAutomationTextRange target)
    {
        const ComUia.TextPatternRangeEndpoint first = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
        const ComUia.TextPatternRangeEndpoint last = ComUia.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;
        var visible = text.GetVisibleRanges();
        var ordered = new List<ComUia.IUIAutomationTextRange>();
        for (var i = 0; visible is not null && i < visible.Length; i++)
            if (visible.GetElement(i) is { } item) ordered.Add(item);
        ordered.Sort((left, right) => left.CompareEndpoints(first, right, first));
        var comparisons = new List<object>();
        for (var i = 0; i < Math.Min(ordered.Count, InboxVisibilityDiagnosticLimit); i++)
        {
            var item = ordered[i];
            comparisons.Add(new
            {
                index = i,
                startVsPreviousEnd = i == 0 ? (int?)null : item.CompareEndpoints(first, ordered[i - 1], last),
                startVsTargetStart = item.CompareEndpoints(first, target, first),
                endVsTargetStart = item.CompareEndpoints(last, target, first),
                startVsTargetEnd = item.CompareEndpoints(first, target, last),
                endVsTargetEnd = item.CompareEndpoints(last, target, last),
            });
        }
        var rectangles = target.GetBoundingRectangles();
        var viewport = FindInboxReviewList(run)!.Current.BoundingRectangle;
        // Record geometry and comparisons once on failure, never raw approval or credential text.
        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "inbox-range-visibility.json"),
            JsonSerializer.Serialize(new
            {
                observedAfterTimeoutUtc = DateTimeOffset.UtcNow,
                targetLength = target.GetText(-1).Length, documentLength = text.DocumentRange.GetText(-1).Length,
                targetEndVsDocumentEnd = target.CompareEndpoints(last, text.DocumentRange, last),
                visibleRangeCount = visible?.Length ?? 0, nullRangeCount = (visible?.Length ?? 0) - ordered.Count, comparisons,
                rangesTruncated = visible is not null && visible.Length > InboxVisibilityDiagnosticLimit,
                rectangleCoordinateCount = rectangles?.Length ?? 0,
                rectangleCoordinates = rectangles?.Take(InboxVisibilityDiagnosticLimit * 4).ToArray(),
                rectanglesTruncated = rectangles is not null && rectangles.Length > InboxVisibilityDiagnosticLimit * 4,
                viewport = new { viewport.Left, viewport.Top, viewport.Right, viewport.Bottom },
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    private static AutomationElement? FindInboxReviewAction(GatewayFixtureRun run, string id) =>
        FindInboxReviewList(run)?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static async Task RevealInboxDecisionAsync(GatewayFixtureRun run, string id)
    {
        var list = FindInboxReviewList(run)!;
        if (!list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
        {
            var scroller = list.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            if (scroller is not null)
                pattern = scroller.GetCurrentPattern(ScrollPattern.Pattern);
        }
        if (pattern is ScrollPattern scroll && scroll.Current.VerticallyScrollable)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        await WaitUiAsync(run, () => FindInboxReviewAction(run, id) is { Current.IsOffscreen: false, Current.IsEnabled: true },
            "visible enabled Inbox decision button");
    }

    private static async Task AssertInboxDecisionAsync(GatewayFixtureRun run, string id, string decision, string title)
    {
        await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
            request.Method == "plugin.approval.resolve" && request.ApprovalId == id && request.Outcome == "ok")),
            "actual Inbox plugin decision RPC");
        var request = Assert.Single(run.Gateway.Requests, request => request.Method == "plugin.approval.resolve" && request.ApprovalId == id);
        Assert.Equal(decision, request.Decision);
        Assert.Equal("ok", request.Outcome);
        await WaitUiAsync(run, () => FindInboxReviewExpander(run, title) is null, "decided Inbox approval removed");
    }
}
