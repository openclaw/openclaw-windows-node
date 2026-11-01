using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewaySpeechPlaybackTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReplacementCannotRetireCurrentSpeech(bool oversized)
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Synthesis = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var current = speech.SpeakAsync("Current synthetic speech.", true, (_, _) => Task.CompletedTask);
        var currentToken = wire.LastCancellation;
        try
        {
            await speech.SpeakAsync(oversized ? new string('a', 4097) : " ", true,
                (_, _) => throw new Exception("Must not play"));
            Assert.False(currentToken.IsCancellationRequested);
            Assert.False(current.IsCompleted);
            Assert.Equal(1, wire.SynthesisCalls);
        }
        finally
        {
            speech.Stop();
            await current.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ParentRetirementRejectsAudioBeforeLinkedCancellationPropagates()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = audio.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        using var parent = new CancellationTokenSource();
        var plays = 0;
        var run = speech.SpeakAsync("Synthetic pending reply.", true,
            (_, _) => { plays++; return Task.CompletedTask; }, cancellationToken: parent.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register last so CancelAsync pauses before notifying its linked child.
        using var barrier = parent.Token.Register(() => { entered.TrySetResult(); Assert.True(release.Wait(Deadline)); });
        var cancellation = parent.CancelAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(wire.LastCancellation.IsCancellationRequested);
            audio.SetResult(SpeechGatewayProxy.Clip);
            await run.WaitAsync(Deadline);
            Assert.Equal(0, plays);
        }
        finally
        {
            release.Set();
            await cancellation.WaitAsync(Deadline);
            speech.Stop();
            await run.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task PreCanceledAdmissionCannotRetireNewerSpeech()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Synthesis = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var current = speech.SpeakAsync("Current synthetic request.", true, (_, _) => Task.CompletedTask);
        var currentToken = wire.LastCancellation;
        try
        {
            await speech.SpeakAsync("Retired synthetic request.", true,
                (_, _) => throw new Exception("Must not play"), cancellationToken: new CancellationToken(canceled: true));
            Assert.False(currentToken.IsCancellationRequested);
            Assert.False(current.IsCompleted);
            Assert.Equal(1, wire.SynthesisCalls);
        }
        finally
        {
            speech.Stop();
            await current.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("target")]
    [InlineData("gateway")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task RetiringSynthesisRejectsLateAudio(string retirement)
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var late = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = late.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetTarget("conversation-a");
        var plays = 0;
        var run = speech.SpeakAsync("Synthetic text.", manual: true, (_, _) => { plays++; return Task.CompletedTask; });
        Assert.Equal(1, wire.SynthesisCalls);
        switch (retirement)
        {
            case "stop": speech.Stop(); break;
            case "target": speech.SetTarget("conversation-b"); break;
            case "gateway": speech.SetClient(SpeechGatewayProxy.Create().Client); break;
            case "disconnect": wire.Disconnect(); break;
            case "dispose": speech.Dispose(); break;
        }
        await run.WaitAsync(Deadline);
        late.SetResult(SpeechGatewayProxy.Clip);
        Assert.Equal(0, plays);
        Assert.Equal(1, wire.SynthesisCalls);
        Assert.True(wire.LastCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task StopDuringPlaybackCancelsNativeOwnerAndDoesNotPublishCompletion()
    {
        var (client, _) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        var run = speech.SpeakAsync("Synthetic.", true, (_, ct) =>
        {
            started.SetResult(ct);
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var playbackToken = await started.Task.WaitAsync(Deadline);
        speech.Stop();
        await run.WaitAsync(Deadline);
        Assert.True(playbackToken.IsCancellationRequested);
        Assert.DoesNotContain(states, state => state.Phase == GatewaySpeechPhase.Completed);
    }

    [Fact]
    public async Task ReconnectOnSameClientCannotAdoptOldSynthesis()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var late = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = late.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var plays = 0;
        Task Play(GatewaySpeechAudio _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); plays++; return Task.CompletedTask; }
        var old = speech.SpeakAsync("Old synthetic text.", true, Play);
        wire.Disconnect();
        wire.Connected = true;
        wire.Epoch = 2;
        wire.Synthesis = Task.FromResult(SpeechGatewayProxy.Clip);
        await speech.SpeakAsync("New synthetic text.", true, Play).WaitAsync(Deadline);
        late.SetResult(SpeechGatewayProxy.Clip);
        await old.WaitAsync(Deadline);
        Assert.Equal(1, plays);
        Assert.Equal(2, wire.LastEpoch);
        Assert.Equal(2, wire.SynthesisCalls);
    }

    [Fact]
    public async Task ResponseFromAnotherChatTargetDoesNotDispatch()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetTarget("conversation-a");
        await speech.SpeakAsync("Synthetic.", false, (_, _) => throw new Exception("Must not play"), "conversation-b");
        Assert.Equal(0, wire.SynthesisCalls);
    }

    [Theory]
    [InlineData("conversation-a")]
    [InlineData(null)]
    public async Task AutomaticSpeechAfterClearingTheTargetDoesNotDispatch(string? responseTarget)
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetTarget("conversation-a");
        speech.SetTarget(null);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;

        await speech.SpeakAsync("Late automatic reply.", false,
            (_, _) => throw new Exception("Must not play"), responseTarget).WaitAsync(Deadline);

        Assert.Equal(0, wire.SynthesisCalls);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData("conversation-a")]
    [InlineData(null)]
    public async Task AutomaticSpeechForTheActiveTargetStillPlays(string? responseTarget)
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetTarget("conversation-a");
        var plays = 0;

        await speech.SpeakAsync("Active reply.", false,
            (_, _) => { plays++; return Task.CompletedTask; }, responseTarget).WaitAsync(Deadline);

        Assert.Equal(1, wire.SynthesisCalls);
        Assert.Equal(1, plays);
    }

    [Theory]
    [InlineData("conversation-a")]
    [InlineData(null)]
    public async Task LateAutomaticReplyCannotInterruptManualReadAloudWithoutAnActiveTarget(string? responseTarget)
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = audio.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var plays = 0;
        var preview = speech.SpeakAsync("Manual preview.", true,
            (_, ct) => { ct.ThrowIfCancellationRequested(); plays++; return Task.CompletedTask; });
        Assert.Equal(1, wire.SynthesisCalls);
        var previewToken = wire.LastCancellation;

        var automatic = speech.SpeakAsync("Late automatic reply.", false,
            (_, _) => throw new Exception("Must not play"), responseTarget);
        try
        {
            Assert.True(automatic.IsCompleted);
            Assert.Equal(1, wire.SynthesisCalls);
            Assert.False(previewToken.IsCancellationRequested);
        }
        finally
        {
            audio.SetResult(SpeechGatewayProxy.Clip);
            await Task.WhenAll(preview, automatic).WaitAsync(Deadline);
        }

        Assert.Equal(1, plays);
    }

    [Fact]
    public async Task ActualProviderIsReportedFromTheAudioResult()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Synthesis = Task.FromResult(SpeechGatewayProxy.Clip with { Provider = "openai" });
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true, (_, _) => Task.CompletedTask);
        var completed = Assert.Single(states, state => state.Phase == GatewaySpeechPhase.Completed);
        Assert.Equal("openai", completed.Provider);
        Assert.Equal("GatewayVoice_ActualProvider", GatewaySpeechPresentation.Format(completed, key => key));
    }

    [Fact]
    public async Task NativePlaybackFailureIsVisibleAndNotRetried()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true,
            (_, _) => Task.FromException(new InvalidOperationException("Private decoder details")));
        Assert.Contains(states, state => state.Failure == GatewaySpeechFailure.PlaybackFailed);
        Assert.Equal(1, wire.SynthesisCalls);
        Assert.All(states, state => Assert.DoesNotContain("Private decoder", state.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisconnectedAdmissionIsReportedToTheVisibleStateWithoutDispatch()
    {
        using var speech = new GatewaySpeechPlayback();
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true, (_, _) => throw new Exception("Must not play"));
        Assert.Equal(GatewaySpeechFailure.Disconnected, Assert.Single(states).Failure);
    }

    [Fact]
    public async Task LateFailureCannotReplaceNewTargetState()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var late = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = late.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetTarget("conversation-a");
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        var old = speech.SpeakAsync("Old synthetic.", true, (_, _) => Task.CompletedTask);
        speech.SetTarget("conversation-b");
        wire.Synthesis = Task.FromResult(SpeechGatewayProxy.Clip);
        await speech.SpeakAsync("New synthetic.", true, (_, _) => Task.CompletedTask);
        late.SetException(new GatewaySpeechException(GatewaySpeechFailure.SynthesisFailed));
        await old.WaitAsync(Deadline);
        Assert.Equal(GatewaySpeechPhase.Completed, states.Last().Phase);
        Assert.DoesNotContain(states, state => state.Phase == GatewaySpeechPhase.Failed);
    }

    [Fact]
    public async Task SameClientDisconnectPublishesTerminalFailureAndRejectsLateCompletion()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var late = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = late.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        var run = speech.SpeakAsync("Synthetic.", true, (_, _) => throw new Exception("Must not play"));
        wire.Disconnect();
        await run.WaitAsync(Deadline);
        Assert.Equal(GatewaySpeechFailure.Disconnected, states.Last().Failure);
        late.SetResult(SpeechGatewayProxy.Clip);
        Assert.DoesNotContain(states, state => state.Phase == GatewaySpeechPhase.Completed);
    }

    [Fact]
    public async Task SynthesisTimeoutFailureIsReportedWithoutRetryOrPlayback()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Synthesis = Task.FromException<GatewaySpeechAudio>(new GatewaySpeechException(GatewaySpeechFailure.TimedOut));
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true, (_, _) => throw new Exception("Must not play"));
        Assert.Equal(1, wire.SynthesisCalls);
        Assert.Equal(GatewaySpeechFailure.TimedOut, states.Last().Failure);
    }

    [Fact]
    public async Task ExternalReadAloudCancellationPublishesIdleWithoutLateFailure()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        var late = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Synthesis = late.Task;
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        using var preview = new CancellationTokenSource();
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        var run = speech.SpeakAsync("Synthetic preview.", true,
            (_, _) => throw new Exception("Must not play"), cancellationToken: preview.Token);
        preview.Cancel();
        await run.WaitAsync(Deadline);
        Assert.Equal(GatewaySpeechPhase.Idle, states.Last().Phase);
        late.SetException(new GatewaySpeechException(GatewaySpeechFailure.SynthesisFailed));
        Assert.DoesNotContain(states, state => state.Phase == GatewaySpeechPhase.Failed);
    }


}

// Supplies only transport inputs and records calls. All cancellation, target,
// epoch, provider and playback decisions above execute the production owner.
public class SpeechGatewayProxy : DispatchProxy
{
    public static readonly GatewaySpeechAudio Clip = new([1, 2, 3], "audio/mpeg", "fish-audio");
    public Task<GatewaySpeechAudio> Synthesis { get; set; } = Task.FromResult(Clip);
    public bool Connected { get; set; } = true;
    public long Epoch { get; set; } = 1;
    public int SynthesisCalls { get; private set; }
    public long LastEpoch { get; private set; }
    public CancellationToken LastCancellation { get; private set; }
    private IOperatorGatewayClient _client = null!;
    private EventHandler<ConnectionStatus>? _status;

    public static (IOperatorGatewayClient Client, SpeechGatewayProxy Wire) Create()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, SpeechGatewayProxy>();
        var proxy = (SpeechGatewayProxy)(object)client;
        proxy._client = client;
        return (client, proxy);
    }
    public void Disconnect()
    {
        Connected = false;
        _status?.Invoke(_client, ConnectionStatus.Disconnected);
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsConnectedToGateway": return Connected;
            case "get_SessionMutationConnectionEpoch": return Connected ? (long?)Epoch : null;
            case "get_GrantedOperatorScopes": return new[] { "operator.read", "operator.write" };
            case "get_AdvertisedServerMethods": return new[] { "tts.speak" };
            case "add_StatusChanged": _status += (EventHandler<ConnectionStatus>)args![0]!; return null;
            case "remove_StatusChanged": _status -= (EventHandler<ConnectionStatus>)args![0]!; return null;
            case "SynthesizeSpeechAsync":
                SynthesisCalls++;
                LastEpoch = (long)args![1]!;
                LastCancellation = (CancellationToken)args[2]!;
                return Synthesis;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
