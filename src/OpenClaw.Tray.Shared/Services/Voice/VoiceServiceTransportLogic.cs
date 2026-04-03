using OpenClaw.Shared;
using Windows.Media.Devices;
using Windows.Media.SpeechRecognition;

namespace OpenClawTray.Services.Voice;

public static class VoiceServiceTransportLogic
{
    private static readonly TimeSpan HypothesisPromotionWindow = TimeSpan.FromSeconds(2);

    public static TaskCompletionSource<bool> GetOrCreateTransportReadySource(
        ConnectionStatus transportStatus,
        TaskCompletionSource<bool>? existingReadySource,
        out bool shouldStartConnection)
    {
        if (transportStatus == ConnectionStatus.Connecting && existingReadySource != null)
        {
            shouldStartConnection = false;
            return existingReadySource;
        }

        shouldStartConnection = true;
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static bool UsesCloudTextToSpeechRuntime(VoiceProviderOption provider)
    {
        return provider.TextToSpeechHttp != null || provider.TextToSpeechWebSocket != null;
    }

    public static bool ShouldAcceptAssistantReply(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        bool acceptedViaLateReplyGrace = false)
    {
        return awaitingReply || isSpeaking || queuedReplyCount > 0 || acceptedViaLateReplyGrace;
    }

    public static bool ShouldAcceptLateAssistantReply(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        string? lateReplySessionKey,
        DateTime? lateReplyGraceUntilUtc,
        string? incomingSessionKey,
        DateTime utcNow)
    {
        return !awaitingReply &&
               !isSpeaking &&
               queuedReplyCount == 0 &&
               !string.IsNullOrWhiteSpace(lateReplySessionKey) &&
               !string.IsNullOrWhiteSpace(incomingSessionKey) &&
               IsMatchingSessionKey(incomingSessionKey, lateReplySessionKey) &&
               lateReplyGraceUntilUtc.HasValue &&
               utcNow <= lateReplyGraceUntilUtc.Value;
    }

    public static bool ShouldRestartRecognitionAfterCompletion(
        bool running,
        VoiceActivationMode mode,
        bool restartInProgress,
        bool awaitingReply,
        bool isSpeaking)
    {
        return running &&
               mode == VoiceActivationMode.TalkMode &&
               !restartInProgress &&
               !awaitingReply &&
               !isSpeaking;
    }

    public static string DescribeRecognitionCompletionRestartDecision(
        bool running,
        VoiceActivationMode mode,
        bool restartInProgress,
        bool awaitingReply,
        bool isSpeaking)
    {
        if (!running)
        {
            return "runtime-not-running";
        }

        if (mode != VoiceActivationMode.TalkMode)
        {
            return $"mode={mode}";
        }

        if (restartInProgress)
        {
            return "controlled-restart-in-progress";
        }

        if (awaitingReply)
        {
            return "awaiting-reply";
        }

        if (isSpeaking)
        {
            return "speaking";
        }

        return "eligible";
    }

    public static bool ShouldRebuildRecognitionAfterCompletion(
        SpeechRecognitionResultStatus status,
        bool sessionHadActivity,
        bool sessionHadCaptureSignal,
        bool restartInProgress,
        bool awaitingReply,
        bool isSpeaking)
    {
        if (restartInProgress || awaitingReply || isSpeaking || sessionHadActivity)
        {
            return false;
        }

        return status == SpeechRecognitionResultStatus.UserCanceled;
    }

    public static string DescribeRecognitionCompletionRebuildDecision(
        SpeechRecognitionResultStatus status,
        bool sessionHadActivity,
        bool sessionHadCaptureSignal,
        bool restartInProgress,
        bool awaitingReply,
        bool isSpeaking)
    {
        if (restartInProgress)
        {
            return "controlled-restart-in-progress";
        }

        if (awaitingReply)
        {
            return "awaiting-reply";
        }

        if (isSpeaking)
        {
            return "speaking";
        }

        if (sessionHadActivity)
        {
            return "session-had-activity";
        }

        if (sessionHadCaptureSignal)
        {
            return "capture-signal-without-recognition";
        }

        return status switch
        {
            SpeechRecognitionResultStatus.UserCanceled => "user-canceled-without-activity",
            SpeechRecognitionResultStatus.TimeoutExceeded => "disabled-official-session-restart-only (status=TimeoutExceeded)",
            _ => $"disabled-official-session-restart-only (status={status})"
        };
    }

    public static string SelectRecognizedText(
        string recognizedText,
        string? latestHypothesisText,
        DateTime latestHypothesisUtc,
        DateTime utcNow,
        out bool promotedHypothesis)
    {
        promotedHypothesis = false;

        if (string.IsNullOrWhiteSpace(recognizedText) ||
            string.IsNullOrWhiteSpace(latestHypothesisText) ||
            utcNow - latestHypothesisUtc > HypothesisPromotionWindow)
        {
            return recognizedText;
        }

        var normalizedResult = recognizedText.Trim();
        var normalizedHypothesis = latestHypothesisText.Trim();

        if (normalizedHypothesis.Length <= normalizedResult.Length + 3)
        {
            return normalizedResult;
        }

        if (!normalizedHypothesis.EndsWith(normalizedResult, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedResult;
        }

        promotedHypothesis = true;
        return normalizedHypothesis;
    }

    public static string? SelectCompletionFallbackText(
        bool sessionHadActivity,
        string? latestHypothesisText,
        DateTime latestHypothesisUtc,
        DateTime utcNow)
    {
        if (!sessionHadActivity ||
            string.IsNullOrWhiteSpace(latestHypothesisText) ||
            utcNow - latestHypothesisUtc > HypothesisPromotionWindow)
        {
            return null;
        }

        return latestHypothesisText.Trim();
    }

    public static bool ShouldClearTranscriptDraftAfterCompletion(
        bool awaitingReply,
        bool isSpeaking,
        bool usedFallbackTranscript)
    {
        return !awaitingReply &&
               !isSpeaking &&
               !usedFallbackTranscript;
    }

    public static bool ShouldRepromptAfterIncompleteRecognition(
        bool sessionHadActivity,
        bool awaitingReply,
        bool isSpeaking,
        bool usedFallbackTranscript)
    {
        return sessionHadActivity &&
               !awaitingReply &&
               !isSpeaking &&
               !usedFallbackTranscript;
    }

    public static bool ShouldRefreshRecognitionForDefaultCaptureDeviceChange(
        bool running,
        VoiceActivationMode mode,
        string? configuredInputDeviceId,
        AudioDeviceRole role)
    {
        return running &&
               mode == VoiceActivationMode.TalkMode &&
               string.IsNullOrWhiteSpace(configuredInputDeviceId) &&
               role == AudioDeviceRole.Default;
    }

    private static bool IsMatchingSessionKey(string? first, string? second)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(first) ? "main" : first,
            string.IsNullOrWhiteSpace(second) ? "main" : second,
            StringComparison.OrdinalIgnoreCase);
    }
}
