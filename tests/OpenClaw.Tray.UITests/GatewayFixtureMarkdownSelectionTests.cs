// Shipped-app acceptance coverage for Markdown selection in the production chat timeline.
// Run with dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj
// --filter FullyQualifiedName~MarkdownSelection_.
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using Windows.System;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string MarkdownSelectionOpening = "FIXTURE SELECTION opening paragraph.";
    private const string MarkdownSelectionSecondParagraph = "FIXTURE SELECTION second paragraph.";
    private const string MarkdownSelectionListFirst = "FIXTURE SELECTION list item one.";
    private const string MarkdownSelectionListSecond = "FIXTURE SELECTION list item two.";
    private const string MarkdownSelectionCodeFirst = "const selectionProof = \"fixture\";";
    private const string MarkdownSelectionCodeSecond = "console.log(selectionProof);";
    private const string MarkdownSelectionTableHeader = "Fixture selection table";
    private const string MarkdownSelectionTableCell = "FIXTURE SELECTION table cell.";
    private const string MarkdownSelectionSummary = "FIXTURE SELECTION collapsed summary.";
    private const string MarkdownSelectionCollapsedBody = "FIXTURE SELECTION hidden collapsed body.";
    private const string MarkdownSelectionBeforeMessage =
        "FIXTURE SELECTION BEFORE: a separate message must not be copied.";
    private const int MarkdownSelectionFillerCount = 18;
    private const uint UnicodeTextClipboardFormat = 13;
    private static readonly TimeSpan ClipboardNoSelectionObservationWindow = TimeSpan.FromSeconds(1);
    private static readonly string ExpectedMarkdownSelectionClipboard = string.Join("\n",
    [
        "FIXTURE SELECTION opening paragraph.",
        "FIXTURE SELECTION second paragraph.",
        "\u2022 FIXTURE SELECTION list item one.",
        "\u2022 FIXTURE SELECTION list item two.",
        "const selectionProof = \"fixture\";",
        "console.log(selectionProof);",
        "FIXTURE SELECTION scroll filler 01: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 02: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 03: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 04: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 05: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 06: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 07: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 08: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 09: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 10: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 11: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 12: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 13: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 14: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 15: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 16: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 17: keep native timeline scrolling in the path.",
        "FIXTURE SELECTION scroll filler 18: keep native timeline scrolling in the path.",
        "Fixture selection table\tState",
        "FIXTURE SELECTION table cell.\tvisible",
        "FIXTURE SELECTION collapsed summary."
    ]);

    private static readonly string MarkdownSelectionMessage = string.Join("\n\n",
    [
        MarkdownSelectionOpening,
        MarkdownSelectionSecondParagraph,
        $"- {MarkdownSelectionListFirst}\n- {MarkdownSelectionListSecond}",
        $"```csharp\n{MarkdownSelectionCodeFirst}\n{MarkdownSelectionCodeSecond}\n```",
        .. Enumerable.Range(1, MarkdownSelectionFillerCount)
            .Select(index => $"FIXTURE SELECTION scroll filler {index:D2}: keep native timeline scrolling in the path."),
        $"| {MarkdownSelectionTableHeader} | State |\n| --- | --- |\n| {MarkdownSelectionTableCell} | visible |",
        $"<details>\n<summary>{MarkdownSelectionSummary}</summary>\n\n{MarkdownSelectionCollapsedBody}\n</details>"
    ]);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("KeyboardProof", "Required")]
    public async Task MarkdownSelection_CopiesAcrossBlocksAndExcludesCollapsedAndNeighboringContent()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.MarkdownSelectionTitle, GatewayScenario.MarkdownSelectionSessionKey);
            await WaitUiAsync(run, () => FindSelectionText(run, MarkdownSelectionOpening) is not null,
                "synthetic Markdown selection history rendered");
            LogRuntimeIdentity(run, output.WriteLine);

            var window = FindHub(run);
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            var timeline = FindTimeline(run)
                ?? throw new InvalidOperationException("The production chat ItemsView was not mounted.");
            var (scrollElement, timelineScroll) = FindTimelineScroll(timeline);
            var scrollBounds = scrollElement.Current.BoundingRectangle;
            output.WriteLine($"Timeline scroll owner {scrollElement.Current.ControlType.ProgrammaticName} " +
                $"'{scrollElement.Current.Name}' at {scrollBounds}; initial vertical position " +
                $"{timelineScroll.Current.VerticalScrollPercent}% / {timelineScroll.Current.VerticalViewSize}% view.");

            ClearClipboard();
            await DragSelectionToAsync(run, window, timeline, scrollElement, timelineScroll, output.WriteLine,
                MarkdownSelectionOpening, MarkdownSelectionTableCell);
            var tableCell = FindSelectionText(run, MarkdownSelectionTableCell)
                ?? throw new InvalidOperationException("The selected Markdown table cell disappeared before copy.");
            var selectionHost = FindMarkdownSelectionHost(tableCell);
            var selectedOverlayText = selectionHost is null ? null : ReadMarkdownSelectionText(selectionHost);
            output.WriteLine($"Pre-copy selection overlay text: " +
                $"'{EscapeForLog(selectedOverlayText ?? "<legacy provider unavailable>")}'.");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, window, "markdown-selection-table-cell-overlay.png",
                    tableCell, selectionHost ?? tableCell);
            NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
            var blocksCopied = NormalizeClipboardText(await WaitForClipboardTextAsync(run));
            AssertClipboardGolden(ExpectedSelectionThroughTableCell(), blocksCopied,
                "Selection through first table cell", selectedOverlayText);
            Assert.DoesNotContain("FIXTURE SELECTION BEFORE", blocksCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION AFTER", blocksCopied, StringComparison.Ordinal);

            var summary = await WaitForSelectionExpanderAsync(run);
            var expander = (ExpandCollapsePattern)summary.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            Assert.Equal(ExpandCollapseState.Collapsed, expander.Current.ExpandCollapseState);
            var disclosureRange = await WaitForRangeScopedDisclosureAsync(run, timeline);
            Assert.Equal(MarkdownSelectionSummary, NormalizeClipboardText(disclosureRange.RangeText));
            Assert.DoesNotContain(MarkdownSelectionCollapsedBody, disclosureRange.RangeText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION BEFORE", disclosureRange.RangeText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION AFTER", disclosureRange.RangeText, StringComparison.Ordinal);
            Assert.Single(disclosureRange.Children);
            Assert.True(Automation.Compare(summary, disclosureRange.Expander),
                "The Markdown TextPattern selection range must expose its native disclosure Expander through GetChildren().");
            timelineScroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            await WaitUiAsync(run, () => IsSelectionTextVisible(timeline,
                    FindSelectionText(run, MarkdownSelectionSummary)),
                "disclosure summary is fully visible before expansion");
            var collapsedBody = FindSelectionText(run, MarkdownSelectionCollapsedBody);
            Assert.False(IsSelectionTextVisible(timeline, collapsedBody),
                "Collapsed details content must not be visible in the production timeline.");
            expander.Expand();
            AutomationElement? expandedBody = null;
            await WaitUiAsync(run, () => IsSelectionTextVisible(timeline,
                    expandedBody = FindSelectionText(run, MarkdownSelectionCollapsedBody)),
                "native details disclosure expands its Markdown body");
            Assert.True(IsSelectionTextVisible(timeline, FindSelectionText(run, MarkdownSelectionCollapsedBody)),
                "Expanded details content must be visible in the production timeline.");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, window, "markdown-selection-expanded.png", summary, expandedBody!);
            expander.Collapse();
            await WaitUiAsync(run, () => !IsSelectionTextVisible(timeline,
                    FindSelectionText(run, MarkdownSelectionCollapsedBody)),
                "native details disclosure collapses its Markdown body");
            Assert.False(IsSelectionTextVisible(timeline, FindSelectionText(run, MarkdownSelectionCollapsedBody)),
                "Collapsed details content must not remain visible in the production timeline.");

            ClearClipboard();
            await DragSelectionToAsync(run, window, timeline, scrollElement, timelineScroll, output.WriteLine,
                MarkdownSelectionOpening, MarkdownSelectionSummary);
            NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
            var fullVisibleMessageCopied = NormalizeClipboardText(await WaitForClipboardTextAsync(run));
            AssertClipboardGolden(ExpectedSelectionThroughSummary(), fullVisibleMessageCopied,
                "Selection through disclosure summary");
            Assert.DoesNotContain(MarkdownSelectionCollapsedBody, fullVisibleMessageCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION BEFORE", fullVisibleMessageCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION AFTER", fullVisibleMessageCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("Copy code", fullVisibleMessageCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("csharp", fullVisibleMessageCopied, StringComparison.Ordinal);

            var codeToolbar = timeline.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>()
                .SingleOrDefault(element => element.Current.AutomationId.StartsWith("ChatCopy_code_",
                    StringComparison.Ordinal));
            Assert.NotNull(codeToolbar);

            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => FindSelectionText(run, GatewayScenario.MainSentinel) is
                { Current.IsOffscreen: false }, "separate session history rendered");
            ClearClipboard();
            await DragSelectionBetweenTextAsync(run, window,
                GatewayScenario.MainSentinel, GatewayScenario.MainSentinel);
            NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
            Assert.Equal(GatewayScenario.MainSentinel,
                NormalizeClipboardText(await WaitForClipboardTextAsync(run)));

            await SelectSessionAsync(run, GatewayScenario.MarkdownSelectionTitle,
                GatewayScenario.MarkdownSelectionSessionKey);
            AutomationElement? restoredAnchor = null;
            await WaitUiAsync(run, () => (restoredAnchor = FindSelectionText(run, MarkdownSelectionOpening)) is not null,
                "Markdown session restored after switching");
            var restoredSelectionHost = FindMarkdownSelectionHost(restoredAnchor!);
            Assert.NotNull(restoredSelectionHost);
            restoredSelectionHost.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(AutomationElement.FocusedElement, restoredSelectionHost),
                "restored Markdown message owns keyboard copy");
            var restoredSelection = ReadMarkdownSelectionText(restoredSelectionHost);
            // Returning to the same message may retain its selection, never another thread's text.
            Assert.True(restoredSelection.Length == 0 || restoredSelection == ExpectedSelectionThroughSummary(),
                $"Restored selection must be empty or belong to this message, not another conversation. " +
                $"Actual: '{EscapeForLog(restoredSelection)}'.");
            ClearClipboard();
            NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
            string restoredCopy;
            if (restoredSelection.Length == 0)
            {
                // test-timing-exception: negative probe that an empty selection leaves the clipboard empty
                await Task.Delay(ClipboardNoSelectionObservationWindow);
                restoredCopy = NormalizeClipboardText(ReadClipboardTextOrEmpty());
            }
            else
            {
                restoredCopy = NormalizeClipboardText(await WaitForClipboardTextAsync(run));
            }
            Assert.Equal(restoredSelection, restoredCopy);
            Assert.DoesNotContain(GatewayScenario.MainSentinel, restoredCopy, StringComparison.Ordinal);

            AutomationElement? keyboardTimeline = null;
            await WaitUiAsync(run, () => (keyboardTimeline = FindTimeline(run)) is not null,
                "mounted chat timeline after session restoration and copy feedback");
            var (_, keyboardScroll) = FindTimelineScroll(keyboardTimeline!);
            keyboardScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            AutomationElement? keyboardAnchor = null;
            AutomationElement? keyboardTableEnd = null;
            AutomationElement? keyboardEnd = null;
            await WaitUiAsync(run, () =>
                    (keyboardAnchor = FindSelectionText(keyboardTimeline, MarkdownSelectionOpening)) is not null
                    && (keyboardTableEnd = FindSelectionText(keyboardTimeline, MarkdownSelectionTableCell)) is not null
                    && (keyboardEnd = FindSelectionText(keyboardTimeline, MarkdownSelectionSummary)) is not null
                    && IsSelectionTextVisible(keyboardTimeline!, keyboardAnchor)
                    && keyboardTableEnd.Current.IsOffscreen && keyboardEnd.Current.IsOffscreen,
                "visible keyboard anchor with table and disclosure fully clipped from the timeline viewport");
            Assert.NotNull(keyboardAnchor);
            Assert.NotNull(keyboardTableEnd);
            Assert.NotNull(keyboardEnd);
            await NativeKeyboardProof.ActivateAsync(hwnd);
            keyboardAnchor = await WaitForStableSelectionTextAsync(run, MarkdownSelectionOpening,
                "keyboard anchor bounds stable before the caret click");
            // Sample the start offset only after the scroll reset has settled.
            var initialKeyboardScroll = keyboardScroll.Current.VerticalScrollPercent;
            using (var click = new NativeSelectionDrag(run, window, FirstGlyphLeftHalf(keyboardAnchor,
                       MarkdownSelectionOpening)))
            {
            }
            NativeKeyboardProof.Press(hwnd, VirtualKey.Down, shift: true);
            for (var attempt = 0; attempt < 8 && keyboardEnd.Current.IsOffscreen; attempt++)
            {
                var previousScroll = keyboardScroll.Current.VerticalScrollPercent;
                NativeKeyboardProof.Press(hwnd, VirtualKey.PageDown);
                await WaitUiAsync(run, () => keyboardScroll.Current.VerticalScrollPercent > previousScroll
                    || !keyboardEnd.Current.IsOffscreen,
                    "PageDown navigates the production timeline to the clipped Markdown endpoint");
            }
            Assert.True(keyboardScroll.Current.VerticalScrollPercent > initialKeyboardScroll,
                "Keyboard navigation must scroll the production timeline from its initial position.");
            Assert.False(keyboardTableEnd.Current.IsOffscreen,
                "Keyboard navigation must reach the fully clipped table block.");
            Assert.False(keyboardEnd.Current.IsOffscreen,
                "Keyboard navigation must reach the fully clipped disclosure summary.");
            NativeKeyboardProof.Press(hwnd, VirtualKey.A, control: true);
            NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
            var keyboardCopied = NormalizeClipboardText(await WaitForClipboardTextAsync(run));
            AssertClipboardGolden(ExpectedMarkdownSelectionClipboard, keyboardCopied,
                "Keyboard select-all");
            Assert.DoesNotContain(MarkdownSelectionCollapsedBody, keyboardCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("Copy code", keyboardCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION BEFORE", keyboardCopied, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE SELECTION AFTER", keyboardCopied, StringComparison.Ordinal);

            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            {
                AutomationElement? composer = null;
                await WaitUiAsync(run, () => (composer = FindById(run, "ChatComposerInput")) is not null,
                    "composer available for the copied screenshot");
                OwnedWindowCapture.Save(run, window, "markdown-selection-copied.png", summary, composer!);
            }
        }, scenario: GatewayScenario.CreateMarkdownSelection(MarkdownSelectionMessage));
    }

    private static async Task DragSelectionToAsync(
        GatewayFixtureRun run,
        AutomationElement window,
        AutomationElement timeline,
        AutomationElement scrollElement,
        ScrollPattern scroll,
        Action<string> writeLine,
        string startText,
        string endText)
    {
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        await WaitUiAsync(run, () => FindSelectionText(run, startText) is
            { Current.IsOffscreen: false }, "selection anchor visible at timeline start");
        var admissionText = startText[..Math.Min(startText.Length, 7)];
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        await NativeKeyboardProof.ActivateAsync(hwnd);

        var admissionAnchor = await WaitForStableSelectionTextAsync(run, MarkdownSelectionBeforeMessage,
            "independent selection-admission message is visible with stable bounds");
        await AssertInitialSelectionAdmissionAsync(run, window, hwnd, admissionAnchor,
            admissionText, writeLine);

        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        AutomationElement? end = null;
        await WaitUiAsync(run, () => (end = FindSelectionText(run, endText)) is not null,
            $"selection endpoint {endText} rendered");
        var anchor = await WaitForStableSelectionTextAsync(run, startText,
            "selection anchor bounds stable after admission probe and scroll reset");
        var selectionHost = FindMarkdownSelectionHost(anchor);
        var start = FirstGlyphLeftHalf(anchor, startText);
        var admissionPoint = LastGlyphRightHalf(anchor, admissionText);
        var firstGlyphBounds = GetGlyphBounds(anchor, startText, first: true);
        var admissionGlyphBounds = GetGlyphBounds(anchor, admissionText, first: false);
        var viewport = selectionHost?.Current.BoundingRectangle ?? timeline.Current.BoundingRectangle;
        viewport.Intersect(timeline.Current.BoundingRectangle);
        viewport.Intersect(scrollElement.Current.BoundingRectangle);
        Assert.False(viewport.IsEmpty, "The selection surface must intersect the production ScrollView viewport.");
        var dpi = GetDpiForWindow(hwnd);
        Assert.True(dpi > 0, "The fixture window must expose its DPI for scale-aware edge input.");
        // UIA reports the timeline without its bottom fade band, but the ScrollView (and Reactor's
        // drag viewport) extends under that band. Cross the whole band, as a drag onto the composer would.
        var outsideMargin = (int)Math.Ceiling((OpenClawTray.Chat.ChatVisuals.TranscriptFadeHeight + 8d) * dpi / 96d);
        var edge = new System.Drawing.Point((int)(viewport.Left + viewport.Width / 2),
            (int)Math.Ceiling(viewport.Bottom) + outsideMargin);
        var windowBounds = window.Current.BoundingRectangle;
        Assert.True(Contains(anchor.Current.BoundingRectangle, start),
            $"The drag anchor {start} must be inside the first glyph's automation surface {anchor.Current.BoundingRectangle}.");
        Assert.True(Contains(firstGlyphBounds, start),
            $"The drag anchor {start} must be inside the first glyph {firstGlyphBounds}.");
        Assert.True(Contains(anchor.Current.BoundingRectangle, admissionPoint),
            $"The admission point {admissionPoint} must be inside the anchor automation surface {anchor.Current.BoundingRectangle}.");
        Assert.True(Contains(admissionGlyphBounds, admissionPoint),
            $"The admission point {admissionPoint} must be inside the last admission glyph {admissionGlyphBounds}.");
        Assert.True(Contains(windowBounds, edge),
            $"The autoscroll point {edge} must remain inside the fixture window {windowBounds}.");
        Assert.False(Contains(viewport, edge),
            $"The autoscroll point {edge} must cross the visible selection-host viewport {viewport}.");
        writeLine($"Selection drag geometry after probe: host '{selectionHost?.Current.ClassName ?? "legacy text leaf"}' " +
            $"bounds {selectionHost?.Current.BoundingRectangle.ToString() ?? "unavailable"}; first glyph {firstGlyphBounds}; " +
            $"mouse-down {start}; in-host admission move {admissionPoint}; viewport {viewport}; " +
            $"ScrollPattern owner {scrollElement.Current.BoundingRectangle}; DPI {dpi}; " +
            $"outside-viewport point {edge}; fixture window {windowBounds}.");

        using var drag = new NativeSelectionDrag(run, window, start);
        Assert.True(drag.IsButtonDown, "The native left button must remain down for the selection drag.");
        if (selectionHost is not null)
            await WaitForCollapsedFocusedSelectionAsync(run, selectionHost,
                "production drag press focuses the Markdown selection Document with a collapsed range");
        drag.MoveTo(admissionPoint);
        if (selectionHost is not null)
            await WaitForSelectionPrefixAsync(run, selectionHost, admissionText, writeLine,
                "production in-viewport move admits a non-empty opening-text selection");
        else
            writeLine("Baseline message has no Markdown selection Document; independent prefix-copy probe verified native drag admission.");
        for (var attempt = 0; attempt < 30 && !IsSelectionTextVisible(timeline, end!); attempt++)
        {
            var before = scroll.Current.VerticalScrollPercent;
            drag.MoveTo(edge);
            Assert.True(drag.IsButtonDown, "The native left button must remain down during edge autoscroll.");
            await run.WaitForAsync(() =>
            {
                try
                {
                    var currentOffset = scroll.Current.VerticalScrollPercent;
                    var selectionText = selectionHost is null
                        ? null
                        : ReadMarkdownSelectionText(selectionHost);
                    var selectionProgress = selectionText is null
                        ? "<legacy-provider-unavailable>"
                        : $"length={selectionText.Length}, end='{SelectionTailForLog(selectionText)}'";
                    writeLine($"Edge poll toward '{endText}': selected {selectionProgress}; " +
                        $"vertical offset={currentOffset}% (before {before}%).");
                    return Task.FromResult(currentOffset > before || IsSelectionTextVisible(timeline, end));
                }
                catch (ElementNotAvailableException) { return Task.FromResult(false); }
            }, $"production timeline edge-scrolls toward {endText}");
        }

        Assert.True(IsSelectionTextVisible(timeline, end!),
            $"Edge scrolling did not fully reveal selection endpoint {endText}; " +
            $"endpoint bounds {end!.Current.BoundingRectangle}, viewport {viewport}.");
        var stopPoint = new System.Drawing.Point(edge.X, (int)Math.Ceiling(viewport.Bottom) - 1);
        Assert.True(Contains(viewport, stopPoint),
            $"The autoscroll stop point {stopPoint} must be inside the viewport {viewport}.");
        drag.MoveTo(stopPoint);
        // Autoscroll can apply one more tick after the pointer re-enters the viewport; target the
        // endpoint glyph only once its bounds stop moving.
        var finalEnd = await WaitForStableSelectionTextAsync(run, endText,
            $"selection endpoint {endText} settles after autoscroll stops");
        Assert.True(IsSelectionTextVisible(timeline, finalEnd),
            $"The selection endpoint moved outside the viewport while stopping autoscroll; " +
            $"endpoint bounds {finalEnd.Current.BoundingRectangle}, viewport {viewport}.");
        var finalGlyphBounds = GetGlyphBounds(finalEnd, endText, first: false);
        var finalPoint = LastGlyphTrailingEdge(finalEnd, endText);
        Assert.True(Contains(viewport, finalPoint),
            $"The final selection point {finalPoint} must remain inside the viewport {viewport}.");
        drag.MoveTo(finalPoint);
        Assert.True(drag.IsButtonDown, "The native left button must remain down at the final selection endpoint.");
        var actualPoint = drag.CurrentPosition;
        var selectedBeforeRelease = selectionHost is null
            ? "<legacy provider unavailable>"
            : EscapeForLog(ReadMarkdownSelectionText(selectionHost));
        writeLine($"Final selection endpoint '{endText}': glyph bounds {finalGlyphBounds}; " +
            $"requested pointer {finalPoint}; actual pointer {actualPoint}; viewport {viewport}; " +
            $"selection host text before release '{selectedBeforeRelease}'.");
    }

    private static async Task DragSelectionBetweenTextAsync(
        GatewayFixtureRun run,
        AutomationElement window,
        string startText,
        string endText)
    {
        var start = await WaitForStableSelectionTextAsync(run, startText,
            $"selection start {startText} visible with stable bounds");
        var end = await WaitForStableSelectionTextAsync(run, endText,
            $"selection end {endText} visible with stable bounds");
        await NativeKeyboardProof.ActivateAsync(new IntPtr(window.Current.NativeWindowHandle));
        using (var drag = new NativeSelectionDrag(run, window, FirstGlyphLeftHalf(start, startText)))
            drag.MoveTo(LastGlyphTrailingEdge(end, endText));
        var selectionHost = FindMarkdownSelectionHost(end)
            ?? throw new InvalidOperationException("The selected message has no Markdown selection Document.");
        await WaitUiAsync(run, () => Automation.Compare(AutomationElement.FocusedElement, selectionHost)
            && ReadMarkdownSelectionText(selectionHost).Length > 0,
            "native pointer selection and keyboard focus after release");
    }

    private static System.Drawing.Point FirstGlyphLeftHalf(AutomationElement element, string text)
    {
        var bounds = GetGlyphBounds(element, text, first: true);
        return new((int)Math.Floor(bounds.Left + Math.Max(1, bounds.Width / 8)),
            (int)Math.Round(bounds.Top + bounds.Height / 2));
    }

    private static System.Drawing.Point LastGlyphTrailingEdge(AutomationElement element, string text)
    {
        var bounds = GetGlyphBounds(element, text, first: false);
        // A rounded UIA glyph box's last interior pixel can precede the trailing caret midpoint.
        return new((int)Math.Ceiling(bounds.Right),
            (int)Math.Round(bounds.Top + bounds.Height / 2));
    }

    private static System.Drawing.Point LastGlyphRightHalf(AutomationElement element, string text)
    {
        var bounds = GetGlyphBounds(element, text, first: false);
        return new((int)Math.Floor(bounds.Left + bounds.Width * 7 / 8),
            (int)Math.Round(bounds.Top + bounds.Height / 2));
    }

    private static bool Contains(System.Windows.Rect bounds, System.Drawing.Point point) =>
        !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && point.X >= bounds.Left && point.X < bounds.Right
        && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    private static async Task AssertInitialSelectionAdmissionAsync(
        GatewayFixtureRun run,
        AutomationElement window,
        nint hwnd,
        AutomationElement anchor,
        string expectedText,
        Action<string> writeLine)
    {
        var mouseDown = FirstGlyphLeftHalf(anchor, expectedText);
        var admissionMove = LastGlyphRightHalf(anchor, expectedText);
        var glyphBounds = GetGlyphBounds(anchor, expectedText, first: true);
        var selectionHost = FindMarkdownSelectionHost(anchor);
        Assert.True(Contains(anchor.Current.BoundingRectangle, mouseDown),
            $"The admission mouse-down {mouseDown} must be inside the independent message's " +
            $"automation surface {anchor.Current.BoundingRectangle}.");
        Assert.True(Contains(glyphBounds, mouseDown),
            $"The admission mouse-down {mouseDown} must be inside its first glyph {glyphBounds}.");
        Assert.True(Contains(anchor.Current.BoundingRectangle, admissionMove),
            $"The admission move {admissionMove} must be inside the independent message's " +
            $"automation surface {anchor.Current.BoundingRectangle}.");
        using (var probe = new NativeSelectionDrag(run, window, mouseDown))
        {
            Assert.True(probe.IsButtonDown, "The native left button must remain down during the admission probe.");
            if (selectionHost is not null)
                await WaitForCollapsedFocusedSelectionAsync(run, selectionHost,
                    "admission probe press focuses its Markdown selection Document with a collapsed range");
            probe.MoveTo(admissionMove);
            Assert.True(probe.IsButtonDown, "The native left button must remain down during the admission probe.");
            if (selectionHost is not null)
                await WaitForSelectionPrefixAsync(run, selectionHost, expectedText, writeLine,
                    "admission probe selects a non-empty prefix in its independent Markdown message");
            writeLine($"Admission probe kept the native left button down from " +
                $"{mouseDown} to {admissionMove} inside first-glyph bounds {glyphBounds} and " +
                $"message bounds {anchor.Current.BoundingRectangle}; " +
                $"selection Document {(selectionHost is null ? "unavailable on baseline" : "focused")}.");
        }
        if (selectionHost is null)
        {
            var legacySelection = ReadLegacyTextSelection(anchor);
            if (legacySelection is not null)
            {
                Assert.Equal(expectedText, legacySelection);
                writeLine($"Baseline RichTextBlock TextPattern selected exact admission prefix '{legacySelection}'.");
            }
            else
            {
                ClearClipboard();
                NativeKeyboardProof.Press(hwnd, VirtualKey.C, control: true);
                var copied = NormalizeClipboardText(await WaitForClipboardTextAsync(run));
                Assert.Equal(expectedText, copied);
                writeLine($"Baseline native Ctrl+C copied exact admission prefix '{copied}'.");
            }
            ClearClipboard();
        }
        NativeKeyboardProof.Press(hwnd, VirtualKey.Escape);
        if (selectionHost is not null)
            await WaitForCollapsedFocusedSelectionAsync(run, selectionHost,
                "Escape collapses the independent admission probe without leaving a stale selection");
    }

    private static async Task WaitForCollapsedFocusedSelectionAsync(
        GatewayFixtureRun run, AutomationElement selectionHost, string description)
    {
        await WaitUiAsync(run, () => Automation.Compare(AutomationElement.FocusedElement, selectionHost)
            && ReadMarkdownSelectionText(selectionHost).Length == 0, description);
        Assert.True(Automation.Compare(AutomationElement.FocusedElement, selectionHost),
            $"Expected the Markdown selection Document to remain focused, found " +
            $"'{AutomationElement.FocusedElement.Current.ClassName}' / " +
            $"'{AutomationElement.FocusedElement.Current.ControlType.ProgrammaticName}'.");
        Assert.DoesNotContain(((TextPattern)selectionHost.GetCurrentPattern(TextPattern.Pattern)).GetSelection(),
            range => !string.IsNullOrEmpty(range.GetText(-1)));
    }

    private static async Task WaitForSelectionPrefixAsync(
        GatewayFixtureRun run, AutomationElement selectionHost, string expectedText,
        Action<string> writeLine, string description)
    {
        string? selected = null;
        await WaitUiAsync(run, () =>
        {
            selected = ReadMarkdownSelectionText(selectionHost);
            writeLine($"Selection admission poll: focused={Automation.Compare(AutomationElement.FocusedElement, selectionHost)}; " +
                $"selected='{EscapeForLog(selected)}'.");
            return string.Equals(selected, expectedText, StringComparison.Ordinal)
                && ExpectedMarkdownSelectionClipboard.StartsWith(expectedText, StringComparison.Ordinal);
        }, description);
        Assert.Equal(expectedText, selected);
        Assert.True(ExpectedMarkdownSelectionClipboard.StartsWith(expectedText, StringComparison.Ordinal),
            $"The selected text '{EscapeForLog(expectedText)}' must be a prefix of the independent clipboard golden.");
    }

    private static AutomationElement? FindMarkdownSelectionHost(AutomationElement leaf)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (AutomationElement? current = leaf; current is not null; current = walker.GetParent(current))
            if (current.Current.ClassName == "MarkdownTextSelection")
                return current;
        return null;
    }

    private static string ReadMarkdownSelectionText(AutomationElement selectionHost)
    {
        var ranges = ((TextPattern)selectionHost.GetCurrentPattern(TextPattern.Pattern)).GetSelection();
        if (ranges.Length != 1)
            throw new InvalidOperationException(
                $"Markdown selection Document returned {ranges.Length} UIA selection ranges instead of one.");
        return NormalizeClipboardText(ranges[0].GetText(-1));
    }

    private static string? ReadLegacyTextSelection(AutomationElement leaf)
    {
        if (!leaf.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern)
            || rawPattern is not TextPattern textPattern)
            return null;
        var ranges = textPattern.GetSelection();
        return ranges.Length == 0 ? null : NormalizeClipboardText(string.Join("\n",
            ranges.Select(range => range.GetText(-1))));
    }

    private static bool NearlyEqual(System.Windows.Rect left, System.Windows.Rect right) =>
        Math.Abs(left.Left - right.Left) <= 0.5
        && Math.Abs(left.Top - right.Top) <= 0.5
        && Math.Abs(left.Width - right.Width) <= 0.5
        && Math.Abs(left.Height - right.Height) <= 0.5;

    // Native input targets glyph geometry, so acquire the text inside the wait and return it only
    // once two consecutive samples agree; scrolling or a re-render resets the sample.
    private static async Task<AutomationElement> WaitForStableSelectionTextAsync(
        GatewayFixtureRun run, string text, string description)
    {
        AutomationElement? element = null;
        System.Windows.Rect? previousBounds = null;
        await WaitUiAsync(run, () =>
        {
            element = FindSelectionText(run, text);
            if (element is null || element.Current.IsOffscreen)
            {
                previousBounds = null;
                return false;
            }

            var bounds = element.Current.BoundingRectangle;
            var stable = previousBounds is { } previous && NearlyEqual(previous, bounds);
            previousBounds = bounds;
            return stable;
        }, description, onProbeError: _ => previousBounds = null);
        return element!;
    }

    private static string EscapeForLog(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void AssertClipboardGolden(
        string expected, string actual, string label, string? selectedOverlayText = null)
    {
        var normalizedExpected = NormalizeClipboardText(expected);
        var clipboardMatchesGolden = string.Equals(normalizedExpected, actual, StringComparison.Ordinal);
        var overlayMatchesClipboard = selectedOverlayText is null
            || string.Equals(selectedOverlayText, actual, StringComparison.Ordinal);
        Assert.True(clipboardMatchesGolden && overlayMatchesClipboard,
            $"{label} clipboard mismatch ({normalizedExpected.Length} expected chars, {actual.Length} actual chars)." +
            $"\nExpected: {EscapeForLog(normalizedExpected)}\nActual:   {EscapeForLog(actual)}" +
            (selectedOverlayText is null
                ? string.Empty
                : $"\nPre-copy UIA selection overlay: {EscapeForLog(selectedOverlayText)}" +
                  $"\nOverlay matches clipboard: {overlayMatchesClipboard}"));
    }

    private static string SelectionTailForLog(string text)
    {
        var escaped = EscapeForLog(text);
        const int MaximumTailLength = 80;
        return escaped.Length <= MaximumTailLength ? escaped : $"...{escaped[^MaximumTailLength..]}";
    }

    private static System.Windows.Rect GetGlyphBounds(AutomationElement element, string text, bool first)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern)
            || rawPattern is not TextPattern textPattern)
            throw new InvalidOperationException($"UI Automation text '{text}' does not expose TextPattern.");
        var documentRange = textPattern.DocumentRange;
        var documentText = documentRange.GetText(-1);
        var textOffset = documentText.IndexOf(text, StringComparison.Ordinal);
        if (textOffset < 0)
            throw new InvalidOperationException(
                $"UI Automation TextPattern range did not contain '{text}'. Range text: {documentText}");
        var glyphStart = textOffset + (first ? 0 : text.Length - 1);
        var prefixUnits = StringInfo.ParseCombiningCharacters(documentText[..glyphStart]).Length;
        var suffixUnits = StringInfo.ParseCombiningCharacters(documentText[(glyphStart + 1)..]).Length;
        var glyph = documentRange.Clone();
        glyph.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, prefixUnits);
        glyph.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -suffixUnits);
        var glyphText = glyph.GetText(-1);
        var expectedGlyph = first ? text[..1] : text[^1..];
        if (!string.Equals(glyphText, expectedGlyph, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"UI Automation range selected '{glyphText}' instead of the { (first ? "first" : "last") } glyph '{expectedGlyph}' in '{text}'.");
        var rectangles = glyph.GetBoundingRectangles();
        if (rectangles.Length == 0 || rectangles[0].IsEmpty)
        {
            var lineBounds = string.Join("; ", documentRange.GetBoundingRectangles()
                .Select(bounds => bounds.ToString()));
            throw new InvalidOperationException(
                $"UI Automation did not expose a glyph rectangle for '{text}'. " +
                $"Glyph text '{glyph.GetText(-1)}'; range text '{documentText}'; " +
                $"offset {textOffset}, glyph start {glyphStart}, element bounds {element.Current.BoundingRectangle}; " +
                $"document bounds: {lineBounds}");
        }
        return rectangles[0];
    }

    private static string ExpectedSelectionThroughTableCell()
    {
        var secondColumnValue = ExpectedMarkdownSelectionClipboard.IndexOf("\tvisible", StringComparison.Ordinal);
        if (secondColumnValue < 0)
            throw new InvalidOperationException("The independent table golden is missing its tab-separated visible cell.");
        return ExpectedMarkdownSelectionClipboard[..secondColumnValue];
    }

    private static string ExpectedSelectionThroughSummary() => ExpectedMarkdownSelectionClipboard;

    private static AutomationElement? FindSelectionText(GatewayFixtureRun run, string text) =>
        FindSelectionText(FindTimeline(run), text);

    private static AutomationElement? FindSelectionText(AutomationElement? timeline, string text)
    {
        if (timeline is null) return null;
        return timeline.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .FirstOrDefault(element => element.Current.Name.Contains(text, StringComparison.Ordinal));
    }

    private static bool IsSelectionTextVisible(AutomationElement timeline, AutomationElement? element)
    {
        if (element is null || element.Current.IsOffscreen) return false;
        var viewport = timeline.Current.BoundingRectangle;
        var bounds = element.Current.BoundingRectangle;
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
            && bounds.Top >= viewport.Top - 1 && bounds.Bottom <= viewport.Bottom + 1
            && bounds.Right > viewport.Left && bounds.Left < viewport.Right;
    }

    private static (AutomationElement Element, ScrollPattern Pattern) FindTimelineScroll(AutomationElement timeline)
    {
        if (timeline.TryGetCurrentPattern(ScrollPattern.Pattern, out var direct))
            return (timeline, (ScrollPattern)direct);
        var scrollable = timeline.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
            .Cast<AutomationElement>()
            .Select(element => (Element: element, Pattern: (ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern)))
            .Where(item => item.Pattern.Current.VerticallyScrollable)
            .OrderByDescending(item =>
            {
                var bounds = item.Element.Current.BoundingRectangle;
                return bounds.Width * bounds.Height;
            })
            .FirstOrDefault();
        return scrollable.Element is null
            ? throw new InvalidOperationException("The production chat ItemsView has no accessible vertical scroll control.")
            : scrollable;
    }

    private static async Task<AutomationElement> WaitForSelectionExpanderAsync(GatewayFixtureRun run)
    {
        AutomationElement? expander = null;
        await WaitUiAsync(run, () => (expander = FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, MarkdownSelectionSummary),
            new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)))) is not null,
            "native details summary expander");
        return expander!;
    }

    private static async Task<(AutomationElement Expander, string RangeText, AutomationElement[] Children)> WaitForRangeScopedDisclosureAsync(
        GatewayFixtureRun run, AutomationElement timeline)
    {
        (AutomationElement Expander, string RangeText, AutomationElement[] Children)? result = null;
        await WaitUiAsync(run, () => (result = FindRangeScopedDisclosure(timeline)) is not null,
            "summary-only Markdown TextPattern range exposes its native disclosure Expander");
        return result!.Value;
    }

    private static (AutomationElement Expander, string RangeText, AutomationElement[] Children)? FindRangeScopedDisclosure(
        AutomationElement timeline)
    {
        var textProviders = timeline.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true))
            .Cast<AutomationElement>();
        foreach (var provider in textProviders)
        {
            var documentRange = ((TextPattern)provider.GetCurrentPattern(TextPattern.Pattern)).DocumentRange;
            var documentText = documentRange.GetText(-1);
            var summaryStart = documentText.IndexOf(MarkdownSelectionSummary, StringComparison.Ordinal);
            if (summaryStart < 0 || !documentText.Contains(MarkdownSelectionOpening, StringComparison.Ordinal))
                continue;

            var range = documentRange.Clone();
            var afterSummary = summaryStart + MarkdownSelectionSummary.Length;
            var prefixUnits = StringInfo.ParseCombiningCharacters(documentText[..summaryStart]).Length;
            var trailingUnits = StringInfo.ParseCombiningCharacters(documentText[afterSummary..]).Length;
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, prefixUnits);
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -trailingUnits);
            var rangeText = range.GetText(-1);
            if (!string.Equals(NormalizeClipboardText(rangeText), MarkdownSelectionSummary, StringComparison.Ordinal))
                continue;

            var children = range.GetChildren();
            var expander = children.FirstOrDefault(child =>
                child.Current.Name == MarkdownSelectionSummary
                && child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _));
            if (expander is not null)
                return (expander, rangeText, children);
        }
        return null;
    }

    private static void LogRuntimeIdentity(GatewayFixtureRun run, Action<string> writeLine)
    {
        var configuredApp = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("The fixture app path was not explicitly configured.");
        var expectedAppPath = Path.GetFullPath(configuredApp);
        using var process = Process.GetProcessById(run.AppProcessId);
        var loadedAppPath = process.MainModule?.FileName
            ?? throw new InvalidOperationException("The fixture process did not expose its loaded apphost path.");
        if (!string.Equals(expectedAppPath, Path.GetFullPath(loadedAppPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Fixture app path mismatch: configured '{expectedAppPath}', loaded '{loadedAppPath}'.");
        writeLine($"Fixture apphost: {loadedAppPath}; SHA256={HashFile(loadedAppPath)}");

        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        foreach (var moduleName in new[] { "OpenClaw.Tray.WinUI.dll", "Reactor.dll", "Reactor.Advanced.dll" })
        {
            var module = modules.SingleOrDefault(candidate =>
                string.Equals(candidate.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"The fixture process did not expose loaded module '{moduleName}'.");
            writeLine($"Loaded module {module.ModuleName}: {module.FileName}; SHA256={HashFile(module.FileName)}");
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> WaitForClipboardTextAsync(GatewayFixtureRun run)
    {
        string? text = null;
        var lastClipboardError = 0;
        try
        {
            await run.WaitForAsync(() =>
            {
                if (!TryReadClipboardText(out text, out lastClipboardError))
                    return Task.FromResult(false);
                return Task.FromResult(!string.IsNullOrEmpty(text));
            }, "copied Markdown selection");
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Clipboard text was not available after the copy action (last Win32 error {lastClipboardError}).", ex);
        }
        return text!;
    }

    private static string NormalizeClipboardText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static string ReadClipboardTextOrEmpty()
    {
        if (!TryReadClipboardText(out var text, out var error) && error != 0)
            throw new InvalidOperationException($"Could not inspect the isolated desktop clipboard (Win32 error {error}).");
        return NormalizeClipboardText(text ?? string.Empty);
    }

    private static void ClearClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException($"Could not open the isolated desktop clipboard (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            if (!EmptyClipboard())
                throw new InvalidOperationException($"Could not clear the isolated desktop clipboard (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally { CloseClipboard(); }
    }

    private static bool TryReadClipboardText(out string? text, out int error)
    {
        text = null;
        error = 0;
        if (!OpenClipboard(IntPtr.Zero))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            if (!IsClipboardFormatAvailable(UnicodeTextClipboardFormat))
                return false;
            var handle = GetClipboardData(UnicodeTextClipboardFormat);
            if (handle == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                text = Marshal.PtrToStringUni(pointer);
                return text is not null;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private sealed class NativeSelectionDrag : IDisposable
    {
        private const uint MouseInput = 0;
        private const uint MouseMove = 0x0001;
        private const uint LeftButtonDown = 0x0002;
        private const uint LeftButtonUp = 0x0004;
        private const uint MouseAbsolute = 0x8000;
        private const uint MouseVirtualDesk = 0x4000;
        private const int VirtualScreenLeft = 76;
        private const int VirtualScreenTop = 77;
        private const int VirtualScreenWidth = 78;
        private const int VirtualScreenHeight = 79;
        private readonly GatewayFixtureRun _run;
        private readonly nint _window;
        private readonly nint _previousDpiContext;
        private bool _buttonDown;

        public NativeSelectionDrag(GatewayFixtureRun run, AutomationElement window, System.Drawing.Point start)
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") != "1")
                throw new InvalidOperationException("Native selection input requires the Gateway fixture UI opt-in.");
            _run = run;
            _window = new IntPtr(window.Current.NativeWindowHandle);
            if (window.Current.ProcessId != run.AppProcessId || GetForegroundWindow() != _window)
                throw new InvalidOperationException("Native selection input requires the foreground fixture-owned window.");
            _previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (_previousDpiContext == 0)
                throw new InvalidOperationException("Could not establish physical-screen coordinates for native selection input.");

            try
            {
                MoveTo(start);
                SendMouse(LeftButtonDown);
                _buttonDown = true;
            }
            catch
            {
                SetThreadDpiAwarenessContext(_previousDpiContext);
                throw;
            }
        }

        public bool IsButtonDown => _buttonDown && (GetAsyncKeyState(0x01) & 0x8000) != 0;
        public System.Drawing.Point CurrentPosition
        {
            get
            {
                if (!GetCursorPos(out var actual))
                    throw new InvalidOperationException(
                        $"Could not inspect the native pointer position (Win32 error {Marshal.GetLastWin32Error()}).");
                return actual;
            }
        }

        public void MoveTo(System.Drawing.Point point)
        {
            _run.EnsureRunning();
            if (GetForegroundWindow() != _window)
                throw new InvalidOperationException("The fixture window lost foreground ownership during native selection.");
            if (_buttonDown)
            {
                SendAbsoluteMouseMove(point);
            }
            else if (!SetCursorPos(point.X, point.Y))
            {
                throw new InvalidOperationException(
                    $"Could not move the pointer to selection start ({point.X}, {point.Y}) (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            var actual = new System.Drawing.Point();
            if (!GetCursorPos(out actual))
                throw new InvalidOperationException($"Could not inspect the native pointer position (Win32 error {Marshal.GetLastWin32Error()}).");
            if (Math.Abs(actual.X - point.X) > 1 || Math.Abs(actual.Y - point.Y) > 1)
                throw new InvalidOperationException(
                    $"Native pointer reached ({actual.X}, {actual.Y}), not requested point ({point.X}, {point.Y}).");
            var root = GetAncestor(WindowFromPoint(actual), 2);
            if (root != _window)
                throw new InvalidOperationException(
                    $"Native selection point ({actual.X}, {actual.Y}) is outside the fixture-owned window.");
        }

        public void Dispose()
        {
            try
            {
                if (_buttonDown)
                {
                    SendMouse(LeftButtonUp);
                    _buttonDown = false;
                }
            }
            finally { SetThreadDpiAwarenessContext(_previousDpiContext); }
        }

        private void SendMouse(uint flags)
        {
            var input = new NativeInput
            {
                Type = MouseInput,
                Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = flags } }
            };
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
                throw new InvalidOperationException($"Native selection input failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        private static void SendAbsoluteMouseMove(System.Drawing.Point point)
        {
            var left = GetSystemMetrics(VirtualScreenLeft);
            var top = GetSystemMetrics(VirtualScreenTop);
            var width = GetSystemMetrics(VirtualScreenWidth);
            var height = GetSystemMetrics(VirtualScreenHeight);
            if (width <= 1 || height <= 1)
                throw new InvalidOperationException("The virtual desktop does not expose usable pointer bounds.");
            var x = (int)Math.Round((point.X - left) * 65535d / (width - 1), MidpointRounding.AwayFromZero);
            var y = (int)Math.Round((point.Y - top) * 65535d / (height - 1), MidpointRounding.AwayFromZero);
            var input = new NativeInput
            {
                Type = MouseInput,
                Data = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput
                    {
                        X = Math.Clamp(x, 0, 65535),
                        Y = Math.Clamp(y, 0, 65535),
                        Flags = MouseMove | MouseAbsolute | MouseVirtualDesk,
                    }
                }
            };
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
                throw new InvalidOperationException(
                    $"Native selection move failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint Type;
            public NativeInputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct NativeInputUnion
        {
            [FieldOffset(0)] public NativeMouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out System.Drawing.Point point);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(System.Drawing.Point point);
        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint handle);
}
