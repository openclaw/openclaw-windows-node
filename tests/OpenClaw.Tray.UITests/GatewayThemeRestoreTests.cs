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
