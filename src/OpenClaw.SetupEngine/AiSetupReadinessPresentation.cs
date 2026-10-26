using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Display hints only. Provider and Local AI actions retain their own authority checks.</summary>
public static class AiSetupReadinessPresentation
{
    public static string CompletionFailureKey(Exception error, string fallbackKey) => error switch
    {
        SetupNativeOwnershipException => "Onboarding_Ready_OwnershipChanged",
        SetupNativeCompletionTimeoutException { Phase: SetupNativeCompletionPhase.Verification } => "Onboarding_Ready_Timeout",
        SetupNativeCompletionTimeoutException timeout => "Onboarding_Ready_Timeout" + timeout.Phase,
        NativeGatewayStartupTimeoutException => "Onboarding_Ready_TimeoutGatewayStartup",
        OperationCanceledException or TimeoutException => "Onboarding_Ready_Timeout",
        _ => fallbackKey,
    };

    public static string TitleKey(bool pinnedModel, bool localOperation, bool choicesPrepared, bool busy, bool failed,
        bool localAdmissionFailed = false) =>
        pinnedModel || localOperation
            ? failed && !busy ? "VerificationFailedTitle" : localOperation && busy ? "LocalInstalling" : "VerifyingTitle"
            : choicesPrepared || localAdmissionFailed && !busy ? "Title.Text" : "Preparing";

    public static bool ShowLocalRecovery(bool busy, bool authorityReplaced, LocalAiOnboardingSnapshot? snapshot) =>
        !busy && !authorityReplaced && snapshot is not null &&
        (snapshot.CanReview || snapshot.CanUse || snapshot.CanRefresh);

    public static bool IsManagerRecoveryBlocked(bool authorityReplaced, GatewayAiSetupPhase phase) =>
        authorityReplaced && phase is GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or
            GatewayAiSetupPhase.VerificationRequired or GatewayAiSetupPhase.Prepared;

    public static bool ShowLocalChoice(bool nativeRoute, bool nativeAdmitted, LocalAiOnboardingSnapshot snapshot) =>
        (!nativeRoute || snapshot.Target?.IsNative == true ||
            nativeAdmitted && snapshot.State is LocalAiOnboardingState.Checking or LocalAiOnboardingState.Unknown) &&
        snapshot.ShowLocalChoice;
}
