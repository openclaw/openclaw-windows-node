namespace OpenClaw.Tray.Tests;

public sealed class AccessibilityThemeResourceSourceTests
{
    [Fact]
    public void MainWindowShells_PreserveMicaAndHighContrastChatResources()
    {
        // Retirement: replace when production window shells can be mounted in native tests.
        var settings = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml"));
        var workspace = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml"));
        var resources = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        // The one Mica backdrop and title bar belong to the Workspace window; the embedded Settings view owns neither.
        Assert.Single(workspace.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        Assert.DoesNotContain(settings.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        Assert.DoesNotContain(settings.Descendants(), element => element.Name.LocalName == "TitleBar");
        var title = workspace.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "WorkspaceTitleBar");
        Assert.Null(title.Attribute("Background"));
        Assert.DoesNotContain(settings.Descendants(), element =>
            (string?)element.Attribute("Background") == "{ThemeResource NavigationViewContentBackground}");
        foreach (var theme in resources.Descendants().Where(element =>
            element.Name.LocalName == "ResourceDictionary" && element.Attribute(x + "Key") is not null))
        {
            var canvas = theme.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatCanvasBrush");
            var highContrast = (string?)theme.Attribute(x + "Key") == "HighContrast";
            Assert.Equal(highContrast ? "{ThemeResource SystemColorWindowColor}" : "Transparent",
                (string?)canvas.Attribute("Color"));
            foreach (var (key, nativeColor) in new Dictionary<string, string>
            {
                ["ChatComposerBrush"] = "CardBackgroundFillColorDefault",
                ["ChatCardBrush"] = "CardBackgroundFillColorDefault",
                ["ChatStrokeBrush"] = "ControlStrokeColorDefault",
                ["ChatQuestionCardStrokeBrush"] = "ControlStrokeColorDefault",
                ["ChatTextBrush"] = "TextFillColorPrimary",
                ["ChatStrongTextBrush"] = "TextFillColorPrimary",
                ["ChatSecondaryTextBrush"] = "TextFillColorSecondary",
                ["ChatQuestionOptionBrush"] = "ControlFillColorDefault",
                ["ChatQuestionOptionHoverBrush"] = "ControlFillColorSecondary"
            })
            {
                var brush = theme.Elements().Single(element => (string?)element.Attribute(x + "Key") == key);
                var systemColor = key is "ChatComposerBrush" or "ChatCardBrush" or
                    "ChatQuestionOptionBrush" or "ChatQuestionOptionHoverBrush"
                    ? "SystemColorWindowColor" : "SystemColorWindowTextColor";
                Assert.Equal($"{{ThemeResource {(highContrast ? systemColor : nativeColor)}}}",
                    (string?)brush.Attribute("Color"));
            }
        }
    }

    [Theory]
    [InlineData("Default", "SystemAccentColorLight2", "0.12")]
    [InlineData("Light", "SystemAccentColorDark1", "0.08")]
    [InlineData("HighContrast", "SystemColorWindowTextColor", null)]
    public void ChatInteractions_UseNativeAccentAndContrastResources(string themeName, string accent, string? selectedOpacity)
    {
        var resources = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = resources.Descendants().Single(element =>
            element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(x + "Key") == themeName);
        var highContrast = themeName == "HighContrast";
        foreach (var (key, color) in new Dictionary<string, string>
        {
            ["ChatClawBrush"] = accent,
            ["ChatQuestionSelectedBrush"] = highContrast ? "SystemColorWindowColor" : accent,
            ["ChatQuestionSelectedStrokeBrush"] = highContrast ? "SystemColorHighlightColor" : accent
        })
        {
            var brush = theme.Elements().Single(element => (string?)element.Attribute(x + "Key") == key);
            Assert.Equal($"{{ThemeResource {color}}}", (string?)brush.Attribute("Color"));
            if (key == "ChatQuestionSelectedBrush")
                Assert.Equal(selectedOpacity, (string?)brush.Attribute("Opacity"));
        }
        var submit = resources.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatQuestionSubmitStyle");
        Assert.Contains(submit.Elements(), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "{ThemeResource AccentFillColorDefaultBrush}");
        Assert.Contains(submit.Elements(), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{ThemeResource TextOnAccentFillColorPrimaryBrush}");
    }

    [Fact]
    public void TrayThemeChanges_AreOwnedByXamlResourcesInsteadOfAccessibilitySettings()
    {
        var connection = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        var connectionXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml");
        var hub = ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        var hubXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml");
        var timeline = ReadSource("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");
        var resources = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml");

        Assert.DoesNotContain("AccessibilitySettings", connection);
        Assert.DoesNotContain("HighContrastChanged", connection);
        Assert.DoesNotContain("TrySubscribeAccessibilitySettings", connection);
        Assert.DoesNotContain("AccessibilitySettings", hub);
        Assert.DoesNotContain("HighContrastChanged", hub);
        Assert.DoesNotContain("AccessibilitySettings", timeline);
        Assert.DoesNotContain("TryDetectHighContrast", timeline);

        Assert.Contains("ConnectionCapabilityPillActiveBorderStyle", connection);
        Assert.Contains("Theme.Ref(\"CardBackgroundFillColorDefaultBrush\")", timeline);
        Assert.Contains("Theme.Ref(\"ControlStrokeColorDefaultBrush\")", timeline);
        Assert.Contains("ConnectionCapabilityPillSuccessBrush", connectionXaml);
        Assert.Contains("Source=\"{StaticResource Connection_Icon}\"", hubXaml);
        Assert.Contains("MicaBackdrop", ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml"));
        Assert.Contains("new ImageIcon", hub);
        Assert.Contains("NavView.Resources[\"Agents_Icon\"]", hub);
        Assert.Contains("ApplyHighContrastFallbackIfNeeded", hub);
        Assert.Contains("HubNavigationUseHighContrastIcons", hub);
        Assert.Contains("SwapToFontIcons", hub);
        Assert.Contains("FluentIconCatalog.Build", hub);
        Assert.Contains("item == AgentsNavItem", hub);
        Assert.DoesNotContain("NavAdvanced", hub);
        Assert.DoesNotContain("content.Equals(\"Advanced\"", hub);
        Assert.Contains("return FluentIconCatalog.Build(\"\\uE700\", 20);", hub);
        Assert.DoesNotContain("<IconSourceElement", hubXaml);
        Assert.DoesNotContain("FontIconSource", hubXaml);
        var stateIconBlock = connection[
            connection.IndexOf("if (stateGlyph != null)", StringComparison.Ordinal)
            ..connection.IndexOf("content.Children.Add(stateIcon)", connection.IndexOf(
                "if (stateGlyph != null)", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("Style = (Style)Resources[iconStyleKey]", stateIconBlock);
        Assert.DoesNotContain("Style = (Style)Resources[textStyleKey]", stateIconBlock);
        Assert.Contains("<ResourceDictionary x:Key=\"HighContrast\">", resources);
        Assert.Contains("SystemColorWindowColor", resources);
        Assert.Contains("SystemColorHighlightColor", resources);
        Assert.Contains("<x:Boolean x:Key=\"HubNavigationUseHighContrastIcons\">True</x:Boolean>", resources);
    }

    [Fact]
    public void SystemAndConnectionNavigationSvgIcons_HaveHighContrastGlyphFallbacks()
    {
        // Fixed-color SVGs are swapped for system-foreground glyphs in High Contrast; an unmapped tag
        // would silently render the generic hamburger glyph instead of a meaningful icon.
        var hub = ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        var settings = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml"));
        var fallbackTags = System.Text.RegularExpressions.Regex
            .Matches(hub, "\\{\\s*\"([a-z-]+)\",")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var menu = settings.Descendants().Single(element => element.Name.LocalName == "NavigationView.MenuItems");
        var systemSvgTags = menu.Elements()
            .SkipWhile(element => (string?)element.Attribute("Content") != "System")
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName is not ("NavigationViewItemHeader" or "NavigationViewItemSeparator"))
            .Where(element => element.Descendants().Any(icon => icon.Name.LocalName == "ImageIcon"))
            .Select(element => (string?)element.Attribute("Tag"))
            .OfType<string>()
            .ToArray();

        var connection = Assert.Single(menu.Elements(),
            element => (string?)element.Attribute("Tag") == "connection");
        Assert.Contains(connection.Descendants(), icon => icon.Name.LocalName == "ImageIcon");
        Assert.Contains("connection", fallbackTags);
        Assert.Contains("about", systemSvgTags);
        Assert.All(systemSvgTags, tag => Assert.Contains(tag, fallbackTags));
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
