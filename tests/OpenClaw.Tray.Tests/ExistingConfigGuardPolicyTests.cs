using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ExistingConfigGuardPolicyTests
{
    [Fact]
    public void NoExistingConfig_DoesNotRequireConfirmation()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.False(guard.HasExistingConfiguration());
    }

    [Fact]
    public void ExistingConfig_RequiresConfirmation_BeforeLocalSetupStarts()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://remote:9000" };
        var guard = new OnboardingExistingConfigGuard(settings, temp.Path, temp.StatePath);

        Assert.True(guard.HasExistingConfiguration());
        Assert.True(ShouldBlockLocalSetup(replaceConfirmed: false, guard));
        Assert.False(ShouldBlockLocalSetup(replaceConfirmed: true, guard));
    }

    private static bool ShouldBlockLocalSetup(bool replaceConfirmed, OnboardingExistingConfigGuard guard) =>
        !replaceConfirmed && guard.HasExistingConfiguration();

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
