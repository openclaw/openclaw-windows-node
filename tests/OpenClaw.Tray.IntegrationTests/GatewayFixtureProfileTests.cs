using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.IntegrationTests;

[CollectionDefinition("Gateway fixture environment", DisableParallelization = true)]
public sealed class GatewayFixtureEnvironmentCollection { }

[Collection("Gateway fixture environment")]
public sealed class GatewayFixtureProfileTests
{
    private static readonly Uri Endpoint = new("ws://127.0.0.1:49231/");

    [Theory]
    [InlineData("ws://example.org:49231/")]
    [InlineData("ws://localhost:49231/")]
    [InlineData("wss://127.0.0.1:49231/")]
    [InlineData("ws://127.0.0.1:18789/")]
    [InlineData("ws://127.0.0.1:8765/")]
    [InlineData("ws://secret@127.0.0.1:49231/")]
    [InlineData("ws://127.0.0.1:49231/?token=secret")]
    [InlineData("relative")]
    public void EndpointMustBeDedicatedNumericLoopback(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            new GatewayFixtureProfile(new Uri(endpoint, UriKind.RelativeOrAbsolute), "fixture-token"));
    }

    [Fact]
    public void ProfileUsesRealRegistryFormatAndSyntheticSafeSettings()
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "only-this-fixture");
        var registry = new GatewayRegistry(profile.DataDirectory);
        registry.Load();
        var record = Assert.Single(registry.GetAll());
        Assert.Equal(record.Id, registry.ActiveGatewayId);
        Assert.Equal(profile.GatewayId, record.Id);
        Assert.Equal(Endpoint.AbsoluteUri, record.Url);
        Assert.Equal("only-this-fixture", record.SharedGatewayToken);
        Assert.Null(record.SetupManagedDistroName);
        Assert.False(record.IsLocal);
        Assert.Null(record.SshTunnel);
        Assert.Null(record.BootstrapToken);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.True(settings.RootElement.GetProperty("EnableMcpServer").GetBoolean());
        foreach (var disabled in new[]
        {
            "EnableNodeMode", "AutoStart", "GlobalHotkeyEnabled", "NodeSystemRunEnabled",
            "NodeBrowserProxyEnabled", "NodeScreenEnabled", "NodeCameraEnabled",
            "NodeLocationEnabled", "NodeCanvasEnabled", "EnableManagedLocalGatewayAutoRepair",
            "NodeOllamaInferenceEnabled", "VoiceTtsEnabled", "UseLegacyWebChat"
        })
            Assert.False(settings.RootElement.GetProperty(disabled).GetBoolean(), disabled);
        Assert.False(File.Exists(Path.Combine(profile.DataDirectory, "device-key-ed25519.json")));
        Assert.Empty(Directory.EnumerateFiles(profile.SetupDirectory));
    }

    [Fact]
    public void SecondaryPeerIsSavedInactiveWithIndependentDestinationAndCredential()
    {
        var secondary = new GatewayFixtureSecondaryPeer(new Uri("ws://127.0.0.1:49232/"),
            "secondary-fixture-token", "Fixture bravo");
        using var profile = new GatewayFixtureProfile(Endpoint, "primary-fixture-token", secondaryPeer: secondary);
        var registry = new GatewayRegistry(profile.DataDirectory);
        registry.Load();
        Assert.Equal(2, registry.GetAll().Count);
        Assert.Equal(profile.GatewayId, registry.ActiveGatewayId);
        var primary = Assert.Single(registry.GetAll(), record => record.Id == profile.GatewayId);
        var saved = Assert.Single(registry.GetAll(), record => record.Id == profile.SecondaryGatewayId);
        Assert.NotEqual(primary.Id, saved.Id);
        Assert.Equal(Endpoint.AbsoluteUri, primary.Url);
        Assert.Equal("primary-fixture-token", primary.SharedGatewayToken);
        Assert.Equal(secondary.Endpoint.AbsoluteUri, saved.Url);
        Assert.Equal(secondary.Token, saved.SharedGatewayToken);
        Assert.Equal(secondary.FriendlyName, saved.FriendlyName);
        Assert.False(saved.IsLocal);
        Assert.Null(saved.SetupManagedDistroName);
        Assert.Null(saved.BootstrapToken);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.Equal(Endpoint.AbsoluteUri, settings.RootElement.GetProperty("GatewayUrl").GetString());
        Assert.False(settings.RootElement.GetProperty("EnableNodeMode").GetBoolean());
    }

    [Theory]
    [InlineData("ws://127.0.0.1:49231/", "secondary-fixture-token")]
    [InlineData("ws://127.0.0.1:49232/", "primary-fixture-token")]
    public void SecondaryPeerCannotShareThePrimaryEndpointOrCredential(string endpoint, string token)
    {
        var secondary = new GatewayFixtureSecondaryPeer(new Uri(endpoint), token, "Fixture bravo");
        Assert.Throws<ArgumentException>(() =>
            new GatewayFixtureProfile(Endpoint, "primary-fixture-token", secondaryPeer: secondary));
    }

    [Fact]
    public void McpOnlyProfileCannotAcquireAnInactiveSavedGateway()
    {
        var secondary = new GatewayFixtureSecondaryPeer(new Uri("ws://127.0.0.1:49232/"),
            "secondary-fixture-token", "Fixture bravo");
        Assert.Throws<ArgumentException>(() => new GatewayFixtureProfile(Endpoint, "primary-fixture-token",
            startupMode: GatewayFixtureStartupMode.McpOnly, secondaryPeer: secondary));
    }

    [Fact]
    public void McpPortCannotOverlapTheSecondaryPeer()
    {
        using var app = CreateFakeSupportedApp();
        var secondary = new GatewayFixtureSecondaryPeer(new Uri("ws://127.0.0.1:49232/"),
            "secondary-fixture-token", "Fixture bravo");
        using var profile = new GatewayFixtureProfile(Endpoint, "primary-fixture-token", secondaryPeer: secondary);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), secondary.Endpoint.Port));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreferenceSeedsCannotEnableHostEffects(bool showToolCalls)
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token", showToolCalls);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.Equal(showToolCalls, settings.RootElement.GetProperty("ShowChatToolCalls").GetBoolean());
        foreach (var name in new[] { "EnableNodeMode", "AutoStart", "NodeSystemRunEnabled", "GlobalHotkeyEnabled" })
            Assert.False(settings.RootElement.GetProperty(name).GetBoolean(), name);
    }

    [Fact]
    public void McpOnlyProfileHasNoGatewayTargetOrStoredGatewayCredential()
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "synthetic-unused", startupMode: GatewayFixtureStartupMode.McpOnly);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.Equal("", settings.RootElement.GetProperty("GatewayUrl").GetString());
        Assert.True(settings.RootElement.GetProperty("EnableMcpServer").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(File.Exists(Path.Combine(profile.DataDirectory, "gateways.json")));
        Assert.False(Directory.Exists(Path.Combine(profile.DataDirectory, "gateways")));
    }

    [Fact]
    public void UnavailableGatewayProfileRetainsItsConfiguredDestination()
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "synthetic-unused",
            startupMode: GatewayFixtureStartupMode.GatewayUnavailable);
        var registry = new GatewayRegistry(profile.DataDirectory);
        registry.Load();
        Assert.Equal(Endpoint.AbsoluteUri, Assert.Single(registry.GetAll()).Url);
        Assert.Equal(profile.GatewayId, registry.ActiveGatewayId);
    }

    [Fact]
    public void UnsupportedStartupModeIsRejectedBeforeCreatingAProfile() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayFixtureProfile(Endpoint, "synthetic", startupMode: (GatewayFixtureStartupMode)999));

    [Fact]
    public void TwoProfilesNeverShareIdentityOrStateAndCleanupIsOwned()
    {
        using var sentinel = new TempDirectory();
        var sentinelFile = sentinel.Combine("installed-pairing.json");
        File.WriteAllText(sentinelFile, "synthetic sentinel, never a real pairing");
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", sentinel.Path);
        using var second = new GatewayFixtureProfile(new Uri("ws://127.0.0.1:49232/"), "second-token");
        var first = new GatewayFixtureProfile(Endpoint, "first-token");
        Assert.NotEqual(first.RunDirectory, second.RunDirectory);
        Assert.NotEqual(first.GatewayId, second.GatewayId);
        Assert.NotEqual(first.DataDirectory, sentinel.Path);
        var firstRoot = first.RunDirectory;
        first.Dispose();
        first.Dispose();
        Assert.False(Directory.Exists(firstRoot));
        Assert.True(Directory.Exists(second.DataDirectory));
        Assert.Equal("synthetic sentinel, never a real pairing", File.ReadAllText(sentinelFile));
    }

    [Fact]
    public void ChildEnvironmentClearsInheritedOverridesWithoutChangingParent()
    {
        using var app = CreateFakeSupportedApp();
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", "must-not-use")
            .Set("OPENCLAW_TRAY_LOCALAPPDATA_DIR", "must-not-use")
            .Set("OPENCLAW_ACCESSIBILITY_TEST_CHAT", "1")
            .Set("OPENCLAW_FORCE_ONBOARDING", "1")
            .Set("OPENCLAW_FUTURE_UNRECOGNIZED_OVERRIDE", "must-not-use");

        var start = profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), 49233);
        Assert.False(start.UseShellExecute);
        Assert.Equal(profile.DataDirectory, start.Environment["OPENCLAW_TRAY_DATA_DIR"]);
        Assert.Equal(profile.SetupDirectory, start.Environment["OPENCLAW_TRAY_LOCAL_DATA_DIR"]);
        Assert.Equal(profile.RoamingRoot, start.Environment["OPENCLAW_TRAY_APPDATA_DIR"]);
        Assert.Equal(profile.LocalRoot, start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"]);
        Assert.Equal(4, new[] { profile.DataDirectory, profile.SetupDirectory, profile.RoamingRoot, profile.LocalRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(Directory.Exists(profile.LocalRoot));
        Assert.True(Directory.Exists(profile.RoamingRoot));
        Assert.Equal("1", start.Environment["OPENCLAW_GATEWAY_FIXTURE"]);
        Assert.Equal("49233", start.Environment["OPENCLAW_MCP_PORT"]);
        Assert.False(start.Environment.ContainsKey("OPENCLAW_ACCESSIBILITY_TEST_CHAT"));
        Assert.False(start.Environment.ContainsKey("OPENCLAW_FORCE_ONBOARDING"));
        Assert.False(start.Environment.ContainsKey("OPENCLAW_FUTURE_UNRECOGNIZED_OVERRIDE"));
        Assert.Equal("must-not-use", Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"));
        Assert.Equal("must-not-use", Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"));
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void SetupHandoffIsExplicitAndPassedAsOneOpaqueArgument()
    {
        using var app = CreateFakeSupportedApp();
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        var start = profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), 49233, "native:synthetic-handoff");
        Assert.Equal(new[] { "--post-setup-launch", "native:synthetic-handoff" }, start.ArgumentList);
        Assert.Equal(profile.LocalRoot, start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(8765)]
    [InlineData(18789)]
    [InlineData(49231)]
    public void McpPortCannotBeInvalidDefaultOrGatewayPort(int port)
    {
        using var app = CreateFakeSupportedApp();
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), port));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runtimeOptions":{"configProperties":{}}}""")]
    [InlineData("""{"runtimeOptions":{"configProperties":{"OpenClaw.GatewayFixtureIsolationVersion":0}}}""")]
    public void OlderAppIsRejectedBeforeProcessLaunch(string runtimeConfig)
    {
        using var app = CreateFakeSupportedApp();
        File.WriteAllText(app.Combine("OpenClaw.Tray.WinUI.runtimeconfig.json"), runtimeConfig);
        Assert.Throws<InvalidDataException>(() =>
            GatewayFixtureProfile.ValidateApp(app.Combine("OpenClaw.Tray.WinUI.exe")));
    }

    [Fact]
    public void MissingAppNeverFallsBackToInstalledExecutable()
    {
        using var directory = new TempDirectory();
        Assert.Throws<FileNotFoundException>(() =>
            GatewayFixtureProfile.ValidateApp(directory.Combine("OpenClaw.Tray.WinUI.exe")));
    }

    private static TempDirectory CreateFakeSupportedApp()
    {
        var directory = new TempDirectory();
        File.WriteAllText(directory.Combine("OpenClaw.Tray.WinUI.exe"), "test metadata only; never executable");
        File.WriteAllText(directory.Combine("OpenClaw.Tray.WinUI.runtimeconfig.json"),
            """{"runtimeOptions":{"configProperties":{"OpenClaw.GatewayFixtureIsolationVersion":1}}}""");
        return directory;
    }
}
