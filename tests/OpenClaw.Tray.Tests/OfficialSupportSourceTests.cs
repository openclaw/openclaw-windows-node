using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class OfficialSupportSourceTests
{
    // WinUI owners are not loaded by Tray.Tests. Retire these source guards
    // when native navigation can be invoked directly in this test project.
    [Fact]
    public void WorkspaceAndAboutUseOfficialSupportWithoutReplacingDocsOrCommunity()
    {
        Assert.Contains("https://community.openclaw.ai/", Read("Helpers", "SupportLinks.cs"));
        var workspace = Read("Windows", "WorkspaceWindow.xaml.cs");
        Assert.Contains("""("Support", SupportLinks.OfficialSupportUrl)""", workspace);
        Assert.Contains("""("Documentation", "https://docs.openclaw.ai")""", workspace);
        Assert.Contains("""("Community", "https://discord.gg/clawd")""", workspace);
        var markup = Read("Pages", "SettingsPage.xaml");
        Assert.Contains("x:Uid=\"SettingsPage_AppInfoSupport\"", markup);
        Assert.Contains("Click=\"OnSupportLink\"", markup);
        Assert.Contains("""OpenShellTarget(SupportLinks.OfficialSupportUrl, "support")""",
            Read("Pages", "SettingsPage.xaml.cs"));
    }

    [Fact]
    public void BothNativeSupportLabelsExistInEveryLocale()
    {
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");
        foreach (var file in Directory.GetFiles(root, "Resources.resw", SearchOption.AllDirectories))
        {
            var values = XDocument.Load(file).Root!.Elements("data")
                .ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
            Assert.False(string.IsNullOrWhiteSpace(values["WorkspaceShell_Support"]));
            Assert.Equal(values["WorkspaceShell_Support"], values["SettingsPage_AppInfoSupport.Content"]);
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "en-us")
                Assert.Equal("Get support", values["WorkspaceShell_Support"]);
        }
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        new[] { TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI" }.Concat(path).ToArray()));
}
