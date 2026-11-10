using System.Text.Json;
using System.Collections.Immutable;
using System.Xml.Linq;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubSearchCatalogTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigationUsesDestinationNamesAndNativeSymbolGlyphsWithoutChangingLegacyCommands(bool connected)
    {
        var resources = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value);
        var commands = HubPageRegistry.BuildCommands(new("main", true, null, [],
            resources.ToImmutableDictionary()));
        Assert.Contains(commands, command => command.Title == "Go to Config");
        var items = HubSearchCatalog.Navigation(commands, connected, true, key => resources[key]).ToArray();
        Assert.All(items, item =>
        {
            Assert.DoesNotContain("Go to", item.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Single(item.Icon);
            Assert.InRange(item.Icon[0], '\uE000', '\uF8FF');
        });
        if (connected)
        {
            Assert.Equal("Config", Assert.Single(items, item => item.Target.Value == "config").Title);
            var systems = Assert.Single(items, item => item.Target.Value == "systems");
            Assert.Equal("Systems", systems.Title);
            Assert.Equal("/settings/devices", systems.DashboardPath);
            Assert.Equal("Automations", Assert.Single(items, item => item.Target.Value == "agent:main:cron").Title);
        }
        else
        {
            Assert.DoesNotContain(items, item => item.Target.Value is "config" or "systems" or "agent:main:cron");
        }
        Assert.Equal("Connection", Assert.Single(items, item => item.Target.Value == "connection").Title);
        var models = Assert.Single(items, item => item.Title == "Models");
        Assert.Equal(new(HubSearchTargetKind.Page, "model"), models.Target);
        Assert.Equal(models, Assert.Single(HubSearch.Find(items, "Default model")).Item);
        var general = Assert.Single(items, item => item.Title == "General");
        Assert.Equal(new(HubSearchTargetKind.Page, "settings"), general.Target);
        Assert.Equal("About", Assert.Single(items, item => item.Target.Value == "about").Title);
        Assert.Equal("Dashboard", Assert.Single(items, item =>
            item.Target == new HubSearchTarget(HubSearchTargetKind.Dashboard, "")).Title);
        foreach (var query in new[] { "General", "Notification preferences", "notification-preferences" })
            Assert.Equal(general, Assert.Single(HubSearch.Find(items, query)).Item);
        Assert.Contains(HubSearch.Find(items, "notifications"), match => match.Item.Target.Value == "notifications");
        Assert.Empty(HubSearch.Find(items, "Agent Events"));
        Assert.Empty(HubSearch.Find(items, "Event Stream"));
        Assert.DoesNotContain(items, item => item.Target.Value.Contains("agentevents", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Channels", true, true)]
    [InlineData("Go to Channels", true, false)]
    [InlineData("Channels", false, false)]
    public void ChannelsSearchOpensDashboardInsteadOfRetiredNativePage(
        string query, bool connected, bool dashboardSupported)
    {
        var resources = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToImmutableDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value);
        var commands = HubPageRegistry.BuildCommands(new("main", false, null, [], resources));
        var items = HubSearchCatalog.Navigation(commands, connected, dashboardSupported, key => resources[key]);
        var result = Assert.Single(HubSearch.Find(items, query), match => match.Item.Title == "Channels").Item;

        Assert.Equal(HubSearchGroup.Dashboard, result.Group);
        Assert.True(result.OpensDashboard);
        Assert.Equal(new HubSearchTarget(HubSearchTargetKind.Dashboard, "channels"), result.Target);
        Assert.Equal("channels", result.DashboardPath);
        Assert.Null(HubPageRegistry.ResolvePage("channels"));

        var opened = new List<string?>();
        var navigated = new List<HubSearchTarget>();
        var executor = new HubCommandExecutor(() => null, () => null, opened.Add);
        executor.Execute(result.Target, navigated.Add);
        Assert.Equal(["channels"], opened);
        Assert.Empty(navigated);
    }

    [Fact]
    public void NavigationDestinationLabelsExistInEverySupportedLocale()
    {
        var resourceRoot = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings");
        var commands = HubPageRegistry.BuildCommands(new("main", true, null, [], ImmutableDictionary<string, string>.Empty));
        var keys = commands.Select(command => HubPageRegistry.SearchPresentation(
            command.Action.Kind == HubCommandActionKind.OpenDashboard ? "dashboard" : command.Action.Value!).TitleKey).Distinct();
        foreach (var file in Directory.EnumerateFiles(resourceRoot, "Resources.resw", SearchOption.AllDirectories))
        {
            var resources = XDocument.Load(file).Root!.Elements("data")
                .ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
            foreach (var key in keys)
                Assert.True(resources.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value), $"{file}: missing {key}");
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":null}")]
    [InlineData("{\"schema\":[]}")]
    public void InvalidSchemaDoesNotLookLikeASuccessfulEmptyIndex(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidDataException>(() => HubSearchCatalog.Config(json.RootElement, true, out _));
    }

    [Theory]
    [InlineData("agent:main:main", "/chat/main")]
    [InlineData("agent:work:global", "/chat/work/~key/global")]
    [InlineData("agent:work:12345678", "/chat/work/~key/12345678")]
    [InlineData("agent:work:dm:user.one", "/chat/work/dm/user%2Eone")]
    [InlineData("agent:work:~literal", "/chat/work/~~literal")]
    [InlineData("agent:work:.", "/chat/work/~dot")]
    [InlineData("agent:work:..", "/chat/work/~dotdot")]
    [InlineData("agent:work:hello world", "/chat/work/hello%20world")]
    [InlineData("agent:work:caf\u00e9", "/chat/work/caf%C3%A9")]
    [InlineData("agent:work:", null)]
    [InlineData("unknown", null)]
    public void DashboardSessionLinksPreserveExactKeys(string key, string? expected)
    {
        Assert.Equal(expected, HubSearchCatalog.SessionPath(key));
    }

    [Fact]
    public void DashboardMainSessionUsesAuthoritativeIdentityRatherThanAssumingTheMainSuffix()
    {
        Assert.Equal("/chat/work", HubSearchCatalog.SessionPath("agent:work:custom-home", isMain: true));
        Assert.Equal("/chat/work/~key/main", HubSearchCatalog.SessionPath("agent:work:main", isMain: false));
    }

    [Fact]
    public void ContextualDashboardPath_UsesCurrentChatSessionWhenHomeHistoryHasNoSession()
    {
        Assert.Equal("/chat/work/~key/12345678", HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home), "agent:work:12345678", isMain: false, "2026.10.9"));
    }

    [Fact]
    public void ContextualDashboardPath_PrefersVisibleSettingsOverRetainedChat()
    {
        Assert.Equal("/usage", HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("usage"), "agent:work:12345678", isMain: false, "2026.10.9"));
    }

    [Fact]
    public void ContextualDashboardPath_UsesExplicitSessionAndFallsBackWithoutVerifiedCounterpart()
    {
        Assert.Equal("/chat/work/~key/main", HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home, "agent:work:main"), "agent:main:main",
            isMain: false, "2026.10.9"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("local-ai"), null, null, "2026.10.9"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("agent:research"), null, null, "2026.10.9"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("config"), null, null, "2026.11.1"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home), null, null, "2026.10.9"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Notifications), "agent:work:12345678", isMain: false, "2026.10.9"));
    }

    [Fact]
    public void ConfigurationSearchExcludesSensitiveSubtreesAndNeverIndexesValues()
    {
        using var json = JsonDocument.Parse("""
            {"schema":{"properties":{"heartbeat":{"title":"Heartbeat","type":"string"},
                "secret":{"properties":{"token":{"title":"Private"}}}}},
             "uiHints":{"secret":{"sensitive":true}},
             "value":{"heartbeat":"private-current-value"}}
            """);
        var item = Assert.Single(HubSearchCatalog.Config(json.RootElement, true, out var partial));
        Assert.False(partial);
        Assert.Equal("Heartbeat", item.Title);
        Assert.Equal("heartbeat", item.Target.Focus);
        Assert.DoesNotContain("private-current-value", item.Keywords);
    }

    [Fact]
    public void ScopedSkillsKeepAgentIdentityAndOpenTheDashboardRatherThanARetiredNativePage()
    {
        using var json = JsonDocument.Parse("""{"skills":[{"name":"Weather","skillKey":"weather"}]}""");
        var a = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "alpha", true));
        var b = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "beta", true));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), a.Target);
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), b.Target);
        var fallback = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "alpha", false));
        Assert.Equal(new(HubSearchTargetKind.Dashboard, ""), fallback.Target);
    }

    [Fact]
    public void ReleasedSkillsRouteDoesNotEnableOtherUnverifiedDashboardRoutes()
    {
        const string release = "2026.11.1";
        Assert.False(HubSearchCatalog.SupportsDashboard(release));
        Assert.True(HubSearchCatalog.SupportsSkillsDashboard(release));
        using var json = JsonDocument.Parse("""{"skills":[{"name":"Weather","skillKey":"weather"}],"models":[{"id":"one"}]}""");
        var skill = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "main",
            HubSearchCatalog.SupportsDashboard(release), HubSearchCatalog.SupportsSkillsDashboard(release)));
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), skill.Target);
        Assert.Equal("/skills", skill.DashboardPath);
        Assert.Empty(HubSearchCatalog.ParseCatalog("models.list", json.RootElement, "main",
            HubSearchCatalog.SupportsDashboard(release), HubSearchCatalog.SupportsSkillsDashboard(release)));
        Assert.Null(HubSearchCatalog.Counterpart("config", HubSearchCatalog.SupportsDashboard(release)));
    }

    [Theory]
    [InlineData("2026.10.9", true)]
    [InlineData("2026.11.1", true)]
    [InlineData("2026.11.1-beta.1", false)]
    [InlineData("2026.11.2", false)]
    [InlineData(null, false)]
    public void SkillsRoutingOnlyUsesSpecificallyVerifiedReleases(string? version, bool expected) =>
        Assert.Equal(expected, HubSearchCatalog.SupportsSkillsDashboard(version));

    [Fact]
    public void ModelsWithTheSameIdAcrossProvidersAreDistinctNavigationTargets()
    {
        using var json = JsonDocument.Parse("""{"models":[{"id":"one","provider":"a","name":""},{"id":"one","provider":"b","name":" "}]}""");
        var items = HubSearchCatalog.ParseCatalog("models.list", json.RootElement, "main", true);
        Assert.Equal(2, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.Equal("one", item.Title));
        Assert.All(items, item => Assert.Equal(
            new(HubSearchTargetKind.Dashboard, "/settings/model-providers"), item.Target));
    }

    [Theory]
    [InlineData("2026.10.9", true)]
    [InlineData("2026.10.9-1", true)]
    [InlineData("2026.10.9-beta.1", false)]
    [InlineData("2026.9.1", false)]
    [InlineData(null, false)]
    public void UnverifiedDashboardVersionsDoNotProduceGuessedSpecificRoutes(string? version, bool expected)
    {
        Assert.Equal(expected, HubSearchCatalog.SupportsDashboard(version));
    }
}
