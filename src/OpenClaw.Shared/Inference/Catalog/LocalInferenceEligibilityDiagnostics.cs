namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Discriminant for why Local AI is unavailable. Carries no language-specific text;
/// UI layers turn this into localized copy using their own resource strings.</summary>
public enum LocalInferenceUnavailableReasonKind
{
    RuntimeUnavailable,
    NoNvidiaGpu,
    UnknownModel,
    HardwareFactsIncomplete,
    InsufficientGpuMemory,
    DriverTooOld,
    CudaCapabilityTooLow,
    Unknown,
    InsufficientSystemMemory,
    InsufficientDiskSpace,
}

/// <summary>
/// Locale-neutral facts describing why Local AI is unavailable. This type intentionally carries
/// no English (or any other language) text: <see cref="OpenClaw.Shared"/> stays locale-neutral,
/// and each UI layer (setup, Hub) formats <see cref="Kind"/> plus these facts into localized
/// copy using its own resource strings.
/// </summary>
public sealed record LocalInferenceUnavailableReason(
    LocalInferenceUnavailableReasonKind Kind,
    string? ModelDisplayName,
    double RequiredGigabytes,
    double? DetectedGigabytes,
    string? DetectedDriverVersion,
    string MinimumDriverVersion,
    double RequiredSystemGigabytes = 0,
    double RequiredDiskGigabytes = 0);

public static class LocalInferenceEligibilityDiagnostics
{
    public static LocalInferenceUnavailableReason GetUnavailableReason(LocalInferenceEligibilityResult eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);

        LocalInferenceUnavailableReasonKind kind = eligibility.SelectionFailureCode switch
        {
            LocalInferenceSelectionFailureCode.RuntimeUnavailable =>
                LocalInferenceUnavailableReasonKind.RuntimeUnavailable,
            LocalInferenceSelectionFailureCode.NoNvidiaGpu =>
                LocalInferenceUnavailableReasonKind.NoNvidiaGpu,
            LocalInferenceSelectionFailureCode.UnknownModel =>
                LocalInferenceUnavailableReasonKind.UnknownModel,
            _ => eligibility.FailureCode switch
            {
                LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete =>
                    LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete,
                LocalInferenceEligibilityFailureCode.InsufficientGpuMemory =>
                    LocalInferenceUnavailableReasonKind.InsufficientGpuMemory,
                LocalInferenceEligibilityFailureCode.DriverTooOld =>
                    LocalInferenceUnavailableReasonKind.DriverTooOld,
                LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow =>
                    LocalInferenceUnavailableReasonKind.CudaCapabilityTooLow,
                LocalInferenceEligibilityFailureCode.InsufficientSystemMemory =>
                    LocalInferenceUnavailableReasonKind.InsufficientSystemMemory,
                LocalInferenceEligibilityFailureCode.InsufficientDiskSpace =>
                    LocalInferenceUnavailableReasonKind.InsufficientDiskSpace,
                _ => LocalInferenceUnavailableReasonKind.Unknown,
            },
        };

        return new LocalInferenceUnavailableReason(
            kind,
            eligibility.Plan?.Model.DisplayName,
            ToGigabytes(eligibility.RequiredTotalMemoryBytes),
            eligibility.DetectedTotalMemoryBytes is { } detected ? ToGigabytes(detected) : null,
            eligibility.SelectedGpu?.DriverVersion,
            LocalInferenceEligibility.MinimumNvidiaDriverVersion.ToString(),
            ToGigabytes(eligibility.RequiredSystemMemoryBytes),
            ToGigabytes(eligibility.RequiredDiskBytes));
    }

    private static double ToGigabytes(long bytes) => bytes / (1024d * 1024d * 1024d);
}
