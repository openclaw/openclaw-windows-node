using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests the pure guard policy logic for SetupWarningPage decisions.
/// No FunctionalUI / no WinUI dependencies — tests the C# predicate rules only.
/// </summary>
public class SetupWarningPageGuardPolicyTests
{
    [Fact]
    public void ChooseLocal_NoExistingConfig_DoesNotRequireConfirmation()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        // With no existing config, the guard returns false — local path is safe.
        Assert.False(guard.HasExistingConfiguration());
    }

    [Fact]
    public void ChooseLocal_WithExistingConfig_RequiresConfirmation_BeforeAdvancing()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://remote:9000" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);
        var state = new OnboardingState(settings);
        state.ExistingConfigGuard = guard;

        Assert.True(guard.HasExistingConfiguration());

        // Without confirmation, defense-in-depth policy should block.
        var blocked = !state.ReplaceExistingConfigurationConfirmed
            && state.ExistingConfigGuard?.HasExistingConfiguration() == true;
        Assert.True(blocked);

        // After setting confirmation, block is lifted.
        state.ReplaceExistingConfigurationConfirmed = true;
        blocked = !state.ReplaceExistingConfigurationConfirmed
            && state.ExistingConfigGuard?.HasExistingConfiguration() == true;
        Assert.False(blocked);
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
