// Proves plugin and imported Gateway themes reach the Companion's theme catalog through the real
// authenticated fixture socket, and that plugins.changed and users.prefs.changed re-read it.
// Run with dotnet test --filter FullyQualifiedName~GatewayThemeCatalogWireTests.
using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Services;
using OpenClawTray.Theming;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class GatewayThemeCatalogWireTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task PluginAndImportedThemesArriveAndPushedEventsRefreshTheCatalog()
    {
        var scenario = GatewayScenario.CreateThemes();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var identity = new TempDirectory("theme-wire-");
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: identity.Path, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        await ConnectAsync(client);
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());
        try
        {
            catalog.SetConnection(client, "fixture-gw");
            await WaitForAsync(catalog, () => catalog.Options.Any(option => option.Id == ThemesFixture.UserThemeId));

            var neon = Assert.Single(catalog.Options, option => option.Id == ThemesFixture.NeonThemeId);
            Assert.Equal(GatewayThemeSource.Plugin, neon.Descriptor.Source);
            Assert.Equal("fixture-gw", neon.GatewayId);
            Assert.NotNull(neon.Definition!.Dark);
            Assert.Null(neon.Definition.Light);
            var paper = Assert.Single(catalog.Options, option => option.Id == ThemesFixture.PaperThemeId);
            Assert.NotNull(paper.Definition!.Light);
            Assert.NotNull(paper.Definition.Dark);
            var sunset = Assert.Single(catalog.Options, option => option.Id == ThemesFixture.UserThemeId);
            Assert.Equal(GatewayThemeSource.User, sunset.Descriptor.Source);
            Assert.Null(sunset.Definition!.Dark);
            Assert.DoesNotContain(catalog.Options, option => option.Id == ThemesFixture.RevealedThemeId);

            scenario.Themes!.RevealPluginTheme();
            await server.PublishPluginsChangedAsync();
            await WaitForAsync(catalog, () => catalog.Options.Any(option => option.Id == ThemesFixture.RevealedThemeId));
            Assert.Equal(BuiltinGatewayThemes.All.Length + 4, catalog.Options.Count);

            var reads = server.Requests.Count(request => request.Method == GatewayThemeProtocol.ListMethod);
            await server.PublishUserPrefsChangedAsync();
            await server.WaitForCompletedRequestAsync(GatewayThemeProtocol.ListMethod, occurrence: reads + 1).WaitAsync(Deadline);

            Assert.DoesNotContain(server.Requests, request => request.Method is "themes.set" or "themes.import");
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            catalog.SetConnection(null, null);
            await client.DisconnectAsync();
        }
    }

    // A dashboard theme change reaches a following Companion through users.prefs.changed: the catalog re-reads
    // themes.list and the follower records the new selection, while a theme it can't render is not followed.
    [Fact]
    public async Task FollowingCompanionTracksTheGatewaySelectionPushedByPrefsEvents()
    {
        var scenario = GatewayScenario.CreateThemes();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var identity = new TempDirectory("theme-follow-");
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: identity.Path, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        await ConnectAsync(client);
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());
        var appearance = new AppearanceSettings { Theme = new ThemeSelection { Id = "tide" }, FollowGatewayTheme = true };
        var applied = 0;
        using var follower = new GatewayThemeFollower(catalog, () => appearance, next => appearance = next, () => applied++);
        try
        {
            catalog.SetConnection(client, "fixture-gw");
            await WaitForAsync(catalog, () => appearance.FollowedTheme?.Id == "claw");
            Assert.Equal("claw", AppearanceResolver.ResolveTheme(appearance)?.Id);

            scenario.Themes!.SelectTheme(ThemesFixture.NeonThemeId);
            await server.PublishUserPrefsChangedAsync();
            await WaitForAsync(catalog, () => appearance.FollowedTheme?.Id == ThemesFixture.NeonThemeId);
            var neon = AppearanceResolver.ResolveTheme(appearance);
            Assert.Equal(GatewayThemeSource.Plugin, neon?.Source);
            Assert.Equal("fixture-gw", appearance.FollowedTheme!.OriginGatewayId);
            Assert.Equal(2, applied);

            scenario.Themes.SelectTheme("nebula");
            await server.PublishUserPrefsChangedAsync();
            await WaitForAsync(catalog, () => catalog.GatewayCurrent?.Id == "nebula");
            Assert.Equal(ThemesFixture.NeonThemeId, AppearanceResolver.ResolveTheme(appearance)?.Id);
            Assert.Equal(2, applied);

            Assert.DoesNotContain(server.Requests, request => request.Method is "themes.set" or "themes.import" or "users.prefs.set");
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            catalog.SetConnection(null, null);
            await client.DisconnectAsync();
        }
    }

    private static async Task ConnectAsync(OpenClawGatewayClient client)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHandshake(object? sender, EventArgs args) => ready.TrySetResult();
        client.HandshakeSucceeded += OnHandshake;
        try
        {
            await client.ConnectAsync();
            await ready.Task.WaitAsync(Deadline);
        }
        finally { client.HandshakeSucceeded -= OnHandshake; }
    }

    private static async Task WaitForAsync(GatewayThemeCatalog catalog, Func<bool> predicate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs args)
        {
            if (predicate()) ready.TrySetResult();
        }
        catalog.Changed += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await ready.Task.WaitAsync(Deadline);
        }
        finally { catalog.Changed -= Check; }
    }
}
