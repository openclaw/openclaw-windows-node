using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenClawTray.Chat;
using OpenClawTray.Chat.Markdown;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "NativeMarkdownSelectionProof")]
public sealed class ChatMessageTextSelectionProofTests(UIThreadFixture ui)
{
    public static IEnumerable<object[]> Themes
    {
        get
        {
            yield return ["Light"];
            if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1")
            {
                yield return ["Dark"];
                yield return ["HighContrast"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task DisclosureSelection_VisibleSummaryAndExpandedBody(string theme)
    {
        await ui.ResetContainerAsync();
        ChatThemeProofScope? resources = null;
        NativeChatMessageText? owner = null;
        ReactorHostControl? host = null;
        string? copied = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                host = new ReactorHostControl { Width = 620, RequestedTheme = resources.ElementTheme };
                var element = ReactorChatTimeline.BuildSafeMarkdown(
                    "Before paragraph.\n\n<details><summary>Visible summary title</summary>\n\nHidden body.\n\n</details>\n\nAfter paragraph.",
                    text => { copied = text; return true; });
                host.Content = host.Reconciler.Mount(element, () => { });
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                owner = Assert.Single(FindLogical<NativeChatMessageText>(host!));
                owner.RefreshDocument();
                var provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetPattern(PatternInterface.Text));
                Console.WriteLine($"Rendered collapsed document: {owner.Document.Text.Replace("\n", "\\n")}");
                provider.DocumentRange.Select();
                owner.ProcessKey(VirtualKey.C, control: true, shift: false);
                Assert.Equal("Before paragraph.\n\nVisible summary title\n\nAfter paragraph.", copied);
                Assert.True(owner.BeginSelection(PointForText(owner, provider, "summary", false)));
                owner.ExtendSelection(PointForText(owner, provider, "summary", true));
                owner.EndSelection();
                owner.ProcessKey(VirtualKey.C, control: true, shift: false);
                Assert.Equal("summary", copied);
                provider.GetSelection()[0].GetBoundingRectangles(out var rectangles);
                Assert.NotEmpty(rectangles);
                var expander = Assert.Single(FindLogical<Expander>(host!));
                Assert.NotNull(FrameworkElementAutomationPeer.CreatePeerForElement(expander)!
                    .GetPattern(PatternInterface.ExpandCollapse));
                expander.IsExpanded = true;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Console.WriteLine($"Rendered expanded document: {owner.Document.Text.Replace("\n", "\\n")}");
                Assert.Equal("Before paragraph.\n\nVisible summary title\n\nHidden body.\n\nAfter paragraph.", owner.Document.Text);
                expander.Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Outer body.", IsTextSelectionEnabled = true },
                        new Expander
                        {
                            Content = new TextBlock { Text = "Nested hidden body.", IsTextSelectionEnabled = true },
                        },
                        new TextBlock { Text = "Outer tail.", IsTextSelectionEnabled = true },
                    },
                };
                var nested = Assert.IsType<Expander>(((StackPanel)expander.Content).Children[1]);
                NativeChatMessageText.ConfigureDisclosure(nested, "Nested summary");
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Equal("Before paragraph.\n\nVisible summary title\n\nOuter body.\n\nNested summary\n\nOuter tail.\n\nAfter paragraph.", owner.Document.Text);
                nested.IsExpanded = true;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Contains("Nested summary\n\nNested hidden body.\n\nOuter tail.", owner.Document.Text);
                expander.IsExpanded = false;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.DoesNotContain("Nested summary", owner.Document.Text);
                Assert.DoesNotContain("Outer body.", owner.Document.Text);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                owner?.Dispose();
                ui.Container.Children.Clear();
                resources?.Dispose();
            });
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task SelectionPaint_PartialBlocksSurviveInactiveFocusAndReconcile(string theme)
    {
        await ui.ResetContainerAsync();
        ChatThemeProofScope? resources = null;
        ReactorHostControl? host = null;
        NativeChatMessageText? owner = null;
        ITextProvider? provider = null;
        Border? surface = null;
        Button? outside = null;
        Element? element = null;
        UIElement? native = null;
        const string markdown = "Opening paragraph with **bold evidence**.\n\n<details><summary>Visible summary title</summary>\n\nHidden body.\n\n</details>\n\n- plugin purpose and default activation.\n\n```text\ncode one\ncode two\n```\n\n# Ending heading";
        var start = 0;
        var end = 0;
        Rect[] regions = [];
        Rect[] excluded = [];
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
                host = new ReactorHostControl { Width = 620, RequestedTheme = resources.ElementTheme };
                element = ReactorChatTimeline.BuildSafeMarkdown(markdown, _ => true);
                native = host.Reconciler.Mount(element, () => { });
                host.Content = native;
                surface = new Border
                {
                    Child = host,
                    Width = 652,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        theme == "Dark" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                outside = new Button { Content = "Outside message", VerticalAlignment = VerticalAlignment.Bottom };
                ui.Container.Children.Add(new ScrollViewer { Content = surface, Height = 600, Width = 700 });
                ui.Container.Children.Add(outside);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                owner = Assert.Single(FindLogical<NativeChatMessageText>(host!));
                owner.RefreshDocument();
                provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetPattern(PatternInterface.Text));
                start = owner.Document.Text.IndexOf("summary", StringComparison.Ordinal);
                end = owner.Document.Text.IndexOf("Ending", StringComparison.Ordinal) + 3;
                Assert.DoesNotContain("Hidden body.", owner.Document.Text);
            });
            async Task<(byte[] Pixels, int Width, int Height)> Frame(string name) =>
                await ui.RunOnUIAsync(async () =>
                {
                    Console.WriteLine($"Frame {name}: selected={owner!.Document.SelectedText.Length}; ownerFocus={owner.FocusState}; focused={FocusManager.GetFocusedElement(owner.XamlRoot)?.GetType().Name}");
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(surface!);
                    var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                    await VisualTestCapture.CaptureAsync(surface!, $"SelectionPaint-{theme}-{name}");
                    return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
                });
            var before = await Frame("before");
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(owner!.Focus(FocusState.Programmatic));
                owner.SelectRange(start, end);
                provider!.GetSelection()[0].GetBoundingRectangles(out var rectangles);
                var bounds = FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetBoundingRectangle();
                var scale = owner.XamlRoot.RasterizationScale;
                var result = new List<Rect>();
                for (var index = 0; index < rectangles.Length; index += 4)
                {
                    var local = new Point((rectangles[index] - bounds.X) / scale,
                        (rectangles[index + 1] - bounds.Y) / scale);
                    var origin = owner.TransformToVisual(surface).TransformPoint(local);
                    result.Add(new Rect(origin.X * scale, origin.Y * scale,
                        rectangles[index + 2], rectangles[index + 3]));
                }
                regions = result.ToArray();
                Assert.NotEmpty(regions);
                result.Clear();
                foreach (var text in new[] { "Opening", "Visible", "heading" })
                {
                    var range = provider.DocumentRange.FindText(text, false, false);
                    Assert.NotNull(range);
                    range.GetBoundingRectangles(out var remaining);
                    for (var index = 0; index < remaining.Length; index += 4)
                    {
                        var local = new Point((remaining[index] - bounds.X) / scale,
                            (remaining[index + 1] - bounds.Y) / scale);
                        var origin = owner.TransformToVisual(surface).TransformPoint(local);
                        result.Add(new Rect(origin.X * scale, origin.Y * scale,
                            remaining[index + 2], remaining[index + 3]));
                    }
                }
                excluded = result.ToArray();
                Assert.NotEmpty(excluded);
            });
            await ui.YieldToRenderAsync();
            void AssertPaint((byte[] Pixels, int Width, int Height) frame, bool selected, Rect[]? areas = null)
            {
                var color = new Windows.UI.ViewManagement.UISettings().UIElementColor(
                    Windows.UI.ViewManagement.UIElementType.Highlight);
                var foreground = new Windows.UI.ViewManagement.UISettings().UIElementColor(
                    Windows.UI.ViewManagement.UIElementType.HighlightText);
                foreach (var region in areas ?? regions)
                {
                    var count = 0;
                    var textPixels = 0;
                    var area = 0;
                    for (var y = Math.Max(0, (int)region.Top); y < Math.Min(frame.Height, (int)region.Bottom); y++)
                        for (var x = Math.Max(0, (int)region.Left); x < Math.Min(frame.Width, (int)region.Right); x++)
                        {
                            var offset = (y * frame.Width + x) * 4;
                            area++;
                            if (Math.Abs(frame.Pixels[offset] - color.B) <= 8 &&
                                Math.Abs(frame.Pixels[offset + 1] - color.G) <= 8 &&
                                Math.Abs(frame.Pixels[offset + 2] - color.R) <= 8)
                                count++;
                            if (Math.Abs(frame.Pixels[offset] - foreground.B) <= 8 &&
                                Math.Abs(frame.Pixels[offset + 1] - foreground.G) <= 8 &&
                                Math.Abs(frame.Pixels[offset + 2] - foreground.R) <= 8)
                                textPixels++;
                        }
                    Console.WriteLine($"Rendered paint {theme}: selected={selected}, area={area}, pixels={count}");
                    if (selected)
                    {
                        Assert.True(count >= Math.Max(3, area / 8), $"No system highlight fill in {region}.");
                        Assert.True(textPixels > 0, $"No system selected text color in {region}.");
                    }
                    else
                        Assert.Equal(0, count);
                }
            }
            AssertPaint(before, false);
            var active = await Frame("active");
            AssertPaint(active, true);
            AssertPaint(active, false, excluded);
            await ui.RunOnUIAsync(() => Assert.True(outside!.Focus(FocusState.Programmatic)));
            await ui.YieldToRenderAsync();
            AssertPaint(await Frame("inactive"), true);
            await ui.RunOnUIAsync(() =>
            {
                var next = ReactorChatTimeline.BuildSafeMarkdown(markdown, _ => true);
                native = host!.Reconciler.Reconcile(element!, next, native!, () => { });
                element = next;
                host.Content = native;
            });
            await ui.YieldToRenderAsync();
            AssertPaint(await Frame("reconciled"), true);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                owner?.Dispose();
                ui.Container.Children.Clear();
                resources?.Dispose();
            });
        }
    }

    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [MemberData(nameof(Themes))]
    public async Task RoutedInput_SelectsAcrossNativeMarkdownChildren(string theme)
    {
        await ui.ResetContainerAsync();
        NativeChatMessageText? owner = null;
        ITextProvider? provider = null;
        ReactorHostControl? host = null;
        Border? surface = null;
        ChatThemeProofScope? resources = null;
        nint window = 0;
        string? copied = null;
        string pointerRoute = "No routed press";
        Expander Disclosure() => FindLogical<Expander>(host!)
            .Single(control => AutomationProperties.GetName(control) == "Visible summary title");
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                host = new ReactorHostControl { Width = 620, RequestedTheme = resources.ElementTheme };
                var element = ReactorChatTimeline.BuildSafeMarkdown(
                    "Opening paragraph with **bold evidence**.\n\n- plugin purpose and default activation.\n\n```text\ncode one\ncode two\n```\n\n<details><summary>Visible summary title</summary>\n\nHidden body.\n\n<details><summary>Nested summary</summary>\n\nNested hidden body.\n\n</details>\n\n</details>\n\n# Ending heading",
                    text => { copied = text; return true; });
                host.Content = host.Reconciler.Mount(element, () => { });
                surface = new Border
                {
                    Child = host,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        theme == "Dark" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                ui.Container.Children.Add(new ScrollViewer
                {
                    Content = surface,
                    Height = 600,
                    Width = 700,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                });
                ui.TestWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(100, 100, 900, 900));
                ui.TestWindow.AppWindow.Show();
                ui.TestWindow.Activate();
                window = WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                owner = Assert.Single(FindLogical<NativeChatMessageText>(host!));
                owner.RefreshDocument();
                provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetPattern(PatternInterface.Text));
                owner.AddHandler(UIElement.PointerPressedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
                        pointerRoute = $"Handled={args.Handled}; source={args.OriginalSource.GetType().Name}"),
                    handledEventsToo: true);
            });
            await NativeKeyboardProof.ActivateAsync(window);
            async Task Move(string text, bool trailing, uint flags = 0, bool shift = false)
            {
                // Expander animates its body translation. Synchronize with rendered
                // geometry, not dispatcher idleness, before injecting screen input.
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Point? last = null;
                var stableFrames = 0;
                EventHandler<object>? layoutFrame = null;
                layoutFrame = (_, _) =>
                {
                    owner!.RefreshDocument();
                    var point = PointForText(owner, provider!, text, trailing);
                    stableFrames = last == point ? stableFrames + 1 : 0;
                    last = point;
                    if (stableFrames >= 3)
                    {
                        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= layoutFrame;
                        settled.TrySetResult();
                    }
                    else
                        owner.InvalidateArrange();
                };
                await ui.RunOnUIAsync(() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += layoutFrame);
                try
                {
                    await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    await ui.RunOnUIAsync(() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= layoutFrame);
                }
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var routedEvent = flags == 2 ? UIElement.PointerPressedEvent :
                    flags == 4 ? UIElement.PointerReleasedEvent : UIElement.PointerMovedEvent;
                var handler = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => received.TrySetResult());
                var point = await ui.RunOnUIAsync<Point>(() =>
                {
                    ui.Container.AddHandler(routedEvent, handler, handledEventsToo: true);
                    var local = PointForText(owner!, provider!, text, trailing);
                    var client = owner!.TransformToVisual(null).TransformPoint(local);
                    var scale = owner!.XamlRoot.RasterizationScale;
                    var screen = NativeKeyboardProof.ClientPoint(window,
                        (int)Math.Round(client.X * scale), (int)Math.Round(client.Y * scale));
                    return Task.FromResult(new Point(screen.X, screen.Y));
                });
                await NativeKeyboardProof.ActivateAsync(window);
                Console.WriteLine($"Pointer geometry {text}: {point.X},{point.Y}");
                try
                {
                    NativeKeyboardProof.Mouse(window, (int)Math.Round(point.X), (int)Math.Round(point.Y), flags, shift);
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    if (shift)
                        NativeKeyboardProof.ReleaseShift(window);
                    await ui.RunOnUIAsync(() => ui.Container.RemoveHandler(routedEvent, handler));
                }
                await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(() => Console.WriteLine(
                    $"Selection {text} flags={flags} shift={shift}: {owner!.Document.Anchor}:{owner.Document.Focus}; {pointerRoute}"));
            }
            async Task Capture(string gesture)
            {
                var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object>? rendering = null;
                rendering = (_, _) =>
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
                    ui.Dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => presented.TrySetResult());
                };
                await ui.RunOnUIAsync(() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering);
                try
                {
                    await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    await ui.RunOnUIAsync(() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering);
                }
                var regions = await ui.RunOnUIAsync<System.Drawing.Rectangle[]>(() =>
                {
                    provider!.GetSelection()[0].GetBoundingRectangles(out var rectangles);
                    var bounds = FrameworkElementAutomationPeer.CreatePeerForElement(owner!)!.GetBoundingRectangle();
                    var scale = owner!.XamlRoot.RasterizationScale;
                    var result = new List<System.Drawing.Rectangle>();
                    for (var index = 0; index < rectangles.Length; index += 4)
                    {
                        var local = new Point((rectangles[index] - bounds.X) / scale,
                            (rectangles[index + 1] - bounds.Y) / scale);
                        var client = owner.TransformToVisual(null).TransformPoint(local);
                        var origin = NativeKeyboardProof.ClientPoint(window,
                            (int)Math.Round(client.X * scale), (int)Math.Round(client.Y * scale));
                        result.Add(new System.Drawing.Rectangle(origin.X, origin.Y,
                            (int)Math.Ceiling(rectangles[index + 2]), (int)Math.Ceiling(rectangles[index + 3])));
                    }
                    return Task.FromResult(result.ToArray());
                });
                var color = new Windows.UI.ViewManagement.UISettings().UIElementColor(
                    Windows.UI.ViewManagement.UIElementType.Highlight);
                var frame = NativeKeyboardProof.Capture(window);
                using var bitmap = frame.Bitmap;
                var directory = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
                    ?? Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!, "TestResults"),
                    $"RoutedSelection-{theme}-{gesture}");
                Directory.CreateDirectory(directory);
                bitmap.Save(Path.Combine(directory, "desktop.png"), System.Drawing.Imaging.ImageFormat.Png);
                Assert.NotEmpty(regions);
                foreach (var region in regions)
                {
                    var local = new System.Drawing.Rectangle(region.X - frame.Bounds.X,
                        region.Y - frame.Bounds.Y, region.Width, region.Height);
                    local.Intersect(new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
                    var selectedPixels = 0;
                    for (var y = local.Top; y < local.Bottom; y++)
                        for (var x = local.Left; x < local.Right; x++)
                        {
                            var pixel = bitmap.GetPixel(x, y);
                            if (Math.Abs(pixel.R - color.R) <= 8 && Math.Abs(pixel.G - color.G) <= 8 &&
                                Math.Abs(pixel.B - color.B) <= 8)
                                selectedPixels++;
                        }
                    Console.WriteLine($"Paint {gesture}: region={local}; system highlight pixels={selectedPixels}");
                    Assert.True(selectedPixels >= Math.Max(3, local.Width * local.Height / 8),
                        $"No visible system selection fill in {gesture}: {selectedPixels} pixels in {local}.");
                }
            }

            await Move("summary", false, 2);
            await Move("Ending heading", true);
            await Move("Ending heading", true, 4);
            await Capture("summary-to-after");
            await ui.RunOnUIAsync(() =>
            {
                Assert.StartsWith("summary title", owner!.Document.SelectedText);
                Assert.EndsWith("Ending heading", owner.Document.SelectedText);
                Assert.False(Disclosure().IsExpanded);
                Assert.DoesNotContain("Hidden body.", owner.Document.Text);
                owner.ProcessKey(VirtualKey.C, control: true, shift: false);
                Assert.Equal(owner.Document.SelectedText, copied);
            });
            await Move("Opening", false, 2);
            await Move("summary", true);
            await Move("summary", true, 4);
            await Capture("paragraph-to-summary");
            await ui.RunOnUIAsync(() =>
            {
                Assert.StartsWith("Opening paragraph", owner!.Document.SelectedText);
                Assert.EndsWith("Visible summary", owner.Document.SelectedText);
                Assert.False(Disclosure().IsExpanded);
            });
            await Move("summary", false, 2);
            await Move("summary", false, 4);
            await ui.RunOnUIAsync(() => Assert.True(Disclosure().IsExpanded));
            NativeKeyboardProof.Press(window, VirtualKey.Space);
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => Assert.False(Disclosure().IsExpanded));
            await Move("summary", false, 2);
            await Move("summary", false, 4);
            await ui.RunOnUIAsync(() => Assert.True(Disclosure().IsExpanded));
            NativeKeyboardProof.Press(window, VirtualKey.Space);
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => Assert.False(Disclosure().IsExpanded));
            await Move("Opening", false, 2);
            await Move("Opening", false, 4);
            await Move("summary", true, 2, shift: true);
            await Move("summary", true, 4);
            await Capture("shift-click-summary");
            await ui.RunOnUIAsync(() =>
            {
                Assert.EndsWith("Visible summary", owner!.Document.SelectedText);
                Assert.False(Disclosure().IsExpanded);
            });
            await Move("summary", false, 2);
            await Move("summary", false, 4);
            await Move("Hidden body.", false, 2);
            await Move("Nested summary", true);
            await Move("Nested summary", true, 4);
            await Capture("expanded-body-to-nested-summary");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Hidden body.\n\nNested summary", owner!.Document.SelectedText);
                Assert.DoesNotContain("Nested hidden body.", owner.Document.Text);
                owner.ProcessKey(VirtualKey.C, control: true, shift: false);
                Assert.Equal("Hidden body.\n\nNested summary", copied);
            });
            await Move("summary", false, 2);
            await Move("summary", false, 4);
            await Move("Opening", false, 2);
            await Move("plugin purpose", true);
            await Capture("drag-list");
            await Move("code two", true);
            await Capture("drag-code");
            await Move("Ending heading", true, 4);
            await Capture("drag-heading");
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(owner!.Document.SelectedText.Contains("Opening paragraph"), pointerRoute);
                Assert.Contains("code two", owner.Document.SelectedText);
                Assert.Contains("Ending heading", owner.Document.SelectedText);
            });
            await Move("Opening", false, 2);
            await Move("Opening", false, 4);
            await Move("code two", true, 2, shift: true);
            await Move("code two", true, 4);
            await Capture("shift-click");
            await ui.RunOnUIAsync(() => Assert.True(owner!.Document.SelectedText.Contains("code two"), pointerRoute));
            NativeKeyboardProof.Press(window, VirtualKey.A, control: true);
            await ui.YieldToRenderAsync();
            await Capture("ctrl-a");
            await ui.RunOnUIAsync(() => Assert.Equal(provider!.DocumentRange.GetText(-1), owner!.Document.SelectedText));
            NativeKeyboardProof.Press(window, VirtualKey.C, control: true);
            await ui.YieldToRenderAsync();
            await Capture("ctrl-c");
            await ui.RunOnUIAsync(() => Assert.Equal(owner!.Document.SelectedText, copied));
            NativeKeyboardProof.Press(window, VirtualKey.Home);
            NativeKeyboardProof.Press(window, VirtualKey.Right, shift: true);
            await ui.YieldToRenderAsync();
            await Capture("keyboard-extend");
            await ui.RunOnUIAsync(() => Assert.Equal(1, owner!.Document.SelectedText.Length));
        }
        finally
        {
            NativeKeyboardProof.ReleaseOwnedInput(window);
            await ui.RunOnUIAsync(() =>
            {
                owner?.Dispose();
                ui.Container.Children.Clear();
                resources?.Dispose();
            });
        }
    }

    private const string Message =
        "Opening paragraph with **bold evidence**.\n\n" +
        "- plugin purpose and default activation.\n" +
        "- app\\dist\\extensions\\gateway-isolation: second item.\n\n" +
        "```text\ncode one\ncode two\n```\n\n" +
        "Metric | Value\n--- | ---\nCount | 2\n\n" +
        "Reference <https://example.invalid/evidence> and trailing text.\n\n" +
        "<details><summary>Collapsed evidence</summary>\n\nHidden evidence.\n\n</details>\n\n" +
        "Final paragraph.";

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task VisibleMessage_PointerKeyboardCopyAndTextProviderShareOneRange(string theme)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? resources = null;
        Border? surface = null;
        Element? current = null;
        UIElement? root = null;
        Action? reconcile = null;
        var disposed = false;
        var message = Message;
        string? copied = null;
        bool TryCopy(string text) { copied = text; return true; }
        NativeChatMessageText? owner = null;
        ITextProvider? provider = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1600));
                host = new ReactorHostControl { Width = 620, RequestedTheme = resources.ElementTheme };
                Action request = () => ui.Dispatcher.TryEnqueue(() =>
                {
                    if (!disposed)
                        reconcile!();
                });
                reconcile = () =>
                {
                    var next = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                    root = host.Reconciler.Reconcile(current!, next, root!, request);
                    current = next;
                    host.Content = root;
                };
                current = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                root = host.Reconciler.Mount(current, request);
                host.Content = root;
                surface = new Border
                {
                    Child = host,
                    Width = 652,
                    Padding = new Thickness(16),
                    VerticalAlignment = VerticalAlignment.Top,
                    RequestedTheme = resources.ElementTheme,
                };
                ui.Container.Children.Add(surface);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                owner = Assert.Single(FindLogical<NativeChatMessageText>(host!));
                owner.RefreshDocument();
                provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetPattern(PatternInterface.Text));
                var text = provider.DocumentRange.GetText(-1);
                Assert.Contains("Opening paragraph with bold evidence.", text);
                Assert.Contains("\u2022 plugin purpose and default activation.\n\u2022 app\\dist", text);
                Assert.Contains("code one\ncode two", text);
                Assert.Contains("Metric\tValue\nCount\t2", text);
                Assert.Contains("Reference https://example.invalid/evidence and trailing text.", text);
                Assert.DoesNotContain("Hidden evidence.", text);
                Assert.Contains("Collapsed evidence", text);
                Assert.DoesNotContain("Copy code", text);
                Assert.Equal(SupportedTextSelection.Single, provider.SupportedTextSelection);

                var start = text.IndexOf("plugin purpose", StringComparison.Ordinal);
                var end = text.IndexOf("code two", StringComparison.Ordinal) + "code two".Length;
                Assert.True(owner.BeginSelection(PointForText(owner, provider, "plugin purpose", trailing: false)));
                owner.ExtendSelection(PointForText(owner, provider, "code two", trailing: true));
                owner.EndSelection();
                Assert.Equal(text[start..end], owner.Document.SelectedText);
                Assert.Equal(owner.Document.SelectedText, Assert.Single(provider.GetSelection()).GetText(-1));
                Assert.True(owner.ProcessKey(VirtualKey.C, control: true, shift: false));
                Assert.Equal(text[start..end], copied);
                Assert.All(FindLogical<RichTextBlock>(host!).Where(block =>
                    CollectText(block).Contains("plugin purpose", StringComparison.Ordinal) ||
                    CollectText(block).Contains("code one", StringComparison.Ordinal)),
                    block => Assert.NotEmpty(block.TextHighlighters.SelectMany(highlight => highlight.Ranges)));
            });
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface!, $"MessageSelection-{theme}"));
            await ui.RunOnUIAsync(() =>
            {
                var text = owner!.Document.Text;
                owner.SelectRange(0, 0);
                Assert.True(owner.ProcessKey(VirtualKey.End, control: true, shift: true));
                Assert.Equal(text, owner.Document.SelectedText);
                Assert.True(owner.ProcessKey(VirtualKey.Home, control: true, shift: false));
                Assert.False(owner.Document.HasSelection);
                Assert.Equal(0, owner.Document.Focus);
                Assert.True(owner.ProcessKey(VirtualKey.Right, control: true, shift: true));
                Assert.Equal("Opening ", owner.Document.SelectedText);

                var range = provider!.DocumentRange.FindText("code one\ncode two", backward: false, ignoreCase: false);
                Assert.NotNull(range);
                range.Select();
                Assert.Equal("code one\ncode two", owner.Document.SelectedText);
                var clone = range.Clone();
                Assert.True(range.Compare(clone));
                clone.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
                Assert.Equal("code one\ncode tw", clone.GetText(-1));
                Assert.True(owner.ProcessKey(VirtualKey.C, control: true, shift: false));
                Assert.Equal("code one\ncode two", copied);

                var before = range.GetText(-1);
                message = message.Replace("Opening paragraph", "Opening expanded paragraph", StringComparison.Ordinal);
                reconcile!();
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Equal(before, range.GetText(-1));
                Assert.Equal(before, owner.Document.SelectedText);
                host!.Width = 320;
                surface!.Width = 352;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Equal(before, owner.Document.SelectedText);
                Assert.Equal(before, Assert.Single(provider.GetSelection()).GetText(-1));

                var link = provider.DocumentRange.FindText("https://example.invalid/evidence", false, false);
                link.Select();
                Assert.Equal("https://example.invalid/evidence", owner.Document.SelectedText);
                link.GetBoundingRectangles(out var rectangles);
                Assert.NotEmpty(rectangles);
                Assert.All(rectangles.Chunk(4), rectangle =>
                {
                    Assert.True(rectangle[2] > 0);
                    Assert.True(rectangle[3] > 0);
                });
                Assert.Empty(link.GetChildren());
                Assert.NotNull(link.GetEnclosingElement());
                var nativeText = FindLogical<RichTextBlock>(host!).First(block => CollectText(block).Contains("Opening", StringComparison.Ordinal));
                var nativePeer = FrameworkElementAutomationPeer.CreatePeerForElement(nativeText)!;
                Assert.Contains("Opening", provider.RangeFromChild(
                    new ProviderAccessPeer(nativeText).AsProvider(nativePeer)).GetText(-1));

                var expander = Assert.Single(FindLogical<Expander>(host!));
                Assert.NotNull(FrameworkElementAutomationPeer.CreatePeerForElement(expander)!.GetPattern(PatternInterface.ExpandCollapse));
                expander.IsExpanded = true;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                var hidden = provider.DocumentRange.FindText("Hidden evidence.", false, false);
                Assert.NotNull(hidden);
                hidden.Select();
                expander.IsExpanded = false;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.DoesNotContain("Hidden evidence.", provider.DocumentRange.GetText(-1));
                Assert.DoesNotContain("Hidden evidence.", hidden.GetText(-1));
                Assert.InRange(owner.Document.End, 0, owner.Document.Text.Length);

                var code = provider.DocumentRange.FindText("code one\ncode two", false, false);
                code.Select();
                owner.ProcessKey(VirtualKey.Home, control: false, shift: false);
                owner.ProcessKey(VirtualKey.End, control: false, shift: true);
                Assert.Equal("code two", owner.Document.SelectedText);
                owner.ProcessKey(VirtualKey.Up, control: false, shift: false);
                Assert.InRange(owner.Document.Focus,
                    owner.Document.Text.IndexOf("code one", StringComparison.Ordinal),
                    owner.Document.Text.IndexOf("code two", StringComparison.Ordinal) - 1);

                owner.Update(_ => false);
                owner.SelectRange(0, 7);
                Assert.True(owner.CopySelection());
                Assert.Equal("Copy failed", AutomationProperties.GetItemStatus(owner));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(owner));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                disposed = true;
                owner?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                if (host is not null)
                {
                    surface!.Child = null;
                    host.Content = null;
                    host.Dispose();
                }
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task MessageBoundary_DragScrollAndRealizationReuse(string theme)
    {
        await ui.ResetContainerAsync();
        NativeChatMessageText? first = null;
        NativeChatMessageText? second = null;
        ScrollViewer? scroll = null;
        StackPanel? root = null;
        ChatThemeProofScope? resources = null;
        var scrolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"Line {index:D2} visible message evidence"));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                first = new NativeChatMessageText { Content = new TextBlock { Text = text, IsTextSelectionEnabled = true }, Width = 500 };
                second = new NativeChatMessageText { Content = new TextBlock { Text = "Other message stays isolated.", IsTextSelectionEnabled = true }, Width = 500 };
                first.Update(_ => true);
                second.Update(_ => true);
                scroll = new ScrollViewer { Content = first, Height = 160, Width = 520 };
                scroll.ViewChanged += (_, _) =>
                {
                    if (scroll.VerticalOffset > 0)
                        scrolled.TrySetResult();
                };
                root = new StackPanel { RequestedTheme = resources.ElementTheme, Width = 540 };
                root.Children.Add(scroll);
                root.Children.Add(second);
                ui.Container.Children.Add(root);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                first!.RefreshDocument();
                second!.RefreshDocument();
                var provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(first)!.GetPattern(PatternInterface.Text));
                var other = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(second)!.GetPattern(PatternInterface.Text));
                Assert.Equal(text, provider.DocumentRange.GetText(-1));
                Assert.Throws<ArgumentException>(() => provider.DocumentRange.Compare(other.DocumentRange));
                Assert.Throws<ArgumentException>(() => provider.RangeFromChild(other.DocumentRange.GetEnclosingElement()));
                first.SelectRange(0, text.Length);
                Assert.False(second.Document.HasSelection);
                second.SelectRange(0, 5);
                Assert.Equal(text, first.Document.SelectedText);
                provider.DocumentRange.GetBoundingRectangles(out var rectangles);
                Assert.NotEmpty(rectangles);
                var viewportBounds = FrameworkElementAutomationPeer.CreatePeerForElement(scroll!)!.GetBoundingRectangle();
                Assert.All(rectangles.Chunk(4), rectangle =>
                    Assert.True(rectangle[1] + rectangle[3] <= viewportBounds.Bottom + 1));
                Assert.NotEmpty(provider.GetVisibleRanges());
                var start = PointForText(first, provider, "Line 00", false);
                Assert.True(first.BeginSelection(start));
                var bottom = scroll!.TransformToVisual(first).TransformPoint(new Point(20, scroll.ActualHeight + 30));
                first.ExtendSelection(bottom);
            });
            await scrolled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                first!.EndSelection();
                Assert.True(scroll!.VerticalOffset > 0);
                Assert.True(first.Document.End > 0);
                Assert.Equal("Other", second!.Document.SelectedText);
                scroll.Content = null;
                first.Dispose();
                Assert.Empty(first.Document.Text);
                var retired = first;
                var replacement = "Recycled message with new body.";
                retired.Update(_ => true);
                Assert.Empty(retired.Document.Text);
                first = new NativeChatMessageText
                {
                    Content = new TextBlock { Text = replacement, IsTextSelectionEnabled = true },
                    Width = 500,
                };
                first.Update(_ => true);
                first.Loaded += (_, _) => reused.TrySetResult();
                scroll.Content = first;
                scroll.ChangeView(null, 0, null, disableAnimation: true);
            });
            await ui.YieldToRenderAsync();
            await reused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                first!.Update(_ => true);
                ui.Container.UpdateLayout();
                Assert.True(first.IsLoaded);
                first.RefreshDocument();
                Assert.Equal("Recycled message with new body.", first.Document.Text);
                Assert.False(first.Document.HasSelection);
                first.ProcessKey(VirtualKey.A, control: true, shift: false);
                Assert.Equal("Recycled message with new body.", first.Document.SelectedText);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                first?.Dispose();
                second?.Dispose();
                if (root is not null)
                    ui.Container.Children.Remove(root);
                if (scroll is not null)
                    scroll.Content = null;
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task NativeUnits_RespectWrappingParagraphsAndTransientReload(string theme)
    {
        await ui.ResetContainerAsync();
        const string prose = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu";
        const string code = "code one\ncode two";
        NativeChatMessageText? owner = null;
        StackPanel? root = null;
        ChatThemeProofScope? resources = null;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                RichTextBlock Body(string text)
                {
                    var block = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
                    var paragraph = new Paragraph();
                    paragraph.Inlines.Add(new Run { Text = text });
                    block.Blocks.Add(paragraph);
                    return block;
                }
                var body = new StackPanel();
                body.Children.Add(Body(prose));
                body.Children.Add(Body(code));
                owner = new NativeChatMessageText { Content = body, Width = 140 };
                owner.Loaded += (_, _) => loaded.TrySetResult();
                owner.Unloaded += (_, _) => unloaded.TrySetResult();
                root = new StackPanel { RequestedTheme = resources.ElementTheme };
                root.Children.Add(owner);
                ui.Container.Children.Add(root);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner!)!.GetPattern(PatternInterface.Text));
                Assert.Equal(prose + "\n\n" + code, owner!.Document.Text);
                var line = provider.DocumentRange.FindText("alpha", false, false);
                line.ExpandToEnclosingUnit(TextUnit.Line);
                Assert.StartsWith("alpha", line.GetText(-1));
                Assert.True(line.GetText(-1).Length < prose.Length);
                line.GetBoundingRectangles(out var firstRects);
                Assert.NotEmpty(firstRects);
                Assert.All(firstRects.Chunk(4), rectangle => Assert.Equal(firstRects[1], rectangle[1], precision: 1));
                Assert.Equal(1, line.Move(TextUnit.Line, 1));
                line.GetBoundingRectangles(out var secondRects);
                Assert.True(secondRects[1] > firstRects[1]);
                var paragraph = provider.DocumentRange.FindText("beta", false, false);
                paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                Assert.Equal(prose + "\n\n", paragraph.GetText(-1));
                Assert.Equal(1, paragraph.Move(TextUnit.Paragraph, 1));
                Assert.Equal(code, paragraph.GetText(-1));
                var codeParagraph = provider.DocumentRange.FindText("code two", false, false);
                codeParagraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                Assert.Equal(code, codeParagraph.GetText(-1));
                var format = provider.DocumentRange.FindText("beta", false, false);
                var word = format.Clone();
                format.ExpandToEnclosingUnit(TextUnit.Format);
                word.ExpandToEnclosingUnit(TextUnit.Word);
                Assert.Equal("beta ", format.GetText(-1));
                Assert.True(format.Compare(word));
                Assert.Equal(word.Move(TextUnit.Word, 1), format.Move(TextUnit.Format, 1));
                Assert.True(format.Compare(word));
                owner.SelectRange(0, 5);
                root!.Children.Remove(owner);
            });
            await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(owner!.Document.Text);
                loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                root!.Children.Add(owner);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                // No Update or Refresh: native Loaded must restore the same retained content.
                Assert.Equal(prose + "\n\n" + code, owner!.Document.Text);
                owner.ProcessKey(VirtualKey.A, control: true, shift: false);
                Assert.Equal(owner.Document.Text, owner.Document.SelectedText);
                owner.Dispose();
                owner.Update(_ => true);
                Assert.Empty(owner.Document.Text);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                owner?.Dispose();
                if (root is not null)
                    ui.Container.Children.Remove(root);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    private static Point PointForText(NativeChatMessageText owner, ITextProvider provider, string text, bool trailing)
    {
        var range = provider.DocumentRange.FindText(text, backward: false, ignoreCase: false);
        Assert.NotNull(range);
        range.GetBoundingRectangles(out var rectangles);
        Assert.NotEmpty(rectangles);
        var index = trailing ? rectangles.Length - 4 : 0;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(owner)!;
        var bounds = peer.GetBoundingRectangle();
        var scale = owner.XamlRoot.RasterizationScale;
        return new Point(
            (rectangles[index] - bounds.X + (trailing ? rectangles[index + 2] - 0.1 : 0.1)) / scale,
            (rectangles[index + 1] - bounds.Y + rectangles[index + 3] / 2) / scale);
    }

    private static string CollectText(RichTextBlock text) =>
        string.Concat(text.Blocks.OfType<Paragraph>().SelectMany(paragraph =>
            paragraph.Inlines.OfType<Run>().Select(run => run.Text)));

    private sealed class ProviderAccessPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        internal IRawElementProviderSimple AsProvider(AutomationPeer peer) => ProviderFromPeer(peer);
    }
}
