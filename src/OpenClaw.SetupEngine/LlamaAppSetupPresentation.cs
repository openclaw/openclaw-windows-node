namespace OpenClaw.SetupEngine;

/// <summary>Presentation only. The Gateway owns discovery, provider setup and exact-model verification.</summary>
public static class LlamaAppSetupPresentation
{
    public const string ProviderId = "llama-app";
    public const string Website = "https://llama.app";
    public const string ApiGuide = "https://llama.app/docs/serve";

    public static bool IsModel(string modelRef) => modelRef.StartsWith(ProviderId + "/", StringComparison.Ordinal);

    // Absence of a model in a failed/partial discovery is not a fresh installation.
    // In particular, damaged receipts and unmanaged providers retain their recovery path.
    public static bool PreferForFreshSetup(GatewayAiSetupDetection? detection, LocalAiOnboardingSnapshot? local) =>
        detection is { SetupComplete: false, ConfiguredModel: null, SetupModel: null, UtilityModel: null } &&
        detection.Candidates.Length == 0 && detection.UnavailableCandidates.Length == 0 &&
        local is { HasInstallationEvidence: false, ReceiptIdentity: null, ModelRef: null } &&
        local.State is LocalAiOnboardingState.SetUp or LocalAiOnboardingState.Unsupported;
}
