using System.Collections.Concurrent;
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

    [Fact]
    public async Task DisposeOnTheCallerContext_CompletesAfterInferenceReleasesTheGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SpeechToTextService(new RecordingLogger())
        {
            TranscriptionOverride = token => HoldUntilReleased(entered, release, token)
        };
        using var finished = new SemaphoreSlim(0, 1);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new PostingSynchronizationContext());
            try
            {
                var transcribe = service.TranscribeAsync([], "en");
                if (!entered.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Transcription did not hold the gate.");

                service.Dispose();
                transcribe.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Release();
            }
        })
        {
            IsBackground = true,
            Name = "whisper-dispose-context"
        };
        thread.Start();

        var disposeBlocked = SpinWait.SpinUntil(
            () => entered.Task.IsCompleted
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));
        Assert.True(disposeBlocked, "Dispose did not wait for the in-flight transcription.");
        release.TrySetResult();

        if (!await finished.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            release.TrySetResult();
            try
            {
                if (TranscriptionGate(service).CurrentCount == 0)
                    TranscriptionGate(service).Release();
            }
            catch (SemaphoreFullException)
            {
            }

            thread.Join(TimeSpan.FromSeconds(2));
            Assert.Fail("Dispose stayed blocked after inference finished.");
        }

        Assert.Null(failure);
    }

    [Fact]
    public async Task Dispose_WaitsForTheInFlightTranscriptionBeforeReturning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SpeechToTextService(new RecordingLogger())
        {
            TranscriptionOverride = token => HoldUntilReleased(entered, release, token)
        };

        var transcribe = Task.Run(() => service.TranscribeAsync([], "en"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var disposeReturned = new ManualResetEventSlim();
        var dispose = Task.Run(() =>
        {
            service.Dispose();
            disposeReturned.Set();
        });

        try
        {
            Assert.False(
                disposeReturned.Wait(TimeSpan.FromMilliseconds(500)),
                "Dispose returned while inference still held the gate.");
        }
        finally
        {
            release.TrySetResult();
        }

        await transcribe.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposeReturned.IsSet);
    }

    private static async IAsyncEnumerable<TranscriptionResult> HoldUntilReleased(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return new TranscriptionResult { Text = "done", Language = "en" };
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

internal sealed class PostingSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queued = new();

    public override void Post(SendOrPostCallback callback, object? state) =>
        _queued.Enqueue((callback, state));
}

internal sealed class WhisperModelFactAttribute : FactAttribute
{
    public WhisperModelFactAttribute()
    {
        if (SpeechToTextServiceLifetimeTests.ResolveTinyModel() is null)
            Skip = "Set OPENCLAW_WHISPER_TEST_MODEL to a ggml model file.";
    }
}
