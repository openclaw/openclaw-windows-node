using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Theming;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class GatewayThemeSettingsTests
{
    // Voice's status badge and transcript bubble put text on the voice accent, which takes the theme's primary color;
    // fixed white text was unreadable on light primaries such as CRT's dark one.
    [Fact]
    public void VoiceAccentTextIsReadableOnEveryBuiltInThemesAccent()
    {
        foreach (var theme in BuiltinGatewayThemes.All)
        {
            foreach (var palette in new[] { theme.Light, theme.Dark }.OfType<ResolvedThemePalette>())
            {
                var projection = ThemeResourceProjection.Project(palette);
                var contrast = ThemeLegibility.Contrast(projection.Brushes["VoiceAccentForegroundBrush"], projection.Colors["SystemAccentColor"]);
                Assert.True(contrast >= 3, $"{theme.Id} {palette.Mode}: {contrast:F2}");
            }
        }
    }
    // A transparent plugin or imported theme passes the legibility check over black; its window canvas is that opaque
    // color, so the desktop never shows through a themed window.
    [Fact]
    public void ATransparentThemeStillProjectsAnOpaqueWindowCanvas()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "name": "Glass", "dark": {
              "background": "#00000000", "foreground": "#ffffff", "card": "#00000000", "card-foreground": "#ffffff",
              "popover": "#00000000", "popover-foreground": "#ffffff", "primary": "#5ab6d8", "primary-foreground": "#000000",
              "secondary": "#00000000", "secondary-foreground": "#ffffff", "muted": "#00000000", "muted-foreground": "#cccccc",
              "accent": "#5ab6d8", "accent-foreground": "#000000", "destructive": "#f87171", "destructive-foreground": "#000000",
              "border": "#ffffff33", "input": "#ffffff33", "ring": "#5ab6d8" } }
            """);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(json.RootElement, out var definition, out var error), error);
        var glass = ThemePaletteExpander.Resolve(
            new GatewayThemeDescriptor("user/glass", "Glass", "", GatewayThemeSource.User, [ThemeColorMode.Dark], null), definition);
        Assert.True(ThemeLegibility.IsReadable(glass));

        var canvas = ThemeResourceProjection.Project(glass.Dark!).Brushes["SolidBackgroundFillColorBaseBrush"];
        Assert.Equal(ThemeColor.Black, canvas);
    }

    // A Gateway theme is opaque like the dashboard, except under High Contrast.
    [Theory]
    [InlineData(false, false, "System")]
    [InlineData(true, false, "Opaque")]
    [InlineData(true, true, "System")]
    public void GatewayThemeIsOpaqueOutsideHighContrast(bool themeActive, bool highContrast, string expected) =>
        Assert.Equal(expected, ThemeBackdropPolicy.Choose(themeActive, highContrast).ToString());

    [Fact]
    public void FontMapperKeepsBundledFacesAndEndsInInstalledWindowsFonts()
    {
        var mono = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse(
            "\"JetBrains Mono\", ui-monospace, SFMono-Regular, \"SF Mono\", Menlo, Monaco, Consolas, monospace"),
            ThemeFontMapper.Role.Code);
        var serif = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse("\"Lora\", Georgia, \"Times New Roman\", serif"),
            ThemeFontMapper.Role.Ui);
        var unknown = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse("\"Inter\", -apple-system, BlinkMacSystemFont"),
            ThemeFontMapper.Role.Ui);

        Assert.Equal("ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono, Consolas, Cascadia Mono", mono);
        Assert.Equal("ms-appx:///Assets/Fonts/Lora.ttf#Lora, Georgia, Times New Roman, Segoe UI Variable Text", serif);
        Assert.Equal("Inter, Segoe UI Variable Text", unknown);
    }

    [Fact]
    public void EveryBuiltinThemeTypefaceIsBundledWithTheCompanion()
    {
        var fonts = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Assets", "Fonts");
        var families = BuiltinGatewayThemes.All
            .SelectMany(theme => new[] { theme.Light!, theme.Dark! })
            .SelectMany(palette => new[] { palette.BodyFont.Primary, palette.ChatFont.Primary, palette.MonoFont.Primary })
            .Distinct();

        foreach (var family in families)
        {
            Assert.True(ThemeFontMapper.BundledFamilies.TryGetValue(family, out var file), $"{family} is not bundled.");
            Assert.True(File.Exists(Path.Combine(fonts, file + ".ttf")), $"{file}.ttf is missing.");
        }
    }

    [Fact]
    public void PickerPersistsTheCompanionTheme()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        Assert.Equal("", vm.SelectedThemeKey);
        Assert.Equal("", vm.ThemeChoices[0].Key);

        vm.SelectedThemeKey = "tide";

        Assert.Equal("tide", settings.Appearance.Theme?.Id);
        Assert.Equal("builtin", settings.Appearance.Theme?.Source);
        Assert.Equal("tide", AppearanceResolver.ResolveTheme(new SettingsManager(temp.Path).Appearance)?.Id);

        vm.SelectedThemeKey = "";
        Assert.Null(settings.Appearance.Theme);
    }

    [Fact]
    public void ReplacingPickerItemsKeepsTheSavedTheme()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        vm.SelectedThemeKey = "tide";
        // A WinUI ComboBox clears its selection, writing null, when its ItemsSource is replaced.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.ThemeChoices))
                vm.SelectedThemeKey = null!;
        };

        vm.DefaultThemeLabel = "Windows default (relabelled)";

        Assert.Equal("tide", vm.SelectedThemeKey);
        Assert.Equal("tide", settings.Appearance.Theme?.Id);
        vm.SelectedThemeKey = "";
        Assert.Null(settings.Appearance.Theme);
    }
}
