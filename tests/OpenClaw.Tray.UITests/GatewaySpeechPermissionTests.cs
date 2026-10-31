using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewaySpeechPermissionTests(UIThreadFixture fixture)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("status")]
    [InlineData("synthesis")]
    [InlineData("playback")]
    public Task PersistedTtsPermissionRevocationRetiresAdmittedGatewaySpeech(string phase) =>
        fixture.RunOnUIAsync(async () =>
        {
            using var temp = new TempDirectory("gateway-speech-permission-");
            var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true, TtsProvider = "windows" };
            settings.Save();
            using var coordinator = new OpenClawChatCoordinator(settings, () => null, new QuietLogger(), null);
            var speech = SpeechOwner(coordinator);
            var client = DispatchProxy.Create<IOperatorGatewayClient, PermissionGatewayProxy>();
            var wire = (PermissionGatewayProxy)(object)client;
            // Only external transport is substituted. Settings persistence, Saved
            // notification, coordinator policy and operation retirement are real.
            speech.SetClient(client);
            var status = new TaskCompletionSource<GatewaySpeechStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var audio = new TaskCompletionSource<GatewaySpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (phase == "status") wire.Status = status.Task;
            if (phase == "synthesis") wire.Audio = audio.Task;
            CancellationToken playbackToken = default;
            var run = phase == "playback"
                ? speech.SpeakAsync("Synthetic permission check.", true, (_, ct) =>
                {
                    playbackToken = ct;
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                })
                : coordinator.PreviewGatewayVoiceAsync("Synthetic permission check.", CancellationToken.None);
            Assert.Equal(1, wire.StatusCalls);
            Assert.Equal(phase == "status" ? 0 : 1, wire.SynthesisCalls);
            var operationToken = phase == "playback" ? playbackToken : wire.LastCancellation;
            var saved = 0;
            settings.Saved += (_, _) => saved++;
            try
            {
                settings.NodeTtsEnabled = false;
                settings.Save();
                Assert.Equal(1, saved);
                Assert.False(new SettingsManager(temp.Path).NodeTtsEnabled);
                Assert.True(settings.UseGatewayVoice);
                Assert.Equal("windows", settings.TtsProvider);
                Assert.True(operationToken.IsCancellationRequested);
                await run.WaitAsync(Deadline);
                Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);
            }
            finally
            {
                status.TrySetResult(PermissionGatewayProxy.Ready);
                audio.TrySetResult(PermissionGatewayProxy.Clip);
                speech.Stop();
                await run.WaitAsync(Deadline);
            }
            Assert.Equal(phase == "status" ? 0 : 1, wire.SynthesisCalls);
        });

    [Fact]
    public Task DisabledTtsPermissionRejectsManualGatewayPreviewBeforeStatusOrSynthesis() =>
        fixture.RunOnUIAsync(async () =>
        {
            using var temp = new TempDirectory("gateway-speech-denied-");
            var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = false, TtsProvider = "windows" };
            using var coordinator = new OpenClawChatCoordinator(settings, () => null, new QuietLogger(), null);
            var client = DispatchProxy.Create<IOperatorGatewayClient, PermissionGatewayProxy>();
            var wire = (PermissionGatewayProxy)(object)client;
            SpeechOwner(coordinator).SetClient(client);

            await coordinator.PreviewGatewayVoiceAsync("Must not leave Windows.", CancellationToken.None).WaitAsync(Deadline);

            Assert.Equal(0, wire.StatusCalls);
            Assert.Equal(0, wire.SynthesisCalls);
            Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);

            var status = new TaskCompletionSource<GatewaySpeechStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.Status = status.Task;
            settings.NodeTtsEnabled = true;
            settings.Save();
            var enabled = coordinator.PreviewGatewayVoiceAsync("Enabled synthetic preview.", CancellationToken.None);
            Assert.Equal(1, wire.StatusCalls);
            coordinator.StopGatewaySpeaking();
            status.SetResult(PermissionGatewayProxy.Ready);
            await enabled.WaitAsync(Deadline);
        });

    [Fact]
    public Task PermissionRevokedWhileStatusIsPendingPreventsSynthesisBeforeSave() =>
        fixture.RunOnUIAsync(async () =>
        {
            using var temp = new TempDirectory("gateway-speech-admission-");
            var settings = new SettingsManager(temp.Path) { UseGatewayVoice = true, NodeTtsEnabled = true, TtsProvider = "windows" };
            using var coordinator = new OpenClawChatCoordinator(settings, () => null, new QuietLogger(), null);
            var client = DispatchProxy.Create<IOperatorGatewayClient, PermissionGatewayProxy>();
            var wire = (PermissionGatewayProxy)(object)client;
            var status = new TaskCompletionSource<GatewaySpeechStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.Status = status.Task;
            SpeechOwner(coordinator).SetClient(client);
            var run = coordinator.PreviewGatewayVoiceAsync("Pending synthetic preview.", CancellationToken.None);
            Assert.Equal(1, wire.StatusCalls);

            settings.NodeTtsEnabled = false;
            status.SetResult(PermissionGatewayProxy.Ready);
            await run.WaitAsync(Deadline);

            Assert.Equal(0, wire.SynthesisCalls);
            Assert.True(wire.LastCancellation.IsCancellationRequested);
            Assert.Equal(GatewaySpeechPhase.Idle, coordinator.SpeechState.Phase);
        });

    private static GatewaySpeechPlayback SpeechOwner(OpenClawChatCoordinator coordinator) =>
        Assert.IsType<GatewaySpeechPlayback>(typeof(OpenClawChatCoordinator)
            .GetField("_gatewaySpeech", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));

    private sealed class QuietLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

public class PermissionGatewayProxy : DispatchProxy
{
    public static readonly GatewaySpeechStatus Ready = new("openai", ["openai"]);
    public static readonly GatewaySpeechAudio Clip = new([1, 2, 3], "audio/mpeg", "openai");
    public Task<GatewaySpeechStatus> Status { get; set; } = Task.FromResult(Ready);
    public Task<GatewaySpeechAudio> Audio { get; set; } = Task.FromResult(Clip);
    public int StatusCalls { get; private set; }
    public int SynthesisCalls { get; private set; }
    public CancellationToken LastCancellation { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsConnectedToGateway": return true;
            case "get_SessionMutationConnectionEpoch": return (long?)1;
            case "get_GrantedOperatorScopes": return new[] { "operator.read", "operator.write" };
            case "get_AdvertisedServerMethods": return new[] { "tts.status", "tts.speak" };
            case "add_StatusChanged": case "remove_StatusChanged": return null;
            case "GetSpeechStatusAsync":
                StatusCalls++;
                LastCancellation = (CancellationToken)args![1]!;
                return Status;
            case "SynthesizeSpeechAsync":
                SynthesisCalls++;
                LastCancellation = (CancellationToken)args![2]!;
                return Audio;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
