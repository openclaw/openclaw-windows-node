using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;
using Windows.Media.Devices;
using Windows.Media.SpeechRecognition;

namespace OpenClaw.Tray.Tests;

public class VoiceServiceTransportTests
{
    [Fact]
    public void GetOrCreateTransportReadySource_ReusesExistingTaskWhileConnecting()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Connecting, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.Same(existing, result);
        Assert.False((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskWhenDisconnected()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Disconnected, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskAfterError()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Error, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    [Fact]
    public void UsesCloudTextToSpeechRuntime_ReturnsTrueForWebSocketProviders()
    {
        var method = typeof(VoiceService).GetMethod(
            "UsesCloudTextToSpeechRuntime",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var provider = new VoiceProviderOption
        {
            Id = VoiceProviderIds.MiniMax,
            TextToSpeechWebSocket = new VoiceTextToSpeechWebSocketContract
            {
                EndpointTemplate = "wss://example.test/tts"
            }
        };

        var result = (bool)method.Invoke(null, [provider])!;

        Assert.True(result);
    }

    [Theory]
    [InlineData(true, false, 0, false, true)]
    [InlineData(false, true, 0, false, true)]
    [InlineData(false, false, 1, false, true)]
    [InlineData(false, false, 0, true, true)]
    [InlineData(false, false, 0, false, false)]
    public void ShouldAcceptAssistantReply_MatchesPlaybackAndAwaitingState(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        bool acceptedViaLateReplyGrace,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldAcceptAssistantReply",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, [awaitingReply, isSpeaking, queuedReplyCount, acceptedViaLateReplyGrace])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false, false, 0, "main", "main", 30, true)]
    [InlineData(false, false, 0, "main", "main", 121, false)]
    [InlineData(true, false, 0, "main", "main", 30, false)]
    [InlineData(false, true, 0, "main", "main", 30, false)]
    [InlineData(false, false, 1, "main", "main", 30, false)]
    [InlineData(false, false, 0, "main", "other", 30, false)]
    public void ShouldAcceptLateAssistantReply_OnlyMatchesBoundedGraceWindow(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        string lateReplySessionKey,
        string incomingSessionKey,
        int secondsAfterTimeout,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldAcceptLateAssistantReply",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var timeoutUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc);
        var graceUntilUtc = timeoutUtc.AddMinutes(2);
        var result = (bool)method.Invoke(
            null,
            [
                awaitingReply,
                isSpeaking,
                queuedReplyCount,
                lateReplySessionKey,
                graceUntilUtc,
                incomingSessionKey,
                timeoutUtc.AddSeconds(secondsAfterTimeout)
            ])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ShouldRestartRecognitionAfterCompletion_SuppressesControlledRecycle(
        bool restartInProgress,
        bool awaitingReply,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldRestartRecognitionAfterCompletion",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(
            null,
            [
                true,
                VoiceActivationMode.TalkMode,
                restartInProgress,
                awaitingReply,
                false
            ])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(SpeechRecognitionResultStatus.UserCanceled, false, false, false, false, true)]
    [InlineData(SpeechRecognitionResultStatus.TimeoutExceeded, false, false, false, false, true)]
    [InlineData(SpeechRecognitionResultStatus.Success, false, false, false, false, false)]
    [InlineData(SpeechRecognitionResultStatus.UserCanceled, true, false, false, false, false)]
    [InlineData(SpeechRecognitionResultStatus.UserCanceled, false, true, false, false, false)]
    [InlineData(SpeechRecognitionResultStatus.UserCanceled, false, false, true, false, false)]
    [InlineData(SpeechRecognitionResultStatus.UserCanceled, false, false, false, true, false)]
    public void ShouldRebuildRecognitionAfterCompletion_OnlyRebuildsForDeafCanceledSessions(
        SpeechRecognitionResultStatus status,
        bool sessionHadActivity,
        bool restartInProgress,
        bool awaitingReply,
        bool isSpeaking,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldRebuildRecognitionAfterCompletion",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(
            null,
            [status, sessionHadActivity, restartInProgress, awaitingReply, isSpeaking])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Now again testing", "again testing", 1, true, "Now again testing")]
    [InlineData("again testing", "again testing", 1, false, "again testing")]
    [InlineData("Now again testing", "again testing", 3, false, "again testing")]
    [InlineData("This is different", "again testing", 1, false, "again testing")]
    public void SelectRecognizedText_PromotesRecentLongerHypothesisWhenFinalLooksTruncated(
        string hypothesis,
        string recognized,
        int hypothesisAgeSeconds,
        bool expectedPromoted,
        string expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "SelectRecognizedText",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var now = new DateTime(2026, 3, 25, 16, 45, 30, DateTimeKind.Utc);
        var args = new object?[] { recognized, hypothesis, now.AddSeconds(-hypothesisAgeSeconds), now, null };

        var result = (string)method.Invoke(null, args)!;

        Assert.Equal(expected, result);
        Assert.Equal(expectedPromoted, (bool)args[4]!);
    }

    [Theory]
    [InlineData(true, VoiceActivationMode.TalkMode, null, AudioDeviceRole.Default, true)]
    [InlineData(true, VoiceActivationMode.TalkMode, "", AudioDeviceRole.Default, true)]
    [InlineData(true, VoiceActivationMode.TalkMode, "device-1", AudioDeviceRole.Default, false)]
    [InlineData(true, VoiceActivationMode.VoiceWake, null, AudioDeviceRole.Default, false)]
    [InlineData(false, VoiceActivationMode.TalkMode, null, AudioDeviceRole.Default, false)]
    [InlineData(true, VoiceActivationMode.TalkMode, null, AudioDeviceRole.Communications, false)]
    public void ShouldRefreshRecognitionForDefaultCaptureDeviceChange_OnlyRefreshesTalkModeUsingSystemDefaultMic(
        bool running,
        VoiceActivationMode mode,
        string? configuredInputDeviceId,
        AudioDeviceRole role,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldRefreshRecognitionForDefaultCaptureDeviceChange",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [running, mode, configuredInputDeviceId, role])!;

        Assert.Equal(expected, result);
    }

    private static MethodInfo GetMethod()
    {
        return typeof(VoiceService).GetMethod(
            "GetOrCreateTransportReadySource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
