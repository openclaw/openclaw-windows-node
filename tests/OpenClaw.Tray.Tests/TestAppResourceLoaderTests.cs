using System.Xml;
using System.Xml.Linq;
using OpenClaw.TestSupport;
using OpenClaw.Tray.UITests;

namespace OpenClaw.Tray.Tests;

public sealed class TestAppResourceLoaderTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Standard", false)]
    [InlineData(" standard ", false)]
    [InlineData("Production", true)]
    [InlineData(" production ", true)]
    public void Profile_RequiresExplicitProductionSelection(string? profile, bool production)
    {
        Assert.Equal(production, TestAppResourceLoader.IsProductionProfile(profile));
    }

    [Theory]
    [InlineData("HighContrast")]
    [InlineData("1")]
    [InlineData("unknown")]
    public void Profile_RejectsUnknownValuesRatherThanFallingBack(string profile)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestAppResourceLoader.IsProductionProfile(profile));
        Assert.Contains("Standard or Production", error.Message);
    }

    [Fact]
    public void ProductionMarkup_PreservesControlsAndAllProductThemeVariants()
    {
        var resources = TestAppResourceLoader.LoadProductionMarkup(TestRepositoryPaths.GetRepositoryRoot());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(resources.Descendants(), element => element.Name.LocalName == "XamlControlsResources");
        Assert.DoesNotContain(resources.Descendants(), element => element.Attribute("Source") is not null);
        Assert.Equal(new[] { "Default", "Light", "HighContrast" },
            resources.Element(presentation + "ResourceDictionary.ThemeDictionaries")!
                .Elements().Select(element => (string?)element.Attribute(x + "Key")));
        foreach (var key in new[] { "ChatCanvasBrush" })
            Assert.Contains(resources.Descendants(), element => (string?)element.Attribute(x + "Key") == key);

        var highContrast = resources.Element(presentation + "ResourceDictionary.ThemeDictionaries")!
            .Elements().Single(element => (string?)element.Attribute(x + "Key") == "HighContrast");
        var textBrush = highContrast.Elements().Single(element => (string?)element.Attribute(x + "Key") == "TextFillColorSecondaryBrush");
        Assert.Equal("{ThemeResource SystemColorWindowTextColor}", (string?)textBrush.Attribute("Color"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void ProductionMarkup_RequiresRepositoryRoot(string? root)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestAppResourceLoader.LoadProductionMarkup(root));
        Assert.Contains("OPENCLAW_REPO_ROOT", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionMarkup_DoesNotRecoverFromMissingOrMalformedDictionary(bool malformed)
    {
        using var temp = new TempDirectory();
        var appDirectory = temp.Combine("src", "OpenClaw.Tray.WinUI");
        var themeDirectory = Path.Combine(appDirectory, "Themes");
        Directory.CreateDirectory(themeDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "App.xaml"), """
            <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="Themes/Required.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """);
        if (malformed)
        {
            File.WriteAllText(Path.Combine(themeDirectory, "Required.xaml"), "<ResourceDictionary");
            Assert.Throws<XmlException>(() => TestAppResourceLoader.LoadProductionMarkup(temp.Path));
        }
        else
        {
            Assert.Throws<FileNotFoundException>(() => TestAppResourceLoader.LoadProductionMarkup(temp.Path));
        }
    }
}
