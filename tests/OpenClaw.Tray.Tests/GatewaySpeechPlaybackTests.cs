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
    [InlineData("stop")]
    [InlineData("mute")]
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
            case "mute": speech.SetMuted(true); break;
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
    public async Task MutedAutomaticSpeechIsSuppressedButManualReadAloudStillWorks()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        speech.SetMuted(true);
        var plays = 0;
        Task Play(GatewaySpeechAudio _, CancellationToken ct) { plays++; return Task.CompletedTask; }
        await speech.SpeakAsync("Automatic.", false, Play);
        Assert.Equal(0, wire.SynthesisCalls);
        await speech.SpeakAsync("Manual.", true, Play);
        Assert.Equal(1, wire.SynthesisCalls);
        Assert.Equal(1, plays);
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

    [Fact]
    public async Task MissingConfiguredProviderFailsBeforeSynthesisWithoutLocalFallback()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Status = new GatewaySpeechStatus("fish-audio", Array.Empty<string>());
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true, (_, _) => throw new Exception("Must not play"));
        Assert.Contains(states, state => state.Failure == GatewaySpeechFailure.ProviderUnavailable);
        Assert.Equal(0, wire.SynthesisCalls);
    }

    [Fact]
    public async Task GatewayFallbackIsReportedFromTheActualAudioResult()
    {
        var (client, wire) = SpeechGatewayProxy.Create();
        wire.Status = new GatewaySpeechStatus("fish-audio", ["fish-audio", "openai"]);
        wire.Synthesis = Task.FromResult(SpeechGatewayProxy.Clip with { Provider = "openai" });
        using var speech = new GatewaySpeechPlayback();
        speech.SetClient(client);
        var states = new List<GatewaySpeechPlaybackState>();
        speech.Changed += states.Add;
        await speech.SpeakAsync("Synthetic.", true, (_, _) => Task.CompletedTask);
        var completed = Assert.Single(states, state => state.Phase == GatewaySpeechPhase.Completed);
        Assert.Equal("openai", completed.Provider);
        Assert.Equal("fish-audio", completed.ConfiguredProvider);
        Assert.Equal("GatewayVoice_ActualFallback", GatewaySpeechPresentation.Format(completed, key => key));
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
    public async Task UnknownSynthesisOutcomeIsReportedWithoutRetryOrPlayback()
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
    public async Task ExternalPreviewCancellationPublishesIdleWithoutLateFailure()
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

    [Fact]
    public void GatewayOptInRoundTripsWithoutChangingTheLocalNodeProvider()
    {
        using var temp = new TempDirectory("gateway-voice-settings-");
        var settings = new SettingsManager(temp.Path);
        Assert.False(settings.UseGatewayVoice);
        settings.TtsProvider = "windows";
        settings.UseGatewayVoice = true;
        settings.Save();
        var reloaded = new SettingsManager(temp.Path);
        Assert.True(reloaded.UseGatewayVoice);
        Assert.Equal("windows", reloaded.TtsProvider);
        Assert.Equal("windows", TtsCapability.ResolveProvider(null, reloaded.TtsProvider));
        Assert.DoesNotContain("gateway", TtsCapability.AllProviders);
    }
}

// Supplies only transport inputs and records calls. All cancellation, target,
// epoch, provider and playback decisions above execute the production owner.
public class SpeechGatewayProxy : DispatchProxy
{
    public static readonly GatewaySpeechAudio Clip = new([1, 2, 3], "audio/mpeg", "fish-audio");
    public GatewaySpeechStatus Status { get; set; } = new("fish-audio", ["fish-audio"]);
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
            case "get_AdvertisedServerMethods": return new[] { "tts.status", "tts.speak" };
            case "add_StatusChanged": _status += (EventHandler<ConnectionStatus>)args![0]!; return null;
            case "remove_StatusChanged": _status -= (EventHandler<ConnectionStatus>)args![0]!; return null;
            case "GetSpeechStatusAsync": return Task.FromResult(Status);
            case "SynthesizeSpeechAsync":
                SynthesisCalls++;
                LastEpoch = (long)args![1]!;
                LastCancellation = (CancellationToken)args[2]!;
                return Synthesis;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
