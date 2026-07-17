using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

            var visibleText = FindDescendants<TextBlock>(host!)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
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

            var visibleText = FindDescendants<TextBlock>(host!)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
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
        var props = BuildProps(rows, scrollToBottomToken: 0);
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

        var props = BuildProps(rows, scrollToBottomToken: 0);
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

    // Load-earlier / prepend restore. This scenario isn't reproducible on a fresh live session
    // (which has no earlier history to load), so it is proven here instead of in the recording.
    // When earlier messages are prepended: (1) the user's visible position is preserved (the
    // offset shifts by exactly the inserted content height via QueuePreservePrependOffset), and
    // (2) VerticalAnchorRatio is restored to 1.0 afterward so subsequent streaming/append
    // follow keeps working. A regression that left anchoring at NaN would silently break
    // bottom-follow for the rest of the session — this test guards both invariants.
    [Fact]
    public async Task PrependHistory_PreservesOffset_AndRestoresBottomAnchoring()
    {
        await _ui.ResetContainerAsync();

        const int rows = 60;
        const string sessionId = "ui-proof-prepend";
        FunctionalHostControl? host = null;

        var entries = BuildEntries(rows, textRevision: 0);
        var props = BuildPropsFrom(sessionId, entries, hasMoreHistory: true, scrollToBottomToken: 0);
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

        // User scrolls up to a mid position (where the "load earlier" affordance lives), then loads.
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
            var expectedOffset = oldOffset + delta;
            var offsetError = Math.Abs(scrollViewer.VerticalOffset - expectedOffset);
            Console.WriteLine(
                "CHAT_TIMELINE_PREPEND_RESTORE " +
                $"oldOffset={oldOffset:0.0} oldScrollable={oldScrollable:0.0} " +
                $"newScrollable={scrollViewer.ScrollableHeight:0.0} delta={delta:0.0} " +
                $"expectedOffset={expectedOffset:0.0} offset={scrollViewer.VerticalOffset:0.0} " +
                $"offsetError={offsetError:0.0} anchorRatio={scrollViewer.VerticalAnchorRatio}");

            // Prepending pushes existing content down by delta; the user's visible rows must be
            // preserved by shifting the offset by that same delta (not reset to top or bottom).
            Assert.True(delta > 0, $"prepending earlier history should grow the scrollable extent; delta={delta:0.0}");
            Assert.True(
                offsetError <= 16,
                "prepend must preserve the reading position (offset shifts by inserted height); " +
                $"expected={expectedOffset:0.0}, actual={scrollViewer.VerticalOffset:0.0}, error={offsetError:0.0}");

            // Primary invariant: anchoring is restored to the bottom (1.0), NOT left disabled (NaN).
            Assert.False(
                double.IsNaN(scrollViewer.VerticalAnchorRatio),
                "prepend correction must restore anchoring; VerticalAnchorRatio was left at NaN (disabled)");
            Assert.Equal(1.0, scrollViewer.VerticalAnchorRatio, precision: 6);
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

    // Bounded, deterministic render-queue drain (replaces a single fixed Task.Delay(50)).
    // ItemsRepeater realizes rows and the ScrollViewer settles its extent estimate across
    // several dispatcher/layout ticks. Rather than sleep one magic interval, each pass forces
    // layout and then awaits a LOW-priority dispatcher callback: because WinUI runs layout and
    // render callbacks at higher priority, a Low continuation only resumes once that pass's
    // queued work has drained. A small per-pass delay floor gives the composition render loop
    // room to realize viewport rows. The pass count is fixed, so this can never spin or block
    // indefinitely even if layout never fully quiesces.
    private async Task DrainRenderQueueAsync()
    {
        const int maxDrainPasses = 6;
        for (var pass = 0; pass < maxDrainPasses; pass++)
        {
            await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
            await _ui.YieldToRenderAsync();
            await Task.Delay(5);
        }
    }

    private static OpenClawChatTimelineProps BuildProps(int rows, int scrollToBottomToken, int textRevision = 0) =>
        BuildPropsFrom(
            "ui-proof-large-chat",
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
