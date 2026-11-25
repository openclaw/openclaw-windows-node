using System.Xml.Linq;
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
    [Theory]
    [InlineData("dark", "#000000", true)]
    [InlineData("light", "#050505", false)]
    public void TransparentThemesAreJudgedOnTheirProjectedWindowCanvas(string mode, string expectedRoot, bool readable)
    {
        // #5a5a5a clears 3:1 on black but not on the light sidebar's 2% white tint over black.
        using var json = System.Text.Json.JsonDocument.Parse($$"""
            { "name": "Glass", "{{mode}}": {
              "background": "#00000000", "foreground": "#5a5a5a", "card": "#00000000", "card-foreground": "#ffffff",
              "popover": "#00000000", "popover-foreground": "#ffffff", "primary": "#5ab6d8", "primary-foreground": "#000000",
              "secondary": "#00000000", "secondary-foreground": "#ffffff", "muted": "#00000000", "muted-foreground": "#cccccc",
              "accent": "#5ab6d8", "accent-foreground": "#000000", "destructive": "#f87171", "destructive-foreground": "#000000",
              "border": "#ffffff33", "input": "#ffffff33", "ring": "#5ab6d8" } }
            """);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(json.RootElement, out var definition, out var error), error);
        var colorMode = mode == "light" ? ThemeColorMode.Light : ThemeColorMode.Dark;
        var glass = ThemePaletteExpander.Resolve(
            new GatewayThemeDescriptor("user/glass", "Glass", "", GatewayThemeSource.User, [colorMode], null), definition);
        var canvas = ThemeResourceProjection.Project(glass.PaletteFor(colorMode)).Brushes["SolidBackgroundFillColorBaseBrush"];
        Assert.Equal(ThemeColor.FromHex(expectedRoot), canvas);
        Assert.Equal(readable, ThemeLegibility.IsReadable(glass));
    }

    // A Gateway theme is opaque like the dashboard, except under High Contrast.
    [Theory]
    [InlineData(false, false, "System")]
    [InlineData(true, false, "Opaque")]
    [InlineData(true, true, "System")]
    public void GatewayThemeIsOpaqueOutsideHighContrast(bool themeActive, bool highContrast, string expected) =>
        Assert.Equal(expected, ThemeBackdropPolicy.Choose(themeActive, highContrast).ToString());

    // The applicator moves only these app brushes onto the theme's accent ramp. Matching by color would also capture
    // fixed brushes that equal a Windows accent shade, such as the #0078D4 session tint.
    [Fact]
    public void AccentBoundAppKeysAreExactlyTheXamlBrushesBoundToAnAccentShade()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tray = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var files = Directory.GetFiles(Path.Combine(tray, "Themes"), "*.xaml").Append(Path.Combine(tray, "App.xaml"));
        var bound = files
            .SelectMany(file => XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            .SelectMany(dictionaries => dictionaries.Elements().Where(d => (string?)d.Attribute(x + "Key") is "Default" or "Light"))
            .SelectMany(dictionary => dictionary.Elements())
            .Where(resource => resource.DescendantsAndSelf().Attributes().Any(a => a.Value.Contains("SystemAccentColor", StringComparison.Ordinal)))
            .Select(resource => (string)resource.Attribute(x + "Key")!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(bound.Order(StringComparer.Ordinal), ThemeResourceProjection.AccentBoundAppKeys.Order(StringComparer.Ordinal));
    }

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

    // Windows selects a variable font's weights from its named instances. Without them only the default instance
    // remains, which is Black for Fraunces, so each bundled family must name a Regular weight.
    [Fact]
    public void EveryBundledTypefaceNamesARegularWeight()
    {
        var fonts = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Assets", "Fonts");
        foreach (var file in ThemeFontMapper.BundledFamilies.Values.Distinct())
        {
            var weights = NamedWeights(File.ReadAllBytes(Path.Combine(fonts, file + ".ttf")));
            Assert.True(weights.Contains(400), $"{file}.ttf names weights [{string.Join(", ", weights)}], not 400.");
        }
    }

    // Reads the wght coordinate of each OpenType fvar named instance.
    private static List<double> NamedWeights(byte[] font)
    {
        static int U16(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        static int I32(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        var weights = new List<double>();
        for (var table = 0; table < U16(font, 4); table++)
        {
            var record = 12 + 16 * table;
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) != "fvar") continue;
            var fvar = I32(font, record + 8);
            int axes = fvar + U16(font, fvar + 4), axisCount = U16(font, fvar + 8), axisSize = U16(font, fvar + 10);
            int instanceCount = U16(font, fvar + 12), instanceSize = U16(font, fvar + 14);
            var wght = Enumerable.Range(0, axisCount)
                .Single(axis => System.Text.Encoding.ASCII.GetString(font, axes + axis * axisSize, 4) == "wght");
            for (var instance = 0; instance < instanceCount; instance++)
                weights.Add(I32(font, axes + axisCount * axisSize + instance * instanceSize + 4 + 4 * wght) / 65536.0);
        }
        return weights;
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

    [Fact]
    public void UnlistedSavedThemeSurvivesThePickerWritingNull()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "name": "Glass", "dark": {
              "background": "#101010", "foreground": "#ffffff", "card": "#181818", "card-foreground": "#ffffff",
              "popover": "#181818", "popover-foreground": "#ffffff", "primary": "#5ab6d8", "primary-foreground": "#000000",
              "secondary": "#202020", "secondary-foreground": "#ffffff", "muted": "#202020", "muted-foreground": "#cccccc",
              "accent": "#5ab6d8", "accent-foreground": "#000000", "destructive": "#f87171", "destructive-foreground": "#000000",
              "border": "#ffffff33", "input": "#ffffff33", "ring": "#5ab6d8" } }
            """);
        var saved = new ThemeSelection { Id = "user/glass", Name = "Glass", Source = "user", Definition = json.RootElement.Clone() };
        settings.Appearance = new AppearanceSettings { Theme = saved };
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        Assert.Equal("user/glass", vm.SelectedThemeKey);

        vm.SelectedThemeKey = null!;

        Assert.Equal("user/glass", vm.SelectedThemeKey);
        Assert.Equal(saved, settings.Appearance.Theme);
    }
}
