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
        Assert.Equal("1", start.Environment["OPENCLAW_GATEWAY_FIXTURE"]);
        Assert.Equal("49233", start.Environment["OPENCLAW_MCP_PORT"]);
        Assert.False(start.Environment.ContainsKey("OPENCLAW_ACCESSIBILITY_TEST_CHAT"));
        Assert.False(start.Environment.ContainsKey("OPENCLAW_FORCE_ONBOARDING"));
        Assert.False(start.Environment.ContainsKey("OPENCLAW_TRAY_LOCALAPPDATA_DIR"));
        Assert.False(start.Environment.ContainsKey("OPENCLAW_FUTURE_UNRECOGNIZED_OVERRIDE"));
        Assert.Equal("must-not-use", Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"));
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
