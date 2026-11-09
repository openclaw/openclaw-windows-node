using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class AdaptiveSettingsLayoutSourceTests
{
    // Tray.Tests cannot mount WinUI controls. Retire these prohibited-measurement
    // guards when native narrow-viewport coverage exercises the same fields.
    [Theory]
    [InlineData("LocalAiPage")]
    [InlineData("VoiceSettingsPage")]
    [InlineData("DefaultModelPage")]
    public void SettingsFormsDoNotForceFieldsWiderThanTheirViewport(string page)
    {
        var markup = XDocument.Parse(Read("Pages", page + ".xaml"));
        var fields = markup.Descendants().Where(element =>
            element.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox" or "Slider" or "ProgressBar");
        Assert.NotEmpty(fields);
        foreach (var field in fields)
        {
            Assert.Null(field.Attribute("Width"));
            Assert.Null(field.Attribute("MinWidth"));
            if (field.Attribute("MaxWidth") is not null)
                Assert.Equal(field.Name.LocalName is "Slider" or "ProgressBar" ? "Stretch" : "Left",
                    field.Attribute("HorizontalAlignment")?.Value);
        }
    }

    [Fact]
    public void GeneratedSchemaFieldsDoNotForceFixedOrMinimumWidths()
    {
        var source = Read("Controls", "SchemaConfigEditor.xaml.cs");
        Assert.DoesNotMatch(@"\bMinWidth\s*=\s*\d+", source);
        // Excludes MaxWidth and the compact icon-only array remove button.
        Assert.DoesNotMatch(@"(?<!\w)Width\s*=\s*(?:200|250|300|350)\b", source);
        Assert.Contains("MaxWidth = 350", source);
        Assert.DoesNotMatch(@"MaxWidth\s*=\s*350,\s*HorizontalAlignment\s*=\s*HorizontalAlignment\.Stretch", source);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source,
            @"MaxWidth\s*=\s*350,\s*HorizontalAlignment\s*=\s*HorizontalAlignment\.Left").Count);
    }

    [Fact]
    public void ConfigResizeKeepsDraftControlsMountedAndBoundsBothViewports()
    {
        var markup = XDocument.Parse(Read("Pages", "ConfigPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = markup.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "SchemaTreeGrid");
        Assert.Null(editor.Attribute("MinHeight"));
        var search = markup.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "ConfigSearchBox");
        Assert.Null(search.Attribute("Width"));
        Assert.Single(markup.Descendants(), element => element.Name.LocalName == "TreeView");

        var source = Read("Pages", "ConfigPage.xaml.cs");
        var resize = source[source.IndexOf("private void OnSchemaTreeGridSizeChanged", StringComparison.Ordinal)..
            source.IndexOf("private void ApplyPaneVisibility", StringComparison.Ordinal)];
        Assert.Contains("e.NewSize.Width", resize);
        Assert.Contains("Math.Min(CompactTreeMaxHeight, e.NewSize.Height / 3)", resize);
        Assert.Contains("DetailRow.Height = compact ? new GridLength(1, GridUnitType.Star)", resize);
        Assert.Contains("Grid.SetRow(DetailPane, compact ? 1 : 0)", resize);
        Assert.Contains("Grid.SetColumnSpan(TreePane, compact ? 5 : 1)", resize);
        Assert.Contains("TreeSplitter.Visibility = compact ? Visibility.Collapsed", resize);
        Assert.DoesNotMatch(@"RenderTree|RestoreSelectedDetail|RenderDetail|CancelDetailRender|Children\.Clear|new SchemaConfigEditor", resize);
        Assert.DoesNotMatch(@"_selectedPath\s*=|_pendingChanges\.|_editSnapshot\s*=|Frame\.Navigate|\.Focus\(", resize);
        Assert.Contains("JsonSplitterColumn.Width = new GridLength(_jsonPreviewVisible ? SplitterWidth : 0)", source);
    }

    [Theory]
    [InlineData("LocalAiPage", "EngineActions", "ModelActions", "GatewayActions")]
    [InlineData("VoiceSettingsPage", "DownloadActions", "KokoroActions", "PiperActions")]
    [InlineData("DefaultModelPage", "ModelActions", "ProviderApiKeyPanel", "ProviderSignInPanel")]
    public void ActionRowsAdaptFromTheRouteViewportWithoutReplacingTheirControls(
        string page, string first, string second, string third)
    {
        var source = Read("Pages", page + ".xaml.cs");
        Assert.Contains("SizeChanged += OnLayoutSizeChanged", source);
        Assert.Contains("e.NewSize.Width", source);
        Assert.Contains("Orientation.Vertical : Orientation.Horizontal", source);
        var markup = XDocument.Parse(Read("Pages", page + ".xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { first, second, third })
        {
            Assert.Contains(name + ".Orientation = orientation", source);
            var row = markup.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name);
            Assert.Equal("StackPanel", row.Name.LocalName);
            Assert.Contains(row.Elements(), element => element.Name.LocalName is "Button" or "PasswordBox" or "ComboBox");
        }
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        new[] { TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI" }.Concat(path).ToArray()));
}
