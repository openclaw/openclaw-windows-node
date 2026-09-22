namespace OpenClaw.Tray.Tests;

public sealed class AccessibilityThemeResourceSourceTests
{
    [Fact]
    public void ChatShell_UsesOneGalleryContentLayerAndPreservesHighContrast()
    {
        // Retirement: replace when both production window shells can be mounted in native tests.
        var hub = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml"));
        var popup = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "ChatWindow.xaml"));
        var resources = System.Xml.Linq.XElement.Parse(ReadSource("src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(hub.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        Assert.Contains("SystemBackdrop = new MicaBackdrop();",
            ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "ChatWindow.xaml.cs"));
        foreach (var shell in new[] { hub, popup })
        {
            var title = shell.Descendants().Single(element =>
                (string?)element.Attribute(x + "Name") == (shell == hub ? "AppTitleBar" : "ChatTitleBar"));
            Assert.Null(title.Attribute("Background"));
        }
        var layer = Assert.Single(popup.Descendants(), element =>
            (string?)element.Attribute("Background") == "{ThemeResource NavigationViewContentBackground}");
        Assert.Equal("1", (string?)layer.Attribute("Grid.Row"));
        Assert.Null(layer.Attribute("Visibility"));
        Assert.Equal("False", (string?)layer.Attribute("IsHitTestVisible"));
        Assert.DoesNotContain(hub.Descendants(), element =>
            (string?)element.Attribute("Background") == "{ThemeResource NavigationViewContentBackground}");
        foreach (var theme in resources.Descendants().Where(element =>
            element.Name.LocalName == "ResourceDictionary" && element.Attribute(x + "Key") is not null))
        {
            var canvas = theme.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatCanvasBrush");
            var highContrast = (string?)theme.Attribute(x + "Key") == "HighContrast";
            Assert.Equal(highContrast ? "{ThemeResource SystemColorWindowColor}" : "Transparent",
                (string?)canvas.Attribute("Color"));
            if (!highContrast)
            {
                var composer = theme.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatComposerBrush");
                Assert.Equal("{ThemeResource CardBackgroundFillColorDefault}", (string?)composer.Attribute("Color"));
            }
        }
    }

    [Fact]
    public void TrayThemeChanges_AreOwnedByXamlResourcesInsteadOfAccessibilitySettings()
    {
        var connection = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        var connectionXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml");
        var hub = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        var hubXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml");
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
        Assert.Contains("ImageIcon Source=\"{StaticResource Chat_Icon}\"", hubXaml);
        Assert.Contains("new ImageIcon", hub);
        Assert.Contains("NavView.Resources[\"Agents_Icon\"]", hub);
        Assert.Contains("ApplyHighContrastFallbackIfNeeded", hub);
        Assert.Contains("HubNavigationUseHighContrastIcons", hub);
        Assert.Contains("SwapToFontIcons", hub);
        Assert.Contains("FluentIconCatalog.Build", hub);
        Assert.Contains("item == NavAdvanced", hub);
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

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
