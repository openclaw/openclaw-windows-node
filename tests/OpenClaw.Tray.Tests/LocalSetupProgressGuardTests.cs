using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests the defense-in-depth guard policy for LocalSetupProgressPage.
/// Validates the predicate rule without rendering FunctionalUI.
/// </summary>
public class LocalSetupProgressGuardTests
{
    [Fact]
    public void DefenseInDepthGuard_ShouldBlock_WhenExistingConfigAndNotConfirmed()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { Token = "existing-token" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);
        var state = new OnboardingState(settings);
        state.ExistingConfigGuard = guard;
        state.ReplaceExistingConfigurationConfirmed = false;

        // Gate: block if !confirmed && guard.HasExistingConfiguration
        var shouldBlock = !state.ReplaceExistingConfigurationConfirmed
            && state.ExistingConfigGuard?.HasExistingConfiguration() == true;

        Assert.True(shouldBlock);
    }

    [Fact]
    public void DefenseInDepthGuard_ShouldAllow_WhenExistingConfigAndConfirmed()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { Token = "existing-token" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);
        var state = new OnboardingState(settings);
        state.ExistingConfigGuard = guard;
        state.ReplaceExistingConfigurationConfirmed = true;

        var shouldBlock = !state.ReplaceExistingConfigurationConfirmed
            && state.ExistingConfigGuard?.HasExistingConfiguration() == true;

        Assert.False(shouldBlock);
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
