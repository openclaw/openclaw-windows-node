using NAudio.Wave;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;
using OpenClaw.TestSupport;
using OpenClawTray.Services;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

// The real service constructs real pipelines. Only device callbacks and model
// initialization are controlled; no microphone/model/network IO is performed.
public sealed class VoiceServicePlaybackMutingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const int RequestTimeoutMs = 120_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingLongLivedCapture_IsMutedAndRecovers_AndRestartInheritsIntent(bool voiceChat)
    {
        await using var run = new CaptureRun();
        if (voiceChat) await run.Voice.StartVoiceChatAsync();
        else await run.Voice.StartPushToTalkAsync();
        var first = run.Captures[0];
        using var playback = run.Voice.AcquirePlaybackMute();
        first.EmitAudio();
        Assert.Equal(0, run.Levels);
        playback.Dispose();
        first.EmitAudio();
        Assert.Equal(1, run.Levels);
        await run.Voice.StopAsync();
        Assert.Equal(1, first.DisposeCount);

        run.Voice.IsMutedForPlayback = true;
        if (voiceChat) await run.Voice.StartVoiceChatAsync();
        else await run.Voice.StartPushToTalkAsync();
        run.Captures[1].EmitAudio();
        Assert.Equal(1, run.Levels);
        Assert.True(run.Pipelines[1].IsMuted);
        run.Voice.IsMutedForPlayback = false;
        run.Captures[1].EmitAudio();
        Assert.Equal(2, run.Levels);
    }

    [Fact]
    public async Task OneShotCreatedDuringPlayback_IsMuted_Recovers_AndUnregistersOnCancellation()
    {
        await using var run = new CaptureRun();
        using var playback = run.Voice.AcquirePlaybackMute();
        using var cancellation = new CancellationTokenSource();
        var listen = run.Voice.ListenOnceAsync(new SttListenArgs { TimeoutMs = RequestTimeoutMs }, cancellation.Token);
        try
        {
            await run.Captures[0].Started.Task.WaitAsync(TestTimeout);
            Assert.True(run.Pipelines[0].IsMuted);
            run.Captures[0].EmitAudio();
            Assert.Equal(0, run.Levels);
            playback.Dispose();
            run.Captures[0].EmitAudio();
            Assert.Equal(1, run.Levels);
        }
        finally { cancellation.Cancel(); }
        // Preserve the existing cancellation/flush result contract.
        await Assert.ThrowsAsync<TimeoutException>(() => listen.WaitAsync(TestTimeout));
        Assert.Equal(1, run.Captures[0].DisposeCount);
        run.Voice.IsMutedForPlayback = true;
        Assert.False(run.Pipelines[0].IsMuted); // no writes to a retired capture
    }

    [Fact]
    public async Task FixedCaptureCreatedDuringPlayback_DropsSamplesAndUnregistersAfterCompletion()
    {
        await using var run = new CaptureRun();
        using var playback = run.Voice.AcquirePlaybackMute();
        using var cancellation = new CancellationTokenSource();
        var capture = run.Voice.TranscribeFixedDurationAsync(
            new SttTranscribeArgs { MaxDurationMs = RequestTimeoutMs }, cancellation.Token);
        try
        {
            await run.Captures[0].Started.Task.WaitAsync(TestTimeout);
            run.Captures[0].EmitAudio();
            Assert.True(run.Pipelines[0].IsMuted);
            Assert.Equal(0, run.Levels);
        }
        finally { cancellation.Cancel(); }
        var result = await capture.WaitAsync(TestTimeout);
        Assert.False(result.Transcribed);
        Assert.Empty(result.Text);
        Assert.Equal(1, run.Captures[0].DisposeCount);
        playback.Dispose();
        Assert.True(run.Pipelines[0].IsMuted); // registration already removed
    }

    [Fact]
    public async Task CancellingOneShot_DoesNotReplaceStopOrUnmuteTheLongLivedOwner()
    {
        await using var run = new CaptureRun();
        using var playback = run.Voice.AcquirePlaybackMute();
        await run.Voice.StartVoiceChatAsync();
        using var cancellation = new CancellationTokenSource();
        var listen = run.Voice.ListenOnceAsync(new SttListenArgs { TimeoutMs = RequestTimeoutMs }, cancellation.Token);
        await run.Captures[1].Started.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        await Assert.ThrowsAsync<TimeoutException>(() => listen.WaitAsync(TestTimeout));
        Assert.Equal(VoiceMode.VoiceChat, run.Voice.CurrentMode);
        Assert.Equal(AudioPipelineState.Listening, run.Pipelines[0].State);
        Assert.Equal(0, run.Captures[0].DisposeCount);
        Assert.Equal(1, run.Captures[1].DisposeCount);
        Assert.True(run.Pipelines[0].IsMuted);
        playback.Dispose();
        Assert.False(run.Pipelines[0].IsMuted);
        await run.Voice.StopAsync();
        Assert.Equal(1, run.Captures[0].DisposeCount);
    }

    [Theory]
    [InlineData("ptt")]
    [InlineData("chat")]
    [InlineData("listen")]
    [InlineData("fixed")]
    public async Task CaptureStartError_UnregistersItsPipelineAndReleasesDevice(string mode)
    {
        await using var run = new CaptureRun(failStart: true);
        using var playback = run.Voice.AcquirePlaybackMute();
        Task start = mode switch
        {
            "ptt" => run.Voice.StartPushToTalkAsync(),
            "chat" => run.Voice.StartVoiceChatAsync(),
            "listen" => (Task)run.Voice.ListenOnceAsync(new SttListenArgs { TimeoutMs = RequestTimeoutMs }, default),
            _ => run.Voice.TranscribeFixedDurationAsync(new SttTranscribeArgs { MaxDurationMs = RequestTimeoutMs }, default)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TestTimeout));
        Assert.Equal(1, run.Captures[0].DisposeCount);
        playback.Dispose();
        Assert.True(run.Pipelines[0].IsMuted); // a leaked registration would write false
        Assert.Equal(VoiceMode.Inactive, run.Voice.CurrentMode);
    }

    [Fact]
    public async Task PlaybackWithNoVoiceService_MutesCaptureCreatedLater_WithoutAnotherSpeechRequest()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path) { NodeSttEnabled = false };
        using var domain = new PlaybackMuteState();
        VoiceService? recordingService = null;
        Func<VoiceService?> recordingResolver = () => recordingService;
        var playback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new OpenClawChatCoordinator(settings, () => null,
            NullLogger.Instance, null, domain.Acquire, (_, _) => playback.Task, () => { });
        var speaking = coordinator.SpeakResponseAsync("Synthetic playback before STT enable.");
        Assert.Null(recordingResolver());
        Assert.True(domain.IsMuted);

        settings.NodeSttEnabled = true;
        await using var run = new CaptureRun(sharedMute: domain);
        recordingService = run.Voice;
        Assert.Same(run.Voice, recordingResolver());
        await run.Voice.StartVoiceChatAsync();
        run.Captures[0].EmitAudio();
        Assert.Equal(0, run.Levels);
        Assert.True(run.Pipelines[0].IsMuted);
        playback.SetResult();
        await speaking.WaitAsync(TestTimeout);
        run.Captures[0].EmitAudio();
        Assert.Equal(1, run.Levels);
        Assert.False(run.Pipelines[0].IsMuted);
    }

    private sealed class CaptureRun : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        public VoiceService Voice { get; }
        public List<FakeCapture> Captures { get; } = new();
        public List<AudioPipeline> Pipelines { get; } = new();
        public int Levels { get; private set; }

        public CaptureRun(bool failStart = false, PlaybackMuteState? sharedMute = null)
        {
            var settings = new SettingsManager(_directory.Path);
            Voice = new VoiceService(NullLogger.Instance, settings, sharedMute, stt =>
            {
                var device = new FakeCapture(failStart);
                var pipeline = new AudioPipeline(NullLogger.Instance, stt, () => device,
                    (_, token) => Task.Delay(Timeout.Infinite, token), TestTimeout,
                    () => "Synthetic device timeout");
                Captures.Add(device);
                Pipelines.Add(pipeline);
                return pipeline;
            }, () => Task.CompletedTask);
            Voice.AudioLevelChanged += _ => Levels++;
        }

        public async ValueTask DisposeAsync()
        {
            await Voice.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class FakeCapture(bool failStart) : IAudioCapture
    {
        public WaveFormat WaveFormat { get; } = new(16000, 16, 1);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording()
        {
            if (failStart) throw new InvalidOperationException("Synthetic capture startup failure");
            Started.TrySetResult();
        }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
        public void Dispose() => DisposeCount++;
        public void EmitAudio() => DataAvailable?.Invoke(this, new WaveInEventArgs([0, 16], 2));
    }
}
