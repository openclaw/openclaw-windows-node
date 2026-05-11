using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class SettingsManagerIsolationTests
{
    [Fact]
    public void OpenClawTrayDataDirRedirectsSettingsAwayFromRealAppData()
    {
        var previousOverride = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        var isolatedDirectory = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        var isolatedSettingsPath = Path.Combine(isolatedDirectory, "settings.json");
        var realSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray",
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
                try { Directory.Delete(isolatedDirectory, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
