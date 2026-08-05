namespace OpenClaw.Tray.Tests;

public sealed class AccessibilityThemeResourceSourceTests
{
    [Fact]
    public void TrayThemeChanges_AreOwnedByXamlResourcesInsteadOfAccessibilitySettings()
    {
        var connection = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        var connectionXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml");
        var hub = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        var hubXaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml");
        var timeline = ReadSource("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");
        var resources = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml");

        Assert.DoesNotContain("AccessibilitySettings", connection);
        Assert.DoesNotContain("HighContrastChanged", connection);
        Assert.DoesNotContain("TrySubscribeAccessibilitySettings", connection);
        Assert.DoesNotContain("AccessibilitySettings", hub);
        Assert.DoesNotContain("HighContrastChanged", hub);
        Assert.DoesNotContain("AccessibilitySettings", timeline);
        Assert.DoesNotContain("TryDetectHighContrast", timeline);

        Assert.Contains("ConnectionCapabilityPillActiveBorderStyle", connection);
        Assert.Contains("ChatUserBubbleSelectionStyle", timeline);
        Assert.Contains("ChatToolCardBorderStyle", timeline);
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
        Assert.Contains("ChatUserBubbleSelectionHighlightBrush", resources);
        Assert.Contains("SystemColorHighlightColor", resources);
        Assert.Contains("<x:Double x:Key=\"ChatAccessibleBorderThickness\">2</x:Double>", resources);
        Assert.Contains("<x:Boolean x:Key=\"HubNavigationUseHighContrastIcons\">True</x:Boolean>", resources);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
