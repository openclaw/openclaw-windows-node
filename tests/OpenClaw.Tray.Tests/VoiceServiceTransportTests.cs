using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;
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

    private static MethodInfo GetMethod()
    {
        return typeof(VoiceService).GetMethod(
            "GetOrCreateTransportReadySource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
