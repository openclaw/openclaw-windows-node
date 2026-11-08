using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Shared;

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
        Assert.False(profile.NodeBrowserProxyEnabled);
        Assert.False(File.Exists(Path.Combine(profile.DataDirectory, "device-key-ed25519.json")));
        Assert.Empty(Directory.EnumerateFiles(profile.SetupDirectory));
    }

    [Fact]
    public void McpOnlyProfileDoesNotSupplyGatewayCredentialsOrNodeExecution()
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "unused-fixture-token", connectGateway: false);
        var registry = new GatewayRegistry(profile.DataDirectory);
        registry.Load();
        var record = Assert.Single(registry.GetAll());
        Assert.False(profile.ConnectGateway);
        Assert.False(profile.NodeBrowserProxyEnabled);
        Assert.Null(record.SharedGatewayToken);
        Assert.Null(record.BootstrapToken);
        Assert.False(File.Exists(Path.Combine(profile.DataDirectory, "gateways", record.Id, "device-key-ed25519.json")));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.True(settings.RootElement.GetProperty("EnableMcpServer").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("EnableManagedLocalGatewayAutoRepair").GetBoolean());
    }

    [Fact]
    public void BrowserProxyCannotUseAnMcpOnlyProfile()
    {
        Assert.Throws<ArgumentException>(() =>
            new GatewayFixtureProfile(Endpoint, "fixture-token", enableNodeBrowserProxy: true, connectGateway: false));
    }

    [Fact]
    public void BrowserProxyProfileKeepsExecutionCapabilitiesOff()
    {
        using var profile = new GatewayFixtureProfile(Endpoint, "only-this-fixture", enableNodeBrowserProxy: true);
        Assert.True(profile.NodeBrowserProxyEnabled);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.DataDirectory, "settings.json")));
        Assert.True(settings.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(settings.RootElement.GetProperty("EnableMcpServer").GetBoolean());
        Assert.True(settings.RootElement.GetProperty("NodeBrowserProxyEnabled").GetBoolean());
        foreach (var disabled in new[]
        {
            "NodeSystemRunEnabled", "NodeScreenEnabled", "NodeCameraEnabled", "NodeLocationEnabled", "NodeCanvasEnabled"
        })
            Assert.False(settings.RootElement.GetProperty(disabled).GetBoolean(), disabled);
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
    public void CleanupAcceptsAnAlreadyRemovedOwnedProfileRoot()
    {
        var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        var root = profile.RunDirectory;
        Directory.Delete(root, recursive: true);
        profile.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CleanupWaitsForTransientLogSharingLock()
    {
        var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        var root = profile.RunDirectory;
        using var environment = new EnvironmentScope("OPENCLAW_GATEWAY_FIXTURE_CLEANUP_TIMEOUT_SECONDS", "5");
        using var log = new FileStream(Path.Combine(profile.DataDirectory, "openclaw-tray.log"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(() =>
        {
            started.SetResult();
            profile.Dispose();
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => cleanup.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.True(Directory.Exists(root));
            log.Dispose();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            log.Dispose();
            try { await cleanup; }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CleanupSharingLockDeadlineIsVisibleAndCleanupCanResumeAfterRelease()
    {
        var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        var root = profile.RunDirectory;
        using var environment = new EnvironmentScope("OPENCLAW_GATEWAY_FIXTURE_CLEANUP_TIMEOUT_SECONDS", "0.1");
        using var log = new FileStream(Path.Combine(profile.DataDirectory, "openclaw-tray.log"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var error = Assert.Throws<TimeoutException>(profile.Dispose);
            Assert.IsType<IOException>(error.InnerException);
            Assert.Contains(root, error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(profile.DataDirectory, "openclaw-tray.log")));
            log.Dispose();
            profile.Dispose();
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            log.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
        Assert.Equal(Path.Combine(profile.RunDirectory, "local"), start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"]);
        Assert.Equal(profile.SetupDirectory, GatewayFixtureIsolation.Get(
            name => start.Environment.TryGetValue(name, out var value) ? value : null).LocalDataDirectory);
        Assert.False(start.Environment.ContainsKey("OPENCLAW_FUTURE_UNRECOGNIZED_OVERRIDE"));
        Assert.Equal("must-not-use", Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"));
        Assert.Equal("must-not-use", Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"));
        Assert.Empty(start.ArgumentList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupHandoffIsExplicitAndPassedAsOneOpaqueArgument(bool restart)
    {
        using var app = CreateFakeSupportedApp();
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        var start = profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), 49233,
            "native:synthetic-handoff", postSetupRestart: restart);
        Assert.Equal(restart
            ? new[] { "--post-setup-restart", "--post-setup-launch", "native:synthetic-handoff" }
            : new[] { "--post-setup-launch", "native:synthetic-handoff" }, start.ArgumentList);
        Assert.Equal(profile.LocalRoot, start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"]);
    }

    [Fact]
    public void SetupRestartRequiresAnExplicitHandoff()
    {
        using var app = CreateFakeSupportedApp();
        using var profile = new GatewayFixtureProfile(Endpoint, "fixture-token");
        Assert.Throws<ArgumentException>(() =>
            profile.CreateStartInfo(app.Combine("OpenClaw.Tray.WinUI.exe"), 49233, postSetupRestart: true));
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
