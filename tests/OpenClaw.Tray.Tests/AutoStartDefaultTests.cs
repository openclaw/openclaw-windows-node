using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug 5 (PR #274 smoke test): the Launch-at-Login checkbox on the Ready page
/// previously defaulted OFF and was purely cosmetic. These tests pin the new
/// default and verify the AutoStart setting round-trips through persistence.
///
/// The ReadyPage WinUI component itself is verified manually + via screenshot
/// (per AGENTS.md UX rules); these tests cover the contract it relies on.
/// </summary>
public sealed class AutoStartDefaultTests : IDisposable
{
    private readonly string _isolatedDir;

    public AutoStartDefaultTests()
    {
        _isolatedDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void FreshSettingsManager_DefaultsAutoStartTrue()
    {
        var settings = new SettingsManager(_isolatedDir);
        Assert.True(settings.AutoStart);
    }

    [Fact]
    public void AutoStart_RoundTripsThroughSave()
    {
        var settings = new SettingsManager(_isolatedDir);
        settings.AutoStart = false;
        settings.Save();

        var reloaded = new SettingsManager(_isolatedDir);
        Assert.False(reloaded.AutoStart);

        reloaded.AutoStart = true;
        reloaded.Save();

        var third = new SettingsManager(_isolatedDir);
        Assert.True(third.AutoStart);
    }
}
