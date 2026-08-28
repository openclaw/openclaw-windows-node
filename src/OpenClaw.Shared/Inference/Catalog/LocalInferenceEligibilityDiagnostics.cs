namespace OpenClaw.Shared.Inference.Catalog;

public static class LocalInferenceEligibilityDiagnostics
{
    public static string DescribeUnavailable(LocalInferenceEligibilityResult eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);

        return eligibility.SelectionFailureCode switch
        {
            LocalInferenceSelectionFailureCode.RuntimeUnavailable =>
                "This Local AI release does not include a native llama-server runtime for the detected Windows architecture.",
            LocalInferenceSelectionFailureCode.NoNvidiaGpu =>
                "No NVIDIA GPU was reported by the NVIDIA driver. Install or repair the NVIDIA driver, then try setup again.",
            LocalInferenceSelectionFailureCode.UnknownModel =>
                "The selected model is not available in this Local AI release.",
            _ => eligibility.FailureCode switch
            {
                LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete =>
                    "OpenClaw could not read a stable NVIDIA GPU identifier, memory, driver, or CUDA capability.",
                LocalInferenceEligibilityFailureCode.InsufficientGpuMemory =>
                    $"{eligibility.Plan?.Model.DisplayName ?? "The selected model"} requires " +
                    $"{FormatSize(eligibility.RequiredTotalMemoryBytes)} of GPU memory for model weights, KV cache, and runtime workspace. " +
                    $"OpenClaw detected {FormatOptionalSize(eligibility.DetectedTotalMemoryBytes)}.",
                LocalInferenceEligibilityFailureCode.DriverTooOld =>
                    $"NVIDIA driver {eligibility.SelectedGpu?.DriverVersion ?? "unknown"} was detected. " +
                    $"Local AI requires version {LocalInferenceEligibility.MinimumNvidiaDriverVersion} or newer.",
                LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow =>
                    "The NVIDIA driver does not provide CUDA 13 support. A separate CUDA Toolkit is not required.",
                _ => "OpenClaw could not verify the Local AI requirements on this system.",
            },
        };
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / 1_000_000_000d:0.#} GB";

    private static string FormatOptionalSize(long? bytes) =>
        bytes is { } value ? FormatSize(value) : "an unknown amount";
}
