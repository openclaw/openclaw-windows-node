using OpenClawTray.Services;
using OpenClaw.Shared.Codex;
using OpenClaw.Tray.Tests.Presentation;

namespace OpenClaw.Tray.Tests;

public class ConsentAndSettingsSaveTests
{
    [Fact]
    public void CodexSessionAccessUi_IsInteractiveLocalizedAndTransportSourcesHaveNoSettingsAssignment()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT must identify the test worktree.");
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml"));
        var resources = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"));
        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var transportSources = new[]
        {
            Path.Combine(root, "src", "OpenClaw.Shared", "Mcp", "McpToolBridge.cs"),
            Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "NodeCapabilityRegistry.cs"),
            Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs"),
        }.Select(File.ReadAllText);

        Assert.Contains("SelectedIndex=\"{Binding CodexSessionAccessIndex, Mode=TwoWay}\"", xaml);
        Assert.Contains("SettingsPage_CodexSessionAccess_Off", xaml);
        Assert.Contains("SettingsPage_CodexSessionAccess_ReadOnly", xaml);
        Assert.Contains("SettingsPage_CodexSessionAccess_ReadAndSteer", xaml);
        Assert.Contains("Catalog available", resources);
        Assert.Contains("Catalog unavailable", resources);
        Assert.Contains("Steering unavailable", resources);
        Assert.Contains("owner control", resources, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stage 0 did not pass validation", resources);
        Assert.Contains("does not change Gateway configuration", resources);
        Assert.DoesNotContain('\u2014', resources);
        Assert.All(transportSources, source => Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"\bCodexSessionAccess\s*=", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            source));
        Assert.Contains("_nodeService?.RefreshCodexSessionAccess()", app);
    }

    [Fact]
    public async Task Save_IsThreadSafe_ConcurrentCallsDoNotCorruptFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            settings.GatewayUrl = "ws://localhost:9999";

            // Fire many concurrent saves — none should throw or corrupt
            var tasks = Enumerable.Range(0, 20).Select(i =>
            {
                return Task.Run(() =>
                {
                    settings.ScreenRecordingConsentGiven = (i % 2 == 0);
                    settings.Save();
                });
            }).ToArray();

            await Task.WhenAll(tasks);

            // Verify file is still valid JSON and loadable
            var reloaded = new SettingsManager(tempDir);
            Assert.Equal("ws://localhost:9999", reloaded.GatewayUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Save_RaisesSavedEvent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            var eventRaised = false;
            settings.Saved += (s, e) => eventRaised = true;

            settings.ScreenRecordingConsentGiven = true;
            settings.Save();

            Assert.True(eventRaised);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConsentFlags_PersistAcrossReload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            Assert.False(settings.ScreenRecordingConsentGiven);
            Assert.False(settings.CameraRecordingConsentGiven);

            settings.ScreenRecordingConsentGiven = true;
            settings.CameraRecordingConsentGiven = true;
            settings.Save();

            var reloaded = new SettingsManager(tempDir);
            Assert.True(reloaded.ScreenRecordingConsentGiven);
            Assert.True(reloaded.CameraRecordingConsentGiven);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConsentFlags_CanBeRevoked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            settings.ScreenRecordingConsentGiven = true;
            settings.Save();

            // Revoke
            settings.ScreenRecordingConsentGiven = false;
            settings.Save();

            var reloaded = new SettingsManager(tempDir);
            Assert.False(reloaded.ScreenRecordingConsentGiven);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
