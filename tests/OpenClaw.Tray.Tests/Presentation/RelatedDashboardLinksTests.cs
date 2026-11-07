using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class RelatedDashboardLinksTests
{
    private const string VerifiedVersion = "2026.10.9";

    private static readonly (string File, string Tag)[] HostPages =
    [
        ("InstancesPage.xaml", "instances"),
        ("CronPage.xaml", "cron"),
        ("UsagePage.xaml", "usage"),
        ("SessionsPage.xaml", "sessions"),
        ("DefaultModelPage.xaml", "model"),
        ("LocalAiPage.xaml", "local-ai"),
        ("PermissionsPage.xaml", "permissions"),
        ("SandboxPage.xaml", "sandbox"),
        ("DebugPage.xaml", "debug"),
    ];

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026.11.1")]
    [InlineData("2026.10.8")]
    [InlineData("not-a-version")]
    public void UnverifiedGatewayVersions_ShowNoLinks(string? version)
    {
        foreach (var (_, tag) in HostPages)
            Assert.Empty(RelatedDashboardLinks.For(tag, version, canAdmin: true));
    }

    [Theory]
    [InlineData("sandbox", true, "/settings/approvals|/settings/security|/settings/secrets")]
    [InlineData("sandbox", false, "/settings/approvals")]
    [InlineData("permissions", true, "/settings/approvals|/settings/security|/settings/secrets")]
    [InlineData("instances", false, "/systems|/settings/approvals|/logs")]
    [InlineData("cron", true, "/settings/automation|/activity|/logs")]
    [InlineData("cron", false, "/activity|/logs")]
    [InlineData("agent:research:cron", false, "/activity|/logs")]
    [InlineData("usage", false, "/activity|/settings/model-providers")]
    [InlineData("sessions", false, "/activity|/logs")]
    [InlineData("model", false, "/settings/model-providers")]
    [InlineData("local-ai", false, "/settings/model-providers")]
    [InlineData("debug", false, "/logs|/activity")]
    public void VerifiedGateway_ProjectsPageLinksInOrder_AndHidesAdminRoutesWithoutAdmin(
        string tag, bool canAdmin, string expectedPaths)
    {
        var links = RelatedDashboardLinks.For(tag, VerifiedVersion, canAdmin);

        Assert.Equal(expectedPaths.Split('|'), links.Select(link => link.Path));
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("config")]
    [InlineData("voice")]
    [InlineData("settings")]
    [InlineData("about")]
    [InlineData("notifications")]
    [InlineData("agent:main")]
    [InlineData("chat")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void PagesWithoutRelatedDashboardPages_ShowNoLinks(string? tag)
    {
        Assert.Empty(RelatedDashboardLinks.For(tag, VerifiedVersion, canAdmin: true));
    }

    [Fact]
    public void EveryLink_UsesTheVerifiedRouteCatalog_AndHasLocalizedCopy()
    {
        var resources = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
                "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value);
        var catalog = HubSearchCatalog.DashboardRoutes.ToDictionary(route => route.Id);

        foreach (var ids in RelatedDashboardLinks.PageRoutes.Values)
            Assert.Equal(ids.Length, ids.Distinct().Count());
        foreach (var (_, tag) in HostPages)
        foreach (var link in RelatedDashboardLinks.For(tag, VerifiedVersion, canAdmin: true))
        {
            Assert.Equal(catalog[link.Id].Path, link.Path);
            Assert.False(string.IsNullOrWhiteSpace(resources[link.TitleKey]));
            Assert.False(string.IsNullOrWhiteSpace(resources[link.DescriptionKey]));
        }
    }

    [Fact]
    public void InScopePages_HostTheSharedSectionOnce_WithTheirOwnTag()
    {
        foreach (var (file, tag) in HostPages)
        {
            var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", file);
            var hosts = Regex.Matches(xaml, @"<local:RelatedSettingsSection\b[^>]*PageTag=""([^""]+)""");
            Assert.True(hosts.Count == 1, $"{file} must host RelatedSettingsSection exactly once.");
            Assert.Equal(tag, hosts[0].Groups[1].Value);
            Assert.Contains("xmlns:local=\"using:OpenClawTray.Controls\"", xaml);
        }
    }

    [Theory]
    [InlineData("ConnectionPage.xaml")]
    [InlineData("ConfigPage.xaml")]
    [InlineData("WorkspacePage.xaml")]
    public void ExcludedPages_DoNotHostTheSection(string file)
    {
        Assert.DoesNotContain("RelatedSettingsSection",
            Read("src", "OpenClaw.Tray.WinUI", "Pages", file));
    }

    [Fact]
    public void Section_LaunchesOnlyThroughAppDashboardCommand_WithoutBuildingUrls()
    {
        var code = Read("src", "OpenClaw.Tray.WinUI", "Controls", "RelatedSettingsSection.xaml.cs");

        Assert.Contains("RelatedDashboardLinks.For(", code);
        Assert.Contains(".OpenDashboard(", code);
        // Compact link rows: HyperlinkButton keeps a working UIA Invoke, unlike the stock Toolkit SettingsCard.
        Assert.Contains("HyperlinkButton", code);
        Assert.DoesNotContain("new SettingsCard", code);
        Assert.DoesNotContain("SetupActionCard", code);
        Assert.DoesNotContain("http", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Launcher.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new Uri", code, StringComparison.Ordinal);
    }
}
