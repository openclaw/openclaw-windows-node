using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class OnboardingExistingConfigGuardTests
{
    [Fact]
    public void HasExistingConfiguration_ReturnsFalse_WhenNoConfigExists()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.False(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsTrue_WhenOperatorDeviceTokenExists()
    {
        using var temp = new TempDir();
        var identity = new DeviceIdentity(temp.Path);
        identity.Initialize();
        identity.StoreDeviceToken("operator-device-token");
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.True(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsTrue_WhenNodeDeviceTokenExists()
    {
        using var temp = new TempDir();
        var identity = new DeviceIdentity(temp.Path);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "node-device-token");
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.True(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsTrue_WhenGatewayUrlIsNonDefault()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://remotehost:18789" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.True(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsFalse_WhenGatewayUrlIsDefault()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://localhost:18789" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.False(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsTrue_WhenSetupStateIsComplete()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        File.WriteAllText(temp.StatePath, JsonSerializer.Serialize(new { Phase = "Complete" }));
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.True(guard.HasExistingConfiguration());
    }

    [Fact]
    public void HasExistingConfiguration_ReturnsFalse_WhenSetupStateIsFailed()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        File.WriteAllText(temp.StatePath, JsonSerializer.Serialize(new { Phase = "Failed" }));
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.False(guard.HasExistingConfiguration());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "openclaw-guard-test-" + Guid.NewGuid().ToString("N"));
        public string StatePath => System.IO.Path.Combine(Path, "setup-state.json");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
