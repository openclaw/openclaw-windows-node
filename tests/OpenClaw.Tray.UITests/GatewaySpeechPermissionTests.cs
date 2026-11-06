using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

// Real coordinator/settings integration; transport and playback are substituted.
public sealed class GatewaySpeechPermissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MuteStopsAutomaticSpeechButStillAllowsManualReadAloud()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true };
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () => null);
        var (speech, wire) = Connect(coordinator);
        coordinator.SetSpeechTarget("conversation-a");
        wire.Audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var automatic = coordinator.SpeakResponseAsync("Allowed automatic reply.", "conversation-a");
        Assert.Equal(1, wire.SynthesisCalls);
        coordinator.IsMuted = true;
        await automatic.WaitAsync(Deadline);
        await coordinator.SpeakResponseAsync("Muted automatic reply.", "conversation-a");
        Assert.Equal(1, wire.SynthesisCalls);
        var manual = coordinator.SpeakChatTextAsync("Manual reply while muted.");
        Assert.Equal(2, wire.SynthesisCalls);
        speech.Stop();
        await manual.WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrMuteDoesNotCancelReentrantManualRecovery(bool mute)
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true };
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () => null);
        var (speech, wire) = Connect(coordinator);
        wire.Audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var retiring = coordinator.SpeakChatTextAsync("Retiring synthetic request.");
        Task? recovery = null;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = wire.LastCancellation.Register(() =>
        {
            recovery = coordinator.SpeakChatTextAsync("New synthetic manual recovery.");
            entered.TrySetResult(wire.LastCancellation);
        });
        try
        {
            if (mute) coordinator.IsMuted = true;
            else coordinator.StopSpeaking();
            var recoveryToken = await entered.Task.WaitAsync(Deadline);
            await retiring.WaitAsync(Deadline);
            Assert.Equal(2, wire.SynthesisCalls);
            Assert.False(recoveryToken.IsCancellationRequested);
            Assert.NotNull(recovery);
            Assert.False(recovery.IsCompleted);
            Assert.Equal(GatewaySpeechPhase.Synthesizing, coordinator.SpeechState.Phase);
        }
        finally
        {
            speech.Stop();
            await retiring.WaitAsync(Deadline);
            if (recovery is not null) await recovery.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task RejectedBackgroundReplyDoesNotAcquireCaptureMute()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true };
        using var mute = new PlaybackMuteState();
        var transitions = new List<bool>();
        using var capture = mute.RegisterCapture(transitions.Add);
        transitions.Clear();
        var acquisitions = 0;
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () =>
        {
            acquisitions++;
            return mute.Acquire();
        });
        var (_, wire) = Connect(coordinator);
        coordinator.SetSpeechTarget("conversation-a");

        await coordinator.SpeakResponseAsync("Background synthetic reply.", "conversation-b").WaitAsync(Deadline);

        Assert.Equal(0, wire.SynthesisCalls);
        Assert.Equal(0, acquisitions);
        Assert.Empty(transitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedPermissionRevocationRetiresSynthesisAndPlayback(bool playing)
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true };
        settings.SaveOrThrow();
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () => null);
        var (speech, wire) = Connect(coordinator);
        var audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!playing) wire.Audio = audio.Task;
        CancellationToken playbackToken = default;
        var generation = Assert.IsType<CancellationTokenSource>(typeof(OpenClawChatCoordinator)
            .GetField("_speechCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
        var run = playing
            ? speech.SpeakAsync("Synthetic playback.", true, (_, token) =>
            {
                playbackToken = token;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, cancellationToken: generation.Token)
            : coordinator.SpeakChatTextAsync("Synthetic synthesis.");
        Assert.Equal(1, wire.SynthesisCalls);
        var token = playing ? playbackToken : wire.LastCancellation;
        try
        {
            settings.NodeTtsEnabled = false;
            settings.SaveOrThrow();
            Assert.False(new SettingsManager(temp.Path).NodeTtsEnabled);
            await run.WaitAsync(Deadline);
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);
        }
        finally
        {
            audio.TrySetResult(PermissionGatewayProxy.Clip);
            speech.Stop();
            await run.WaitAsync(Deadline);
        }
        Assert.Equal(1, wire.SynthesisCalls);
    }

    [Fact]
    public async Task DisabledPermissionRejectsReadAloudAndReenableAllowsAdmission()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = false };
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () => null);
        var (speech, wire) = Connect(coordinator);
        await coordinator.SpeakChatTextAsync("Disabled synthetic read aloud.").WaitAsync(Deadline);
        Assert.Equal(0, wire.SynthesisCalls);
        Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);
        wire.Audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        settings.NodeTtsEnabled = true;
        settings.SaveOrThrow();
        var enabled = coordinator.SpeakChatTextAsync("Enabled synthetic read aloud.");
        Assert.Equal(1, wire.SynthesisCalls);
        speech.Stop();
        await enabled.WaitAsync(Deadline);
    }

    [Fact]
    public async Task UnsavedRevocationRejectsSynthesizedAudioBeforePlayback()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true };
        using var coordinator = new OpenClawChatCoordinator(settings, () => null, NullLogger.Instance, null, () => null);
        var (_, wire) = Connect(coordinator);
        var audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Audio = audio.Task;
        var run = coordinator.SpeakChatTextAsync("Pending synthetic read aloud.");
        Assert.Equal(1, wire.SynthesisCalls);
        settings.NodeTtsEnabled = false;
        audio.SetResult(PermissionGatewayProxy.Clip);
        await run.WaitAsync(Deadline);
        Assert.True(wire.LastCancellation.IsCancellationRequested);
        Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);
    }

    private static (GatewaySpeechPlayback Speech, PermissionGatewayProxy Wire) Connect(OpenClawChatCoordinator coordinator)
    {
        var speech = Assert.IsType<GatewaySpeechPlayback>(typeof(OpenClawChatCoordinator)
            .GetField("_gatewaySpeech", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
        var client = DispatchProxy.Create<IOperatorGatewayClient, PermissionGatewayProxy>();
        speech.SetClient(client);
        return (speech, (PermissionGatewayProxy)(object)client);
    }
}

public class PermissionGatewayProxy : DispatchProxy
{
    public static readonly GatewaySpeechAudio Clip = new([1, 2, 3], "audio/mpeg", "openai");
    public Task<GatewaySpeechAudio> Audio { get; set; } = Task.FromResult(Clip);
    public int SynthesisCalls { get; private set; }
    public CancellationToken LastCancellation { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsConnectedToGateway": return true;
            case "get_SessionMutationConnectionEpoch": return (long?)1;
            case "get_GrantedOperatorScopes": return new[] { "operator.write" };
            case "get_AdvertisedServerMethods": return new[] { "tts.speak" };
            case "add_StatusChanged": case "remove_StatusChanged": return null;
            case "SynthesizeSpeechAsync":
                SynthesisCalls++;
                LastCancellation = (CancellationToken)args![2]!;
                return Audio;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
