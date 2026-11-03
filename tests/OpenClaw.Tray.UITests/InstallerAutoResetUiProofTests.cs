using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Real-UI proof of the installer-level Local AI recovery: when the setup pipeline fails with
/// <c>LocalAiReceiptUnusable</c>, the mounted ProgressPage invokes the tray reset automatically
/// (no confirmation dialog), announces the removal on the real page, and reruns the same
/// pipeline. The reset is a recording double that deletes the seeded receipt exactly like the
/// engine's final step, so the rerun's reconcile observes the removed installation.
/// </summary>
[Collection(UICollection.Name)]
public sealed class InstallerAutoResetUiProofTests(UIThreadFixture ui)
{
    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task UnusableReceipt_InstallerAutoResetsWithoutDialogAndRerunsPipeline()
    {
        const string modelId = LocalModelCatalog.Qwen38_27BModelId;
        var hardware = new CudaHostHardwareProbe().Probe();
        var eligibility = LocalInferenceEligibility.Evaluate(hardware, modelId);
        if (eligibility.Plan is null || eligibility.SelectedGpu?.StableId is not { Length: > 0 })
            return; // The pipeline's own hardware admission fails on this host; nothing to prove here.

        using var directory = new TempDirectory("openclaw-auto-reset-");
        var config = new SetupConfig
        {
            NativeLocalAiAcquisition = true,
            LocalAi = { Enabled = true, SelectedModelId = modelId },
        };
        var configPath = directory.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        var dataDir = directory.Combine("data");
        var localDataDir = directory.Combine("local");
        var paths = new LocalAiPaths(localDataDir);
        paths.EnsureDirectories();
        await new LocalAiManifestStore(paths).SaveAsync(StaleManifest(), CancellationToken.None);

        var host = new RecordingResetHost(paths);

        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            SetupWindow? window = null;
            Frame? frame = null;
            IDisposable? navigation = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(
                    configPath: configPath, dataDir: dataDir, localDataDir: localDataDir,
                    commandLineArgs: [], localAiHost: host));
                var root = Assert.IsType<Grid>(window.Content);
                frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                navigation = OnboardingNativeProof.TrackNavigation(frame);
                frame.Navigate(typeof(ProgressPage), new ProgressPageArgs(config, false, false, dataDir, localDataDir));
                Assert.IsType<ProgressPage>(frame.Content);

                // The pipeline fails on the seeded unusable receipt and invokes the reset
                // automatically: no dialog was shown and no interaction happened in between.
                await WaitAsync(() => host.ResetCalls == 1, "automatic reset invocation");
                Assert.Equal(1, host.ResetCalls);

                // While the reset runs, the real page announces the removal through the
                // subtitle and the loading overlay reports the dedicated reset step.
                var page = Assert.IsType<ProgressPage>(frame.Content);
                await WaitAsync(() =>
                    ((TextBlock)page.FindName("SubtitleText")).Text ==
                    "Removing the old Local AI installation before retrying setup...",
                    "reset announcement");
                Assert.Equal(SetupLoadingStep.ResetLocalAi,
                    window.LoadingProgress.Current?.Step);

                // The failure page never appeared before the automatic reset.
                Assert.IsType<ProgressPage>(frame.Content);

                // Release the reset. The double deletes the receipt like the engine's final
                // step, so the rerun's reconcile skips the removed installation instead of
                // failing again, and the pipeline continues into acquisition.
                host.Release();
                await WaitAsync(() => JournalPipelineRuns(dataDir) == 2 &&
                    JournalReconcileOutcome(dataDir, run: 2) == "Skipped", "second pipeline run");

                var journal = ReadJournal(dataDir);
                Assert.Equal(2, CountOccurrences(journal, "\"pipeline_started\""));
                var firstReconcile = JournalReconcileOutcome(dataDir, run: 1);
                Assert.Equal("FailedTerminal", firstReconcile);
                Assert.Contains(
                    "does not match the selected runtime, GPU, and model recipe",
                    ReadJournal(dataDir));
                Assert.Equal(1, host.ResetCalls);
                Assert.IsType<ProgressPage>(frame.Content);
            }
            finally
            {
                if (frame?.Content is IAsyncDisposable disposable)
                    await disposable.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(10));
                }
                navigation?.Dispose();
            }
        });
    }

    private static LocalAiInstallManifest StaleManifest()
    {
        var revision = new string('a', 40);
        return new LocalAiInstallManifest
        {
            EngineVersion = "b0.0.1",
            Architecture = "arm64",
            RuntimeId = "llama-server-stale-proof",
            ModelCatalogId = "stale-model-proof",
            SelectedGpuId = "GPU-0",
            ExecutablePath = "engines/llama-server.exe",
            RuntimeAssets =
            [
                new LocalAiAssetReceipt
                {
                    FileName = "llama-server.exe",
                    SourceUrl = "https://example.invalid/llama-server.exe",
                    SizeBytes = 1,
                    Sha256 = new string('0', 64),
                },
            ],
            ModelPath = "models/stale-model.gguf",
            ModelId = $"stale/stale@{revision}",
            ModelAlias = "stale",
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "stale-model.gguf",
                SourceUrl = $"https://huggingface.co/stale/stale/resolve/{revision}/stale-model.gguf",
                SizeBytes = 1,
                Sha256 = new string('0', 64),
            },
            ContextLength = 32768,
        };
    }

    private async Task WaitAsync(Func<bool> predicate, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!predicate())
        {
            if (timeout.IsCancellationRequested)
                Assert.Fail($"Timed out waiting for {label}.");
            await ui.YieldToRenderAsync();
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string ReadJournal(string dataDir)
    {
        string directory = Path.Combine(dataDir, "Logs", "Setup");
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.journal.jsonl")
            : [];
        Assert.Single(files);
        // The pipeline keeps the journal open and auto-flushed; read it with shared access.
        using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int JournalPipelineRuns(string dataDir) =>
        CountOccurrences(ReadJournal(dataDir), "\"pipeline_started\"");

    private static string? JournalReconcileOutcome(string dataDir, int run)
    {
        const string stepId = "\"StepId\":\"reconcile-local-ai-installation\"";
        var events = ReadJournal(dataDir)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(stepId, StringComparison.Ordinal) &&
                line.Contains("\"Event\":\"completed\"", StringComparison.Ordinal))
            .ToArray();
        if (events.Length < run)
            return null;
        var outcome = System.Text.RegularExpressions.Regex.Match(events[run - 1], "\"Outcome\":\"([^\"]+)\"");
        return outcome.Success ? outcome.Groups[1].Value : null;
    }

    private static int CountOccurrences(string text, string fragment)
    {
        int count = 0;
        for (int index = 0; (index = text.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0; index += fragment.Length)
            count++;
        return count;
    }

    /// <summary>
    /// Records the automatic reset and blocks until released, then removes the managed LocalAI
    /// tree exactly like the reset engine's final step. Never called from observation.
    /// </summary>
    private sealed class RecordingResetHost(LocalAiPaths paths) : ISetupLocalAiHost
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resetCalls;

        public int ResetCalls => Volatile.Read(ref _resetCalls);

        public void Release() => _release.TrySetResult();

        public async Task<string?> ResetLocalAiAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _resetCalls);
            await _release.Task.WaitAsync(ct);
            if (Directory.Exists(paths.RootDirectory))
                Directory.Delete(paths.RootDirectory, recursive: true);
            return null;
        }

        public GatewayRegistrySnapshot BeginGatewaySetup() => new([], null);
        public Task ReconcileGatewaySetupAsync(GatewayRegistrySnapshot expectedOutput, string? completedGatewayId) =>
            Task.CompletedTask;
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct) =>
            Task.FromResult(new LocalAiOnboardingSnapshot(LocalAiOnboardingState.Use));
        public Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            throw new InvalidOperationException("Not used by the installer pipeline.");
        public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            throw new InvalidOperationException("Not used by the installer pipeline.");
    }
}