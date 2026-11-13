// Mounted production rail proof. Run with --filter FullyQualifiedName~HistoryRail_.
// The interactive case requires OPENCLAW_RUN_KEYBOARD_PROOF=1 and an owned desktop.
using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    private static ImmutableList<ChatTimelineItem> HistoryRailEntries(int count) =>
        Enumerable.Range(0, count).Select(index => new ChatTimelineItem(
            $"history-{index}", index % 2 == 0 ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
            index == 1 ? string.Join("\n\n", Enumerable.Repeat("A deliberately taller message.", 16))
                : $"History message {index}.")).ToImmutableList();

    [Fact]
    public void HistoryRail_KeyboardStartsAtReadingPositionAndRetainsPendingSelection()
    {
        ChatHistoryAnchor[] anchors =
        [
            new(0, "first", "User", "First"),
            new(3, "middle", "Assistant", "Middle"),
            new(7, "last", "User", "Last"),
        ];
        HashSet<int> visible = [3];
        Assert.Equal(0, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Up, anchors, null, visible));
        Assert.Equal(2, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Down, anchors, null, visible));
        Assert.Equal(1, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Enter, anchors, null, visible));
        Assert.Equal(1, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Up, anchors, "last", visible));
        Assert.Equal(0, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Home, anchors, "last", visible));
        Assert.Equal(2, ChatHistoryNavigation.KeyboardIndex(VirtualKey.End, anchors, "first", visible));
        visible = [0];
        Assert.Equal(1, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Down, anchors, null, visible));
        Assert.Equal(-1, ChatHistoryNavigation.KeyboardIndex(VirtualKey.Down, [], null, visible));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task HistoryRail_ActualPointerPositionOverridesShiftedPreviewAndKeepsLiveContent(string theme)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var themeScope = new ChatThemeProofScope(theme);
            var surface = new Grid { Width = 100, Height = 180, RequestedTheme = themeScope.ElementTheme };
            var rail = new AnnotatedScrollBar
            {
                Width = 40,
                Height = 180,
                Style = (Style)Application.Current.Resources["ChatHistoryScrollBarStyle"],
            };
            surface.Children.Add(rail);
            using var navigation = new ChatHistoryNavigation(new ItemsView(), _ => { });
            var anchors = Enumerable.Range(0, 40)
                .Select(index => new ChatHistoryAnchor(index, $"pointer-{index}", "Assistant", $"Preview {index}"))
                .ToArray();
            navigation.Update(anchors);
            navigation.SetScrollBar(rail);
            ui.Container.Children.Add(surface);
            try
            {
                await WaitForRenderedConditionAsync(() => rail.Labels.Count > 3
                    && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                        presenter.Content is AnnotatedScrollBarLabel) == rail.Labels.Count,
                    "the native pointer rail is mounted");
                var root = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(rail, 0));
                var labels = Assert.IsType<Grid>(root.FindName("PART_LabelsGrid"));
                var thumb = Assert.IsAssignableFrom<FrameworkElement>(root.FindName("PART_VerticalThumb"));
                var preview = Assert.IsType<ContentPresenter>(root.FindName("PART_TooltipContentPresenter"));
                var label = rail.Labels[rail.Labels.Count / 2];
                var expected = Assert.IsType<ChatHistoryAnchor>(label.Content);
                var position = label.ScrollOffset * (labels.ActualHeight - thumb.ActualHeight);
                preview.Content = anchors[0];
                navigation.UpdatePointerPreview(position);
                Assert.Equal(expected.Key, Assert.IsType<ChatHistoryAnchor>(preview.Content).Key);
                await WaitForRenderedConditionAsync(() =>
                    FindDescendants<ContentPresenter>(rail).Any(presenter =>
                        ReferenceEquals(presenter.Content, label)
                        && FindDescendants<Border>(presenter).Any(border => border.ActualWidth >= 31)),
                    "actual pointer selection expands the intended tick");
                var tick = Assert.Single(FindDescendants<Border>(FindDescendants<ContentPresenter>(rail)
                    .Single(presenter => ReferenceEquals(presenter.Content, label))));
                AssertHistoryTickInk(tick, Assert.IsType<Grid>(root));

                var originalLabels = rail.Labels.ToArray();
                anchors = anchors.Select(anchor => anchor.Key == expected.Key
                    ? anchor with { Sender = "Updated sender", Preview = "Updated preview" } : anchor).ToArray();
                navigation.Update(anchors);
                Assert.True(originalLabels.SequenceEqual(rail.Labels));
                var updated = Assert.IsType<ChatHistoryAnchor>(preview.Content);
                Assert.Equal("Updated sender", updated.Sender);
                Assert.Equal("Updated preview", updated.Preview);
                Assert.Equal(updated, navigation.AnchorAt(label.ScrollOffset));
                Assert.Null(navigation.AnchorAtPointer(double.NaN));
            }
            finally
            {
                ui.Container.Children.Remove(surface);
            }
        });
    }

    [Fact]
    public async Task HistoryRail_GrowsWithMessagesAndCapsAtTheResizedTranscriptViewport()
    {
        var provider = new ProofProvider();
        var snapshot = await provider.LoadAsync();
        provider.Publish(WorkSnapshot(snapshot, "proof", HistoryRailEntries(2), active: false));
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            foreach (var count in new[] { 2, 7, 240 })
            {
                await ui.RunOnUIAsync(() => provider.Publish(
                    WorkSnapshot(snapshot, "proof", HistoryRailEntries(count), active: false)));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                    var visibleTicks = FindDescendants<ContentPresenter>(rail).Count(presenter =>
                        presenter.Content is AnnotatedScrollBarLabel &&
                        presenter.Visibility == Visibility.Visible);
                    var parent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(rail));
                    var viewport = parent.ActualHeight;
                    return viewport > 0 && rail.Labels.Count > 1
                        && (count > 7 || rail.Labels.Count == count)
                        && rail.Labels[^1].Content is ChatHistoryAnchor { RowIndex: var last } && last == count - 1
                        && Math.Abs(rail.ActualHeight - Math.Min((count - 1) * 10 + 16, viewport)) <= 1
                        && visibleTicks == rail.Labels.Count;
                }, $"{count} message annotations render native ticks"));
                await ui.RunOnUIAsync(async () =>
                {
                    var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                    var items = Assert.Single(FindDescendants<ItemsView>(host));
                    await WaitForRenderedConditionAsync(() =>
                    {
                        var scroll = items.ScrollView;
                        if (scroll.State != ScrollingInteractionState.Idle
                            || scroll.Content is not ItemsRepeater repeater
                            || repeater.TryGetElement(count - 1) is not FrameworkElement { ActualHeight: > 0 } tail)
                            return false;
                        var bounds = Bounds(tail, scroll);
                        return bounds.Bottom > 0 && bounds.Bottom <= scroll.ViewportHeight + 2;
                    }, "the new conversation tail is visible before checking viewport tick tones");
                    await WaitForStableHistoryTicksAsync(rail);
                    var parent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(rail));
                    // This fixture has no history-error banner. Its allocated height stays
                    // authoritative while Reactor replaces RowDefinitions during reconciliation.
                    var viewport = parent.ActualHeight;
                    var expectedHeight = Math.Min((count - 1) * 10 + 16, viewport);
                    Assert.InRange(rail.ActualHeight, expectedHeight - 1, expectedHeight + 1);
                    Assert.Equal(1, rail.Opacity);
                    AssertRailClearance(surface, host, rail);
                    var ticks = FindDescendants<ContentPresenter>(rail)
                        .Where(presenter => presenter.Content is AnnotatedScrollBarLabel &&
                            presenter.Visibility == Visibility.Visible).ToArray();
                    Assert.True(ticks.Length > 1, "The native template must actually render ticks.");
                    Assert.Empty(FindDescendants<TextBlock>(rail));
                    AssertHistoryTickGaps(rail);
                    var anchors = rail.Labels.Select(label => Assert.IsType<ChatHistoryAnchor>(label.Content)).ToArray();
                    Assert.Equal(0, anchors[0].RowIndex);
                    Assert.Equal("History message 0.", anchors[0].Preview);
                    Assert.Equal(count - 1, anchors[^1].RowIndex);
                    Assert.StartsWith(count == 2 ? "A deliberately taller message." : $"History message {count - 1}.",
                        anchors[^1].Preview);
                    if (count == 240)
                    {
                        Assert.True(anchors.Length < count, "Long histories must skip intermediate messages.");
                        Assert.True(anchors.Zip(anchors.Skip(1)).All(pair =>
                            pair.Second.RowIndex > pair.First.RowIndex));
                    }
                    if (count == 7)
                    {
                        Assert.Equal(7, ticks.Length);
                        var tops = ticks.Select(tick => Bounds(tick, rail).Top).Order().ToArray();
                        // WinUI rounds each label independently onto physical pixels.
                        var tolerance = 1 / surface.XamlRoot.RasterizationScale;
                        for (var index = 1; index < tops.Length - 1; index++)
                        {
                            var expectedTop = tops[0] + (tops[^1] - tops[0]) * index / 6;
                            Assert.InRange(tops[index] - expectedTop, -tolerance, tolerance);
                        }
                        foreach (var presenter in ticks)
                        {
                            var tick = Assert.Single(FindDescendants<Border>(presenter));
                            Assert.Equal(NativeProofLayout.RoundedViewSize(8, surface.XamlRoot.RasterizationScale),
                                tick.ActualWidth, 5);
                            Assert.Equal(NativeProofLayout.RoundedViewSize(2, surface.XamlRoot.RasterizationScale),
                                tick.ActualHeight, 5);
                            Assert.Equal(new CornerRadius(1), tick.CornerRadius);
                        }
                        var quiet = Assert.IsType<SolidColorBrush>(
                            Assert.Single(FindDescendants<Border>(ticks[0])).Background);
                        var visible = Assert.IsType<SolidColorBrush>(
                            Assert.Single(FindDescendants<Border>(ticks[^1])).Background);
                        Assert.Equal(0.4, quiet.Opacity, 6);
                        Assert.Equal(1, visible.Opacity);
                        Assert.Equal(quiet.Color, visible.Color);
                    }
                });
                if (count == 240)
                    await CaptureAsync(surface, "HistoryRail-Spaced-240");
            }
            var previousCount = 0;
            await ui.RunOnUIAsync(() =>
            {
                previousCount = Assert.Single(FindDescendants<AnnotatedScrollBar>(host)).Labels.Count;
                surface.Height = 460;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                var parent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(rail));
                var viewport = parent.ActualHeight;
                return viewport > 0 && viewport < 460 && Math.Abs(rail.ActualHeight - viewport) <= 1
                    && rail.Labels.Count < previousCount && rail.Labels.Count > 1
                    && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                        presenter.Content is AnnotatedScrollBarLabel &&
                        presenter.Visibility == Visibility.Visible) == rail.Labels.Count;
            }, "the rail fits the resized transcript cell"));
            await ui.RunOnUIAsync(async () =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                await WaitForStableHistoryTicksAsync(rail);
                var parent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(rail));
                var viewport = parent.ActualHeight;
                Assert.InRange(rail.ActualHeight, viewport - 1, viewport + 1);
                Assert.True(rail.ActualHeight < 460);
                Assert.InRange(rail.Labels.Count, 2, previousCount - 1);
                AssertHistoryTickGaps(rail);
                Assert.Equal(239, Assert.IsType<ChatHistoryAnchor>(rail.Labels[^1].Content).RowIndex);
            });
            await CaptureAsync(surface, "HistoryRail-Spaced-240-short");
            await ui.RunOnUIAsync(() =>
            {
                previousCount = Assert.Single(FindDescendants<AnnotatedScrollBar>(host)).Labels.Count;
                surface.Height = 800;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                return rail.Labels.Count > previousCount
                    && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                        presenter.Content is AnnotatedScrollBarLabel &&
                        presenter.Visibility == Visibility.Visible) == rail.Labels.Count;
            }, "growing the viewport renders the expanded tick budget"));
            await ui.RunOnUIAsync(async () =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                await WaitForStableHistoryTicksAsync(rail);
                Assert.True(rail.Labels.Count > previousCount, "Growing the viewport should restore more ticks.");
                AssertHistoryTickGaps(rail);
            });
        }, renderProvider: provider);
    }

    private static async Task WaitForStableHistoryTicksAsync(AnnotatedScrollBar rail)
    {
        global::Windows.Foundation.Rect[]? previous = null;
        const double tolerance = 0.01;
        await WaitForRenderedConditionAsync(() =>
        {
            var bounds = FindDescendants<ContentPresenter>(rail)
                .Where(presenter => presenter.Content is AnnotatedScrollBarLabel
                    && presenter.Visibility == Visibility.Visible)
                .SelectMany(FindDescendants<Border>)
                .Select(tick => Bounds(tick, rail)).ToArray();
            if (bounds.Length != rail.Labels.Count || bounds.Length == 0
                || bounds.Any(rect => rect.Width <= 0 || rect.Height <= 0))
            {
                previous = null;
                return false;
            }
            var stable = previous is not null && previous.Length == bounds.Length
                && previous.Zip(bounds).All(pair =>
                    Math.Abs(pair.First.X - pair.Second.X) <= tolerance
                    && Math.Abs(pair.First.Y - pair.Second.Y) <= tolerance
                    && Math.Abs(pair.First.Width - pair.Second.Width) <= tolerance
                    && Math.Abs(pair.First.Height - pair.Second.Height) <= tolerance);
            previous = bounds;
            return stable;
        }, "native history tick bounds stop moving");
    }

    private static void AssertHistoryTickInk(Border tick, Grid themeRoot)
    {
        var reference = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "Foreground=\"{ThemeResource ChatTextBrush}\" />");
        themeRoot.Children.Add(reference);
        try
        {
            var actual = Assert.IsType<SolidColorBrush>(tick.Background);
            var expected = Assert.IsType<SolidColorBrush>(reference.Foreground);
            Assert.Equal(themeRoot.ActualTheme, tick.ActualTheme);
            Assert.Equal(expected.Color, actual.Color);
            Assert.Equal(expected.Opacity, actual.Opacity);
        }
        finally
        {
            themeRoot.Children.Remove(reference);
        }
    }

    private static void AssertHistoryTickGaps(AnnotatedScrollBar rail)
    {
        var ticks = FindDescendants<ContentPresenter>(rail)
            .Where(presenter => presenter.Content is AnnotatedScrollBarLabel &&
                presenter.Visibility == Visibility.Visible)
            .Select(presenter => Bounds(Assert.Single(FindDescendants<Border>(presenter)), rail))
            .OrderBy(bounds => bounds.Top).ToArray();
        Assert.Equal(rail.Labels.Count, ticks.Length);
        for (var index = 1; index < ticks.Length; index++)
            Assert.True(ticks[index].Top - ticks[index - 1].Bottom >= 4 - 0.0001,
                $"Ticks {index - 1} and {index} need at least 4 DIPs of clear space; "
                + $"actual gap={ticks[index].Top - ticks[index - 1].Bottom}, "
                + $"count={ticks.Length}, railHeight={rail.ActualHeight}.");
    }

    [Fact]
    public async Task HistoryRail_StreamingPreviewKeepsExistingTickPresenters()
    {
        var provider = new ProofProvider();
        var snapshot = await provider.LoadAsync();
        var entries = HistoryRailEntries(240);
        entries = entries.SetItem(239, entries[239] with { Text = "Starting", IsStreaming = true });
        provider.Publish(WorkSnapshot(snapshot, "proof", entries, active: true));
        await WithChatAsync(800, async (_, host, _, _) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                await WaitForRenderedConditionAsync(() =>
                    rail.Labels.Count > 1 && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                        presenter.Content is AnnotatedScrollBarLabel) == rail.Labels.Count,
                    "streaming history ticks are mounted");
                var labels = rail.Labels.ToArray();
                var presenters = FindDescendants<ContentPresenter>(rail)
                    .Where(presenter => presenter.Content is AnnotatedScrollBarLabel).ToArray();
                var interrupted = false;
                void Observe(object? sender, object args) =>
                    interrupted |= !presenters.SequenceEqual(FindDescendants<ContentPresenter>(rail)
                        .Where(presenter => presenter.Content is AnnotatedScrollBarLabel));
                CompositionTarget.Rendering += Observe;
                try
                {
                    foreach (var text in new[] { "A streaming answer", "A streaming answer with more detail",
                        new string('x', 260) })
                    {
                        entries = entries.SetItem(239, entries[239] with { Text = text });
                        provider.Publish(WorkSnapshot(snapshot, "proof", entries, active: true));
                        await WaitForRenderedConditionAsync(() =>
                            FindDescendants<TextBlock>(host).Any(block => block.Text == text)
                            || FindDescendants<RichTextBlock>(host).Any(block => CollectText(block) == text),
                            "the streamed response is rendered");
                        Assert.True(labels.SequenceEqual(rail.Labels),
                            "Preview text must not replace the native labels.");
                        Assert.True(presenters.SequenceEqual(FindDescendants<ContentPresenter>(rail)
                            .Where(presenter => presenter.Content is AnnotatedScrollBarLabel)));
                    }
                    Assert.False(interrupted, "Streaming must not remove ticks between rendered frames.");
                }
                finally
                {
                    CompositionTarget.Rendering -= Observe;
                }
            });
        }, renderProvider: provider);
    }

    [Fact]
    public async Task HistoryRail_KeepsTicksMountedWhileVirtualizedHistoryScrolls()
    {
        var provider = new ProofProvider();
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), "proof", HistoryRailEntries(240), active: false));
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                return rail.Labels.Count > 1 && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                    presenter.Content is AnnotatedScrollBarLabel && presenter.Visibility == Visibility.Visible)
                    == rail.Labels.Count;
            }, "all history ticks are rendered before scrolling"));
            await ui.RunOnUIAsync(async () =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                var scroll = Assert.Single(FindDescendants<ItemsView>(host)).ScrollView;
                Assert.Equal(ScrollingScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
                var ticks = FindDescendants<ContentPresenter>(rail)
                    .Where(presenter => presenter.Content is AnnotatedScrollBarLabel).ToArray();
                var interruptedFrames = 0;
                var extentChanges = 0;
                var initialOffset = scroll.VerticalOffset;
                var movedBeyondViewport = false;
                void ObserveTicks(object? sender, object args)
                {
                    movedBeyondViewport |= Math.Abs(scroll.VerticalOffset - initialOffset) > scroll.ViewportHeight;
                    var rendered = FindDescendants<ContentPresenter>(rail)
                        .Where(presenter => presenter.Content is AnnotatedScrollBarLabel
                            && presenter.Visibility == Visibility.Visible).ToArray();
                    if (!ticks.SequenceEqual(rendered))
                        interruptedFrames++;
                }
                void ObserveExtent(ScrollView sender, object args) => extentChanges++;
                CompositionTarget.Rendering += ObserveTicks;
                scroll.ExtentChanged += ObserveExtent;
                try
                {
                    scroll.ScrollTo(0, 0,
                        new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                    await WaitForRenderedConditionAsync(() => movedBeyondViewport && extentChanges > 0,
                        "scrolling changes the realized history region and its estimated extent");
                    Assert.Equal(0, interruptedFrames);
                    AssertHistoryTickGaps(rail);
                }
                finally
                {
                    CompositionTarget.Rendering -= ObserveTicks;
                    scroll.ExtentChanged -= ObserveExtent;
                }
            });
        }, renderProvider: provider);
    }

    [NativeKeyboardTheory]
    [InlineData("Light", 7)]
    [InlineData("Dark", 7)]
    [InlineData("Light", 240)]
    [InlineData("Dark", 240)]
    [Trait("Category", "NativeKeyboard")]
    public async Task HistoryRail_HoverClickAndKeyboardNavigateTheKeyedMessage(string theme, int count)
    {
        using var environment = new EnvironmentScope("OPENCLAW_PROOF_THEME", theme);
        var provider = new ProofProvider();
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), "proof", HistoryRailEntries(count), active: false));
        await WithChatAsync(800, async (surface, host, session, _) =>
        {
            AnnotatedScrollBar? rail = null;
            ItemsView? items = null;
            ChatHistoryAnchor? destination = null;
            double tickY = 0;
            int tickIndex = 0;
            nint window = 0;
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                surface.Height = 520;
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.ResizeClient(new((int)(surface.Width * scale), (int)(surface.Height * scale)));
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow);
                rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                items = Assert.Single(FindDescendants<ItemsView>(host));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                rail!.ActualHeight > 0 && rail.ActualHeight <= surface.Height && rail.Labels.Count > 1
                && FindDescendants<ContentPresenter>(rail).Count(presenter =>
                    presenter.Content is AnnotatedScrollBarLabel && presenter.Visibility == Visibility.Visible)
                    == rail.Labels.Count,
                "resized history ticks are ready for native input"));
            await NativeKeyboardProof.ActivateAsync(window);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(1, rail!.Opacity);
                var ticks = FindDescendants<ContentPresenter>(rail!)
                    .Where(presenter => presenter.Content is AnnotatedScrollBarLabel &&
                        presenter.Visibility == Visibility.Visible).ToArray();
                tickIndex = ticks.Length / 2;
                var label = Assert.IsType<AnnotatedScrollBarLabel>(ticks[tickIndex].Content);
                destination = Assert.IsType<ChatHistoryAnchor>(label.Content);
                var bounds = Bounds(ticks[tickIndex], rail!);
                tickY = bounds.Top + bounds.Height / 2;
            });
            await CaptureAsync(surface, $"HistoryRail-{theme}-{count}-default");
            await ui.RunOnUIAsync(() =>
                OnboardingNativeProof.MovePointer(_captureWindow!, rail!, rail!.ActualWidth / 2, tickY));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
                    .Any(popup => FindDescendants<TextBlock>(popup.Child).Any(text => text.Text == destination!.Preview)),
                "hover displays the sampled tick's message preview despite uneven message heights"));
            await ui.RunOnUIAsync(() =>
            {
                var ticks = FindDescendants<ContentPresenter>(rail!)
                    .Where(presenter => presenter.Content is AnnotatedScrollBarLabel &&
                        presenter.Visibility == Visibility.Visible)
                    .Select(presenter => Assert.Single(FindDescendants<Border>(presenter))).ToArray();
                Assert.Equal(rail!.Labels.Count, ticks.Length);
                for (var index = 0; index < ticks.Length; index++)
                {
                    var expected = Math.Abs(index - tickIndex) switch
                    {
                        0 => 32d,
                        1 => 24d,
                        2 => 16d,
                        3 => 12d,
                        _ => 8d,
                    };
                    Assert.Equal(NativeProofLayout.RoundedViewSize(expected, surface.XamlRoot.RasterizationScale),
                        ticks[index].ActualWidth, 5);
                }
                AssertHistoryTickInk(ticks[tickIndex], Assert.IsType<Grid>(VisualTreeHelper.GetChild(rail, 0)));
                AssertHistoryTickGaps(rail);
            });
            await CaptureAsync(surface, $"HistoryRail-{theme}-{count}-hover");
            await ui.RunOnUIAsync(() =>
                OnboardingNativeProof.TapPointer(_captureWindow!, rail!, rail!.ActualWidth / 2, tickY));
            await AwaitHistoryRowAsync($"history-{destination!.RowIndex}");
            await CaptureAsync(surface, $"HistoryRail-{theme}-{count}-middle");
            await ui.RunOnUIAsync(() => Assert.True(rail!.Focus(FocusState.Keyboard)));
            foreach (var (key, id) in new[]
            {
                (VirtualKey.Home, "history-0"),
                (VirtualKey.Down, "history-1"),
                (VirtualKey.End, $"history-{count - 1}"),
            })
            {
                NativeKeyboardProof.Press(window, key);
                await AwaitHistoryRowAsync(id);
                await ui.RunOnUIAsync(() =>
                    Assert.Contains(id == "history-1" ? "A deliberately taller message." : $"History message {id[8..]}.",
                        AutomationProperties.GetItemStatus(rail!)));
            }
            await CaptureAsync(surface, $"HistoryRail-{theme}-{count}-latest");

            async Task AwaitHistoryRowAsync(string id) =>
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    var scroll = items!.ScrollView;
                    if (scroll.State != ScrollingInteractionState.Idle)
                        return false;
                    var index = int.Parse(id.AsSpan("history-".Length), System.Globalization.CultureInfo.InvariantCulture);
                    if (scroll.Content is not ItemsRepeater repeater
                        || repeater.TryGetElement(index) is not FrameworkElement row
                        || repeater.GetElementIndex(row) != index)
                        return false;
                    var expectedText = index == 1 ? "A deliberately taller message." : $"History message {index}.";
                    var renderedText = string.Concat(FindDescendants<TextBlock>(row).Select(text => text.Text)
                        .Concat(FindDescendants<RichTextBlock>(row).Select(CollectText)));
                    if (!renderedText.Contains(expectedText, StringComparison.Ordinal))
                        return false;
                    var bounds = Bounds(row, scroll);
                    return index == count - 1
                        ? bounds.Bottom <= scroll.ViewportHeight + 2 && bounds.Bottom > 0
                        : Math.Abs(bounds.Top) <= 2;
                }, $"history destination {id} is visible at its intended position"));
        }, renderProvider: provider, interactiveKeyboard: true);
    }
}
