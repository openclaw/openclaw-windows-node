using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class UninstallOnboardingSettingsTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task Reset_RemovesLegacyCredentialsWithoutChangingOtherPreferences(
        bool preserveNodeSettings, bool hasGatewayUrl, bool hasTokens)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var settings = new JsonObject
        {
            ["EnableNodeMode"] = true,
            ["AutoStart"] = true,
            ["EnableMcpServer"] = true,
            ["Theme"] = "Dark",
            ["Preferences"] = new JsonObject { ["Accent"] = "Blue" },
        };
        if (hasGatewayUrl)
            settings["GatewayUrl"] = "ws://localhost:18789";
        if (hasTokens)
        {
            settings["Token"] = "gateway-token";
            settings["BootstrapToken"] = "test-auth-token";
        }
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, settings.ToJsonString());
        // This pins the reset helper's boundary, not the separate registry-cleanup path.
        const string externalRegistry = """{"activeId":"external","gateways":[{"id":"external","url":"wss://gateway.example","sharedGatewayToken":"test-token-placeholder"}]}""";
        File.WriteAllText(temp.Combine("gateways.json"), externalRegistry);

        await RunResetAsync(temp, preserveNodeSettings);

        settings.Remove("GatewayUrl");
        settings.Remove("Token");
        settings.Remove("BootstrapToken");
        settings["EnableNodeMode"] = preserveNodeSettings;
        settings["AutoStart"] = preserveNodeSettings;
        Assert.True(JsonNode.DeepEquals(settings, JsonNode.Parse(File.ReadAllText(path))));
        Assert.Equal(externalRegistry, File.ReadAllText(temp.Combine("gateways.json")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        var log = File.ReadAllText(temp.Combine("uninstall.log"));
        Assert.DoesNotContain("gateway-token", log);
        Assert.DoesNotContain("test-auth-token", log);
        Assert.DoesNotContain("test-token-placeholder", log);
        var resetJson = File.ReadAllText(path);
        await RunResetAsync(temp, preserveNodeSettings);
        Assert.Equal(resetJson, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_AlreadyCleanSettings_DoesNotRewrite(bool preserveNodeSettings)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var path = temp.Combine("settings.json");
        const string cleanJson = """{ "Theme": "Dark" }""";
        File.WriteAllText(path, cleanJson);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWrite = File.GetLastWriteTimeUtc(path);

        await RunResetAsync(temp, preserveNodeSettings);

        Assert.Equal(cleanJson, File.ReadAllText(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        Assert.Contains("No onboarding settings needed reset.", File.ReadAllText(temp.Combine("uninstall.log")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_WriteFailure_ReportsWarningAndPreservesOriginal(bool preserveNodeSettings)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var path = temp.Combine("settings.json");
        const string original = """{"Token":"gateway-token","BootstrapToken":"test-auth-token","Theme":"Dark"}""";
        File.WriteAllText(path, original);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await RunResetAsync(temp, preserveNodeSettings, expectedWarnings: 1);

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        var log = File.ReadAllText(temp.Combine("uninstall.log"));
        Assert.Contains("Failed to reset onboarding settings:", log);
        Assert.DoesNotContain("Reset onboarding settings;", log);
        Assert.DoesNotContain("gateway-token", log);
        Assert.DoesNotContain("test-auth-token", log);
    }

    private static async Task RunResetAsync(
        TempDirectory temp, bool preserveNodeSettings, int expectedWarnings = 0)
    {
        var stdout = await UninstallScriptHost.RunAsync(
            temp,
            [
                "Ensure-AppRoot", "Write-GatewayLog", "Add-CleanupWarning",
                "Read-JsonFile", "Write-JsonFileAtomic", "Reset-OnboardingSettings",
            ],
            """
            Reset-OnboardingSettings -DataDir $AppRoot -PreserveNodeSettings:([bool]::Parse($env:OPENCLAW_TEST_PRESERVE_NODE))
            Write-Output $cleanupWarnings.Count
            """,
            new Dictionary<string, string?> { ["OPENCLAW_TEST_PRESERVE_NODE"] = preserveNodeSettings.ToString() });
        Assert.Equal(expectedWarnings.ToString(), stdout.Trim());
    }
}
