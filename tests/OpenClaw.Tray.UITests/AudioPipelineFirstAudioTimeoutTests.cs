using NAudio.Wave;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClawTray.Services;
using System.Collections.Concurrent;

namespace OpenClaw.Tray.UITests;

public sealed class AudioPipelineFirstAudioTimeoutTests
{
    private const string TimeoutMessage =
        "No audio was received. Check that your microphone is connected and selected as the Windows input device, then try again.";

    [Fact]
    public async Task NoCallback_StopsAndDisposesCapture_AndReportsError()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);
        var diagnostics = new ConcurrentQueue<string>();
        pipeline.DiagnosticMessage += diagnostics.Enqueue;

        await pipeline.StartAsync(new AudioPipelineOptions());
        delay.Expire();
        await WaitForStateAsync(pipeline, AudioPipelineState.Error);
        await WaitForConditionAsync(() => diagnostics.Contains(TimeoutMessage));

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Contains(TimeoutMessage, diagnostics);

        capture.EmitLateData([0, 0]);
        Assert.Equal(AudioPipelineState.Error, pipeline.State);

        await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
    }

    [Fact]
    public async Task FirstNonemptyAudio_CancelsTimeout()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        await pipeline.StartAsync(new AudioPipelineOptions());
        capture.EmitData([0, 0]);
        delay.Expire();
        await Task.Delay(50);

        Assert.Equal(AudioPipelineState.Listening, pipeline.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(0, capture.DisposeCount);
    }

    [Fact]
    public async Task StopBeforeTimeout_PreventsLateError()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        await pipeline.StartAsync(new AudioPipelineOptions());
        await pipeline.StopAsync();
        delay.Expire();
        await Task.Delay(50);

        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task Restart_DoesNotRetainPriorWatchdog()
    {
        var firstDelay = new ControlledDelay();
        var secondDelay = new ControlledDelay();
        var delays = new Queue<ControlledDelay>([firstDelay, secondDelay]);
        var firstCapture = new FakeAudioCapture();
        var secondCapture = new FakeAudioCapture();
        var captures = new Queue<FakeAudioCapture>([firstCapture, secondCapture]);
        await using var pipeline = CreatePipeline(
            () => captures.Dequeue(),
            (timeout, token) => delays.Dequeue().DelayAsync(timeout, token));

        await pipeline.StartAsync(new AudioPipelineOptions());
        await pipeline.StopAsync();
        await pipeline.StartAsync(new AudioPipelineOptions());

        firstDelay.Expire();
        await Task.Delay(50);

        Assert.Equal(AudioPipelineState.Listening, pipeline.State);
        Assert.Equal(0, secondCapture.StopCount);
        Assert.Equal(0, secondCapture.DisposeCount);

        secondCapture.EmitData([0, 0]);
        secondDelay.Expire();
        await Task.Delay(50);
        Assert.Equal(AudioPipelineState.Listening, pipeline.State);
    }

    [Fact]
    public async Task RecordingStoppedError_CancelsTimeout_AndKeepsExistingErrorRoute()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);
        var diagnostics = new ConcurrentQueue<string>();
        pipeline.DiagnosticMessage += diagnostics.Enqueue;

        await pipeline.StartAsync(new AudioPipelineOptions());
        capture.EmitRecordingStopped(new InvalidOperationException("device lost"));
        delay.Expire();
        await Task.Delay(50);

        Assert.Equal(AudioPipelineState.Error, pipeline.State);
        Assert.Contains("⚠️ Microphone error: device lost", diagnostics);
        Assert.DoesNotContain(TimeoutMessage, diagnostics);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task FixedCaptureRecordingError_CleansUpAndCanRestart()
    {
        var firstCapture = new FakeAudioCapture();
        var secondCapture = new FakeAudioCapture();
        var captures = new Queue<FakeAudioCapture>([firstCapture, secondCapture]);
        await using var pipeline = CreatePipeline(
            () => captures.Dequeue(),
            static (timeout, token) => Task.Delay(timeout, token));

        var fixedCaptureTask = pipeline.CaptureFixedDurationAsync(10_000);
        Assert.True(firstCapture.StartRecordingEntered.Wait(TimeSpan.FromSeconds(2)));
        firstCapture.EmitRecordingStopped(new InvalidOperationException("device lost"));
        await WaitForStateAsync(pipeline, AudioPipelineState.Error);

        Assert.Equal(1, firstCapture.DisposeCount);
        await pipeline.StopAsync();
        await pipeline.StartAsync(new AudioPipelineOptions());

        Assert.Equal(AudioPipelineState.Listening, pipeline.State);
        Assert.Equal(0, secondCapture.DisposeCount);
        await fixedCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CleanRecordingStopBeforeFirstAudio_DoesNotDisableTimeout()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        await pipeline.StartAsync(new AudioPipelineOptions());
        capture.EmitRecordingStopped();
        delay.Expire();
        await WaitForStateAsync(pipeline, AudioPipelineState.Error);

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentStopAndDispose_AreIdempotent()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        await pipeline.StartAsync(new AudioPipelineOptions());
        await Task.WhenAll(pipeline.StopAsync(), pipeline.DisposeAsync().AsTask());

        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task TimeoutAndStop_AreSerializedWithoutLateError()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture { BlockStopRecording = true };
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);
        var states = new ConcurrentQueue<AudioPipelineState>();
        pipeline.StateChanged += states.Enqueue;

        await pipeline.StartAsync(new AudioPipelineOptions());
        delay.Expire();
        Assert.True(capture.StopRecordingEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopTask = pipeline.StopAsync();
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        capture.AllowStopRecording.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.Equal(AudioPipelineState.Stopped, states.Last());
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task StopDuringStartup_WaitsForInitializationThenCleansUp()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture { BlockStartRecording = true };
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        var startTask = pipeline.StartAsync(new AudioPipelineOptions());
        Assert.True(capture.StartRecordingEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopTask = pipeline.StopAsync();
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        capture.AllowStartRecording.Set();
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task FixedDurationCapture_DoesNotArmStreamingWatchdog()
    {
        var delay = new ControlledDelay();
        var capture = new FakeAudioCapture();
        await using var pipeline = CreatePipeline(() => capture, delay.DelayAsync);

        var samples = await pipeline.CaptureFixedDurationAsync(25);

        Assert.Empty(samples);
        Assert.Equal(0, delay.CallCount);
        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task FixedCaptureLateTeardown_CannotStopRestartedStream()
    {
        var delay = new ControlledDelay();
        var teardownGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCapture = new FakeAudioCapture();
        var secondCapture = new FakeAudioCapture();
        var captures = new Queue<FakeAudioCapture>([firstCapture, secondCapture]);
        await using var pipeline = CreatePipeline(
            () => captures.Dequeue(),
            delay.DelayAsync,
            () => teardownGate.Task);

        var fixedCaptureTask = pipeline.CaptureFixedDurationAsync(10_000);
        Assert.True(firstCapture.StartRecordingEntered.Wait(TimeSpan.FromSeconds(2)));
        await pipeline.StopAsync();

        await pipeline.StartAsync(new AudioPipelineOptions());
        teardownGate.SetResult();
        await fixedCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AudioPipelineState.Listening, pipeline.State);
        Assert.Equal(0, secondCapture.StopCount);
        Assert.Equal(0, secondCapture.DisposeCount);
    }

    [Fact]
    public async Task StartupCancellation_ReturnsToStoppedWithoutMicrophoneError()
    {
        var delay = new ControlledDelay();
        var factoryEntered = new ManualResetEventSlim();
        var allowFactory = new ManualResetEventSlim();
        var capture = new FakeAudioCapture();
        using var cancellation = new CancellationTokenSource();
        await using var pipeline = CreatePipeline(
            () =>
            {
                factoryEntered.Set();
                allowFactory.Wait(TimeSpan.FromSeconds(2));
                return capture;
            },
            delay.DelayAsync);
        var diagnostics = new ConcurrentQueue<string>();
        pipeline.DiagnosticMessage += diagnostics.Enqueue;

        var startTask = pipeline.StartAsync(new AudioPipelineOptions(), cancellation.Token);
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        allowFactory.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.Equal(AudioPipelineState.Stopped, pipeline.State);
        Assert.DoesNotContain(diagnostics, message => message.Contains("Mic error", StringComparison.Ordinal));
        Assert.Equal(1, capture.DisposeCount);
    }

    private static AudioPipeline CreatePipeline(
        Func<IAudioCapture> captureFactory,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<Task>? beforeFixedCaptureTeardownAsync = null)
    {
        return new AudioPipeline(
            NullLogger.Instance,
            new SpeechToTextService(NullLogger.Instance),
            captureFactory,
            delayAsync,
            TimeSpan.FromSeconds(5),
            () => TimeoutMessage,
            beforeFixedCaptureTeardownAsync);
    }

    private static async Task WaitForStateAsync(AudioPipeline pipeline, AudioPipelineState state)
        => await WaitForConditionAsync(() => pipeline.State == state);

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task DelayAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Expire() => _completion.TrySetResult();
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private EventHandler<WaveInEventArgs>? _dataAvailable;
        private EventHandler<WaveInEventArgs>? _lastDataAvailable;

        public WaveFormat WaveFormat { get; } = new(16000, 16, 1);
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool BlockStartRecording { get; init; }
        public bool BlockStopRecording { get; init; }
        public ManualResetEventSlim StartRecordingEntered { get; } = new();
        public ManualResetEventSlim AllowStartRecording { get; } = new();
        public ManualResetEventSlim StopRecordingEntered { get; } = new();
        public ManualResetEventSlim AllowStopRecording { get; } = new();

        public event EventHandler<WaveInEventArgs>? DataAvailable
        {
            add
            {
                _dataAvailable += value;
                _lastDataAvailable = value;
            }
            remove => _dataAvailable -= value;
        }

        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording()
        {
            StartRecordingEntered.Set();
            if (BlockStartRecording)
                AllowStartRecording.Wait(TimeSpan.FromSeconds(2));
        }

        public void StopRecording()
        {
            StopCount++;
            StopRecordingEntered.Set();
            if (BlockStopRecording)
                AllowStopRecording.Wait(TimeSpan.FromSeconds(2));
        }

        public void Dispose() => DisposeCount++;

        public void EmitData(byte[] data) =>
            _dataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));

        public void EmitLateData(byte[] data) =>
            _lastDataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));

        public void EmitRecordingStopped(Exception? exception = null) =>
            RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));
    }
}
