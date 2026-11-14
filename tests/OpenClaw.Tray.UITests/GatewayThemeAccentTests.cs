// Proves accent visuals follow the Gateway theme's accent and return to the Windows accent when the theme is cleared:
// app brushes bound to a SystemAccentColor shade (the Hub navigation indicator, the usage palette, the automations hero
// gradient), the busy claw and code that reads the accent fill color. Brushes are recolored in place, so running
// animations and mounted controls update without a restart.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeAccentTests.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Themes;
using OpenClawTray.Theming;
using Windows.UI.ViewManagement;
using FunctionalTheme = OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeAccentTests(UIThreadFixture ui)
{
    [Fact]
    public Task AccentVisualsTakeTheThemeAccentAndReturnToTheWindowsAccent() => ui.RunOnUIAsync(() =>
    {
        using var resources = new ChatThemeProofScope("Dark");
        var nav = Assert.IsType<SolidColorBrush>(Themed("NavigationViewSelectionIndicatorForeground"));
        var usage = Assert.IsType<SolidColorBrush>(Themed("UsagePalette1Brush"));
        var claw = Assert.IsType<SolidColorBrush>(Themed("ChatClawBrush"));
        var hero = Assert.IsType<LinearGradientBrush>(Themed("AutomationsHeroBrush"));
        var settings = new UISettings();
        var windows = new
        {
            Accent = settings.GetColorValue(UIColorType.Accent),
            Light1 = settings.GetColorValue(UIColorType.AccentLight1),
            Light2 = settings.GetColorValue(UIColorType.AccentLight2),
            Fill = FunctionalTheme.ResolveColor("AccentFillColorDefault", ElementTheme.Dark),
        };
        Assert.Equal(windows.Accent, nav.Color);
        Assert.Equal(windows.Light2, usage.Color);
        Assert.Equal(windows.Light1, hero.GradientStops[0].Color);

        var applicator = new AppThemeApplicator();
        try
        {
            foreach (var id in new[] { "crt", "manuscript" })
            {
                var theme = BuiltinGatewayThemes.All.Single(entry => entry.Id == id);
                var palette = theme.PaletteFor(ThemeColorMode.Dark);
                var ramp = ThemeResourceProjection.Project(palette).Colors;
                Assert.True(applicator.Apply(theme));

                Assert.Equal(Color(ramp["SystemAccentColor"]), nav.Color);
                Assert.Equal(Color(ramp["SystemAccentColorLight2"]), usage.Color);
                Assert.Equal(Color(ramp["SystemAccentColorLight1"]), hero.GradientStops[0].Color);
                Assert.Equal(Color(palette.Accent), claw.Color);
                Assert.Equal(Color(palette.Primary), FunctionalTheme.ResolveColor("AccentFillColorDefault", ElementTheme.Dark));
                Assert.NotEqual(windows.Accent, nav.Color);
            }

            Assert.True(applicator.Apply(null));
            Assert.Equal(windows.Accent, nav.Color);
            Assert.Equal(windows.Light2, usage.Color);
            Assert.Equal(windows.Light1, hero.GradientStops[0].Color);
            Assert.Equal(windows.Fill, FunctionalTheme.ResolveColor("AccentFillColorDefault", ElementTheme.Dark));
        }
        finally
        {
            applicator.Apply(null);
        }
    });

    private static object Themed(string key) =>
        Find(Application.Current.Resources, key) ?? throw new KeyNotFoundException(key);

    // The dark ("Default") theme dictionary entry, wherever the app merges it.
    private static object? Find(ResourceDictionary dictionary, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue("Default", out var theme) &&
            theme is ResourceDictionary themed && themed.TryGetValue(key, out var value))
            return value;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged is not Microsoft.UI.Xaml.Controls.XamlControlsResources && Find(merged, key) is { } found)
                return found;
        }
        return null;
    }

    private static Windows.UI.Color Color(ThemeColor color) => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
}
