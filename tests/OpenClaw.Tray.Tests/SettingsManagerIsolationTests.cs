using OpenClawTray.Services;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClaw.TestSupport;
using OpenClawTray;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

[CollectionDefinition(OpenClawTrayDataDirEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class OpenClawTrayDataDirEnvironmentCollection
{
    public const string Name = "OpenClawTrayDataDirEnvironment";
}

[Collection(OpenClawTrayDataDirEnvironmentCollection.Name)]
public sealed class SettingsManagerIsolationTests
{
    [Fact]
    public void DefaultSettingsPath_UsesCompiledIdentityAndWindowsRoamingFolder()
    {
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", null)
            .Set("OPENCLAW_APP_IDENTITY", AppIdentity.IsDev ? "release" : "dev");
#if DEV_BUILD
        Assert.Equal("OpenClawTray-Dev", AppIdentity.DataDirectoryName);
#else
        Assert.Equal("OpenClawTray", AppIdentity.DataDirectoryName);
#endif
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppIdentity.DataDirectoryName,
                "settings.json"),
            SettingsManager.SettingsPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SandboxSettings_PersistInSelectedProfileAndFeedRuntimePolicy(bool packagePrivateLayout)
    {
        using var directory = new TempDirectory();
        // Exercise persistence in both layouts, not Windows' package virtualization itself.
        var roaming = packagePrivateLayout
            ? directory.Combine("Packages", "companion-test-family", "LocalCache", "Roaming")
            : directory.Combine("Roaming");
        var profile = Path.Combine(roaming, AppIdentity.DataDirectoryName);
        var otherProfile = Path.Combine(roaming, AppIdentity.IsDev ? "OpenClawTray" : "OpenClawTray-Dev");
        Directory.CreateDirectory(otherProfile);
        var otherSettingsPath = Path.Combine(otherProfile, "settings.json");
        const string otherSettings = """{"SandboxTimeoutMs":145000,"SystemRunAllowOutbound":false}""";
        File.WriteAllText(otherSettingsPath, otherSettings);
        var stateDirectory = directory.Combine("gateway-state");
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", profile)
            .Set("OPENCLAW_STATE_DIR", stateDirectory);
        var grant = directory.Combine("custom-grant");
        Directory.CreateDirectory(grant);

        var settings = new SettingsManager
        {
            SystemRunSandboxEnabled = true,
            SystemRunBlockHostFallbackWhenMxcUnavailable = true,
            SystemRunAllowOutbound = true,
            SystemRunAllowWindowsUi = true,
            SandboxClipboard = SandboxClipboardMode.Both,
            SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly,
            SandboxDownloadsAccess = SandboxFolderAccess.ReadWrite,
            SandboxDesktopAccess = SandboxFolderAccess.ReadOnly,
            SandboxTimeoutMs = 95_000,
            SandboxMaxOutputBytes = 16 * 1024 * 1024,
            SandboxCustomFolders = [new() { Path = grant, Access = SandboxFolderAccess.ReadWrite }]
        };
        settings.SaveOrThrow();

        Assert.Equal(Path.Combine(profile, "settings.json"), SettingsManager.SettingsPath);
        Assert.Equal(profile, settings.SettingsDirectory);
        Assert.True(File.Exists(SettingsManager.SettingsPath));
        var reloaded = new SettingsManager(profile);
        Assert.True(reloaded.SystemRunSandboxEnabled);
        Assert.True(reloaded.SystemRunBlockHostFallbackWhenMxcUnavailable);
        Assert.Equal(SandboxFolderAccess.ReadOnly, reloaded.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.ReadWrite, reloaded.SandboxDownloadsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, reloaded.SandboxDesktopAccess);
        Assert.Equal(16 * 1024 * 1024, reloaded.SandboxMaxOutputBytes);

        var policy = MxcPolicyBuilder.ForSystemRun(reloaded.ToSettingsData(), profile);
        Assert.True(policy.Network!.AllowOutbound);
        Assert.True(policy.Ui!.AllowWindows);
        Assert.Equal(ClipboardPolicy.All, policy.Ui.Clipboard);
        Assert.Equal(95_000, policy.TimeoutMs);
        Assert.Contains(grant, policy.Filesystem!.ReadwritePaths!);
        Assert.Contains(profile, policy.Filesystem.DeniedPaths!);

        reloaded.SystemRunAllowOutbound = false;
        reloaded.SystemRunAllowWindowsUi = false;
        reloaded.SandboxClipboard = SandboxClipboardMode.None;
        reloaded.SandboxCustomFolders.Clear();
        reloaded.SandboxTimeoutMs = 105_000;
        reloaded.SaveOrThrow();
        var restricted = new SettingsManager();
        policy = MxcPolicyBuilder.ForSystemRun(restricted.ToSettingsData(), profile);
        Assert.False(policy.Network!.AllowOutbound);
        Assert.False(policy.Ui!.AllowWindows);
        Assert.Equal(ClipboardPolicy.None, policy.Ui.Clipboard);
        Assert.Empty(restricted.SandboxCustomFolders);
        Assert.DoesNotContain(grant, policy.Filesystem!.ReadwritePaths!);
        Assert.Equal(105_000, policy.TimeoutMs);
        Assert.Equal(otherSettings, File.ReadAllText(otherSettingsPath));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(profile, "exec-approvals.json")));
    }

    [Fact]
    public void OpenClawTrayDataDirRedirectsSettingsAwayFromRealAppData()
    {
        var previousOverride = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        var isolatedDirectory = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        var isolatedSettingsPath = Path.Combine(isolatedDirectory, "settings.json");
        var realSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.DataDirectoryName,
            "settings.json");
        var realSettingsBefore = File.Exists(realSettingsPath)
            ? File.ReadAllText(realSettingsPath)
            : null;
        var marker = $"ws://settings-isolation-{Guid.NewGuid():N}.invalid";

        try
        {
            Directory.CreateDirectory(isolatedDirectory);
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", isolatedDirectory);

            var settings = new SettingsManager
            {
                GatewayUrl = marker
            };
            settings.Save();

            Assert.Equal(isolatedDirectory, SettingsManager.SettingsDirectoryPath);
            Assert.True(File.Exists(isolatedSettingsPath));
            Assert.Contains(marker, File.ReadAllText(isolatedSettingsPath));
            if (realSettingsBefore is not null)
            {
                Assert.Equal(realSettingsBefore, File.ReadAllText(realSettingsPath));
            }
            else
            {
                Assert.False(File.Exists(realSettingsPath));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", previousOverride);
            if (Directory.Exists(isolatedDirectory))
            {
                // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                try { Directory.Delete(isolatedDirectory, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public void LegacyGatewayCredentialsLoadForMigrationButAreNotSaved()
    {
        var previousOverride = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        var isolatedDirectory = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        var isolatedSettingsPath = Path.Combine(isolatedDirectory, "settings.json");

        try
        {
            Directory.CreateDirectory(isolatedDirectory);
            File.WriteAllText(
                isolatedSettingsPath,
                """
                {
                  "GatewayUrl": "ws://legacy.example.invalid",
                  "Token": "legacy-shared-token",
                  "BootstrapToken": "legacy-bootstrap-token",
                  "EnableMcpServer": true
                }
                """);

            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", isolatedDirectory);

            var settings = new SettingsManager();

            Assert.Equal("legacy-shared-token", settings.LegacyToken);
            Assert.Equal("legacy-bootstrap-token", settings.LegacyBootstrapToken);
            Assert.True(settings.HasLegacyGatewayCredentials);

            settings.Save();

            using var saved = JsonDocument.Parse(File.ReadAllText(isolatedSettingsPath));
            Assert.False(saved.RootElement.TryGetProperty("Token", out _));
            Assert.False(saved.RootElement.TryGetProperty("BootstrapToken", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", previousOverride);
            if (Directory.Exists(isolatedDirectory))
            {
                // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                try { Directory.Delete(isolatedDirectory, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public void SaveOrThrow_PropagatesPersistenceFailureWhileSaveRemainsBestEffort()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        var blockedDirectory = Path.Combine(root, "not-a-directory");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(blockedDirectory, "blocks directory creation");
            var settings = new SettingsManager(blockedDirectory);

            Assert.Throws<IOException>(() => settings.SaveOrThrow());
            Assert.Null(Record.Exception(settings.Save));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the assertion.
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
