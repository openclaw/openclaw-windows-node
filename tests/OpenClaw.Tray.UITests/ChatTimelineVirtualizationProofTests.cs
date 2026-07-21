using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.Graphics;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatTimelineVirtualizationProofTests
{
    private const int InitialRows = 240;

    // Mirrors OpenClawChatTimeline.FollowThreshold (private): the gap-from-bottom (px) within
    // which the timeline treats itself as "following" the newest row. Used by the scroll
    // stability assertions to prove the view is / is not in follow mode.
    private const double FollowThreshold = 60;

    private readonly UIThreadFixture _ui;

    public ChatTimelineVirtualizationProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task LargeNativeChatTimeline_VirtualizesRowsAndFollowsNewMessages()
    {
        await _ui.ResetContainerAsync();

        var props = BuildProps(InitialRows, scrollToBottomToken: 0);
        FunctionalHostControl? host = null;
        var initialCachedVirtualControls = 0;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl
            {
                Width = 860,
                Height = 560,
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            var layout = Assert.IsType<StackLayout>(repeater.Layout);
            Assert.Equal(Orientation.Vertical, layout.Orientation);
            Assert.Equal(2, layout.Spacing);
            Assert.Equal(InitialRows, CountItems(repeater.ItemsSource));
            var realizedRows = Enumerable.Range(0, InitialRows)
                .Count(index => repeater.TryGetElement(index) is not null);
            Assert.InRange(realizedRows, 1, InitialRows - 1);
            initialCachedVirtualControls = host!.CachedVirtualStackControlCount;
            Assert.True(initialCachedVirtualControls > 0);

            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "large chat timeline should overflow and become scrollable");
        });

        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            await _ui.RunOnUIAsync(() =>
            {
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                scrollViewer.ChangeView(
                    null,
                    scrollViewer.ScrollableHeight * fraction,
                    null,
                    disableAnimation: true);
                _ui.Container.UpdateLayout();
            });
            await DrainRenderQueueAsync();
        }

        object? stableItemsSource = null;
        object? stableItemTemplate = null;
        props = BuildProps(InitialRows, scrollToBottomToken: 0, textRevision: 1);
        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.Null(repeater.TryGetElement(0));
            Assert.NotNull(repeater.TryGetElement(InitialRows - 1));
            Assert.InRange(
                host!.CachedVirtualStackControlCount,
                1,
                initialCachedVirtualControls * 2);
            stableItemsSource = repeater.ItemsSource;
            stableItemTemplate = repeater.ItemTemplate;

            host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.NotNull(stableItemsSource);
            Assert.NotNull(stableItemTemplate);
            Assert.Same(stableItemsSource, repeater.ItemsSource);
            Assert.Same(stableItemTemplate, repeater.ItemTemplate);

            var visibleText = CollectVisibleText(host!);
            Assert.Contains(visibleText, text => text.Contains("revision 1", StringComparison.Ordinal));
        });

        var appendedRows = InitialRows + 1;
        props = BuildProps(appendedRows, scrollToBottomToken: 1, textRevision: 1);
        await _ui.RunOnUIAsync(() =>
        {
            host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.Equal(appendedRows, CountItems(repeater.ItemsSource));

            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(
                scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 4,
                $"chat follow should stay near the newest row; offset={scrollViewer.VerticalOffset:0.0}, height={scrollViewer.ScrollableHeight:0.0}");

            var visibleText = CollectVisibleText(host!);
            Assert.Contains(visibleText, text => text.Contains("User proof row 241", StringComparison.Ordinal));

            Console.WriteLine(
                "CHAT_TIMELINE_VIRTUALIZATION_PROOF " +
                $"rows={appendedRows} " +
                $"itemsRepeater={repeater.GetType().Name} " +
                $"layout={((StackLayout)repeater.Layout).Orientation} " +
                $"scrollableHeight={scrollViewer.ScrollableHeight:0.0} " +
                $"verticalOffset={scrollViewer.VerticalOffset:0.0} " +
                "variableHeight=true " +
                "renderChurnStable=true " +
                "newestRowVisible=true");
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Regression proof for the "scrollbar doesn't reach the bottom while the agent is
    // thinking" bug. The thinking indicator and streamed assistant text grow the last row IN
    // PLACE: no new Props.Entries, no ScrollToBottomToken bump, so the timeline's token/append
    // follow branches never fire and there is no reliable SizeChanged to drive
    // QueueScrollToBottom. Root cause: as ItemsRepeater re-realizes the grown row its extent
    // estimate climbs a few px per frame, leaving the offset short of the new bottom with
    // nothing to re-pin it. Fix (Option B): sv.VerticalAnchorRatio = 1.0 keeps the bottom-most
    // realized row glued to the viewport bottom BEFORE each frame is painted as the extent
    // grows, so the view stays pinned without a reactive post-layout ChangeView.
    [Fact]
    public async Task ThinkingAndStreamingGrowth_KeepsViewPinnedToBottom()
    {
        await _ui.ResetContainerAsync();

        const int rows = 60;
        FunctionalHostControl? host = null;

        // 1. Mount a scrollable timeline. First mount is an initial load -> scrolls to bottom.
        var props = BuildProps(rows, scrollToBottomToken: 0, sessionId: "ui-proof-thinking-stream");
        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl { Width = 860, Height = 560, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "timeline should overflow and be scrollable");
            Assert.True(
                scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 4,
                $"precondition: view should start pinned to bottom; offset={scrollViewer.VerticalOffset:0.0}, height={scrollViewer.ScrollableHeight:0.0}");
        });

        // 2. Agent starts thinking: a synthetic indicator row appears. No new Props.Entry,
        //    no ScrollToBottomToken bump. The view must stay pinned to the bottom.
        props = props with { ShowThinkingIndicator = true };
        await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            var gap = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
            Console.WriteLine(
                "CHAT_TIMELINE_PIN_PROOF stage=thinking-indicator " +
                $"gap={gap:0.0} offset={scrollViewer.VerticalOffset:0.0} scrollable={scrollViewer.ScrollableHeight:0.0}");
            Assert.True(
                gap <= 4,
                $"thinking indicator must not break bottom-follow; gap={gap:0.0}, height={scrollViewer.ScrollableHeight:0.0}");
        });

        // 3. Assistant streams: the last entry's text grows over several re-renders while the
        //    thinking indicator is still present. No token bump, no entry-count change, so the
        //    timeline's token/append follow branches never fire — follow relies on the frame-spaced
        //    landing correction. Regression: ItemsRepeater virtualization briefly reports
        //    ScrollableHeight a few px past the realized reachable offset after an in-place row
        //    growth, leaving the scrollbar short of the bottom while the agent is thinking/streaming.
        for (var revision = 1; revision <= 4; revision++)
        {
            var streamed = BuildStreamingEntries(rows, revision);
            props = props with { Entries = streamed };
            await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
            await DrainRenderQueueAsync();
            await DrainRenderQueueAsync();
            await DrainRenderQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                _ui.Container.UpdateLayout();
                var gap = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
                Console.WriteLine(
                    "CHAT_TIMELINE_PIN_PROOF stage=streaming " +
                    $"revision={revision} gap={gap:0.0} offset={scrollViewer.VerticalOffset:0.0} " +
                    $"scrollable={scrollViewer.ScrollableHeight:0.0}");
                Assert.True(
                    gap <= 4,
                    $"streaming revision {revision} must keep view pinned to the bottom; " +
                    $"gap={gap:0.0}, " +
                    $"offset={scrollViewer.VerticalOffset:0.0}, scrollable={scrollViewer.ScrollableHeight:0.0}");
            });
        }

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Companion to ThinkingAndStreamingGrowth: when the USER has deliberately scrolled up to
    // read earlier messages, streaming growth of the newest (off-screen, below the viewport)
    // row must NOT yank the viewport to the bottom. VerticalAnchorRatio = 1.0 anchors the
    // element at the viewport's bottom edge, so content growing further below only extends the
    // scrollable range while the visible rows stay put — the user keeps their reading position.
    // This is the "don't fight the scrollbar" half of the fix.
    [Fact]
    public async Task UserScrolledUpDuringStreaming_KeepsReadingPositionStable()
    {
        await _ui.ResetContainerAsync();

        const int rows = 60;
        FunctionalHostControl? host = null;

        var props = BuildProps(rows, scrollToBottomToken: 0, sessionId: "ui-proof-scrollup");
        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl { Width = 860, Height = 560, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        // User scrolls up well above the follow threshold to read earlier messages.
        var userOffset = 0.0;
        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "timeline should overflow and be scrollable");
            // ~30% from the top => a large gap from the bottom, unambiguously "scrolled up".
            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight * 0.3, null, disableAnimation: true);
            _ui.Container.UpdateLayout();
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            userOffset = scrollViewer.VerticalOffset;
            Assert.True(
                scrollViewer.ScrollableHeight - userOffset > FollowThreshold,
                "precondition: user should be scrolled above the follow threshold; " +
                $"gap={scrollViewer.ScrollableHeight - userOffset:0.0}, threshold={FollowThreshold}");
        });

        // Assistant streams into the newest (off-screen) row while the user stays scrolled up.
        props = props with { ShowThinkingIndicator = true };
        for (var revision = 1; revision <= 4; revision++)
        {
            var streamed = BuildStreamingEntries(rows, revision);
            props = props with { Entries = streamed };
            await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
            await DrainRenderQueueAsync();
            await DrainRenderQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                _ui.Container.UpdateLayout();
                var gap = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
                var drift = Math.Abs(scrollViewer.VerticalOffset - userOffset);
                Console.WriteLine(
                    "CHAT_TIMELINE_SCROLLUP_STABLE " +
                    $"revision={revision} userOffset={userOffset:0.0} offset={scrollViewer.VerticalOffset:0.0} " +
                    $"drift={drift:0.0} gap={gap:0.0} scrollable={scrollViewer.ScrollableHeight:0.0}");

                // The view must NOT be dragged into follow mode...
                Assert.True(
                    gap > FollowThreshold,
                    $"streaming must not drag a scrolled-up user to the bottom; revision {revision}, " +
                    $"gap={gap:0.0}, threshold={FollowThreshold}");
                // ...and the user's reading position must stay put (allow minor extent-estimate wobble).
                Assert.True(
                    drift <= 24,
                    $"user reading position must stay stable while streaming; revision {revision}, " +
                    $"drift={drift:0.0}, userOffset={userOffset:0.0}, offset={scrollViewer.VerticalOffset:0.0}");
            });
        }

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Regression proof for the moderate-scroll band: a user who scrolls JUST above
    // FollowThreshold (gap = 61–900px, well below the large abandonGap) during an ACTIVE settle
    // timer must not be re-pinned to the bottom by subsequent timer ticks or streaming revisions.
    // This is the fix for the "61–900px re-pin" band identified in review: the ViewChanged
    // handler now cancels the settle timer immediately when isFollowing flips false, regardless
    // of whether the gap also exceeds the large abandon threshold. The test uses a bottom-follow
    // starting state with a ScrollToBottomToken (which starts a settle timer), then simulates a
    // user scroll to a gap of ~FollowThreshold+100px (well below abandonGap), and asserts the
    // view is NOT re-pinned across multiple streaming revisions.
    [Fact]
    public async Task ModerateScrollUpDuringSettleTimer_IsNotRepinned()
    {
        await _ui.ResetContainerAsync();

        const int rows = 60;
        FunctionalHostControl? host = null;

        var props = BuildProps(rows, scrollToBottomToken: 0, sessionId: "ui-proof-moderate-scroll");
        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl { Width = 860, Height = 560, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        // Verify we start pinned to the bottom.
        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(
                scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 4,
                $"precondition: view should start pinned to bottom; offset={scrollViewer.VerticalOffset:0.0}, " +
                $"scrollable={scrollViewer.ScrollableHeight:0.0}");
        });

        // Trigger a scroll-to-bottom token bump, which starts a settle timer.
        // Use a fresh token bump so QueueScrollToBottom fires and creates the DispatcherTimer.
        props = props with { ScrollToBottomToken = 1 };
        await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));

        // Yield enough for the enqueued QueueScrollToBottom to fire (starts timer) but not
        // enough for the timer to fully converge and stop (needs ~2 stable ticks = ~32ms+).
        // A single UpdateLayout + YieldToRender puts us right after the initial PinToBottom.
        await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
        await _ui.YieldToRenderAsync();

        // Bounded wait: confirm the settle timer is ACTIVE before proceeding. Poll up to 200ms.
        var timerWasActive = false;
        for (var i = 0; i < 10 && !timerWasActive; i++)
        {
            await Task.Delay(20);
            await _ui.RunOnUIAsync(() =>
            {
                // The settle timer sets scrollSettleTimerRef. We can observe it indirectly:
                // while the timer is active, the ScrollViewer has VerticalAnchorRatio = NaN
                // (QueueScrollToBottom disables anchoring during settling).
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                timerWasActive = double.IsNaN(scrollViewer.VerticalAnchorRatio);
            });
        }
        Assert.True(timerWasActive,
            "precondition: settle timer must be active (anchoring disabled) before user scroll");

        // User scrolls up a MODERATE amount: above FollowThreshold (60px) but well below the
        // abandonGap (900px/1.5 viewports). This is the critical band.
        var userOffset = 0.0;
        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            var targetOffset = Math.Max(0, scrollViewer.ScrollableHeight - 400);
            scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
            _ui.Container.UpdateLayout();
        });

        // Let ViewChanged fire and propagate (the fix: moderate-cancel stops the timer).
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            userOffset = scrollViewer.VerticalOffset;
            var gap = scrollViewer.ScrollableHeight - userOffset;
            Assert.True(
                gap > FollowThreshold && gap < 840,
                "precondition: user should be in the moderate band (above FollowThreshold, below abandonGap); " +
                $"gap={gap:0.0}, threshold={FollowThreshold}");
        });

        // Stream multiple revisions. If the fix works, the timer was cancelled by ViewChanged's
        // moderate-cancel clause and subsequent streaming won't re-pin. Without the fix, the
        // timer would keep calling PinToBottom every 16ms, overriding the user's scroll.
        props = props with { ShowThinkingIndicator = true };
        for (var revision = 1; revision <= 4; revision++)
        {
            var streamed = BuildStreamingEntries(rows, revision);
            props = props with { Entries = streamed };
            await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
            await DrainRenderQueueAsync();
            await DrainRenderQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                _ui.Container.UpdateLayout();
                var gap = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
                var drift = Math.Abs(scrollViewer.VerticalOffset - userOffset);

                // The view must NOT be re-pinned to the bottom.
                Assert.True(
                    gap > FollowThreshold,
                    $"moderate scroll must not be re-pinned to the bottom during streaming; revision {revision}, " +
                    $"gap={gap:0.0}, threshold={FollowThreshold}");
                // Reading position must remain bounded-stable (FunctionalUI re-estimation
                // causes up to ~80px drift per revision, independent of scroll anchoring).
                Assert.True(
                    drift <= 80,
                    $"moderate scroll position must stay bounded-stable while streaming; revision {revision}, " +
                    $"drift={drift:0.0}, userOffset={userOffset:0.0}, offset={scrollViewer.VerticalOffset:0.0}");
            });
        }

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Load-earlier / prepend restore. This scenario isn't reproducible on a fresh live session
    // (which has no earlier history to load), so it is proven here instead of in the recording.
    // When earlier messages are prepended this test guards the invariants a temporary scroll fix
    // CAN hold under the current full-remount FunctionalUI reconciler:
    //   (1) the scrolled-up reader is NOT yanked to the newest row and NOT reset to the very top
    //       (the offset stays in the "reading earlier history" band), and
    //   (2) bottom-follow still works afterward. Under the dynamic-anchoring model,
    //       VerticalAnchorRatio is 1.0 only while the view is in the bottom band and NaN while the
    //       reader is scrolled up (so post-remount extent estimates can't drift their position);
    //       the end-to-end guarantee is that a ScrollToBottomToken bump re-follows to the newest
    //       row. A regression that left follow permanently broken would fail that final assertion.
    //
    // KNOWN LIMITATION (tracked in issue #996): exact pixel-for-pixel reading-position
    // preservation (offset shifting by the full inserted height) is NOT achievable here because
    // the prepend re-mounts the whole Entries collection, resetting the ItemsRepeater's realized
    // rows. That reset defeats BOTH native WinUI scroll anchoring (the anchor element is a new
    // instance post-reset) AND a manual ChangeView(oldOffset + insertedHeight) (jumping into
    // un-realized territory re-estimates row heights and clamps/oscillates the target back). Real
    // preservation needs the incremental keyed reconciliation the Reactor port (#996) provides;
    // this test therefore asserts the bounded reading position, not the exact offset.
    //
    // NOTE: the prepend path keys off the entries-prepended predicate (new first id, unchanged
    // last id, previous first id still present, count grew) — NOT Props.HasMoreHistory — so this
    // test drives it with HasMoreHistory=false on purpose. Rendering the HasMoreHistory "load
    // earlier" Button pulls in theme-brush resource refs the headless UI-test host doesn't
    // realize, which aborts the timeline subtree; keeping it false isolates the offset/anchoring
    // invariant we actually care about here.
    [Fact]
    public async Task PrependHistory_PreservesOffset_AndRestoresBottomAnchoring()
    {
        await _ui.ResetContainerAsync();

        const int rows = 60;
        const string sessionId = "ui-proof-prepend";
        FunctionalHostControl? host = null;

        var entries = BuildEntries(rows, textRevision: 0);
        var props = BuildPropsFrom(sessionId, entries, hasMoreHistory: false, scrollToBottomToken: 0);
        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl { Width = 860, Height = 560, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        // User scrolls up to a mid position to read earlier messages, then history is prepended.
        var oldOffset = 0.0;
        var oldScrollable = 0.0;
        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "timeline should overflow and be scrollable");
            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight * 0.4, null, disableAnimation: true);
            _ui.Container.UpdateLayout();
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            oldOffset = scrollViewer.VerticalOffset;
            oldScrollable = scrollViewer.ScrollableHeight;
        });

        // Prepend 20 earlier messages: NEW ids at the FRONT, same LAST id, previous first id
        // still present, count grows — exactly OpenClawChatTimeline's prependedHistory predicate,
        // which drives QueuePreservePrependOffset (offset preservation + anchoring toggle).
        var prepended = PrependHistory(entries, historyRows: 20);
        props = props with { Entries = prepended };
        await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            var delta = scrollViewer.ScrollableHeight - oldScrollable;
            var offset = scrollViewer.VerticalOffset;
            var gapFromBottom = scrollViewer.ScrollableHeight - offset;
            Console.WriteLine(
                "CHAT_TIMELINE_PREPEND_RESTORE " +
                $"oldOffset={oldOffset:0.0} oldScrollable={oldScrollable:0.0} " +
                $"newScrollable={scrollViewer.ScrollableHeight:0.0} delta={delta:0.0} " +
                $"offset={offset:0.0} gapFromBottom={gapFromBottom:0.0} " +
                $"anchorRatio={scrollViewer.VerticalAnchorRatio}");

            // Prepending earlier history grows the scrollable extent.
            Assert.True(delta > 0, $"prepending earlier history should grow the scrollable extent; delta={delta:0.0}");

            // Bounded reading position (see the KNOWN LIMITATION note above): the scrolled-up
            // reader must NOT be reset to the very top and must NOT be yanked down into follow at
            // the newest row. Exact pixel preservation isn't achievable under the full-remount
            // reconciler (#996), but these two regressions MUST NOT happen.
            Assert.True(
                offset > FollowThreshold,
                $"prepend must not reset the reader to the top; offset={offset:0.0}");
            Assert.True(
                gapFromBottom > FollowThreshold,
                "prepend must not yank a scrolled-up reader down to the newest row; " +
                $"gapFromBottom={gapFromBottom:0.0}, threshold={FollowThreshold}");

            // Follow is now DYNAMICALLY armed: bottom anchoring (VerticalAnchorRatio = 1.0) is
            // enabled only while the view sits in the bottom band, and disabled (NaN) while the
            // user is scrolled up so extent re-estimation can't nudge their held reading position.
            // Since this reader stays scrolled up after the prepend, anchoring being NaN here is
            // CORRECT, not a stuck-disabled regression — the old design kept a single static 1.0,
            // but that let post-reset extent estimates drift the reader. Anchoring must be one of
            // the two well-defined states (NaN while up, or exactly 1.0 at the bottom), never a
            // stale partial ratio. The real guarantee — that bottom-follow still WORKS after a
            // prepend — is proven end-to-end below via the ScrollToBottomToken bump.
            Assert.True(
                double.IsNaN(scrollViewer.VerticalAnchorRatio) || scrollViewer.VerticalAnchorRatio == 1.0,
                "prepend must leave anchoring in a well-defined state (NaN while scrolled up, or " +
                $"1.0 at the bottom); VerticalAnchorRatio was {scrollViewer.VerticalAnchorRatio}");
        });

        // End-to-end: with anchoring restored, a scroll-to-bottom token must follow again.
        props = props with { ScrollToBottomToken = 1 };
        await _ui.RunOnUIAsync(() => host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props)));
        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            var gap = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
            Console.WriteLine($"CHAT_TIMELINE_PREPEND_RESTORE followAfterPrepend gap={gap:0.0}");
            Assert.True(
                gap <= 4,
                $"after prepend + restore, scroll-to-bottom must follow to the newest row; gap={gap:0.0}");
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    [Fact]
    public async Task RealizedComponentRow_ReplacesRootAndPrunesRemovedEffects()
    {
        await _ui.ResetContainerAsync();
        DisposableVirtualRow.CleanupCount = 0;

        FunctionalHostControl? host = null;
        ItemsRepeater? repeater = null;
        UIElement? stableContainer = null;
        var expandedCacheCount = 0;
        var cleanupCountBeforeCollapse = 0;

        await _ui.RunOnUIAsync(() =>
        {
            host = new FunctionalHostControl { Width = 600, Height = 400, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => VirtualVStack(
                0,
                Component<SwappingVirtualRow, SwappingVirtualRowProps>(new SwappingVirtualRowProps(true))));
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            repeater = FindLogical<ItemsRepeater>(host!).Single();
            stableContainer = repeater.TryGetElement(0);
            Assert.IsType<Border>(stableContainer);
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "expanded row");
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "disposable child");
            expandedCacheCount = host!.CachedVirtualStackControlCount;
            Assert.True(expandedCacheCount > 1);
            cleanupCountBeforeCollapse = DisposableVirtualRow.CleanupCount;

            host.Mount(_ => VirtualVStack(
                0,
                Component<SwappingVirtualRow, SwappingVirtualRowProps>(new SwappingVirtualRowProps(false))));
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Same(stableContainer, repeater!.TryGetElement(0));
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "collapsed row");
            Assert.DoesNotContain(FindDescendants<TextBlock>(host!), text => text.Text == "disposable child");
            Assert.Equal(cleanupCountBeforeCollapse + 1, DisposableVirtualRow.CleanupCount);
            Assert.InRange(host!.CachedVirtualStackControlCount, 1, expandedCacheCount - 1);
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Bounded, deterministic render-queue drain. ItemsRepeater realizes rows and the
    // ScrollViewer settles its extent estimate — and re-fires its initial scroll-to-bottom
    // follow — across several dispatcher ticks. Each pass forces layout, then drains the
    // render queue deterministically via a LOW-priority dispatcher callback (WinUI runs
    // layout/render/realization at higher priority, so a Low continuation only resumes once
    // that pass's queued work has run), then yields REAL wall-clock time so timer-based scroll
    // retries can fire. The per-pass delay must stay generous: the previous proven-green drain
    // gave a single contiguous Task.Delay(50) of free dispatcher time, and shrinking that (to a
    // 5ms floor) left the virtualized scroll-to-bottom short of convergence (offset stuck ~30%
    // from the bottom). Three 40ms passes give strictly MORE free dispatcher time than the old
    // 50ms while keeping the drain deterministic. The pass count is fixed, so this can never
    // spin or block indefinitely even if layout never fully quiesces.
    private async Task DrainRenderQueueAsync()
    {
        const int maxDrainPasses = 3;
        for (var pass = 0; pass < maxDrainPasses; pass++)
        {
            await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
            await _ui.YieldToRenderAsync();
            await Task.Delay(40);
        }
    }

    private static OpenClawChatTimelineProps BuildProps(int rows, int scrollToBottomToken, int textRevision = 0, string sessionId = "ui-proof-large-chat") =>
        BuildPropsFrom(
            sessionId,
            BuildEntries(rows, textRevision),
            hasMoreHistory: false,
            scrollToBottomToken);

    private static OpenClawChatTimelineProps BuildPropsFrom(
        string sessionId,
        IReadOnlyList<ChatTimelineItem> entries,
        bool hasMoreHistory,
        int scrollToBottomToken) =>
        new(
            SessionId: sessionId,
            Entries: entries,
            HasMoreHistory: hasMoreHistory,
            OnLoadMoreHistory: null,
            UserSenderLabel: "UI proof user",
            AssistantSenderLabel: "UI proof assistant",
            DefaultModel: "proof-model",
            ShowToolCalls: true,
            ScrollToBottomToken: scrollToBottomToken);

    // Insert `historyRows` NEW entries at the FRONT (ids that sort before the existing ones),
    // keeping the existing entries — and crucially the same LAST entry id — in place. This
    // matches OpenClawChatTimeline's prependedHistory predicate (new first id, unchanged last
    // id, previous first id still present, count grew), so mounting it drives the load-earlier
    // offset-preservation path.
    private static IReadOnlyList<ChatTimelineItem> PrependHistory(IReadOnlyList<ChatTimelineItem> existing, int historyRows)
    {
        var combined = new List<ChatTimelineItem>(historyRows + existing.Count);
        for (var i = 1; i <= historyRows; i++)
        {
            var isUser = i % 2 == 1;
            combined.Add(new ChatTimelineItem(
                Id: $"hist-{i:000}",
                Kind: isUser ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                Text: FormatVariableHeightText(isUser ? "History user" : "History assistant", i, textRevision: 0)));
        }

        combined.AddRange(existing);
        return combined;
    }

    private static IReadOnlyList<ChatTimelineItem> BuildEntries(int rows, int textRevision)
    {
        var entries = new List<ChatTimelineItem>(rows);
        for (var i = 1; i <= rows; i++)
        {
            var isUser = i % 2 == 1;
            entries.Add(new ChatTimelineItem(
                Id: $"proof-{i:000}",
                Kind: isUser ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                Text: FormatVariableHeightText(isUser ? "User" : "Assistant", i, textRevision)));
        }

        return entries;
    }

    // Same entry ids as BuildEntries (so counts/keys are stable, mirroring a streaming
    // update), but the final assistant row's text grows with each revision to emulate the
    // assistant reply streaming in while the thinking indicator is still shown.
    private static IReadOnlyList<ChatTimelineItem> BuildStreamingEntries(int rows, int streamRevision)
    {
        var entries = new List<ChatTimelineItem>(BuildEntries(rows, textRevision: 0));
        var lastIndex = entries.Count - 1;
        var last = entries[lastIndex];
        var streamedBody = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, streamRevision * 3).Select(line => $"streamed line {line} of assistant reply"));
        entries[lastIndex] = last with { Text = last.Text + Environment.NewLine + streamedBody };
        return entries;
    }

    private static string FormatVariableHeightText(string role, int row, int textRevision)
    {
        var detailLines = row % 4;
        var details = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, detailLines).Select(i => $"detail {row}-{i} revision {textRevision}"));
        var heading = $"{role} proof row {row}";
        return string.IsNullOrEmpty(details)
            ? heading
            : heading + Environment.NewLine + details;
    }

    private static int CountItems(object? itemsSource) =>
        itemsSource is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>().Count()
            : 0;

    // Collects visible message text from both TextBlock (lone text blocks,
    // tool rows) and RichTextBlock (coalesced prose + user bubbles), since chat
    // messages now render their prose through a RichTextBlock per message.
    private static string[] CollectVisibleText(DependencyObject host)
    {
        var texts = new List<string>();

        foreach (var tb in FindDescendants<TextBlock>(host))
        {
            if (!string.IsNullOrWhiteSpace(tb.Text)) texts.Add(tb.Text);
        }

        foreach (var rtb in FindDescendants<RichTextBlock>(host))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var block in rtb.Blocks)
            {
                if (block is Paragraph p)
                {
                    foreach (var inline in p.Inlines) AppendInlineText(inline, sb);
                    sb.Append('\n');
                }
            }
            var s = sb.ToString();
            if (!string.IsNullOrWhiteSpace(s)) texts.Add(s);
        }

        return texts.ToArray();
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder sb)
    {
        switch (inline)
        {
            case Run run:
                sb.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines) AppendInlineText(child, sb);
                break;
            case LineBreak:
                sb.Append('\n');
                break;
        }
    }

    private sealed record SwappingVirtualRowProps(bool Expanded);

    private sealed class SwappingVirtualRow : Component<SwappingVirtualRowProps>
    {
        public override Element Render() => Props.Expanded
            ? VStack(
                0,
                TextBlock("expanded row"),
                Component<DisposableVirtualRow>())
            : TextBlock("collapsed row");
    }

    private sealed class DisposableVirtualRow : Component
    {
        internal static int CleanupCount { get; set; }

        public override Element Render()
        {
            UseEffect((Func<Action>)(() => () => CleanupCount++), Array.Empty<object>());
            return TextBlock("disposable child");
        }
    }
}
