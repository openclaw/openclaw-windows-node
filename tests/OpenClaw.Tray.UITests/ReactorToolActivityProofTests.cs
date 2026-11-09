using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Offscreen real-runtime proof for the production Reactor tool activity renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorToolActivityProofTests
{
    private const string DocumentGlyph = "\uE8A5";
    private const string ListGlyph = "\uE8FD";

    private readonly UIThreadFixture _ui;

    public ReactorToolActivityProofTests(UIThreadFixture ui) => _ui = ui;

    [Theory]
    [InlineData("Waiting for approval", true)]
    [InlineData("Retrying the model request", false)]
    [InlineData("Compacting context", false)]
    public async Task RunProgress_PreservesVisibleExceptionalStatusBesideElapsedTime(string label, bool waiting)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var element = Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                DateTimeOffset.UtcNow.AddSeconds(-65), null, label, true, waiting)));
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var text = Assert.Single(FindLogical<TextBlock>(root!),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                Assert.Contains(label, text.Text);
                Assert.Contains("1m", text.Text);
                Assert.IsNotType<LinearGradientBrush>(text.Foreground);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task RunProgress_OutputTokensPrecedeATickingClockWithoutTextShimmer()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            host.Mount(_ => Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                DateTimeOffset.UtcNow.AddSeconds(-65), null, "Working", true, false, OutputTokens: 12345))));
            _ui.Container.Children.Add(host);
            try
            {
                _ui.Container.UpdateLayout();
                await WaitForRenderedConditionAsync(() => FindLogical<TextBlock>(host).Any(block =>
                    AutomationProperties.GetAutomationId(block) == "ChatRunProgressText"), "the run progress mounts");
                var text = Assert.Single(FindLogical<TextBlock>(host),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                var row = Assert.Single(FindLogical<Grid>(host),
                    grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress");
                Assert.Equal(1, row.Opacity);
                Assert.StartsWith("12,345 output tokens \u00b7 ", text.Text);
                Assert.Contains("1m", text.Text);
                Assert.IsNotType<LinearGradientBrush>(text.Foreground);
                var initial = text.Text;
                await WaitForRenderedConditionAsync(() => text.Text != initial, "the running clock ticks");
                Assert.Equal(1, row.Opacity);
                Assert.StartsWith("12,345 output tokens \u00b7 ", text.Text);
                Assert.IsNotType<LinearGradientBrush>(text.Foreground);
            }
            finally
            {
                _ui.Container.Children.Clear();
            }
        });
    }

    [Fact]
    public async Task TextShimmer_FollowsTheThemeOfTheTreeItJoinsAndLaterThemeChanges()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            var text = new TextBlock { Text = "Running" };
            ChatVisuals.TextShimmer(text, true);
            if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ||
                new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground);
                return;
            }

            // Reactor configures the label before it joins the tree, when it still reports the
            // application theme. Join a tree that renders the opposite theme, then switch it back.
            var applicationIsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            var stops = Assert.IsType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground).GradientStops;
            var reference = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource TextFillColorPrimaryBrush}" Width="0" Height="0" />""");
            var secondary = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource TextFillColorSecondaryBrush}" Width="0" Height="0" />""");
            void AssertSweepColors(bool dark)
            {
                var primaryInk = Assert.IsType<SolidColorBrush>(reference.Foreground).Color;
                var softerInk = Assert.IsType<SolidColorBrush>(secondary.Foreground).Color;
                softerInk.A = (byte)Math.Round(softerInk.A - (primaryInk.A - softerInk.A) / 5.0);
                var ink = dark ? softerInk : primaryInk;
                Assert.All(stops.Where((_, index) => index != 2), stop => Assert.Equal(ink, stop.Color));
                var peak = stops[2].Color;
                Assert.Equal(dark ? primaryInk : softerInk, peak);
                Assert.Equal((ink.R, ink.G, ink.B), (peak.R, peak.G, peak.B));
                Assert.True(Math.Abs(peak.A - ink.A) is >= 40 and <= 85,
                    $"The flare must remain subtle and visible: base {ink}, flare {peak}, theme {text.ActualTheme}.");
                Assert.True(dark ? peak.A >= ink.A : peak.A <= ink.A);
                foreach (var stop in stops)
                    Assert.True(ShimmerContrast(stop.Color, dark) >= 4.5,
                        $"The full running label must remain readable: {stop.Color} in {(dark ? "Dark" : "Light")}.");
            }

            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(reference);
            panel.Children.Add(secondary);
            var host = new Border { Child = panel, RequestedTheme = applicationIsDark ? ElementTheme.Light : ElementTheme.Dark };
            try
            {
                _ui.Container.Children.Add(host);
                await _ui.YieldToRenderAsync();
                AssertSweepColors(!applicationIsDark);

                host.RequestedTheme = applicationIsDark ? ElementTheme.Dark : ElementTheme.Light;
                await _ui.YieldToRenderAsync();
                AssertSweepColors(applicationIsDark);

                ChatVisuals.TextShimmer(text, false);
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground);
            }
            finally
            {
                ChatVisuals.TextShimmer(text, false);
                _ui.Container.Children.Clear();
            }
        });
    }

    [Fact]
    public async Task TextShimmer_KeepsTheWholeLabelPaintedThroughoutItsSweep()
    {
        await _ui.ResetContainerAsync();
        foreach (var themeName in new[] { "Light", "Dark" })
        {
            await _ui.RunOnUIAsync(async () =>
            {
                using var theme = new ChatThemeProofScope(themeName);
                var text = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="Inspect the complete sentence while this operation is running." FontSize="13" HorizontalAlignment="Left" Foreground="{ThemeResource ChatSecondaryTextBrush}" />""");
                var primary = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource ChatTextBrush}" Width="0" Height="0" />""");
                var content = new Grid();
                content.Children.Add(text);
                content.Children.Add(primary);
                var host = new Border
                {
                    Child = content, RequestedTheme = theme.ElementTheme, Padding = new Thickness(12),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                _ui.Container.Children.Add(host);
                try
                {
                    _ui.Container.UpdateLayout();
                    await _ui.YieldToRenderAsync();
                    // Use the same native gradient rasterizer without a highlight or motion.
                    // Solid-color text can use different glyph antialiasing from gradient text.
                    var resting = Assert.IsType<SolidColorBrush>(text.Foreground);
                    var primaryAlpha = Assert.IsType<SolidColorBrush>(primary.Foreground).Color.A;
                    var minimumInkAlpha = resting.Color.A - (primaryAlpha - resting.Color.A) / 5.0;
                    var reference = new LinearGradientBrush();
                    reference.GradientStops.Add(new GradientStop { Color = resting.Color, Offset = 0 });
                    reference.GradientStops.Add(new GradientStop { Color = resting.Color, Offset = 1 });
                    text.Foreground = reference;
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(text);
                    var baseline = (await bitmap.GetPixelsAsync()).ToArray();
                    Assert.Contains(Enumerable.Range(0, baseline.Length / 4), pixel => baseline[pixel * 4 + 3] >= 64);
                    ChatVisuals.TextShimmer(text, true);
                    if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ||
                        new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                    {
                        Assert.Same(reference, text.Foreground);
                        return;
                    }
                    var brush = Assert.IsType<LinearGradientBrush>(text.Foreground);
                    foreach (var stop in brush.GradientStops)
                        Assert.True(ShimmerContrast(stop.Color, themeName == "Dark") >= 4.5);
                    Assert.Equal(GradientSpreadMethod.Pad, brush.SpreadMethod);
                    Assert.Equal(0, brush.GradientStops[0].Offset);
                    Assert.Equal(1, brush.GradientStops[^1].Offset);
                    double? previousFlareX = null;
                    foreach (var phase in new[] { 0.2, 0.5, 0.9 })
                    {
                        await WaitForRenderedConditionAsync(() => brush.GradientStops[2].Offset >= phase, "the running-label shine crosses the label");
                        await bitmap.RenderAsync(text);
                        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                        Assert.Equal(baseline.Length, pixels.Length);
                        var weight = 0.0;
                        var weightedX = 0.0;
                        for (var offset = 3; offset < baseline.Length; offset += 4)
                            if (baseline[offset] >= 64)
                            {
                                Assert.True(pixels[offset] >= baseline[offset] * minimumInkAlpha / resting.Color.A - 8,
                                    $"{themeName}, phase {phase}: the shine erased part of the label at pixel {(offset - 3) / 4}: {baseline[offset]} -> {pixels[offset]}; theme {text.ActualTheme}, base {brush.GradientStops[0].Color}.");
                                var baseAlpha = baseline[offset] * brush.GradientStops[0].Color.A / (double)resting.Color.A;
                                var flare = themeName == "Dark" ? pixels[offset] - baseAlpha : baseAlpha - pixels[offset];
                                if (flare >= 3)
                                {
                                    weight += flare;
                                    weightedX += flare * (((offset - 3) / 4) % bitmap.PixelWidth);
                                }
                            }
                        Assert.True(weight > 0, $"{themeName}: no visible flare at phase {phase}.");
                        var flareX = weightedX / weight;
                        if (previousFlareX is { } previousX)
                            Assert.True(flareX > previousX + bitmap.PixelWidth * 0.1,
                                $"{themeName}: the flare did not move across the label ({previousX} -> {flareX}).");
                        previousFlareX = flareX;
                        await VisualTestCapture.CaptureAsync(host, $"RunningFlare-{themeName}-{phase:0.0}");
                    }

                }
                finally
                {
                    ChatVisuals.TextShimmer(text, false);
                    _ui.Container.Children.Clear();
                }
            });
        }
    }

    private static double ShimmerContrast(global::Windows.UI.Color ink, bool dark)
    {
        var background = dark ? 32.0 : 255.0;
        var alpha = ink.A / 255.0;
        double Linear(double value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(alpha * ink.R + (1 - alpha) * background)
            + 0.7152 * Linear(alpha * ink.G + (1 - alpha) * background)
            + 0.0722 * Linear(alpha * ink.B + (1 - alpha) * background);
        var backdrop = Linear(background);
        return (Math.Max(luminance, backdrop) + 0.05) / (Math.Min(luminance, backdrop) + 0.05);
    }

    [Fact]
    public async Task RunningTool_UnmountStopsShimmerBeforeItsTextControlCanBeReused()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("running", "read", "partial output", ChatToolCallStatus.InProgress);
            var element = ToolCallCardRenderer.BuildStandalone(BuildTimeline([entry], 1), entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            await DrainReactorQueueAsync();
            var labels = FindLogical<TextBlock>(root!).ToArray();
            try
            {
                if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
                    !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                    Assert.Contains(labels, label => label.Foreground is Microsoft.UI.Xaml.Media.LinearGradientBrush);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
            Assert.All(labels, label =>
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(label.Foreground));
        });
    }

    [Fact]
    public async Task WorkMotion_UsesEnterOnlyTransitionAndRespectsReducedMotion()
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            var work = Tool("t", "read", "output", ChatToolCallStatus.Success);
            var activity = new ChatToolActivityRow(
                "work", null, [work], ChatToolActivityPresentation.Summarize([work]), [work]);
            var timeline = BuildTimeline([work], 1);
            var animated = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: true, reducedMotion: false);
            Assert.NotNull(animated.ElementTransition);
            Assert.Equal(Transition.Enter(Transition.Fade), animated.ElementTransition.Transition);
            Assert.Equal(Curve.Ease(200, Easing.EaseOut), animated.ElementTransition.Curve);
            var reduced = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: true, reducedMotion: true);
            Assert.Null(reduced.ElementTransition);
            var history = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: false, reducedMotion: false);
            Assert.Null(history.ElementTransition);
        });
    }

    [Fact]
    public async Task CompletedWork_ReusesNativeDisclosureAndToolDetailsWithoutHidingAnswer()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entries = new ChatTimelineItem[]
            {
                new("commentary", ChatTimelineItemKind.Assistant, "Checking",
                    Presentation: new(Phase: "commentary", RunId: "r")),
                Tool("read", "read_file", "<literal output>", ChatToolCallStatus.Success),
                new("answer", ChatTimelineItemKind.Assistant, "Preserved answer",
                    Presentation: new(Phase: "final_answer", RunId: "r")),
            };
            var timeline = BuildTimeline(entries, 1) with { SessionId = "agent:a:dashboard:d" };
            var rows = ChatToolActivityPresentation.Project(entries, timeline.SessionId, 1);
            Assert.Equal("Preserved answer", rows[1].Entry!.Text);
            var work = rows[0];
            var element = ToolCallCardRenderer.BuildActivityCore(timeline, work, false, _ => { },
                entry => TextBlock(entry.Text));
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var disclosure = Assert.Single(FindLogical<Expander>(root!));
                Assert.False(disclosure.IsExpanded);
                Assert.Contains("Worked", AutomationProperties.GetName(disclosure));
                Assert.Empty(FindLogical<RichTextBlock>(root!));
                var header = Assert.IsAssignableFrom<DependencyObject>(disclosure.Header);
                Assert.Contains(FindLogical<TextBlock>(header),
                    text => text.Text == ListGlyph);
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "Worked");
                var expanded = ToolCallCardRenderer.BuildActivityCore(timeline, work, true, _ => { },
                    entry => TextBlock(entry.Text));
                root = host.Reconciler.Reconcile(element, expanded, root!, static () => { });
                element = expanded;
                host.Content = root;
                Assert.Contains(disclosure, FindLogical<Expander>(root!));
                Assert.True(disclosure.IsExpanded);
                Assert.Contains(FindLogical<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(disclosure.Header)),
                    text => text.Text == "Worked · 1 tool call");
                Assert.Contains(FindLogical<TextBlock>(root!), text => text.Text == "Checking");
                // The tool sits in its own collapsed group layer, exactly like the web Worked body.
                var group = Assert.Single(FindLogical<Expander>(root!), expander => !ReferenceEquals(expander, disclosure));
                Assert.False(group.IsExpanded);
                Assert.Contains("1 read", AutomationProperties.GetName(group));
                Assert.Empty(FindLogical<RichTextBlock>(root!));
                Assert.DoesNotContain(FindLogical<TextBlock>(root!), text => text.Text == "Preserved answer");
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Theory]
    [InlineData(true, true, ChatToolCallStatus.InProgress, 1)]
    [InlineData(true, true, ChatToolCallStatus.Success, 2)]
    [InlineData(true, true, ChatToolCallStatus.Error, 2)]
    [InlineData(true, false, ChatToolCallStatus.InProgress, 0)]
    [InlineData(false, true, ChatToolCallStatus.Success, 1)]
    [InlineData(false, false, ChatToolCallStatus.InProgress, 0)]
    public async Task ToolDetails_UseDividersInsteadOfVisibleSectionHeadings(
        bool hasInput, bool hasOutput, ChatToolCallStatus status, int expectedDividers)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("sections", "exec", hasOutput ? "Synthetic output" : "", status) with
            {
                ToolArgs = hasInput ? new System.Text.Json.Nodes.JsonObject { ["command"] = "echo synthetic" } : null,
            };
            var element = ToolCallCardRenderer.BuildStandalone(BuildTimeline([entry], 1), entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                Assert.DoesNotContain(FindLogical<TextBlock>(root!),
                    text => text.Text is "TOOL INPUT" or "TOOL OUTPUT" or "TOOL ERROR");
                var sections = FindLogical<ScrollViewer>(root!).ToArray();
                var expectedLabels = new List<string>();
                var expectedContent = new List<string>();
                if (hasInput)
                {
                    expectedLabels.Add("Tool input");
                    expectedContent.Add("command: echo synthetic");
                }
                if (hasOutput)
                {
                    expectedLabels.Add(status == ChatToolCallStatus.Error ? "Tool error" : "Tool output");
                    expectedContent.Add("Synthetic output");
                }
                Assert.Equal(expectedLabels, sections.Select(AutomationProperties.GetName));
                Assert.Equal(expectedContent, sections.Select(section =>
                    CollectText(Assert.IsType<RichTextBlock>(section.Content))));
                Assert.All(sections, section =>
                    Assert.True(Assert.IsType<RichTextBlock>(section.Content).IsTextSelectionEnabled));
                Assert.Equal(expectedDividers, FindLogical<Border>(root!).Count(border => border.Height == 1));
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolRow_ExposesTargetAndKeepsOpenOutputWhenInputArrivesAndRunFinishes()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("stream", "read_file", "partial output", ChatToolCallStatus.InProgress)
                with { Text = string.Empty };
            var timeline = BuildTimeline([entry], generation: 1);
            var element = ToolCallCardRenderer.BuildStandalone(timeline, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            Assert.NotNull(root);
            host.Content = root;
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root));
                Assert.False(expander.IsExpanded);
                expander.IsExpanded = true;
                var output = Assert.Single(FindLogical<RichTextBlock>(root));
                var section = Assert.Single(FindLogical<Border>(root),
                    border => border.Child is ScrollViewer);
                Assert.Equal(new Thickness(12, 8, 12, 8), section.Padding);
                var scroll = Assert.IsType<ScrollViewer>(section.Child);
                Assert.Equal("Tool output", AutomationProperties.GetName(scroll));
                Assert.Equal(240, scroll.MaxHeight);
                Assert.Empty(FindLogical<TextBlock>(section));
                Assert.DoesNotContain(FindLogical<Border>(root), border => border.Height == 1);
                Assert.Equal("partial output", CollectText(output));

                entry = entry with
                {
                    ToolArgs = new System.Text.Json.Nodes.JsonObject { ["filePath"] = "src\\main.cs" },
                    ToolOutput = "final <literal> output",
                    ToolResult = ChatToolCallStatus.Success,
                    ToolRunId = "observed-run",
                    ToolLegacyTurn = 2,
                };
                var next = ToolCallCardRenderer.BuildStandalone(timeline, entry);
                root = host.Reconciler.Reconcile(element, next, root, static () => { });
                Assert.NotNull(root);
                element = next;
                host.Content = root;

                Assert.Same(expander, Assert.Single(FindLogical<Expander>(root)));
                Assert.True(expander.IsExpanded);
                Assert.Equal("src\\main.cs", AutomationProperties.GetHelpText(expander));
                Assert.Contains("Done", AutomationProperties.GetName(expander), StringComparison.Ordinal);
                Assert.DoesNotContain(FindLogical<TextBlock>(root), text => text.Text == "Done");
                var header = Assert.IsAssignableFrom<DependencyObject>(expander.Header);
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "src\\main.cs");
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == DocumentGlyph);
                Assert.Null(ToolTipService.GetToolTip(expander));
                Assert.Contains(output, FindLogical<RichTextBlock>(root));
                Assert.Equal("final <literal> output", CollectText(output));
                Assert.True(output.IsTextSelectionEnabled);
                Assert.Equal(2, FindLogical<Border>(root).Count(border => border.Height == 1));

                var collapsed = ToolCallCardRenderer.BuildStandalone(
                    timeline with { ToolCallsCollapseVersion = 1 }, entry);
                root = host.Reconciler.Reconcile(element, collapsed, root, static () => { });
                Assert.NotNull(root);
                element = collapsed;
                host.Content = root;
                Assert.False(Assert.Single(FindLogical<Expander>(root)).IsExpanded);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task RunningActivity_UsesSuppliedTitleCategoryIconAndSourceMetadata()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var first = Tool("first", "exec", "first output", ChatToolCallStatus.Success)
                with { ToolRunId = "run" };
            var current = Tool("current", "exec", "partial output", ChatToolCallStatus.InProgress)
                with
                {
                    ToolRunId = "run",
                    ToolInputDetails = new ChatToolInputDetails(
                        """{"command":"dotnet test","title":"Build identical reviewer input"}""",
                        "dotnet test",
                        "Build identical reviewer input"),
                };
            var entries = new[] { first, current };
            var activity = Assert.Single(
                ChatToolActivityPresentation.Project(entries, "session", 1),
                row => row.IsActivityGroup);
            var timestamp = new DateTimeOffset(2026, 1, 2, 17, 5, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 2, 17, 5, 0)));
            var metadata = new ChatEntryMetadata(timestamp, null)
            {
                Presentation = new ChatMessagePresentation(SenderLabel: "RoboClaw"),
            };
            var timeline = BuildTimeline(entries, 1) with
            {
                EntryMetadata = new Dictionary<string, ChatEntryMetadata>
                {
                    [current.Id] = metadata,
                },
            };
            var element = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { });
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root!));
                var header = Assert.IsAssignableFrom<DependencyObject>(expander.Header);
                var headerText = FindLogical<TextBlock>(header).Select(text => text.Text).ToArray();
                Assert.Contains("Build identical reviewer input", headerText);
                Assert.Contains("Running", headerText);
                Assert.Contains(ListGlyph, headerText);

                // Web tool rows carry no hover tooltip.
                Assert.Null(ToolTipService.GetToolTip(expander));
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ConsecutiveTools_RenderCompactActivityWithAccessibleExpansion()
    {
        await _ui.ResetContainerAsync();
        var timeline = BuildTimeline(
        [
            Tool("tool-1", "powershell", "first output", ChatToolCallStatus.Success),
            Tool("tool-2", "read_file", "second output", ChatToolCallStatus.Success),
        ], generation: 1);
        var activityRow = Assert.Single(
            ChatToolActivityPresentation.Project(
                timeline.Entries,
                timeline.SessionId,
                timeline.TimelineGeneration,
                timeline.ShowToolCalls),
            row => row.IsActivityGroup);
        var expansionState = new ChatToolActivityExpansionState();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Element? currentElement = null;
        var disposing = false;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                // Reconcile native controls without attaching them to the VSTest visual tree,
                // where applying the unpackaged Expander template crashes Microsoft.UI.Xaml.
                host = new ReactorHostControl();
                Action? reconcile = null;
                Action requestRender = () =>
                {
                    if (!disposing)
                        _ui.Dispatcher.TryEnqueue(() => reconcile!());
                };
                reconcile = () =>
                {
                    if (disposing)
                        return;

                    var nextElement = ToolCallCardRenderer.BuildActivity(
                        timeline,
                        activityRow,
                        expansionState);
                    root = host.Reconciler.Reconcile(
                        currentElement!,
                        nextElement,
                        root!,
                        requestRender);
                    currentElement = nextElement;
                    host.Content = root;
                };
                currentElement = ToolCallCardRenderer.BuildActivity(
                    timeline,
                    activityRow,
                    expansionState);
                root = host.Reconciler.Mount(currentElement, requestRender);
                host.Content = root;
            });

            await _ui.RunOnUIAsync(() =>
            {
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                var name = AutomationProperties.GetName(activity);
                Assert.Contains("Activity:", name, StringComparison.Ordinal);
                Assert.Contains("2 tools", name, StringComparison.Ordinal);
                Assert.Contains("Collapsed", name, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));

                activity.IsExpanded = true;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.True(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var expanders = FindLogical<Expander>(root!).ToArray();
                var activity = Assert.Single(
                    expanders,
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.True(activity.IsExpanded);
                Assert.Contains("Expanded", AutomationProperties.GetName(activity), StringComparison.Ordinal);

                var nested = expanders
                    .Where(expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.Equal(2, nested.Length);
                Assert.DoesNotContain(
                    FindExpandedLogical<RichTextBlock>(root!),
                    text => text.IsTextSelectionEnabled);
                Assert.All(nested, expander =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander)));
                    expander.IsExpanded = true;
                });

                var selectableOutputs = FindExpandedLogical<RichTextBlock>(root!)
                    .Where(text => text.IsTextSelectionEnabled)
                    .Select(CollectText)
                    .ToArray();
                Assert.Contains("first output", selectableOutputs);
                Assert.Contains("second output", selectableOutputs);
                activity.IsExpanded = false;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.False(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                Assert.Contains("Collapsed", AutomationProperties.GetName(activity), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));
            });
        }
        finally
        {
            if (host is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    disposing = true;
                    if (root is not null && currentElement is not null)
                    {
                        var empty = Empty();
                        root = host.Reconciler.Reconcile(
                            currentElement,
                            empty,
                            root,
                            static () => { });
                        currentElement = empty;
                    }
                    host.Content = null;
                    host.Dispose();
                    themeScope?.Dispose();
                });
            }
        }
    }

    private static ChatTimelinePresentationContext BuildTimeline(
        IReadOnlyList<ChatTimelineItem> entries,
        long generation) =>
        new(
            SessionId: "reactor-tool-activity-proof",
            Entries: entries,
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            TimelineGeneration: generation,
            ShowToolCalls: true);

    private static ChatTimelineItem Tool(
        string id,
        string name,
        string output,
        ChatToolCallStatus result) =>
        new(
            id,
            ChatTimelineItemKind.ToolCall,
            name,
            ToolName: name,
            ToolResult: result,
            ToolOutput: output);

    private static string CollectText(RichTextBlock richTextBlock)
    {
        var text = new System.Text.StringBuilder();
        foreach (var paragraph in richTextBlock.Blocks.OfType<Paragraph>())
        {
            foreach (var inline in paragraph.Inlines)
                AppendInlineText(inline, text);
        }

        return text.ToString();
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder text)
    {
        switch (inline)
        {
            case Run run:
                text.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    AppendInlineText(child, text);
                break;
            case LineBreak:
                text.Append('\n');
                break;
        }
    }

    private static IEnumerable<T> FindExpandedLogical<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T self)
            yield return self;

        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children,
            Border border when border.Child is not null => [border.Child],
            ScrollViewer scrollViewer when scrollViewer.Content is DependencyObject content => [content],
            Expander expander when expander.IsExpanded && expander.Content is DependencyObject content => [content],
            Microsoft.UI.Xaml.Controls.Expander => [],
            ContentControl contentControl when contentControl.Content is DependencyObject content => [content],
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var descendant in FindExpandedLogical<T>(child))
                yield return descendant;
        }
    }

    private async Task DrainReactorQueueAsync()
    {
        for (var pass = 0; pass < 3; pass++)
            await _ui.YieldToRenderAsync();
    }

}
