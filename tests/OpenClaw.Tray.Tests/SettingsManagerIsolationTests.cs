using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Services;

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
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder",
                  "EnableMcpServer": true
                }
                """);

            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", isolatedDirectory);

            var settings = new SettingsManager();

            Assert.Equal("test-auth-token", settings.LegacyToken);
            Assert.Equal("test-token-placeholder", settings.LegacyBootstrapToken);
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
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder",
                  "EnableNodeMode": true
                }
                """);

            var settings = new SettingsManager(dir);

            Assert.Equal("ws://127.0.0.1:18789", settings.GetEffectiveGatewayUrl());
            Assert.False(settings.HasPersistedGatewayUrl);
            Assert.False(settings.HasLegacyGatewayCredentials);
            Assert.Null(settings.LegacyToken);
            Assert.Null(settings.LegacyBootstrapToken);
            Assert.Null(settings.GetLegacyCredentialGatewayUrlOrNull());
            settings.SaveOrThrow();

            var reloaded = new SettingsManager(dir);
            Assert.Null(reloaded.GetLegacyCredentialGatewayUrlOrNull());
            Assert.False(reloaded.HasPersistedGatewayUrl);
            var savedJson = File.ReadAllText(Path.Combine(dir, "settings.json"));
            using var savedDocument = JsonDocument.Parse(savedJson);
            Assert.False(savedDocument.RootElement.TryGetProperty("GatewayUrl", out _));
            Assert.False(savedDocument.RootElement.TryGetProperty("Token", out _));
            Assert.False(savedDocument.RootElement.TryGetProperty("BootstrapToken", out _));
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
    public void FailedSave_DoesNotAdmitSetupGatewayForLegacyRootIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder",
                  "EnableNodeMode": true
                }
                """);
            var identity = new DeviceIdentity(dir);
            identity.Initialize();
            identity.StoreDeviceTokenForRole("operator", "operator-role-token");

            var settings = new SettingsManager(dir);
            Assert.False(settings.HasPersistedGatewayUrl);
            Assert.Null(settings.PersistedGatewayUrl);
            var other = new SettingsManager(dir) { NotificationSound = "external" };
            other.SaveOrThrow();

            Assert.Throws<SettingsPersistenceConflictException>(() =>
                settings.UpdateAndSave(() => settings.GatewayUrl = settings.GatewayUrl));

            Assert.False(settings.HasPersistedGatewayUrl);
            Assert.Null(settings.PersistedGatewayUrl);
            Assert.Null(settings.GetLegacyCredentialGatewayUrlOrNull());
            Assert.Equal(OpenClawTray.AppIdentity.SetupGatewayUrl, settings.GetEffectiveGatewayUrl());
            Assert.False(ResolveInteractive(dir, settings, null, null, out var credential));
            Assert.Null(credential);
            Assert.False(ResolveInteractive(
                dir,
                settings,
                "test-auth-token",
                "test-token-placeholder",
                out var rawCredential));
            Assert.Null(rawCredential);
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
    public void FailedSave_KeepsAnExplicitGatewayTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var settings = new SettingsManager(dir)
            {
                GatewayUrl = "wss://saved.example.invalid",
            };
            settings.SaveOrThrow();
            var other = new SettingsManager(dir) { NotificationSound = "external" };
            other.SaveOrThrow();

            Assert.Throws<SettingsPersistenceConflictException>(() =>
                settings.UpdateAndSave(() => settings.GatewayUrl = "wss://attempt.example.invalid"));

            Assert.True(settings.HasPersistedGatewayUrl);
            Assert.Equal("wss://saved.example.invalid", settings.PersistedGatewayUrl);
            Assert.Equal(
                "wss://saved.example.invalid",
                settings.GetLegacyCredentialGatewayUrlOrNull());
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
    public void ExplicitGatewayUrl_BecomesPersistedLegacyCredentialTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var settings = new SettingsManager(dir)
            {
                GatewayUrl = "wss://gateway.example.test",
            };
            settings.SaveOrThrow();

            var reloaded = new SettingsManager(dir);

            Assert.True(reloaded.HasPersistedGatewayUrl);
            Assert.Equal(
                "wss://gateway.example.test",
                reloaded.GetLegacyCredentialGatewayUrlOrNull());
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
    public void PersistedSshGatewayUrl_UsesEffectiveTunnelEndpointForLegacyCredentialMigration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "GatewayUrl": "wss://gateway.example.test",
                  "UseSshTunnel": true,
                  "SshTunnelLocalPort": 19876
                }
                """);

            var settings = new SettingsManager(dir);

            Assert.True(settings.HasPersistedGatewayUrl);
            Assert.Equal(
                "ws://127.0.0.1:19876",
                settings.GetLegacyCredentialGatewayUrlOrNull());
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
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder",
                  "EnableNodeMode": true
                }
                """);

            var cleared = new SettingsManager(dir);
            Assert.Null(cleared.LegacyToken);
            Assert.Null(cleared.LegacyBootstrapToken);

            Assert.False(ResolveInteractive(dir, cleared, cleared.LegacyToken, cleared.LegacyBootstrapToken, out var clearedCredential));
            Assert.Null(clearedCredential);
            Assert.Null(ChatUrlFromResolvedCredential(clearedCredential));

            Assert.False(ResolveInteractive(
                dir,
                cleared,
                "test-auth-token",
                "test-token-placeholder",
                out var rawCredential));
            Assert.Null(rawCredential);
            Assert.Null(ChatUrlFromResolvedCredential(rawCredential));

            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """
                {
                  "GatewayUrl": "wss://saved.example.invalid",
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder"
                }
                """);
            var saved = new SettingsManager(dir);
            Assert.Equal(
                "wss://saved.example.invalid",
                saved.GetLegacyCredentialGatewayUrlOrNull());
            Assert.True(ResolveInteractive(dir, saved, saved.LegacyToken, saved.LegacyBootstrapToken, out var savedCredential));
            Assert.Equal("test-auth-token", savedCredential!.Token);
            Assert.Contains(
                "test-auth-token",
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

    [Fact]
    public async Task ExplicitSavedGateway_ReceivesCredentialOnItsSocket_RollbackDoesNotOpenSetupPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestHead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accept = AcceptOneHttpRequestAsync(listener, requestHead);
        var savedDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        var urlLessDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(savedDir);
            var gatewayUrl = $"ws://127.0.0.1:{port}";
            File.WriteAllText(
                Path.Combine(savedDir, "settings.json"),
                $$"""
                {
                  "GatewayUrl": "{{gatewayUrl}}",
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder"
                }
                """);
            var saved = new SettingsManager(savedDir);
            Assert.Equal(gatewayUrl, saved.GetLegacyCredentialGatewayUrlOrNull());
            Assert.True(ResolveInteractive(
                savedDir,
                saved,
                saved.LegacyToken,
                saved.LegacyBootstrapToken,
                out var credential));
            var chatUrl = ChatUrlFromResolvedCredential(credential);
            Assert.NotNull(chatUrl);
            Assert.Contains($":{port}/chat?token=", chatUrl, StringComparison.Ordinal);
            Assert.DoesNotContain(
                $":{OpenClawTray.AppIdentity.SetupGatewayPort}/",
                chatUrl,
                StringComparison.Ordinal);

            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(chatUrl);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var head = await requestHead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var requestLine = head.Split('\r', '\n')[0];
            Assert.StartsWith("GET /chat?token=", requestLine, StringComparison.Ordinal);
            var tokenAt = requestLine.IndexOf("token=", StringComparison.Ordinal);
            var tokenValue = requestLine[(tokenAt + "token=".Length)..].Split(' ', '&')[0];
            Assert.False(string.IsNullOrWhiteSpace(tokenValue));
            Console.WriteLine(
                $"ALLOWED_LISTENER port={port} tcp_accepts=1 request=GET /chat token_query=present");

            var setupPort = OpenClawTray.AppIdentity.SetupGatewayPort;
            var before = CountOwnTcpConnectionsToPort(setupPort);
            Directory.CreateDirectory(urlLessDir);
            File.WriteAllText(
                Path.Combine(urlLessDir, "settings.json"),
                """
                {
                  "Token": "test-auth-token",
                  "BootstrapToken": "test-token-placeholder",
                  "EnableNodeMode": true
                }
                """);
            var identity = new DeviceIdentity(urlLessDir);
            identity.Initialize();
            identity.StoreDeviceTokenForRole("operator", "operator-role-token");
            var urlLess = new SettingsManager(urlLessDir);
            var other = new SettingsManager(urlLessDir) { NotificationSound = "external" };
            other.SaveOrThrow();
            Assert.Throws<SettingsPersistenceConflictException>(() =>
                urlLess.UpdateAndSave(() => urlLess.GatewayUrl = urlLess.GatewayUrl));
            Assert.Null(urlLess.GetLegacyCredentialGatewayUrlOrNull());
            Assert.False(ResolveInteractive(urlLessDir, urlLess, null, null, out var rejected));
            Assert.Null(rejected);
            var after = CountOwnTcpConnectionsToPort(setupPort);
            Assert.Equal(before, after);
            Console.WriteLine(
                $"SETUP_PORT port={setupPort} own_tcp_before={before} own_tcp_after={after}");
        }
        finally
        {
            listener.Stop();
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the assertion.
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            foreach (var path in new[] { savedDir, urlLessDir })
            {
                if (!Directory.Exists(path))
                    continue;
                try { Directory.Delete(path, recursive: true); } catch { }
            }
        }
    }

    private static async Task AcceptOneHttpRequestAsync(
        TcpListener listener,
        TaskCompletionSource<string> requestHead)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var readLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[1024];
        var head = new StringBuilder();
        while (head.Length < 8192 && !head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, readLimit.Token);
            if (read == 0)
                break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        requestHead.TrySetResult(head.ToString());
        var response = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response);
    }

    private static int CountOwnTcpConnectionsToPort(int port)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano -p tcp",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
            return -1;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        var suffix = ":" + port;
        var pid = Environment.ProcessId.ToString();
        var count = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[^1] != pid)
                continue;
            if (parts[1].EndsWith(suffix, StringComparison.Ordinal) ||
                parts[2].EndsWith(suffix, StringComparison.Ordinal))
                count++;
        }

        return count;
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
            settings.GetLegacyCredentialGatewayUrlOrNull(),
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
        var chatUrl = SliceMethod(chat, "async Task ApplyWebViewSurfaceAsync(");

        Assert.Contains("_settings.LegacyToken", appChat, StringComparison.Ordinal);
        Assert.Contains("_settings.LegacyBootstrapToken", appChat, StringComparison.Ordinal);
        Assert.Contains("_settings.GetLegacyCredentialGatewayUrlOrNull()", appChat, StringComparison.Ordinal);
        Assert.Contains("InteractiveGatewayCredentialResolver.TryResolve", appChat, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyToken", chatUrl, StringComparison.Ordinal);
        Assert.Contains("settings.LegacyBootstrapToken", chatUrl, StringComparison.Ordinal);
        Assert.Contains("settings.GetLegacyCredentialGatewayUrlOrNull()", chatUrl, StringComparison.Ordinal);
        Assert.Contains("ResolveChatCredential", chatUrl, StringComparison.Ordinal);
        Assert.Contains("ChatSurfaceResolver.BuildChatUrl", chatUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.GetEffectiveGatewayUrl()", chatUrl, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string signatureText)
    {
        var signature = source.IndexOf(signatureText, StringComparison.Ordinal);
        Assert.True(signature >= 0, $"Could not find {signatureText}.");
        var next = source.IndexOf("\n    private ", signature + signatureText.Length, StringComparison.Ordinal);
        return next > signature ? source[signature..next] : source[signature..];
    }
}
