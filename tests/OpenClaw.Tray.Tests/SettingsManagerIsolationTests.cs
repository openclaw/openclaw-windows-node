using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Services;
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

    [Fact]
    public void MissingGatewayUrl_DoesNotExposeLegacyTokensForDefaultUrl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "Token": "leftover-shared-token",
                  "BootstrapToken": "leftover-bootstrap-token",
                  "EnableNodeMode": true
                }
                """);

            var settings = new SettingsManager(dir);

            Assert.Equal("ws://127.0.0.1:18789", settings.GetEffectiveGatewayUrl());
            Assert.False(settings.HasPersistedGatewayUrl);
            Assert.False(settings.HasLegacyGatewayCredentials);
            Assert.Null(settings.LegacyToken);
            Assert.Null(settings.LegacyBootstrapToken);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the assertion.
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ClearedLegacyTokens_DoNotResolveInteractiveAppOrChatCredential()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "Token": "leftover-shared-token",
                  "BootstrapToken": "leftover-bootstrap-token",
                  "EnableNodeMode": true
                }
                """);

            var cleared = new SettingsManager(dir);
            Assert.Null(cleared.LegacyToken);
            Assert.Null(cleared.LegacyBootstrapToken);

            Assert.False(ResolveInteractive(dir, cleared, cleared.LegacyToken, cleared.LegacyBootstrapToken, out var clearedCredential));
            Assert.Null(clearedCredential);
            Assert.Null(ChatUrlFromResolvedCredential(clearedCredential));

            Assert.True(ResolveInteractive(
                dir,
                cleared,
                "leftover-shared-token",
                "leftover-bootstrap-token",
                out var rawCredential));
            Assert.Equal("leftover-shared-token", rawCredential!.Token);
            Assert.False(rawCredential.IsBootstrapToken);
            var rawChatUrl = ChatUrlFromResolvedCredential(rawCredential);
            Assert.NotNull(rawChatUrl);
            Assert.Contains("leftover-shared-token", rawChatUrl, StringComparison.Ordinal);

            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "GatewayUrl": "wss://saved.example.invalid",
                  "Token": "saved-shared-token",
                  "BootstrapToken": "saved-bootstrap-token"
                }
                """);
            var saved = new SettingsManager(dir);
            Assert.True(ResolveInteractive(dir, saved, saved.LegacyToken, saved.LegacyBootstrapToken, out var savedCredential));
            Assert.Equal("saved-shared-token", savedCredential!.Token);
            Assert.Contains(
                "saved-shared-token",
                ChatUrlFromResolvedCredential(savedCredential),
                StringComparison.Ordinal);

            AssertInteractiveCallSitesPassLegacyTokens();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the assertion.
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private static bool ResolveInteractive(
        string settingsDirectory,
        SettingsManager settings,
        string? legacyToken,
        string? legacyBootstrapToken,
        out InteractiveGatewayCredential? credential) =>
        InteractiveGatewayCredentialResolver.TryResolve(
            registry: null,
            settingsDirectory,
            DeviceIdentityFileReader.Instance,
            settings.GetEffectiveGatewayUrl(),
            legacyToken,
            legacyBootstrapToken,
            out credential);

    private static string? ChatUrlFromResolvedCredential(InteractiveGatewayCredential? credential) =>
        credential is { IsBootstrapToken: false }
            ? ChatSurfaceResolver.BuildChatUrl(credential.GatewayUrl, credential.Token)
            : null;

    private static void AssertInteractiveCallSitesPassLegacyTokens()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var chat = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs"));
        var appChat = SliceMethod(app, "bool TryResolveChatCredentials(");
        var chatUrl = SliceMethod(chat, "string? TryComputeChatUrl(");
        var chatWeb = SliceMethod(chat, "Task InitializeWebViewAsync(");

        Assert.Contains("_settings.LegacyToken", appChat, StringComparison.Ordinal);
        Assert.Contains("_settings.LegacyBootstrapToken", appChat, StringComparison.Ordinal);
        Assert.Contains("InteractiveGatewayCredentialResolver.TryResolve", appChat, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyToken", chatUrl, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyBootstrapToken", chatUrl, StringComparison.Ordinal);
        Assert.Contains("ChatSurfaceResolver.BuildChatUrl", chatUrl, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyToken", chatWeb, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyBootstrapToken", chatWeb, StringComparison.Ordinal);
        Assert.Contains("InteractiveGatewayCredentialResolver.TryResolve", chatWeb, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string signatureText)
    {
        var signature = source.IndexOf(signatureText, StringComparison.Ordinal);
        Assert.True(signature >= 0, $"Could not find {signatureText}.");
        var next = source.IndexOf("\n    private ", signature + signatureText.Length, StringComparison.Ordinal);
        return next > signature ? source[signature..next] : source[signature..];
    }
}
