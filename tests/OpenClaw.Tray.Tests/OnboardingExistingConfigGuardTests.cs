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
    public void HasExistingConfiguration_ReturnsTrue_WhenOperatorTokenStoredOnlyInPerGatewayDir()
    {
        // Modern pairings (post-GatewayRegistry) store the operator device token
        // at <dataPath>/gateways/<gatewayId>/device-key-ed25519.json via
        // DeviceIdentityStore. The legacy root file is NOT written for fresh
        // pairings, so the guard MUST scan per-gateway directories — otherwise
        // a returning user opening Setup/Reconfigure would not see the
        // "Replace my setup / Keep my setup" warning and could overwrite a
        // working config (Hanselman PR #340 review feedback).
        using var temp = new TempDir();
        var perGatewayDir = Path.Combine(temp.Path, "gateways", "gw-abc");
        Directory.CreateDirectory(perGatewayDir);
        var identity = new DeviceIdentity(perGatewayDir);
        identity.Initialize();
        identity.StoreDeviceToken("per-gateway-operator-token");
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        var summary = guard.GetSummary();
        Assert.True(summary.HasOperatorDeviceToken);
        Assert.True(summary.HasAny);
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
