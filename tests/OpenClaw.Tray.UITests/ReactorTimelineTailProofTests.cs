using System.Diagnostics;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.Graphics;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorTimelineTailProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(360)]
    [InlineData(900)]
    public async Task CachedSessionReturn_ShowsActualTailWithoutAutomationOrScrollRepair(int width)
    {
        await ui.ResetContainerAsync();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        await ui.RunOnUIAsync(() =>
        {
            if (ui.Container.IsLoaded)
            {
                loaded.TrySetResult();
                return;
            }
            ui.Container.Loaded += OnLoaded;
        });
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await ui.RunOnUIAsync(() => ui.Container.Loaded -= OnLoaded); }
        ReactorHostControl? host = null;
        XamlControlsResources? controlsResources = null;
        UIElement? root = null;
        Element? current = null;
        var longHistory = Timeline("long", 240);
        var otherHistory = Timeline("other", 8);
        SizeInt32 originalWindowSize = default;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                // The programmatic test Application needs native theme resources after startup,
                // when its XAML metadata provider is available. Do not substitute dummy brushes.
                controlsResources = new XamlControlsResources();
                Application.Current.Resources.MergedDictionaries.Add(controlsResources);
                originalWindowSize = ui.TestWindow.AppWindow.Size;
                var scale = ui.Container.XamlRoot.RasterizationScale;
                ui.TestWindow.AppWindow.Resize(new SizeInt32(
                    (int)Math.Ceiling((width + 48) * scale),
                    (int)Math.Ceiling(616 * scale)));
                host = new ReactorHostControl
                {
                    Width = width,
                    Height = 520,
                    RequestedTheme = ElementTheme.Light,
                };
                current = Component<ReactorChatTimeline, ReactorChatTimelineProps>(longHistory);
                root = host.Reconciler.Mount(current, static () => { });
                host.Content = root;
                ui.Container.Children.Add(host);
            });
            await AssertVisibleAsync("initial long", 239, "TAIL long 240");

            for (var cycle = 0; cycle < 10; cycle++)
            {
                await SwitchAsync(otherHistory, replaceItemsView: true);
                await AssertVisibleAsync($"other {cycle}", 7, "TAIL other 8");
                await SwitchAsync(longHistory, replaceItemsView: true);
                await AssertVisibleAsync($"returned long {cycle}", 239, "TAIL long 240");
            }

            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(ui.Container, $"SessionReturn{width}"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.IsType<StackLayout>(Items().Layout);
                Assert.Null(Assert.IsType<ItemsRepeater>(Items().ScrollView.Content).TryGetElement(0));
            });

            var appended = longHistory with
            {
                Timeline = longHistory.Timeline with
                {
                    Entries = [.. longHistory.Timeline.Entries, new("long-241", ChatTimelineItemKind.Assistant, "TAIL long 241")],
                },
            };
            await SwitchAsync(appended, replaceItemsView: false);
            await AssertVisibleAsync("following appended row", 240, "TAIL long 241");
            var streaming = appended with
            {
                Timeline = appended.Timeline with
                {
                    Entries = [.. appended.Timeline.Entries.Take(240),
                        new("long-241", ChatTimelineItemKind.Assistant, "Streaming text\n\nTAIL long 241", IsStreaming: true)],
                },
            };
            await SwitchAsync(streaming, replaceItemsView: false);
            await AssertVisibleAsync("following growing row", 240, "TAIL long 241");
            // Only after the natural-return assertions: simulate a reader scrolling away.
            await ScrollToFirstAsync();
            await AssertVisibleAsync("reader at first row", 0, "Message 1");
            var scrolledAway = streaming with
            {
                Timeline = streaming.Timeline with
                {
                    Entries = [.. streaming.Timeline.Entries, new("long-242", ChatTimelineItemKind.Assistant, "TAIL long 242")],
                },
            };
            await SwitchAsync(scrolledAway, replaceItemsView: false);
            await AssertVisibleAsync("append preserves reader position", 0, "Message 1");
            await ui.RunOnUIAsync(() => Assert.InRange(Items().ScrollView.VerticalOffset, 0, 1));

            var nextGeneration = longHistory with
            {
                Timeline = longHistory.Timeline with { TimelineGeneration = 1 },
            };
            await SwitchAsync(nextGeneration, replaceItemsView: true);
            await AssertVisibleAsync("new generation", 239, "TAIL long 240");
            await ui.RunOnUIAsync(() =>
            {
                // Identity-only checks: the next session replaces these pending requests.
                var tokenUpdate = nextGeneration with
                {
                    Timeline = nextGeneration.Timeline with { ScrollToBottomToken = 1 },
                };
                Reconcile(tokenUpdate, replaceItemsView: false);
                Reconcile(tokenUpdate with { HistoryRevision = 2 }, replaceItemsView: false);
                Reconcile(otherHistory, replaceItemsView: true);
                Reconcile(longHistory, replaceItemsView: true);
            });
            await AssertVisibleAsync("return before previous layout", 239, "TAIL long 240");
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    if (current is not null && root is not null)
                        host.Content = host.Reconciler.Reconcile(current, Empty(), root, static () => { });
                    ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                }
                if (controlsResources is not null)
                    Application.Current.Resources.MergedDictionaries.Remove(controlsResources);
                if (originalWindowSize.Width > 0)
                    ui.TestWindow.AppWindow.Resize(originalWindowSize);
            });
        }

        ItemsView Items() => Descendants<ItemsView>(host!).Single();

        async Task ScrollToFirstAsync()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var correlationId = -1;
            ScrollView? scroll = null;
            void OnCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
            {
                if (correlationId == args.CorrelationId)
                    completed.TrySetResult();
            }
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    scroll = Items().ScrollView;
                    scroll.ScrollCompleted += OnCompleted;
                    correlationId = scroll.ScrollTo(0, 0, new ScrollingScrollOptions(
                        ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                    Assert.True(correlationId >= 0, "The reader scroll request was not accepted.");
                });
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await ui.RunOnUIAsync(() =>
                {
                    if (scroll is not null)
                        scroll.ScrollCompleted -= OnCompleted;
                });
            }
        }

        Task SwitchAsync(ReactorChatTimelineProps next, bool replaceItemsView) =>
            ui.RunOnUIAsync(() => Reconcile(next, replaceItemsView));

        void Reconcile(ReactorChatTimelineProps next, bool replaceItemsView)
        {
            var previousItemsView = TestSupport.FindLogical<ItemsView>(root!).Single();
            var nextElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(next);
            root = host!.Reconciler.Reconcile(current!, nextElement, root!, static () => { });
            current = nextElement;
            host.Content = root;
            // Read the logical tree before the next layout attaches a newly mounted control.
            var nextItemsView = TestSupport.FindLogical<ItemsView>(root!).Single();
            if (replaceItemsView)
                Assert.NotSame(previousItemsView, nextItemsView);
            else
                Assert.Same(previousItemsView, nextItemsView);
        }

        async Task AssertVisibleAsync(string phase, int index, string marker)
        {
            var deadline = Stopwatch.StartNew();
            var visible = false;
            var diagnostics = "";
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await ui.YieldToRenderAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await ui.RunOnUIAsync(() =>
                {
                    var items = Descendants<ItemsView>(host!).SingleOrDefault();
                    if (items is null)
                        return;
                    Assert.Same(
                        TestSupport.FindLogical<AnnotatedScrollBar>(root!).Single().ScrollController,
                        items.VerticalScrollController);
                    var scroll = items.ScrollView;
                    var repeater = Descendants<ItemsRepeater>(items).SingleOrDefault();
                    var row = repeater?.TryGetElement(index) as FrameworkElement;
                    var bounds = row is not null && scroll is not null
                        ? row.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight))
                        : default;
                    var markers = row is null ? [] :
                        Descendants<TextBlock>(row)
                            .Where(block => block.Text.Contains(marker, StringComparison.Ordinal))
                            .Cast<FrameworkElement>()
                            .Concat(Descendants<RichTextBlock>(row)
                                .Where(block => CollectText(block).Contains(marker, StringComparison.Ordinal)))
                            .ToArray();
                    visible = row is { ActualHeight: > 0, Visibility: Visibility.Visible } && scroll is not null
                        && markers.Any(block =>
                        {
                            var textBounds = block.TransformToVisual(scroll).TransformBounds(
                                new Rect(0, 0, block.ActualWidth, block.ActualHeight));
                            return block.ActualHeight > 0 && textBounds.Top >= -1
                                && textBounds.Bottom <= scroll.ActualHeight + 1;
                        });
                    diagnostics = $"{phase}: offset={scroll?.VerticalOffset}, extent={scroll?.ExtentHeight}, " +
                        $"viewport={scroll?.ActualHeight}, row={bounds}, marker={marker}, visible={visible}";
                }).WaitAsync(TimeSpan.FromSeconds(15));
                if (visible)
                    break;
            }
            output.WriteLine(diagnostics);
            Assert.True(visible, diagnostics);
        }
    }

    private static string CollectText(RichTextBlock block) => string.Concat(
        block.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
            .Select(run => run.Text));

    private static ReactorChatTimelineProps Timeline(string session, int count)
    {
        var entries = Enumerable.Range(1, count).Select(index => new ChatTimelineItem(
            $"{session}-{index}",
            index % 2 == 0 ? ChatTimelineItemKind.Assistant : ChatTimelineItemKind.User,
            index == count ? $"TAIL {session} {count}" :
                index % 4 == 0 ? $"Message {index}\n\n- First item with enough words to wrap at a narrow width.\n- Second item\n\nEnd."
                : index % 4 == 2 ? $"Message {index}\n\nItem | State\n---|---\nCopper telescope | Parked\nViolet compass | Ready\n\nA small synthetic table."
                : $"Message {index}\n\nA longer paragraph for mixed-height virtualization. The quiet observatory has three windows and a copper telescope. All names and events are fictional."
            )).ToArray();
        return new(ReactorChatTimelineMode.Timeline, new(session, entries, false, null), HistoryRevision: 1);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
