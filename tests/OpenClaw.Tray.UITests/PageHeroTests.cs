using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using NativeGrid = Microsoft.UI.Xaml.Controls.Grid;
using NativeBorder = Microsoft.UI.Xaml.Controls.Border;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Mounted-control contracts, not production-theme or screenshot proof.
[Collection(UICollection.Name)]
public sealed class PageHeroTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id) =>
        FindDescendants<FrameworkElement>(root)
            .SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == id);

    [Fact]
    public async Task Header_ResizeRetainsActionFocusParentAndCaption()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        Button? action = null;
        DependencyObject? parent = null;
        var narrow = false;
        Microsoft.UI.Reactor.Core.Element Render() => PageHero.Header(
            "Hero", "\uE713", "Title", "Title",
            TextBlock("Answer"), TextBlock("Detail"),
            Button("Action", () => { }).AutomationId("Action"),
            narrow, reducedMotion: true, highContrast: true,
            captionLine: TextBlock("Caption").AutomationId("Caption"));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl();
                renderer.Mount(_ => Render());
                surface = new Border { Width = 800, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "Action") is Button { ActualWidth: > 0 }, "hero mounted"));
            await ui.RunOnUIAsync(() =>
            {
                action = Assert.IsType<Button>(Find(surface!, "Action"));
                parent = VisualTreeHelper.GetParent(action);
                Assert.True(action.Focus(FocusState.Programmatic));
            });
            foreach (var width in new[] { 480, 800 })
            {
                await ui.RunOnUIAsync(() =>
                {
                    narrow = width == 480;
                    surface!.Width = width;
                    renderer!.Mount(_ => Render());
                    ui.Container.UpdateLayout();
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => Find(surface!, "Hero") is Border hero && Math.Abs(hero.ActualWidth - width) < 1
                        && parent is Grid host && NativeGrid.GetRow(host) == (narrow ? 1 : 0),
                    "hero resized"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(action, Find(surface!, "Action"));
                    Assert.Same(parent, VisualTreeHelper.GetParent(action!));
                    Assert.Same(action, FocusManager.GetFocusedElement(surface!.XamlRoot));
                    var host = Assert.IsType<Grid>(parent);
                    Assert.Equal(narrow ? 1 : 0, NativeGrid.GetRow(host));
                    Assert.Equal(narrow ? 1 : 2, NativeGrid.GetColumn(host));
                    Assert.Equal(narrow ? 2 : 1, NativeGrid.GetColumnSpan(host));
                    Assert.Same(Find(surface!, "HeroGrid"), VisualTreeHelper.GetParent(host));
                    Assert.Equal("Caption", Assert.IsType<TextBlock>(Find(surface!, "Caption")).Text);
                    Assert.Empty(host.Transitions);
                    Assert.Empty(host.ChildrenTransitions);
                    Assert.Null(Find(surface!, "PageHeroAccentWash"));
                    Assert.Equal(AutomationHeadingLevel.Level1,
                        AutomationProperties.GetHeadingLevel(Find(surface!, "Title")));
                });
                await ui.RunOnUIAsync(() => CaptureIfRequestedAsync(surface!, $"PageHero-Resize-{width}"));
            }
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(760, 3, 1)]
    [InlineData(759, 2, 2)]
    [InlineData(380, 2, 2)]
    [InlineData(379, 1, 3)]
    public async Task Tiles_FillTracksPreserveIconGapAndSpanOddLastTile(double width, int columns, int rows)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        var tiles = new[]
        {
            new PageHeroTile("Static", "1", "\uE713", "Static"),
            new PageHeroTile("Action", "2", "\uE713", "Action", OnInvoked: static () => { }),
            new PageHeroTile("Disabled", "3", "\uE713", "Disabled", OnInvoked: static () => { }, IsEnabled: false),
        };
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl();
                renderer.Mount(_ => PageHero.SummaryTiles("Tiles", "Summary", tiles, width, reducedMotion: true));
                surface = new Border { Width = width, Height = 400, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "Disabled") is Button { ActualWidth: > 0 }, "summary tiles mounted"));
            await ui.RunOnUIAsync(() =>
            {
                var grid = Assert.IsType<Grid>(Find(surface!, "Tiles"));
                Assert.Equal(columns, grid.ColumnDefinitions.Count);
                Assert.Equal(rows, grid.RowDefinitions.Count);
                var disabled = Assert.IsType<Button>(Find(surface!, "Disabled"));
                Assert.False(disabled.IsEnabled);
                Assert.Equal(columns == 2 ? 2 : 1, NativeGrid.GetColumnSpan(disabled));
                foreach (var model in tiles)
                {
                    var tile = Find(surface!, model.AutomationId)!;
                    var expectedWidth = width / columns * NativeGrid.GetColumnSpan(tile) - tile.Margin.Left - tile.Margin.Right;
                    Assert.InRange(Math.Abs(tile.ActualWidth - expectedWidth), 0, 1);
                    var icon = Assert.IsType<Border>(Find(tile, $"{model.AutomationId}:IconWell"));
                    Assert.InRange(icon.ActualWidth, 35, 37);
                    var label = Assert.Single(FindDescendants<TextBlock>(tile), text => text.Text == model.Label);
                    var iconBounds = icon.TransformToVisual(tile).TransformPoint(new Windows.Foundation.Point());
                    var labelBounds = label.TransformToVisual(tile).TransformPoint(new Windows.Foundation.Point());
                    Assert.InRange(labelBounds.X - iconBounds.X - icon.ActualWidth, 11, 13);
                }
            });
            await ui.RunOnUIAsync(() => CaptureIfRequestedAsync(surface!, $"PageHero-Tiles-{width}"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("HighContrast", true)]
    public async Task ThemeAndMotion_KeepAccessibleContentAndClearTransitions(string theme, bool highContrast)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        ChatThemeProofScope? resources = null;
        var reducedMotion = false;
        Microsoft.UI.Reactor.Core.Element Render() => PageHero.Header(
            "ThemeHero", "\uE713", "Shared page", "ThemeTitle",
            TextBlock("Current status"),
            PageHero.AttentionBadge("Attention", "Needs attention", "One item needs review", "\uE7BA",
                PageHeroTone.Caution, reducedMotion),
            Button("Review", () => { }).AutomationId("Review"),
            narrow: false, reducedMotion, highContrast, pageTag: "usage");
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                renderer = new ReactorHostControl { RequestedTheme = resources.ElementTheme };
                renderer.Mount(_ => Render());
                surface = new Border
                {
                    Width = 800, Child = renderer, RequestedTheme = resources.ElementTheme,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                if (ui.IsSlow)
                    ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 700));
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "Review") is Button { ActualWidth: > 0 }, "themed hero mounted"));
            await ui.RunOnUIAsync(() =>
            {
                var hero = Assert.IsType<Border>(Find(surface!, "ThemeHero"));
                Assert.NotNull(hero.Background);
                Assert.NotNull(hero.BorderBrush);
                var marker = Assert.IsType<Border>(Find(surface!, "ThemeHero:Glyph"));
                if (highContrast)
                {
                    Assert.IsType<TextBlock>(marker.Child);
                    Assert.Null(Find(surface!, "PageHeroAccentWash"));
                }
                else
                {
                    Assert.IsType<Image>(marker.Child);
                    Assert.NotNull(Find(surface!, "PageHeroAccentWash"));
                }
                var badge = Find(surface!, "Attention")!;
                Assert.Equal("Needs attention. One item needs review", AutomationProperties.GetName(badge));
                Assert.Equal("One item needs review", AutomationProperties.GetItemStatus(badge));
                var grid = Assert.IsType<Grid>(Find(surface!, "ThemeHeroGrid"));
                if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                {
                    Assert.IsType<RepositionThemeTransition>(Assert.Single(grid.Transitions));
                    Assert.IsType<EntranceThemeTransition>(grid.ChildrenTransitions[0]);
                    Assert.IsType<AddDeleteThemeTransition>(grid.ChildrenTransitions[1]);
                }
                reducedMotion = true;
                renderer!.Mount(_ => Render());
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "ThemeHeroGrid") is Grid { Transitions.Count: 0, ChildrenTransitions.Count: 0 },
                "reduced motion clears existing transitions"));
            await ui.PauseAsync($"PageHero {theme}");
            await ui.RunOnUIAsync(() => CaptureIfRequestedAsync(surface!, $"PageHero-{theme}"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
                resources?.Dispose();
            });
        }
    }

    private static async Task CaptureIfRequestedAsync(FrameworkElement surface, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1"
            || Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is not { Length: > 0 } directory)
            return;
        var started = DateTime.UtcNow;
        await VisualTestCapture.CaptureAsync(surface, name);
        var target = Path.Combine(directory, name);
        Assert.True(Directory.Exists(target) && Directory.GetFiles(target, "*.png")
            .Any(path => File.GetLastWriteTimeUtc(path) >= started && new FileInfo(path).Length > 0),
            $"Requested native-control capture was not produced: {name}");
    }

    [Fact]
    public async Task Reveal_FocusesVisibleControlAfterCollapsedControl()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var hidden = new Button { Content = "Hidden", Visibility = Visibility.Collapsed };
            var visible = new Button { Content = "Visible" };
            var panel = new StackPanel();
            panel.Children.Add(hidden);
            panel.Children.Add(visible);
            ui.Container.Children.Add(panel);
            ui.Container.UpdateLayout();
            PageHero.RevealAndHighlight(panel, reducedMotion: true);
            Assert.Same(visible, FocusManager.GetFocusedElement(panel.XamlRoot));
        });
        await ui.ResetContainerAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "AnimationPreferenceEnabled")]
    public async Task RepeatedReveal_RestoresOriginalWithoutOverwritingExternalBackground(bool replaceBackground)
    {
        await ui.ResetContainerAsync();
        Border? target = null;
        Brush? original = null;
        Brush? firstHighlight = null;
        Brush? expected = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                    "This regression requires native animations enabled.");
                Assert.False(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
                    "This regression exercises highlighting outside OS high contrast.");
                original = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
                target = new Border { Width = 300, Height = 100, Background = original };
                ui.Container.Children.Add(target);
                ui.Container.UpdateLayout();
                PageHero.RevealAndHighlight(target, reducedMotion: false);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => !ReferenceEquals(target!.Background, original), "first highlight applied"));
            await ui.RunOnUIAsync(() =>
            {
                firstHighlight = target!.Background;
                Assert.NotSame(original, firstHighlight);
                PageHero.RevealAndHighlight(target, reducedMotion: false);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => !ReferenceEquals(target!.Background, firstHighlight)
                    && !ReferenceEquals(target.Background, original), "replacement highlight applied"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotSame(firstHighlight, target!.Background);
                Assert.NotSame(original, target.Background);
                // Repeated requests must never retain the previous temporary brush as the baseline.
                PageHero.RevealAndHighlight(target, reducedMotion: true);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => ReferenceEquals(target!.Background, original), "cancellation restores original background"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(original, target!.Background);
                PageHero.RevealAndHighlight(target, reducedMotion: false);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => !ReferenceEquals(target!.Background, original), "final highlight applied"));
            await ui.RunOnUIAsync(() =>
            {
                expected = replaceBackground ? new SolidColorBrush(Microsoft.UI.Colors.Green) : original;
                if (replaceBackground)
                    target!.Background = expected;
            });
            // A sentinel storyboard supplies a native completion boundary after both highlights.
            await ui.RunOnUIAsync(async () =>
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var marker = new SolidColorBrush();
                var animation = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromSeconds(1.5) };
                var storyboard = new Storyboard();
                Storyboard.SetTarget(animation, marker);
                Storyboard.SetTargetProperty(animation, nameof(Brush.Opacity));
                storyboard.Children.Add(animation);
                storyboard.Completed += (_, _) => completed.TrySetResult();
                storyboard.Begin();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Same(expected, target!.Background);
            });
        }
        finally
        {
            await ui.ResetContainerAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "AnimationPreferenceEnabled")]
    public async Task Reveal_PreservesThemeResourceAfterCompletionOrCancellation(bool localResource, bool cancel)
    {
        await ui.ResetContainerAsync();
        Grid? surface = null;
        Border? target = null;
        ReactorHostControl? renderer = null;
        ResourceDictionary? testResources = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(new Windows.UI.ViewManagement.UISettings().AnimationsEnabled);
                Assert.False(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
                testResources = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                        <ResourceDictionary.ThemeDictionaries>
                            <ResourceDictionary x:Key="Light">
                                <SolidColorBrush x:Key="RevealTestBackground" Color="SteelBlue"/>
                            </ResourceDictionary>
                            <ResourceDictionary x:Key="Dark">
                                <SolidColorBrush x:Key="RevealTestBackground" Color="DarkGreen"/>
                            </ResourceDictionary>
                        </ResourceDictionary.ThemeDictionaries>
                    </ResourceDictionary>
                    """);
                // Reactor parses its generated style before attachment, so its resource must be app-visible.
                Application.Current.Resources.MergedDictionaries.Add(testResources);
                surface = new Grid { RequestedTheme = ElementTheme.Light, Width = 300, Height = 100 };
                if (localResource)
                {
                    target = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                Background="{ThemeResource RevealTestBackground}"/>
                        """);
                    surface.Children.Add(target);
                }
                else
                {
                    renderer = new ReactorHostControl();
                    renderer.Mount(_ => Border(Empty()).Background(Theme.Ref("RevealTestBackground"))
                        .AutomationId("ThemedRevealTarget"));
                    surface.Children.Add(renderer);
                }
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                target ??= Find(surface!, "ThemedRevealTarget") as Border;
                return target?.Background is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.SteelBlue;
            }, "light theme resource resolved before reveal"));
            await ui.RunOnUIAsync(() => surface!.RequestedTheme = ElementTheme.Dark);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => target!.Background is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.DarkGreen,
                "fixture resource follows dark theme before reveal"));
            await ui.RunOnUIAsync(() => surface!.RequestedTheme = ElementTheme.Light);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => target!.Background is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.SteelBlue,
                "fixture resource returns to light before reveal"));
            await ui.RunOnUIAsync(() =>
            {
                if (!localResource)
                    Assert.Same(DependencyProperty.UnsetValue, target!.ReadLocalValue(NativeBorder.BackgroundProperty));
                PageHero.RevealAndHighlight(target!, reducedMotion: false);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => target!.Background is SolidColorBrush brush && brush.Color != Microsoft.UI.Colors.SteelBlue,
                "highlight is active"));
            if (cancel)
                await ui.RunOnUIAsync(() => PageHero.RevealAndHighlight(target!, reducedMotion: true));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => target!.Background is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.SteelBlue,
                "highlight completes or cancels"));
            await ui.RunOnUIAsync(() =>
            {
                surface!.RequestedTheme = ElementTheme.Dark;
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => target!.Background is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.DarkGreen,
                "original resource binding follows dark theme after reveal"));
            await ui.RunOnUIAsync(() =>
            {
                if (!localResource)
                    Assert.Same(DependencyProperty.UnsetValue, target!.ReadLocalValue(NativeBorder.BackgroundProperty));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
                if (testResources is not null) Application.Current.Resources.MergedDictionaries.Remove(testResources);
            });
        }
    }
}
