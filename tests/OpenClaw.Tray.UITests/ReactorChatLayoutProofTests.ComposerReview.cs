// Mounted regressions for the composer review: resource lifetime, ring geometry,
// translated popup layout, and bounded transcript composition.
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using System.Runtime.InteropServices.WindowsRuntime;
using static OpenClaw.Tray.UITests.TestSupport;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Fact]
    public async Task ComposerContrast_UpdatesMountedSurfacesAndClearsPooledPlaceholder()
    {
        var oldProbe = ChatThemeChanges.IsHighContrast;
        TextBox? input = null;
        try
        {
            ChatThemeChanges.IsHighContrast = static () => false;
            await WithChatAsync(800, async (surface, _, session, _) =>
            {
                Border? fadeHost = null;
                await ui.RunOnUIAsync(() =>
                {
                    input = FindControl<TextBox>(surface, "ChatComposerInput");
                    Assert.Equal("Enter to send, Shift+Enter for a new line.", AutomationProperties.GetHelpText(input));
                    Assert.NotEqual(DependencyProperty.UnsetValue, input.ReadLocalValue(TextBox.PlaceholderForegroundProperty));
                    var items = Assert.Single(FindDescendants<ItemsView>(surface));
                    fadeHost = Ancestors(items).OfType<Border>().First(border =>
                        ElementCompositionPreview.GetElementChildVisual(border) is SpriteVisual);
                    Assert.Equal(1, ElementCompositionPreview.GetElementVisual(items).Opacity);
                    var shield = FindControl<Button>(surface, "ChatComposerSandbox");
                    Assert.Equal(FlyoutPlacementMode.TopEdgeAlignedLeft, shield.Flyout.Placement);
                    session.ViewModel.SetDraft("preserve this draft");
                });
                ChatThemeChanges.IsHighContrast = static () => true;
                ChatThemeChanges.Refresh();
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal(DependencyProperty.UnsetValue, input!.ReadLocalValue(TextBox.PlaceholderForegroundProperty));
                    Assert.Null(ElementCompositionPreview.GetElementChildVisual(fadeHost!));
                    Assert.Equal("preserve this draft", input.Text);
                });
                ChatThemeChanges.IsHighContrast = static () => false;
                ChatThemeChanges.Refresh();
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.IsAssignableFrom<Brush>(input!.PlaceholderForeground);
                    Assert.IsType<SpriteVisual>(ElementCompositionPreview.GetElementChildVisual(fadeHost!));
                    Assert.Equal("preserve this draft", input.Text);
                });
            });
            // A queued refresh must not reapply the brush after Reactor has unmounted it.
            ChatThemeChanges.Refresh();
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
                Assert.Equal(DependencyProperty.UnsetValue, input!.ReadLocalValue(TextBox.PlaceholderForegroundProperty)));
        }
        finally { ChatThemeChanges.IsHighContrast = oldProbe; }
    }

    [Fact]
    public async Task ContextRing_TracksUsageAndKeepsGeometryWhileTyping()
    {
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            await ui.RunOnUIAsync(() => Assert.DoesNotContain(FindDescendants<Button>(surface),
                button => AutomationProperties.GetAutomationId(button) == "ChatContextUsage"));
            var snapshot = await provider.LoadAsync();
            Geometry? latestGeometry = null;
            foreach (var percent in new[] { 0, 25, 50, 84, 85, 95, 100 })
            {
                var thread = snapshot.Threads[0] with
                {
                    TotalTokens = percent * 100, ContextTokens = 10_000, InputTokens = 120, OutputTokens = 0,
                };
                // Zero total falls back to the input/output pair; use zero counters for the empty window.
                if (percent == 0) thread = thread with { InputTokens = 0 };
                await ui.RunOnUIAsync(() => provider.Publish(snapshot with { Threads = [thread] }));
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var ring = FindControl<Button>(surface, "ChatContextUsage");
                    Assert.EndsWith($"of 10.0K ({percent}%)", AutomationProperties.GetName(ring));
                    var paths = FindDescendants<ShapePath>(ring).ToArray();
                    Assert.Equal(2, paths.Length);
                    var track = Assert.Single(paths, path => path.Opacity < 1);
                    var fill = Assert.Single(paths, path => path.Opacity == 1);
                    Assert.Equal(2.5, track.StrokeThickness);
                    Assert.Equal(2.5, fill.StrokeThickness);
                    Assert.Equal(PenLineCap.Round, fill.StrokeStartLineCap);
                    Assert.Equal(PenLineCap.Round, fill.StrokeEndLineCap);
                    var circle = Assert.IsType<EllipseGeometry>(track.Data);
                    Assert.Equal(new Windows.Foundation.Point(8, 8), circle.Center);
                    Assert.Equal(6.5, circle.RadiusX);
                    if (percent == 0)
                        Assert.Null(fill.Data);
                    else if (percent == 100)
                        Assert.Equal(circle.Bounds, Assert.IsType<EllipseGeometry>(fill.Data).Bounds);
                    else
                    {
                        var arc = Assert.IsType<ArcSegment>(Assert.Single(
                            Assert.Single(Assert.IsType<PathGeometry>(fill.Data).Figures).Segments));
                        Assert.Equal(new Windows.Foundation.Size(6.5, 6.5), arc.Size);
                    }
                    var expectedStyle = percent >= 95 ? "ChatContextRingCriticalStyle"
                        : percent >= 85 ? "ChatContextRingWarningStyle" : "ChatContextRingStyle";
                    Assert.Same(Application.Current.Resources[expectedStyle], fill.Style);
                    latestGeometry = fill.Data;
                    Assert.True(Bounds(ring, surface).Right <= Bounds(
                        FindControl<Button>(surface, "ChatComposerModelPicker"), surface).Left);
                    Assert.DoesNotContain(FindDescendants<FrameworkElement>(surface),
                        item => AutomationProperties.GetAutomationId(item) == "ChatThroughputStrip");
                    AssertComposerBounds(surface);
                });
            }
            await ChangeFlyoutAsync(surface, "ChatContextUsage", true);
            FrameworkElement? popup = null;
            await ui.RunOnUIAsync(() =>
            {
                popup = Assert.IsAssignableFrom<FrameworkElement>(Assert.IsType<Flyout>(
                    FindControl<Button>(surface, "ChatContextUsage").Flyout).Content);
                Assert.Equal("10.0K / 10.0K · 100%", FindControl<TextBlock>(popup, "ChatContextUsageValue").Text);
                var values = FindDescendants<TextBlock>(popup).Select(text => text.Text).ToArray();
                Assert.Contains("Input 120", values);
                Assert.Contains("Output 0", values);
                session.ViewModel.SetDraft("Typing must not rebuild context geometry.");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var ring = FindControl<Button>(surface, "ChatContextUsage");
                Assert.Same(latestGeometry, Assert.Single(FindDescendants<ShapePath>(ring), path => path.Opacity == 1).Data);
                Assert.Same(popup, Assert.IsType<Flyout>(ring.Flyout).Content);
            });
            await ChangeFlyoutAsync(surface, "ChatContextUsage", false);
            foreach (var percent in new[] { 100, 25 })
            {
                var previousPaths = await ui.RunOnUIAsync(() => Task.FromResult(
                    FindDescendants<ShapePath>(FindControl<Button>(surface, "ChatContextUsage"))
                        .Select(path => (Path: path, Geometry: path.Data)).ToArray()));
                await ui.RunOnUIAsync(() => provider.Publish(snapshot));
                await SettleAsync();
                await ui.RunOnUIAsync(() => Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) == "ChatContextUsage"));
                await ui.RunOnUIAsync(() => provider.Publish(snapshot with
                {
                    Threads = [snapshot.Threads[0] with { TotalTokens = percent * 100, ContextTokens = 10_000 }],
                }));
                await SettleAsync();
                var restoredPaths = await ui.RunOnUIAsync(() =>
                {
                    var ring = FindControl<Button>(surface, "ChatContextUsage");
                    Assert.EndsWith($"of 10.0K ({percent}%)", AutomationProperties.GetName(ring));
                    var paths = FindDescendants<ShapePath>(ring).ToArray();
                    Assert.Equal(2, paths.Length);
                    foreach (var path in paths)
                        foreach (var previous in previousPaths)
                            if (!ReferenceEquals(path, previous.Path))
                                Assert.NotSame(previous.Geometry, path.Data);
                    var track = Assert.IsType<EllipseGeometry>(Assert.Single(paths, path => path.Opacity < 1).Data);
                    var fill = Assert.Single(paths, path => path.Opacity == 1);
                    if (percent == 100)
                        Assert.Equal(track.Bounds, Assert.IsType<EllipseGeometry>(fill.Data).Bounds);
                    else
                    {
                        var arc = Assert.IsType<ArcSegment>(Assert.Single(
                            Assert.Single(Assert.IsType<PathGeometry>(fill.Data).Figures).Segments));
                        Assert.Equal(new Windows.Foundation.Point(14.5, 8), arc.Point);
                    }
                    return Task.FromResult(paths.Select(path => (Path: path, Geometry: path.Data)).ToArray());
                });
                await ui.RunOnUIAsync(() => session.ViewModel.SetDraft($"Typing after restoring {percent}% context."));
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var paths = FindDescendants<ShapePath>(FindControl<Button>(surface, "ChatContextUsage")).ToArray();
                    foreach (var restored in restoredPaths)
                    {
                        Assert.Contains(restored.Path, paths);
                        Assert.Same(restored.Geometry, restored.Path.Data);
                    }
                });
            }
        });
    }

    [Fact]
    public async Task ComposerDraft_NativeEditReachesViewModelBeforeAnyComposerRender()
    {
        // TextBox raises TextChanged after the edit (after Normal-priority dispatcher work),
        // while Reactor rewrites a TextBox whose text differs from the rendered draft. A
        // composer render in that gap used to erase the edit. The synchronous assertions
        // after the edit are the regression check: the draft must reach the view model when
        // the edit happens, even while a background update is waiting to apply.
        const string draft = "Keep this native draft.";
        await WithChatAsync(800, async (surface, _, session, _) =>
        {
            TextBox? input = null;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await ui.RunOnUIAsync(() =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                var muted = session.ViewModel.IsSpeakerMuted;
                // Spin rather than block in a pumping wait so the queued update cannot apply first.
                var background = Task.Run(() => session.ViewModel.SetSpeakerMuted(!muted));
                Assert.True(SpinWait.SpinUntil(() => background.IsCompleted, TimeSpan.FromSeconds(10)));
                Assert.Equal(muted, session.ViewModel.IsSpeakerMuted);
                input.TextChanged += (_, _) => changed.TrySetResult();

                input.Text = draft;

                Assert.Equal(draft, session.ViewModel.Draft);
                Assert.Equal(!muted, session.ViewModel.IsSpeakerMuted);
                // Request a composer render before the deferred TextChanged arrives.
                session.ViewModel.SetSpeakerMuted(muted);
            });
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(draft, input!.Text);
                Assert.Equal(draft, session.ViewModel.Draft);
            });
        });
    }

    [Fact]
    public async Task ComposerDraft_LateTextChangedDoesNotRestoreReplacedText()
    {
        // A programmatic draft change (slash commit, clear after send) can land before the
        // deferred TextChanged of an earlier edit. When that event runs before the composer
        // renders the new draft (the usual order), it reads the old TextBox text and must
        // not write it back. If the render happens to run first, this test cannot detect it.
        const string replaced = "Replaced by the composer.";
        await WithChatAsync(800, async (surface, _, session, _) =>
        {
            TextBox? input = null;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await ui.RunOnUIAsync(() =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                input.TextChanged += (_, _) => changed.TrySetResult();
                input.Text = "Typed before the replacement.";
                session.ViewModel.SetDraft(replaced);
            });
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => input!.Text == replaced && session.ViewModel.Draft == replaced,
                "composer renders the replacement draft",
                () => $"text='{input!.Text}', draft='{session.ViewModel.Draft}'"));
        });
    }

    [Theory]
    [InlineData("fr-fr")]
    [InlineData("pt-br")]
    public async Task ContextPopup_WrapsTranslatedEnlargedText(string language)
    {
        var previousLanguage = LocalizationHelper.CurrentLanguage;
        LocalizationHelper.SetLanguageOverride(language);
        try
        {
            await WithChatAsync(320, async (surface, _, _, provider) =>
            {
                var snapshot = await provider.LoadAsync();
                await ui.RunOnUIAsync(() => provider.Publish(snapshot with
                {
                    Threads = [snapshot.Threads[0] with
                    {
                        TotalTokens = 975_000, ContextTokens = 1_000_000, InputTokens = 123_400, OutputTokens = 56_700,
                    }],
                }));
                await SettleAsync();
                await ui.RunOnUIAsync(() => AssertComposerBounds(surface));
                await ChangeFlyoutAsync(surface, "ChatContextUsage", true);
                FrameworkElement? popup = null;
                double normalHeight = 0;
                await ui.RunOnUIAsync(() =>
                {
                    popup = Assert.IsAssignableFrom<FrameworkElement>(Assert.IsType<Flyout>(
                        FindControl<Button>(surface, "ChatContextUsage").Flyout).Content);
                    normalHeight = popup.ActualHeight;
                    foreach (var text in FindDescendants<TextBlock>(popup))
                        text.FontSize *= 2; // Local text-enlargement proxy; never change the user's Windows setting.
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(popup!.ActualHeight > normalHeight);
                    foreach (var text in FindDescendants<TextBlock>(popup))
                    {
                        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                        Assert.False(text.IsTextTrimmed);
                        var bounds = Bounds(text, popup);
                        Assert.InRange(bounds.Left, 0, popup.ActualWidth);
                        Assert.InRange(bounds.Right, 0, popup.ActualWidth + 1);
                        Assert.InRange(bounds.Bottom, 0, popup.ActualHeight + 1);
                    }
                });
                await CaptureAsync(popup!, $"ContextPopup-enlarged-{language}");
            });
        }
        finally { LocalizationHelper.SetLanguageOverride(previousLanguage); }
    }

    [Fact]
    public async Task TranscriptFade_OnlyRedirectsEdgeAndRestoresReplacedChildren()
    {
        await ui.ResetContainerAsync();
        Border? host = null;
        Border? clipper = null;
        TextBlock? content = null;
        var previousProbe = ChatThemeChanges.IsHighContrast;
        try
        {
            ChatThemeChanges.IsHighContrast = static () => false;
            await ui.RunOnUIAsync(() =>
            {
                content = new TextBlock { Text = string.Join("\n", Enumerable.Repeat("Visible transcript", 24)), FontSize = 14 };
                clipper = new Border { Child = content };
                host = new Border { Child = clipper, Width = 400, Height = 400 };
                ui.Container.Children.Add(host);
                ChatTranscriptFade.Attach(host);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                var sprite = Assert.IsType<SpriteVisual>(ElementCompositionPreview.GetElementChildVisual(host!));
                Assert.Equal(32, sprite.Size.Y);
                var mask = Assert.IsType<CompositionMaskBrush>(sprite.Brush);
                var source = Assert.IsType<CompositionVisualSurface>(Assert.IsType<CompositionSurfaceBrush>(mask.Source).Surface);
                Assert.Equal(32, source.SourceSize.Y);
                Assert.InRange(source.SourceOffset.Y, 367.999f, 368.001f);
                Assert.Equal(1, ElementCompositionPreview.GetElementVisual(content!).Opacity);
                Assert.Equal(32, Assert.IsType<InsetClip>(ElementCompositionPreview.GetElementVisual(clipper!).Clip).BottomInset);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(host);
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                Assert.True(pixels.Where((_, i) => i % 4 == 3).Count(alpha => alpha > 64) > 500,
                    "RenderTargetBitmap must still capture readable transcript ink, not an almost-transparent copy.");
                clipper!.Child = new TextBlock { Text = "Replacement transcript", FontSize = 18 };
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var sprite = Assert.IsType<SpriteVisual>(ElementCompositionPreview.GetElementChildVisual(host!));
                var source = Assert.IsType<CompositionVisualSurface>(Assert.IsType<CompositionSurfaceBrush>(
                    Assert.IsType<CompositionMaskBrush>(sprite.Brush).Source).Surface);
                Assert.Equal(ElementCompositionPreview.GetElementVisual(clipper!.Child), source.SourceVisual);
                Assert.Equal(1, ElementCompositionPreview.GetElementVisual(content!).Opacity);
                ChatTranscriptFade.Detach(host!);
                Assert.Null(ElementCompositionPreview.GetElementVisual(clipper).Clip);
                Assert.Null(ElementCompositionPreview.GetElementChildVisual(host!));
            });
            ChatThemeChanges.Refresh();
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Null(ElementCompositionPreview.GetElementChildVisual(host!)));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null) ChatTranscriptFade.Detach(host);
                ui.Container.Children.Clear();
            });
            ChatThemeChanges.IsHighContrast = previousProbe;
        }
    }

    [Fact]
    public async Task TranscriptFade_BringIntoViewLandsTargetsAboveTheBand()
    {
        await ui.ResetContainerAsync();
        Border? host = null;
        ScrollViewer? scroll = null;
        Border? target = null;
        var oldProbe = ChatThemeChanges.IsHighContrast;
        try
        {
            ChatThemeChanges.IsHighContrast = static () => false;
            await ui.RunOnUIAsync(() =>
            {
                var panels = new StackPanel();
                panels.Children.Add(new Border { Height = 600 });
                panels.Children.Add(target = new Border { Height = 20, Background = new SolidColorBrush(Colors.Red) });
                panels.Children.Add(new Border { Height = 600 });
                scroll = new ScrollViewer
                {
                    Content = panels, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                host = new Border
                {
                    Child = new Border { Child = scroll }, Width = 400, Height = 400,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                ui.Container.Children.Add(host);
                ChatTranscriptFade.Attach(host);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll!.ScrollableHeight > 0, "fade probe scroller laid out"));
            await ui.RunOnUIAsync(() => target!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
            // Minimal downward bring would align the target's bottom (620) with the viewport
            // bottom (offset 220), inside the 32-pixel fade; it must clear the band instead.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Math.Abs(scroll!.VerticalOffset - 252) <= 1, "bring clears the fade band",
                () => $"offset={scroll!.VerticalOffset}"));

            await ui.RunOnUIAsync(() =>
            {
                ChatTranscriptFade.Detach(host!);
                Assert.True(scroll!.ChangeView(null, 0, null, disableAnimation: true));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll!.VerticalOffset <= 1, "probe returns to the top", () => $"offset={scroll!.VerticalOffset}"));
            await ui.RunOnUIAsync(() => target!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Math.Abs(scroll!.VerticalOffset - 220) <= 1, "detached bring is minimal",
                () => $"offset={scroll!.VerticalOffset}"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null) ChatTranscriptFade.Detach(host);
                ui.Container.Children.Clear();
            });
            ChatThemeChanges.IsHighContrast = oldProbe;
        }
    }

    [Fact]
    public async Task TranscriptFade_ScrollingRefreshesComposedEdge()
    {
        await ui.ResetContainerAsync();
        Border? host = null;
        ScrollViewer? scroll = null;
        Action? restore = null;
        var oldProbe = ChatThemeChanges.IsHighContrast;
        var captureDirectory = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_DIR");
        try
        {
            ChatThemeChanges.IsHighContrast = static () => false;
            await ui.RunOnUIAsync(() =>
            {
                var panels = new StackPanel();
                panels.Children.Add(new Border { Height = 500, Background = new SolidColorBrush(Colors.Red) });
                panels.Children.Add(new Border { Height = 500, Background = new SolidColorBrush(Colors.Blue) });
                scroll = new ScrollViewer
                {
                    Content = panels, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                host = new Border
                {
                    Child = new Border { Child = scroll }, Width = 400, Height = 400,
                    Background = new SolidColorBrush(Colors.Black),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                ui.Container.Children.Add(host);
                ChatTranscriptFade.Attach(host);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                restore = await MountedControlCapture.ArrangeSceneAsync(ui.TestWindow, host!, "fade-scroll");
                Assert.Equal(0, scroll!.VerticalOffset);
                await MountedControlCapture.OptionalAsync(ui.TestWindow, "fade-scroll-red");
                Assert.True(scroll.ChangeView(null, 600, null, disableAnimation: true));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal(600, scroll!.VerticalOffset, precision: 3);
                Assert.Equal(1, ElementCompositionPreview.GetElementVisual(scroll).Opacity);
                await MountedControlCapture.OptionalAsync(ui.TestWindow, "fade-scroll-blue");
                if (!string.IsNullOrEmpty(captureDirectory))
                {
                    using var first = new System.Drawing.Bitmap(System.IO.Path.Combine(captureDirectory, "fade-scroll-red.png"));
                    using var second = new System.Drawing.Bitmap(System.IO.Path.Combine(captureDirectory, "fade-scroll-blue.png"));
                    var root = Assert.IsAssignableFrom<FrameworkElement>(ui.TestWindow.Content);
                    var point = host!.TransformToVisual(root).TransformPoint(new(host.ActualWidth / 2, host.ActualHeight - 16));
                    var scale = root.XamlRoot.RasterizationScale;
                    var client = ui.TestWindow.AppWindow.ClientSize;
                    var border = (first.Width - client.Width) / 2d;
                    var top = first.Height - client.Height - border;
                    var x = (int)Math.Round(border + point.X * scale);
                    var y = (int)Math.Round(top + point.Y * scale);
                    var before = first.GetPixel(x, y);
                    var after = second.GetPixel(x, y);
                    Assert.True(before.R > 20 && before.R > before.B * 2,
                        $"Expected red ink in the fade band, got {before}.");
                    Assert.True(after.B > 20 && after.B > after.R * 2,
                        $"Scrolled fade band must update to blue, not retain old pixels: {after}.");
                    Assert.InRange(before.R, 70, 190);
                    Assert.InRange(after.B, 70, 190);
                }
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null) ChatTranscriptFade.Detach(host);
                restore?.Invoke();
                ui.Container.Children.Clear();
            });
            ChatThemeChanges.IsHighContrast = oldProbe;
        }
    }
}
