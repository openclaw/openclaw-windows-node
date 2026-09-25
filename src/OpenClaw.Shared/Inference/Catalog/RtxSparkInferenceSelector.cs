namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// Default-recipe routing for RTX Spark, a unified-memory SKU where "total
/// CUDA-visible memory" identifies the physical memory SKU rather than a
/// discrete GPU's VRAM. RTX Spark bypasses the generic priority/fit-test
/// default pick (<see cref="LocalInferenceSelector"/>) in favor of a fixed
/// SKU-to-recipe table NVIDIA specified for this hardware. Runtime/driver
/// eligibility is still assessed uniformly afterward by
/// <see cref="LocalInferenceEligibility"/> for both paths.
/// </summary>
internal static class RtxSparkInferenceSelector
{
    // Empirically confirmed on real RTX Spark hardware: a 48GB-SKU unit's
    // cuMemGetInfo total reads ~48.59e9 bytes (~45.25 GiB), tracking the
    // nominal decimal-GB SKU size closely. This is NOT what nvidia-smi's "FB
    // Memory Usage" (NVML) or Windows' "Total Physical Memory" report on the
    // same box -- both read far lower on Spark's unified-memory design and
    // must never be used for SKU classification; only GpuInfo.GpuVisibleMemoryBytes
    // (CudaHostHardwareProbe's cuMemGetInfo reading) is reliable here.
    // Boundaries are geometric midpoints between nominal decimal-GB SKU
    // sizes; only the 48GB boundary is hardware-verified today.
    private const long DecimalGigabyte = 1_000_000_000L;

    private static readonly long s_boundary32_48 = GeometricMidpointBytes(32, 48);
    private static readonly long s_boundary48_64 = GeometricMidpointBytes(48, 64);
    private static readonly long s_boundary64_128 = GeometricMidpointBytes(64, 128);

    /// <summary>
    /// Returns the default (model, profile) pick for a detected RTX Spark
    /// GPU, or null when this SKU has no recommended local model.
    /// </summary>
    internal static (LocalModelInfo Model, LocalInferenceRunProfile Profile)? SelectDefault(GpuInfo sparkGpu)
    {
        ArgumentNullException.ThrowIfNull(sparkGpu);
        long totalBytes = LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(sparkGpu);

        return totalBytes switch
        {
            _ when totalBytes < s_boundary32_48 => null, // 32GB SKU: no local AI recommended
            _ when totalBytes < s_boundary48_64 => Recipe(LocalModelCatalog.Qwen35B_IQ4XSModelId),
            _ when totalBytes < s_boundary64_128 => Recipe(LocalModelCatalog.Qwen38_27BModelId, ReducedQ8_0ProfileId),
            _ => Recipe(LocalModelCatalog.Qwen38_27B_DFlashModelId),
        };
    }

    private const string ReducedQ8_0ProfileId = "ctx-131072-q8_0";

    private static (LocalModelInfo, LocalInferenceRunProfile) Recipe(string modelId, string? profileId = null)
    {
        LocalModelInfo model = LocalModelCatalog.Find(modelId)
            ?? throw new InvalidOperationException($"RTX Spark recipe '{modelId}' is missing from the catalog.");
        LocalInferenceRunProfile profile = profileId is null
            ? LocalModelCatalog.GetProfiles(model)[0]
            : LocalModelCatalog.FindProfile(model, profileId)
                ?? throw new InvalidOperationException($"RTX Spark recipe '{modelId}' is missing profile '{profileId}'.");
        return (model, profile);
    }

    private static long GeometricMidpointBytes(int lowerNominalGb, int upperNominalGb) =>
        (long)(Math.Sqrt((double)lowerNominalGb * upperNominalGb) * DecimalGigabyte);
}
