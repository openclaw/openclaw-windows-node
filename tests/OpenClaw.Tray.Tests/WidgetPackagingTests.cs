using System.Xml.Linq;
using System.Buffers.Binary;

namespace OpenClaw.Tray.Tests;

public sealed class WidgetPackagingTests
{
    [Fact]
    public void Provider_HandlesNativeNullInventoryBeforeFirstPin()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI",
            "Widgets", "NativeWidgetProvider.cs"));
        Assert.Contains("GetWidgetInfos() ?? []", source);
        Assert.DoesNotContain("GetWidgetInfos().", source);
        Assert.DoesNotContain("GetWidgetInfos()\n", source.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Manifest_RegistersASeparateProviderActivationWithValidAssetsAndMultipleInstances()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var manifest = XDocument.Load(Path.Combine(app, "Package.appxmanifest"));
        var provider = Assert.Single(manifest.Descendants(), e => e.Name.LocalName == "WidgetProvider");
        var activation = Assert.Single(provider.Descendants(), e => e.Name.LocalName == "CreateInstance");
        var classId = (string)activation.Attribute("ClassId")!;
        var classes = manifest.Descendants().Where(e => e.Name.LocalName == "Class").ToArray();
        Assert.Equal(classes.Length, classes.Select(c => (string?)c.Attribute("Id")).Distinct().Count());
        var server = Assert.Single(classes, c => (string?)c.Attribute("Id") == classId).Parent!;
        Assert.Equal("--widget-provider", (string?)server.Attribute("Arguments"));
        Assert.Equal("OpenClaw.Tray.WinUI.exe", (string?)server.Attribute("Executable"));
        var definition = Assert.Single(provider.Descendants(), e => e.Name.LocalName == "Definition");
        Assert.Equal("OpenClawTracker", (string?)definition.Attribute("Id"));
        Assert.Equal("true", (string?)definition.Attribute("AllowMultiple"));
        Assert.Equal("true", (string?)definition.Attribute("IsCustomizable"));
        Assert.Equal(new[] { "small", "medium", "large" },
            definition.Descendants().Where(e => e.Name.LocalName == "Size").Select(e => (string?)e.Attribute("Name")));
        foreach (var asset in provider.Descendants().Attributes("Path"))
            Assert.True(File.Exists(Path.Combine(app, asset.Value)), $"Missing widget asset: {asset.Value}");
        var screenshot = Assert.Single(definition.Descendants(), e => e.Name.LocalName == "Screenshot");
        var image = File.ReadAllBytes(Path.Combine(app, (string)screenshot.Attribute("Path")!));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image.Take(8));
        Assert.Equal(300, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4)));
        Assert.Equal(304, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4)));
    }

    [Fact]
    public void Activation_DoesNotPutProviderLifecycleInAppOrOverwriteUnrelatedComClasses()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var entry = File.ReadAllText(Path.Combine(app, "Program.cs"));
        var provider = File.ReadAllText(Path.Combine(app, "Widgets", "NativeWidgetProvider.cs"));
        var project = File.ReadAllText(Path.Combine(app, "OpenClaw.Tray.WinUI.csproj"));
        var composition = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        Assert.True(entry.IndexOf("WidgetProviderHost.Run()", StringComparison.Ordinal) <
            entry.IndexOf("Application.Start", StringComparison.Ordinal));
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", project);
        Assert.DoesNotContain("WidgetProviderHost", composition);
        Assert.DoesNotContain("NativeWidgetProvider", composition);
        // Generic "first COM class" rewriting would silently replace the widget
        // provider CLSID with the toast CLSID now that the manifest has two servers.
        Assert.Contains("EF9297B3-EEEB-4E50-8306-D1D118E04BC7([\\\"'])", project);
        Assert.Contains("0D582DA5-334F-4FCD-9524-80FE1D138C86", project);
        Assert.Contains("0D582DA5-334F-4FCD-9524-80FE1D138C86", provider);
    }
}
