using System.Text.Json;
using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

public class SetupConfigTests : IDisposable
{
    private readonly string _tempDir;

    public SetupConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_AreReasonable()
    {
        var config = new SetupConfig();
        Assert.Equal("OpenClawGateway", config.DistroName);
        Assert.Equal(18789, config.GatewayPort);
        Assert.Equal("Ubuntu-24.04", config.BaseDistro);
        Assert.False(config.Headless);
        Assert.False(config.DryRun);
        Assert.Equal("trace", config.LogLevel);
        Assert.False(config.RollbackOnFailure);
        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.False(config.SkipPermissions);
        Assert.False(config.SkipWizard);
    }

    [Fact]
    public void ApplyUiDefaults_EnablesRollbackAndClearsHeadless()
    {
        var config = new SetupConfig
        {
            Headless = true,
            RollbackOnFailure = false
        };

        config.ApplyUiDefaults();

        Assert.False(config.Headless);
        Assert.True(config.RollbackOnFailure);
    }

    [Fact]
    public void ApplyUiDefaults_AllowsRollbackOptOut()
    {
        var config = new SetupConfig { RollbackOnFailure = true };

        config.ApplyUiDefaults(rollbackOnFailure: false);

        Assert.False(config.Headless);
        Assert.False(config.RollbackOnFailure);
    }

    [Fact]
    public void EffectiveGatewayUrl_UsesPort()
    {
        var config = new SetupConfig { GatewayPort = 9999 };
        Assert.Equal("ws://localhost:9999", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void EffectiveGatewayUrl_PreferExplicitUrl()
    {
        var config = new SetupConfig { GatewayUrl = "ws://custom:1234" };
        Assert.Equal("ws://custom:1234", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void LoadFromFile_ParsesJson()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """
        {
            "DistroName": "TestDistro",
            "GatewayPort": 12345,
            "Headless": true,
            // comment support
            "Gateway": {
                "Bind": "localhost",
                "ReloadMode": "cold"
            }
        }
        """);

        var config = SetupConfig.LoadFromFile(path);
        Assert.Equal("TestDistro", config.DistroName);
        Assert.Equal(12345, config.GatewayPort);
        Assert.True(config.Headless);
        Assert.Equal("localhost", config.Gateway.Bind);
        Assert.Equal("cold", config.Gateway.ReloadMode);
    }

    [Fact]
    public void FromEnvironment_OverridesDefaults()
    {
        // Set env vars temporarily
        var prevDistro = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_DISTRO");
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        var prevHeadless = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", "EnvDistro");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "9876");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", "true");

            var config = SetupConfig.FromEnvironment();
            Assert.Equal("EnvDistro", config.DistroName);
            Assert.Equal(9876, config.GatewayPort);
            Assert.True(config.Headless);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", prevDistro);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", prevHeadless);
        }
    }

    [Fact]
    public void FromEnvironment_InvalidPort_KeepsDefault()
    {
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "notanumber");
            var config = SetupConfig.FromEnvironment();
            Assert.Equal(18789, config.GatewayPort); // default
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
        }
    }

    [Fact]
    public void CapabilitiesConfig_DefaultsEnableExpectedCategories()
    {
        var caps = new CapabilitiesConfig();
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.Contains("system", categories);
        Assert.Contains("canvas", categories);
        Assert.Contains("screen", categories);
        Assert.Contains("device", categories);
        Assert.Contains("tts", categories);
        Assert.Contains("stt", categories);
    }

    [Fact]
    public void CapabilitiesConfig_DefaultOrderMatchesTrayRegistrationOrder()
    {
        var caps = new CapabilitiesConfig();

        Assert.Equal(
            ["system", "canvas", "screen", "camera", "location", "tts", "stt", "device", "browser"],
            caps.GetEnabledCapabilities().Select(c => c.Category).ToArray());
    }

    [Fact]
    public void CapabilitiesConfig_GetEnabledCommandIds_FlattensEnabledCapabilities()
    {
        var caps = new CapabilitiesConfig
        {
            Camera = false,
            Stt = false
        };

        var commands = caps.GetEnabledCommandIds();

        Assert.Contains("system.notify", commands);
        Assert.Contains("tts.speak", commands);
        Assert.DoesNotContain("camera.snap", commands);
        Assert.DoesNotContain("stt.listen", commands);
        Assert.Equal(commands.Count, commands.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(commands.Order(StringComparer.OrdinalIgnoreCase), commands);
    }

    [Fact]
    public void CapabilitiesConfig_DisabledCategory_NotInList()
    {
        var caps = new CapabilitiesConfig { System = false, Canvas = false };
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.DoesNotContain("system", categories);
        Assert.DoesNotContain("canvas", categories);
        Assert.Contains("screen", categories);
    }

    [Fact]
    public void TraySettingsConfig_MergesIntoFile_OverwritesSetupKeysAndPreservesUnknownKeys()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"CustomKey": "custom_value", "EnableNodeMode": false, "AutoStart": true, "NodeCameraEnabled": false}""");

        var traySettings = new TraySettingsConfig { EnableNodeMode = true, AutoStart = false };
        traySettings.MergeIntoSettingsFile(settingsPath);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.Equal("custom_value", result.RootElement.GetProperty("CustomKey").GetString());
    }

    [Fact]
    public void TraySettingsConfig_CorruptExistingFile_BacksUpAndThrows()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, "{not json");

        var ex = Assert.Throws<InvalidDataException>(() => new TraySettingsConfig().MergeIntoSettingsFile(settingsPath));

        Assert.Contains("settings.json is corrupt", ex.Message);
        Assert.Equal("{not json", File.ReadAllText(settingsPath));
        Assert.Single(Directory.EnumerateFiles(_tempDir, "settings.json.corrupt-*.bak"));
    }

    [Fact]
    public void TraySettingsConfig_CreatesNewFile_WhenMissing()
    {
        var settingsPath = Path.Combine(_tempDir, "newsettings", "settings.json");
        var traySettings = new TraySettingsConfig();
        traySettings.MergeIntoSettingsFile(settingsPath);

        Assert.True(File.Exists(settingsPath));
        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeTtsEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeSttEnabled").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_PreservesNodeSettings_WhenGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: true);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_DisablesNodeSettings_WhenNoGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: false);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.False(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    public void WslConfig_Defaults()
    {
        var wsl = new WslConfig();
        Assert.Equal("openclaw", wsl.User);
        Assert.True(wsl.Systemd);
        Assert.False(wsl.Interop);
    }

    [Fact]
    public void PairingConfig_Defaults()
    {
        var pairing = new PairingConfig();
        Assert.Equal(60, pairing.TimeoutSeconds);
    }

    [Fact]
    public void StepResult_Ok_IsSuccess()
    {
        Assert.True(StepResult.Ok().IsSuccess);
        Assert.True(StepResult.Ok("msg").IsSuccess);
    }

    [Fact]
    public void StepResult_Skip_IsSuccess()
    {
        Assert.True(StepResult.Skip("reason").IsSuccess);
    }

    [Fact]
    public void StepResult_Fail_IsNotSuccess()
    {
        Assert.False(StepResult.Fail("err").IsSuccess);
    }

    [Fact]
    public void StepResult_Terminal_IsNotSuccess()
    {
        Assert.False(StepResult.Terminal("fatal").IsSuccess);
        Assert.Equal(StepOutcome.FailedTerminal, StepResult.Terminal("fatal").Outcome);
    }

    [Fact]
    public void PipelineResult_ExitCodes()
    {
        Assert.Equal(0, new PipelineResult(PipelineOutcome.Success).ExitCode);
        Assert.Equal(1, new PipelineResult(PipelineOutcome.Failed).ExitCode);
        Assert.Equal(3, new PipelineResult(PipelineOutcome.Cancelled).ExitCode);
    }
}
