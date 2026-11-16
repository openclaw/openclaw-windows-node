// Proves each light/dark mode keeps its own colors around a Gateway theme. Clearing the theme gives each mode its own
// Windows colors back, even when a brush was last resolved under the other mode (the dark brush of an app that only
// ever rendered light, or a brush code handed to an element in the other mode), and when the theme was applied under
// High Contrast. A theme applied at startup, before anything rendered, leaves the brushes it does not set on each
// mode's own Windows colors.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeRestoreTests.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Themes;
using OpenClawTray.Chat;
using OpenClawTray.Theming;
using FunctionalTheme = OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeRestoreTests(UIThreadFixture ui)
{
    [Fact]
    public async Task ClearingAThemeRestoresEachModesOwnTextColor()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            // Fresh resources: shared ones may already hold constant colors from an earlier theme.
            var previous = Application.Current.Resources;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                // Only the mode the app is not running in can hold the other mode's value (the bug's precondition).
                var (otherKey, appTheme) = Application.Current.RequestedTheme == ApplicationTheme.Light
                    ? ("Default", ElementTheme.Light)
                    : ("Light", ElementTheme.Dark);
                ResolveUnder(Brush(otherKey), appTheme);
                Assert.Equal(FunctionalTheme.ResolveColor("TextFillColorPrimary", appTheme), Brush(otherKey).Color);

                Assert.True(applicator.Apply(BuiltinGatewayThemes.All.Single(theme => theme.Id == "tide")));
                Assert.True(applicator.Apply(null));

                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    Assert.Equal(FunctionalTheme.ResolveColor("TextFillColorPrimary", theme), Rendered(theme, "ChatTextBrush"));
            }
            finally
            {
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    [Fact]
    public async Task AThemeAppliedBeforeAnythingRendersLeavesUnthemedBrushesOnEachModesOwnColor()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            // Fresh production resources that nothing has rendered yet, as when the app starts with a saved theme.
            var previous = Application.Current.Resources;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                var manuscript = BuiltinGatewayThemes.All.Single(theme => theme.Id == "manuscript");
                Assert.True(applicator.Apply(manuscript));
                foreach (var (theme, mode) in new[] { (ElementTheme.Light, ThemeColorMode.Light), (ElementTheme.Dark, ThemeColorMode.Dark) })
                {
                    var controlFill = Rendered(theme, "ControlFillColorDefaultBrush");
                    var text = Rendered(theme, "TextFillColorPrimaryBrush");
                    Assert.Equal(FunctionalTheme.ResolveColor("ControlFillColorDefault", theme), controlFill);
                    var themed = ThemeResourceProjection.Project(manuscript.PaletteFor(mode)).Brushes["TextFillColorPrimaryBrush"];
                    var expected = Windows.UI.Color.FromArgb(themed.A, themed.R, themed.G, themed.B);
                    Assert.True(expected == text, $"{theme} text {text} is not the theme's {expected}. {Describe("TextFillColorPrimaryBrush")}");
                }
            }
            finally
            {
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // Some framework brushes are read-only, and elements that resolved one never read an application-root override.
    // In the app's own mode the theme replaces it once in WinUI's theme dictionary: later themes recolor the copy
    // (writing the dictionary again fails once windows exist), and clearing the theme gives it Windows' color back.
    [Fact]
    public async Task AReadOnlyFrameworkBrushInTheAppsModeIsReplacedOnceAndFollowsEveryTheme()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            var writable = AppThemeApplicator.IsWritable;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                const string key = "TextFillColorPrimaryBrush";
                var (appTheme, appMode) = AppMode();
                var windows = RenderedBrush(appTheme, key);
                var windowsColor = windows.Color;
                AppThemeApplicator.IsWritable = brush => !ReferenceEquals(brush, windows);

                foreach (var id in new[] { "manuscript", "tide" })
                {
                    var theme = BuiltinGatewayThemes.All.Single(theme => theme.Id == id);
                    Assert.True(applicator.Apply(theme));
                    Assert.Equal(windowsColor, windows.Color);
                    Assert.Equal(Projected(theme, appMode, key), Rendered(appTheme, key));
                }

                Assert.True(applicator.Apply(null));
                Assert.Equal(windowsColor, Rendered(appTheme, key));
            }
            finally
            {
                AppThemeApplicator.IsWritable = writable;
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // Writing WinUI's theme dictionary for the mode the app doesn't run in loads it under the app's mode, which binds its
    // aliases to the app mode's brushes: a dark Hub on light Windows painted its Settings and chat background, buttons
    // and combo boxes light. A read-only brush in that mode is left alone instead.
    [Fact]
    public async Task AReadOnlyBrushInTheOtherModeLeavesThatModesControlsOnTheirOwnColors()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            var writable = AppThemeApplicator.IsWritable;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                var (appTheme, _) = AppMode();
                var (otherTheme, otherMode, otherKey) = appTheme == ElementTheme.Light
                    ? (ElementTheme.Dark, ThemeColorMode.Dark, "Default")
                    : (ElementTheme.Light, ThemeColorMode.Light, "Light");
                // Looked up, not rendered: nothing has loaded that mode's dictionary yet, as at startup.
                var readOnly = FluentBrush(otherKey, "TextFillColorPrimaryBrush");
                AppThemeApplicator.IsWritable = brush => !ReferenceEquals(brush, readOnly);
                var tide = BuiltinGatewayThemes.All.Single(theme => theme.Id == "tide");

                Assert.True(applicator.Apply(tide));

                Assert.Equal(Projected(tide, otherMode, "LayerFillColorDefaultBrush"), Rendered(otherTheme, "NavigationViewContentBackground"));
                Assert.Same(RenderedBrush(otherTheme, "ControlFillColorDefaultBrush"), RenderedBrush(otherTheme, "ButtonBackground"));
                Assert.Equal(FunctionalTheme.ResolveColor("ControlFillColorDefault", otherTheme), Rendered(otherTheme, "ComboBoxBackground"));
            }
            finally
            {
                AppThemeApplicator.IsWritable = writable;
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }
    // A window whose App theme differs from Windows (a dark Hub on light Windows) must paint its own mode's palette:
    // every projected brush, and the Fluent aliases controls resolve them through, such as the NavigationView content
    // layer behind Settings and chat.
    [Fact]
    public async Task EachModeRendersItsOwnPaletteWhenTheAppRunsInTheOtherMode()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                var tide = BuiltinGatewayThemes.All.Single(theme => theme.Id == "tide");
                Assert.True(applicator.Apply(tide));
                var wrong = new List<string>();
                foreach (var (theme, mode) in new[] { (ElementTheme.Light, ThemeColorMode.Light), (ElementTheme.Dark, ThemeColorMode.Dark) })
                {
                    var projection = ThemeResourceProjection.Project(tide.PaletteFor(mode));
                    var expected = projection.Brushes.ToDictionary(pair => pair.Key, pair => pair.Value);
                    expected["NavigationViewContentBackground"] = projection.Brushes["LayerFillColorDefaultBrush"];
                    foreach (var (key, color) in expected)
                    {
                        var target = Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
                        if (Rendered(theme, key) is var rendered && rendered != target)
                            wrong.Add($"{theme} {key} {rendered} != {target}");
                    }
                }
                Assert.True(wrong.Count == 0, string.Join("; ", wrong) + $". {Describe("NavigationViewContentBackground")}");
            }
            finally
            {
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // A theme applied while Windows High Contrast is on must not take the light and dark brushes' values from that
    // moment as their originals; after High Contrast ends, clearing the theme gives each mode its own colors.
    [Fact]
    public async Task ClearingAThemeAppliedUnderHighContrastRestoresEachModesOwnTextColor()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            var highContrast = ChatThemeChanges.IsHighContrast;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                var tide = BuiltinGatewayThemes.All.Single(theme => theme.Id == "tide");
                ChatThemeChanges.IsHighContrast = static () => true;
                Assert.True(applicator.Apply(tide));
                ChatThemeChanges.IsHighContrast = static () => false;
                Assert.True(applicator.Apply(tide));
                Assert.True(applicator.Apply(null));

                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    Assert.Equal(FunctionalTheme.ResolveColor("TextFillColorPrimary", theme), Rendered(theme, "ChatTextBrush"));
            }
            finally
            {
                ChatThemeChanges.IsHighContrast = highContrast;
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // A theme's user-bubble color already carries the dashboard's translucency, so the theme replaces the brush's
    // XAML opacity. Clearing the theme must give Windows default its faint accent wash back, not a solid accent fill.
    [Fact]
    public async Task UserBubbleOpacityFollowsTheThemeAndIsRestored()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            Application.Current.Resources = FreshProductionResources();
            var applicator = new AppThemeApplicator();
            try
            {
                var modes = new[] { ElementTheme.Light, ElementTheme.Dark };
                var original = modes.ToDictionary(theme => theme, theme => RenderedBrush(theme, "ChatUserBrush").Opacity);
                Assert.All(original.Values, opacity => Assert.InRange(opacity, 0.01, 0.5));

                Assert.True(applicator.Apply(BuiltinGatewayThemes.All.Single(theme => theme.Id == "manuscript")));
                foreach (var theme in modes)
                    Assert.Equal(1, RenderedBrush(theme, "ChatUserBrush").Opacity);

                Assert.True(applicator.Apply(null));
                foreach (var theme in modes)
                    Assert.Equal(original[theme], RenderedBrush(theme, "ChatUserBrush").Opacity);
            }
            finally
            {
                applicator.Apply(null);
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    private static (ElementTheme Theme, ThemeColorMode Mode) AppMode() =>
        Application.Current.RequestedTheme == ApplicationTheme.Light
            ? (ElementTheme.Light, ThemeColorMode.Light)
            : (ElementTheme.Dark, ThemeColorMode.Dark);

    private static Windows.UI.Color Projected(ResolvedTheme theme, ThemeColorMode mode, string key)
    {
        var color = ThemeResourceProjection.Project(theme.PaletteFor(mode)).Brushes[key];
        return Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    // The brush WinUI's own theme dictionary holds for key, looked up without rendering anything.
    private static SolidColorBrush FluentBrush(string themeKey, string key)
    {
        static ResourceDictionary? Find(ResourceDictionary dictionary)
        {
            foreach (var merged in dictionary.MergedDictionaries)
            {
                if (merged is XamlControlsResources)
                    return merged;
                if (Find(merged) is { } nested)
                    return nested;
            }
            return null;
        }
        var fluent = (ResourceDictionary)Find(Application.Current.Resources)!.ThemeDictionaries[themeKey];
        return (SolidColorBrush)fluent[key];
    }

    // Controls take the corner radii of the theme in effect at startup, before the first control exists, and keep them
    // for the session: WinUI resolves a control style's radius once, so a later switch would reshape some control types
    // and not others. High Contrast at startup keeps Fluent's geometry.
    [Fact]
    public async Task ControlsKeepTheCornerRadiiDecidedAtStartup()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            var highContrast = ChatThemeChanges.IsHighContrast;
            var crt = BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt");
            try
            {
                Application.Current.Resources = FreshProductionResources();
                var fluent = ((CornerRadius)Application.Current.Resources["ControlCornerRadius"]).TopLeft;
                ChatThemeChanges.IsHighContrast = static () => true;
                new AppThemeApplicator().DecideControlGeometry(crt, ThemeColorMode.Light);
                Assert.Equal(fluent, RenderedButtonRadius());

                Application.Current.Resources = FreshProductionResources();
                ChatThemeChanges.IsHighContrast = static () => false;
                var applicator = new AppThemeApplicator();
                applicator.DecideControlGeometry(crt, ThemeColorMode.Light);
                Assert.Equal(crt.Light!.Radii.Small, RenderedButtonRadius());
                Assert.Equal(crt.Light.Radii.Medium, ((CornerRadius)Application.Current.Resources["OverlayCornerRadius"]).TopLeft);

                applicator.DecideControlGeometry(null, ThemeColorMode.Light);
                // A later theme with other radii changes neither the session's keys nor a control type created after it.
                var claw = BuiltinGatewayThemes.All.Single(theme => theme.Id == "claw");
                Assert.NotEqual(crt.Light.Radii.Small, claw.Light!.Radii.Small);
                Assert.True(applicator.Apply(claw));
                Assert.Equal(crt.Light.Radii.Small, ((CornerRadius)Application.Current.Resources["ControlCornerRadius"]).TopLeft);
                Assert.Equal(crt.Light.Radii.Medium, ((CornerRadius)Application.Current.Resources["OverlayCornerRadius"]).TopLeft);
                var combo = new ComboBox();
                ui.Container.Children.Add(new Border { RequestedTheme = AppMode().Theme, Child = combo });
                combo.UpdateLayout();
                Assert.Equal(crt.Light.Radii.Small, combo.CornerRadius.TopLeft);
                Assert.True(applicator.Apply(null));
                Assert.Equal(crt.Light.Radii.Small, ((CornerRadius)Application.Current.Resources["ControlCornerRadius"]).TopLeft);
                Assert.Equal(crt.Light.Radii.Small, RenderedButtonRadius());
            }
            finally
            {
                ChatThemeChanges.IsHighContrast = highContrast;
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // Writing the other mode's WinUI theme dictionary before that mode renders binds its brush aliases to the app mode's
    // brushes (a dark Hub on light Windows showed light buttons, combo boxes and Settings background), so controls of
    // that mode keep Fluent's radii and their own colors.
    [Fact]
    public async Task ControlsInTheOtherModeKeepFluentRadiiAndTheirOwnColors()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var previous = Application.Current.Resources;
            var highContrast = ChatThemeChanges.IsHighContrast;
            try
            {
                Application.Current.Resources = FreshProductionResources();
                ChatThemeChanges.IsHighContrast = static () => false;
                var (appTheme, appMode) = AppMode();
                var otherTheme = appTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
                var fluent = ((CornerRadius)Application.Current.Resources["ControlCornerRadius"]).TopLeft;
                var crt = BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt");
                Assert.NotEqual(fluent, crt.PaletteFor(appMode).Radii.Small);

                new AppThemeApplicator().DecideControlGeometry(crt, appMode);

                Assert.Equal(crt.PaletteFor(appMode).Radii.Small, RenderedButtonRadius(appTheme));
                Assert.Equal(fluent, RenderedButtonRadius(otherTheme));
                Assert.Same(RenderedBrush(otherTheme, "LayerFillColorDefaultBrush"), RenderedBrush(otherTheme, "NavigationViewContentBackground"));
                Assert.Equal(FunctionalTheme.ResolveColor("ControlFillColorDefault", otherTheme), Rendered(otherTheme, "ButtonBackground"));
            }
            finally
            {
                ChatThemeChanges.IsHighContrast = highContrast;
                Application.Current.Resources = previous;
                ui.Container.Children.Clear();
            }
        });
    }

    // Without a theme the probe renders in the app's own mode, whichever mode an earlier test left the container in.
    private double RenderedButtonRadius(ElementTheme? theme = null)
    {
        var button = new Button { Content = "Radius probe" };
        ui.Container.Children.Add(new Border { RequestedTheme = theme ?? AppMode().Theme, Child = button });
        button.ApplyTemplate();
        button.UpdateLayout();
        return button.CornerRadius.TopLeft;
    }

    private static ResourceDictionary FreshProductionResources() =>
        (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            TestAppResourceLoader.LoadProductionMarkup(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")).ToString());

    private Windows.UI.Color Rendered(ElementTheme theme, string key) => RenderedBrush(theme, key).Color;

    private SolidColorBrush RenderedBrush(ElementTheme theme, string key)
    {
        var probe = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='4' Height='4' Background='{{ThemeResource {key}}}'/>");
        var host = new Border { RequestedTheme = theme, Child = probe };
        ui.Container.Children.Add(host);
        host.UpdateLayout();
        return Assert.IsType<SolidColorBrush>(probe.Background);
    }

    // Failure detail: which brush instance each mode renders, and which one an off-tree element of that mode resolves.
    private string Describe(string key)
    {
        var parts = new List<string> { $"app={Application.Current.RequestedTheme}" };
        var rendered = new Dictionary<ElementTheme, SolidColorBrush>();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            rendered[theme] = RenderedBrush(theme, key);
            var detached = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                $"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{{ThemeResource {key}}}'/>");
            var loaded = detached.Background as SolidColorBrush;
            detached.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            detached.RequestedTheme = theme;
            var flipped = detached.Background as SolidColorBrush;
            parts.Add($"{theme}: rendered {rendered[theme].Color}, off-tree at load {Same(loaded, rendered[theme])} {loaded?.Color}, " +
                $"after flip {Same(flipped, rendered[theme])} {flipped?.Color}");
        }
        parts.Add($"modes share one brush: {ReferenceEquals(rendered[ElementTheme.Light], rendered[ElementTheme.Dark])}");
        parts.Add("log: " + string.Join(" | ", ThemeLogLines()));
        return string.Join("; ", parts);

        static string Same(object? brush, object rendered) =>
            brush is null ? "null" : ReferenceEquals(brush, rendered) ? "same" : "other";
    }

    // The logger writes in the background; wait for the applicator's summary to reach the file.
    private static IEnumerable<string> ThemeLogLines()
    {
        List<string> lines = [];
        SpinWait.SpinUntil(() =>
        {
            try
            {
                using var stream = new FileStream(OpenClawTray.Services.Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                lines = new StreamReader(stream).ReadToEnd().Split('\n').Where(line => line.Contains("[Themes]")).ToList();
            }
            catch (IOException)
            {
            }
            return lines.Any(line => line.Contains("Applied theme"));
        }, TimeSpan.FromSeconds(2));
        return lines.Count == 0 ? ["(no applicator log)"] : lines.TakeLast(8).Select(line => line.Trim());
    }

    // An element in the other mode resolves the shared brush's {ThemeResource} color for that mode.
    private static void ResolveUnder(Brush brush, ElementTheme theme)
    {
        var probe = new Border { Background = brush };
        probe.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        probe.RequestedTheme = theme;
        probe.Background = null;
    }

    private static SolidColorBrush Brush(string themeKey) =>
        Find(Application.Current.Resources, themeKey) ?? throw new KeyNotFoundException($"{themeKey}/ChatTextBrush");

    private static SolidColorBrush? Find(ResourceDictionary dictionary, string themeKey)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme) && theme is ResourceDictionary themed &&
            themed.TryGetValue("ChatTextBrush", out var value) && value is SolidColorBrush brush)
            return brush;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, themeKey) is { } found)
                return found;
        }
        return null;
    }
}
