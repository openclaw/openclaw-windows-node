using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

// Only the external synthesis/player is controlled. These tests run the real
// coordinator, resolver and VoiceService without opening a microphone or model.
public sealed class ChatPlaybackMutingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task VoiceService_RetainsMuteBeforeAnyPipelineExists()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var voice = new VoiceService(NullLogger.Instance, settings);
        voice.IsMutedForPlayback = true;
        Assert.True(voice.IsMutedForPlayback);
        voice.IsMutedForPlayback = false;
        Assert.False(voice.IsMutedForPlayback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionOrFailure_ReleasesStandaloneCaptureMute(bool fail)
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var voice = new VoiceService(NullLogger.Instance, settings);
        var playback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = Create(settings, () => voice, (_, _) =>
        {
            calls++;
            Assert.True(voice.IsMutedForPlayback);
            return playback.Task;
        });
        var speaking = coordinator.SpeakResponseAsync("Synthetic voice check.");
        Assert.True(voice.IsMutedForPlayback);
        if (fail) playback.SetException(new InvalidOperationException("synthetic playback failure"));
        else playback.SetResult();
        await speaking.WaitAsync(TestTimeout);
        Assert.False(voice.IsMutedForPlayback);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OverlappingPlayback_OnSameAndChangedService_ReleasesOnlyItsOwnLease()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var firstVoice = new VoiceService(NullLogger.Instance, settings);
        await using var secondVoice = new VoiceService(NullLogger.Instance, settings);
        VoiceService current = firstVoice;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new Queue<Task>([first.Task, second.Task, third.Task]);
        using var coordinator = Create(settings, () => current, (_, _) => completions.Dequeue());
        var firstSpeaking = coordinator.SpeakResponseAsync("Synthetic one.");
        var secondSpeaking = coordinator.SpeakResponseAsync("Synthetic two.");
        current = secondVoice;
        var thirdSpeaking = coordinator.SpeakResponseAsync("Synthetic three.");
        first.SetResult();
        await firstSpeaking.WaitAsync(TestTimeout);
        Assert.True(firstVoice.IsMutedForPlayback);
        Assert.True(secondVoice.IsMutedForPlayback);
        second.SetResult();
        await secondSpeaking.WaitAsync(TestTimeout);
        Assert.False(firstVoice.IsMutedForPlayback);
        Assert.True(secondVoice.IsMutedForPlayback);
        third.SetResult();
        await thirdSpeaking.WaitAsync(TestTimeout);
        Assert.False(secondVoice.IsMutedForPlayback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposal_CancelsPendingSynthesis_ReleasesMute_AndDoesNotPlayLate(bool dispose)
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var voice = new VoiceService(NullLogger.Instance, settings);
        var synthesis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        var stopped = 0;
        using var coordinator = Create(settings, () => voice, async (_, token) =>
        {
            await synthesis.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            played++;
        }, () => stopped++);
        var speaking = coordinator.SpeakResponseAsync("Synthetic pending synthesis.");
        Assert.True(voice.IsMutedForPlayback);
        if (dispose) coordinator.Dispose();
        else coordinator.StopSpeaking();
        await speaking.WaitAsync(TestTimeout);
        synthesis.SetResult();
        Assert.False(voice.IsMutedForPlayback);
        Assert.Equal(0, played);
        Assert.Equal(dispose ? 1 : 0, stopped);

        // Stop creates a fresh playback lifetime. Disposal does not.
        await coordinator.SpeakResponseAsync("Synthetic recovery.").WaitAsync(TestTimeout);
        Assert.Equal(dispose ? 0 : 1, played);
        Assert.False(voice.IsMutedForPlayback);
    }

    [Fact]
    public async Task CancelledPlayback_ReleasesMute_AndSpeakerMutePreservesManualReadAloud()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var voice = new VoiceService(NullLogger.Instance, settings);
        var calls = 0;
        using var coordinator = Create(settings, () => voice, (_, _) =>
        {
            calls++;
            return Task.FromCanceled(new CancellationToken(true));
        });
        await coordinator.SpeakResponseAsync("Synthetic cancelled speech.").WaitAsync(TestTimeout);
        Assert.False(voice.IsMutedForPlayback);
        coordinator.IsMuted = true;
        await coordinator.SpeakResponseAsync("Synthetic muted reply.");
        Assert.Equal(1, calls);
        await coordinator.SpeakChatTextAsync("Synthetic manual speech.").WaitAsync(TestTimeout);
        Assert.Equal(2, calls);
        Assert.False(voice.IsMutedForPlayback);
    }

    [Fact]
    public async Task StopCancellationCallback_CanAdmitRecoveryWithoutGloballyInterruptingIt()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        await using var voice = new VoiceService(NullLogger.Instance, settings);
        var recoveryPlayback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? recovery = null;
        OpenClawChatCoordinator? owner = null;
        var calls = 0;
        using var coordinator = Create(settings, () => voice, async (_, token) =>
        {
            if (++calls == 1)
            {
                // Cancellation callbacks run in reverse registration order.
                // Admit recovery before the delay continuation can dispose
                // this registration while unwinding the retiring playback.
                var cancelledPlayback = Task.Delay(Timeout.Infinite, token);
                using var registration = token.Register(() =>
                    recovery = owner!.SpeakResponseAsync("Synthetic concurrent recovery."));
                await cancelledPlayback;
            }
            else await recoveryPlayback.Task.WaitAsync(token);
        }, () => recoveryPlayback.TrySetException(new InvalidOperationException("global interrupt")));
        owner = coordinator;
        var retiring = coordinator.SpeakResponseAsync("Synthetic retiring generation.");
        coordinator.StopSpeaking();
        await retiring.WaitAsync(TestTimeout);
        Assert.NotNull(recovery);
        Assert.False(recoveryPlayback.Task.IsCompleted);
        Assert.False(recovery.IsCompleted);
        Assert.True(voice.IsMutedForPlayback);
        recoveryPlayback.TrySetResult();
        await recovery.WaitAsync(TestTimeout);
        Assert.False(voice.IsMutedForPlayback);
    }

    [Fact]
    public async Task NodeStandaloneTransition_AlreadyMutedWithoutAnotherSpeechRequest()
    {
        using var directory = new TempDirectory();
        var settings = new SettingsManager(directory.Path);
        using var sharedMute = new PlaybackMuteState();
        await using var standalone = new VoiceService(NullLogger.Instance, settings, sharedMute);
        await using var node = new VoiceService(NullLogger.Instance, settings, sharedMute);
        VoiceService current = standalone;
        var playback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Create(settings, () => current, (_, _) => playback.Task);
        var speaking = coordinator.SpeakResponseAsync("Synthetic transition.");
        current = node;
        Assert.True(node.IsMutedForPlayback);
        await standalone.DisposeAsync();
        Assert.True(node.IsMutedForPlayback);
        playback.SetResult();
        await speaking.WaitAsync(TestTimeout);
        Assert.False(node.IsMutedForPlayback);
    }

    private static OpenClawChatCoordinator Create(SettingsManager settings,
        Func<VoiceService?> resolve, Func<TtsSpeakArgs, CancellationToken, Task> speak,
        Action? stop = null) =>
        new(settings, () => null, NullLogger.Instance, null, () => resolve()?.AcquirePlaybackMute(), speak, stop ?? (() => { }));
}
