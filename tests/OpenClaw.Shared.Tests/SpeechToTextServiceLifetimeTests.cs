using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class SpeechToTextServiceLifetimeTests
{
    [WhisperModelFact]
    public async Task ReloadDuringTranscription_FinishesTheCurrentResultBeforeReplacingTheFactory()
    {
        var modelPath = ResolveTinyModel();
        Assert.NotNull(modelPath);

        var logger = new RecordingLogger();
        var service = new SpeechToTextService(logger);
        Task<List<TranscriptionResult>>? transcribe = null;
        try
        {
            service.LoadModel(modelPath);
            var samples = Tone(sampleRate: 16_000, seconds: 8);
            transcribe = Task.Run(() => service.TranscribeAsync(samples, "en"));

            var gate = TranscriptionGate(service);
            var heldGate = false;
            var enteredDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < enteredDeadline)
            {
                if (transcribe.IsCompleted)
                    break;
                if (gate.CurrentCount == 0)
                {
                    heldGate = true;
                    break;
                }

                await Task.Delay(5);
            }

            Assert.True(heldGate, transcribe.IsCompleted
                ? "Transcription finished before the reload could overlap it."
                : "Transcription did not acquire the gate.");

            var observedReloadWaiting = false;
            var reloadFinishedDuringTranscription = false;
            var reload = Task.Run(() => service.LoadModel(modelPath));
            var overlapDeadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < overlapDeadline)
            {
                var transcriptionDone = transcribe.IsCompleted;
                var reloadDone = reload.IsCompleted;
                if (!transcriptionDone && !reloadDone)
                    observedReloadWaiting = true;
                if (!transcriptionDone && reloadDone)
                {
                    reloadFinishedDuringTranscription = true;
                    break;
                }

                if (transcriptionDone)
                    break;

                await Task.Delay(20);
            }

            Exception? transcriptionError = null;
            List<TranscriptionResult>? segments = null;
            try
            {
                segments = await transcribe;
            }
            catch (Exception ex)
            {
                transcriptionError = ex;
            }

            Exception? reloadError = null;
            try
            {
                await reload.WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (Exception ex)
            {
                reloadError = ex;
            }

            var loads = logger.Infos.Count(line =>
                line.StartsWith("Whisper model loaded:", StringComparison.Ordinal));
            Assert.True(
                observedReloadWaiting
                && !reloadFinishedDuringTranscription
                && transcriptionError is null
                && reloadError is null
                && segments is not null
                && loads == 2,
                "reloadFinishedDuring=" + reloadFinishedDuringTranscription
                + "; waited=" + observedReloadWaiting
                + "; loads=" + loads
                + "; transcription=" + Describe(transcriptionError)
                + "; reload=" + Describe(reloadError));
        }
        finally
        {
            if (transcribe is not null)
            {
                try
                {
                    await transcribe;
                }
                catch (Exception)
                {
                }
            }

            service.Dispose();
        }
    }

    internal static string? ResolveTinyModel()
    {
        var configured = Environment.GetEnvironmentVariable("OPENCLAW_WHISPER_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
            return null;
        return configured;
    }

    private static SemaphoreSlim TranscriptionGate(SpeechToTextService service)
    {
        var field = typeof(SpeechToTextService).GetField(
            "_gate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = field?.GetValue(service) as SemaphoreSlim;
        Assert.NotNull(gate);
        return gate;
    }

    private static float[] Tone(int sampleRate, int seconds)
    {
        var samples = new float[sampleRate * seconds];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
        return samples;
    }

    private static string Describe(Exception? error) =>
        error is null ? "ok" : error.GetType().Name + ": " + error.Message;

    private sealed class RecordingLogger : IOpenClawLogger
    {
        public List<string> Infos { get; } = new();

        public void Info(string message) => Infos.Add(message);
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}

internal sealed class WhisperModelFactAttribute : FactAttribute
{
    public WhisperModelFactAttribute()
    {
        if (SpeechToTextServiceLifetimeTests.ResolveTinyModel() is null)
            Skip = "Set OPENCLAW_WHISPER_TEST_MODEL to a ggml model file.";
    }
}
