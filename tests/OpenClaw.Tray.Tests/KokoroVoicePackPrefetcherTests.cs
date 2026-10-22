using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// The prefetcher is built unconditionally during tray startup, before the Gateway
/// client initializes, so unusable Kokoro model storage must never escape it.
/// </summary>
public sealed class KokoroVoicePackPrefetcherTests
{
    // A file where models/ should be makes KokoroModelManager's CreateDirectory throw.
    private static TempDirectory DataDirWithBlockedModelStorage()
    {
        var dir = new TempDirectory("kokoro-prefetch-");
        File.WriteAllText(dir.Combine("models"), "");
        return dir;
    }

    private static SettingsManager KokoroSettings(string dir, bool nodeTtsEnabled) =>
        new(dir)
        {
            NodeTtsEnabled = nodeTtsEnabled,
            VoiceTtsEnabled = false,
            TtsProvider = "kokoro",
            TtsKokoroVoiceId = "af_maple",
            TtsKokoroAutoDownload = true,
        };

    // Metered, so a test can never start the real ~380 MB download.
    private static KokoroVoicePackPrefetcher Prefetcher(SettingsManager settings, string dir, IOpenClawLogger logger) =>
        new(settings, logger, dir, isMeteredConnection: () => true);

    [Fact]
    public void TtsDisabled_StartAndSave_LeaveModelStorageUntouched()
    {
        using var dir = DataDirWithBlockedModelStorage();
        var settings = KokoroSettings(dir.Path, nodeTtsEnabled: false);
        var logger = new CapturingLogger();

        Prefetcher(settings, dir.Path, logger).Start();
        settings.Save();

        Assert.True(File.Exists(dir.Combine("models")));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void EligiblePrefetch_WithUnusableStorage_LogsInsteadOfThrowing()
    {
        using var dir = DataDirWithBlockedModelStorage();
        var settings = KokoroSettings(dir.Path, nodeTtsEnabled: true);
        var logger = new CapturingLogger();

        Prefetcher(settings, dir.Path, logger).Start();

        Assert.Contains(logger.Warnings, w => w.StartsWith("Kokoro voice pack prefetch check failed:", StringComparison.Ordinal));
        Assert.True(File.Exists(dir.Combine("models")));
    }

    private sealed class CapturingLogger : IOpenClawLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }
}
