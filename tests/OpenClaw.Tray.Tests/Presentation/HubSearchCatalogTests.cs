using System.Text.Json;
using System.Collections.Immutable;
using System.Xml.Linq;
using OpenClaw.Connection;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubSearchCatalogTests
{
    [Theory]
    [InlineData("config", "/settings/general")]
    [InlineData("connection", "/settings/connection")]
    [InlineData("systems", "/systems")]
    [InlineData("skills", "/skills")]
    [InlineData("cron", "/automations")]
    [InlineData("usage", "/usage")]
    public void SettingsHandoffUsesStableWebRoutes(string tag, string expected)
    {
        Assert.Equal(expected, HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings(tag), null));
    }

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
        var items = HubSearchCatalog.Navigation(commands, connected, key => resources[key]).ToArray();
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
            Assert.Equal("/systems", systems.DashboardPath);
            Assert.Equal("Automations", Assert.Single(items, item => item.Target.Value == "agent:main:cron").Title);
        }
        else
        {
            Assert.DoesNotContain(items, item => item.Target.Value is "config" or "systems" or "agent:main:cron");
            Assert.All(items.Where(item => item.Target.Kind == HubSearchTargetKind.Page),
                item => Assert.Null(item.DashboardPath));
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
    [InlineData("Channels", true)]
    [InlineData("Go to Channels", true)]
    [InlineData("Channels", false)]
    public void ChannelsSearchOpensDashboardInsteadOfRetiredNativePage(
        string query, bool connected)
    {
        var resources = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToImmutableDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value);
        var commands = HubPageRegistry.BuildCommands(new("main", false, null, [], resources));
        var items = HubSearchCatalog.Navigation(commands, connected, key => resources[key]);
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
        Assert.Throws<InvalidDataException>(() => HubSearchCatalog.Config(json.RootElement, out _));
    }

    [Theory]
    [InlineData("agent:main:main", "/chat?session=agent%3Amain%3Amain")]
    [InlineData("agent:work:global", "/chat?session=agent%3Awork%3Aglobal")]
    [InlineData("agent:work:12345678", "/chat?session=agent%3Awork%3A12345678")]
    [InlineData("agent:work:dm:user.one", "/chat?session=agent%3Awork%3Adm%3Auser.one")]
    [InlineData("agent:work:~literal", "/chat?session=agent%3Awork%3A~literal")]
    [InlineData("agent:work:.", "/chat?session=agent%3Awork%3A.")]
    [InlineData("agent:work:..", "/chat?session=agent%3Awork%3A..")]
    [InlineData("agent:work:hello world", "/chat?session=agent%3Awork%3Ahello%20world")]
    [InlineData("agent:work:caf\u00e9", "/chat?session=agent%3Awork%3Acaf%C3%A9")]
    [InlineData("global", "/chat?session=global")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DashboardSessionLinksPreserveExactKeys(string? key, string? expected)
    {
        Assert.Equal(expected, HubSearchCatalog.SessionPath(key));
    }

    [Fact]
    public void DashboardMainSessionUsesAuthoritativeIdentityRatherThanAssumingTheMainSuffix()
    {
        Assert.Equal("/chat?session=agent%3Awork%3Acustom-home", HubSearchCatalog.SessionPath("agent:work:custom-home"));
        Assert.Equal("/chat?session=agent%3Awork%3Amain", HubSearchCatalog.SessionPath("agent:work:main"));
    }

    [Fact]
    public void ContextualDashboardPath_UsesCurrentChatSessionWhenHomeHistoryHasNoSession()
    {
        Assert.Equal("/chat?session=agent%3Awork%3A12345678", HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home), "agent:work:12345678"));
    }

    [Theory]
    [InlineData("agent:work:dashboard:12345678-1234-1234-1234-123456789abc",
        "/chat?session=agent%3Awork%3Adashboard%3A12345678-1234-1234-1234-123456789abc")]
    [InlineData("agent:work:main", "/chat?session=agent%3Awork%3Amain")]
    [InlineData("agent:work:custom-home", "/chat?session=agent%3Awork%3Acustom-home")]
    [InlineData("global", "/chat?session=global")]
    [InlineData("agent:work:a b/c&d#e?f+%",
        "/chat?session=agent%3Awork%3Aa%20b%2Fc%26d%23e%3Ff%2B%25")]
    public async Task ContextualDashboardPath_ReleasedChatReachesBrowserWithExactSession(
        string sessionKey, string expectedPath)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new("wss://gateway.example/control/", "shared secret", false,
                CredentialResolver.SourceSharedGatewayToken),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));
        var path = HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home, sessionKey), "agent:main:main");

        Assert.True(await launcher.OpenAsync(path));
        Assert.Equal($"https://gateway.example/control{expectedPath}#token=shared%20secret", launched);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ContextualDashboardPath_MissingChatSelectionStillOpensHome(string? sessionKey)
    {
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home), sessionKey));
    }

    [Fact]
    public void ContextualDashboardPath_PrefersVisibleSettingsOverRetainedChat()
    {
        Assert.Equal("/usage", HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("usage"), "agent:work:12345678"));
    }

    [Fact]
    public void ContextualDashboardPath_UsesExplicitSessionAndFallsBackWithoutVerifiedCounterpart()
    {
        Assert.Equal("/chat?session=agent%3Awork%3Amain", HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home, "agent:work:main"), "agent:main:main"));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("local-ai"), null));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            WorkspaceDestination.Settings("agent:research"), null));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Home), null));
        Assert.Null(HubSearchCatalog.ContextualDashboardPath(
            new(WorkspacePageId.Notifications), "agent:work:12345678"));
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
        var item = Assert.Single(HubSearchCatalog.Config(json.RootElement, out var partial));
        Assert.False(partial);
        Assert.Equal("Heartbeat", item.Title);
        Assert.Equal("heartbeat", item.Target.Focus);
        Assert.DoesNotContain("private-current-value", item.Keywords);
    }

    [Fact]
    public void ScopedSkillsKeepAgentIdentityAndOpenTheDashboardRatherThanARetiredNativePage()
    {
        using var json = JsonDocument.Parse("""{"skills":[{"name":"Weather","skillKey":"weather"}]}""");
        var a = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "alpha"));
        var b = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "beta"));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), a.Target);
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), b.Target);
    }

    [Fact]
    public void SuccessfulCatalogReadsOfferTheirWebDestinations()
    {
        using var json = JsonDocument.Parse("""{"skills":[{"name":"Weather","skillKey":"weather"}],"models":[{"id":"one"}]}""");
        var skill = Assert.Single(HubSearchCatalog.ParseCatalog("skills.status", json.RootElement, "main"));
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/skills"), skill.Target);
        Assert.Equal("/skills", skill.DashboardPath);
        var model = Assert.Single(HubSearchCatalog.ParseCatalog("models.list", json.RootElement, "main"));
        Assert.Equal(new(HubSearchTargetKind.Dashboard, "/settings/model-providers"), model.Target);
        Assert.Equal("/settings/model-providers", model.DashboardPath);
    }

    [Fact]
    public void ModelsWithTheSameIdAcrossProvidersAreDistinctNavigationTargets()
    {
        using var json = JsonDocument.Parse("""{"models":[{"id":"one","provider":"a","name":""},{"id":"one","provider":"b","name":" "}]}""");
        var items = HubSearchCatalog.ParseCatalog("models.list", json.RootElement, "main");
        Assert.Equal(2, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.Equal("one", item.Title));
        Assert.All(items, item => Assert.Equal(
            new(HubSearchTargetKind.Dashboard, "/settings/model-providers"), item.Target));
    }

}
