using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Whether catalog selection produced a complete native inference plan.</summary>
public enum LocalInferenceSelectionStatus
{
    Selected = 0,
    Unsupported = 1,
}

/// <summary>Stable reason returned when no inference plan can be selected.</summary>
public enum LocalInferenceSelectionFailureCode
{
    None = 0,
    RuntimeUnavailable = 1,
    NoNvidiaGpu = 2,
    UnknownModel = 3,
}

/// <summary>Whether a caller accepted the catalog default or named a model explicitly.</summary>
public enum LocalInferenceModelSelectionOrigin
{
    Default = 0,
    Explicit = 1,
}

/// <summary>A complete, immutable native inference choice.</summary>
public sealed record LocalInferencePlan(
    LlamaRuntimeVariant Runtime,
    LocalModelInfo Model,
    LocalInferenceModelSelectionOrigin ModelSelectionOrigin);

/// <summary>The deterministic result of selecting from the pinned local inference catalog.</summary>
public sealed record LocalInferenceSelectionResult
{
    private LocalInferenceSelectionResult(
        LocalInferenceSelectionStatus status,
        LocalInferenceSelectionFailureCode failureCode,
        LocalInferencePlan? plan)
    {
        Status = status;
        FailureCode = failureCode;
        Plan = plan;
    }

    public LocalInferenceSelectionStatus Status { get; }
    public LocalInferenceSelectionFailureCode FailureCode { get; }
    public LocalInferencePlan? Plan { get; }
    public bool IsSelected => Status == LocalInferenceSelectionStatus.Selected;

    internal static LocalInferenceSelectionResult Selected(LocalInferencePlan plan) =>
        new(LocalInferenceSelectionStatus.Selected, LocalInferenceSelectionFailureCode.None, plan);

    internal static LocalInferenceSelectionResult Unsupported(LocalInferenceSelectionFailureCode failureCode) =>
        new(LocalInferenceSelectionStatus.Unsupported, failureCode, null);
}

/// <summary>
/// Pure selection from a hardware snapshot and optional model ID. The CPU
/// architecture chooses only the native runtime. GPU names and CPU/GPU SKU
/// pairings are not part of qualification.
/// </summary>
public static class LocalInferenceSelector
{
    public static LocalInferenceSelectionResult Select(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        LlamaRuntimeVariant? runtime = LlamaRuntimeCatalog.Find(hardware.CpuArchitecture);
        if (runtime is null)
            return LocalInferenceSelectionResult.Unsupported(
                LocalInferenceSelectionFailureCode.RuntimeUnavailable);

        if (!hardware.HasNvidiaGpu)
            return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.NoNvidiaGpu);

        LocalModelInfo? model;
        LocalInferenceModelSelectionOrigin modelSelectionOrigin;
        if (string.IsNullOrWhiteSpace(requestedModelId))
        {
            model = LocalModelCatalog.Models
                .OrderByDescending(candidate => candidate.Weights.SizeBytes)
                .FirstOrDefault(candidate => hardware.NvidiaGpus.Any(gpu =>
                    LocalInferenceQualificationPolicy.HasRuntimePrerequisites(gpu, runtime) &&
                    LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu) >=
                        LocalInferenceQualificationPolicy.GetRequiredMemoryBytes(candidate)))
                ?? LocalModelCatalog.Models.OrderBy(candidate => candidate.Weights.SizeBytes).First();
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Default;
        }
        else
        {
            model = LocalModelCatalog.Find(requestedModelId);
            if (model is null)
                return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.UnknownModel);
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Explicit;
        }

        return LocalInferenceSelectionResult.Selected(
            new LocalInferencePlan(runtime, model, modelSelectionOrigin));
    }
}

internal static class LocalInferenceQualificationPolicy
{
    public static bool HasCompleteFacts(GpuInfo gpu) =>
        IsStableGpuId(gpu.StableId) &&
        gpu.GpuVisibleMemoryBytes is > 0 &&
        !string.IsNullOrWhiteSpace(gpu.DriverVersion) &&
        gpu.CudaMajorVersion is not null;

    public static bool HasRuntimePrerequisites(GpuInfo gpu, LlamaRuntimeVariant runtime)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(runtime);
        return HasCompleteFacts(gpu) &&
            Version.TryParse(gpu.DriverVersion, out Version? driverVersion) &&
            driverVersion >= LocalInferenceEligibility.MinimumNvidiaDriverVersion &&
            gpu.CudaMajorVersion >= runtime.CudaVersion.Major;
    }

    public static long GetRequiredMemoryBytes(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return SaturatingAdd(
            SaturatingAdd(model.Weights.SizeBytes, GetKvCacheMemoryBytes(model.Recipe)),
            model.Recipe.RuntimeWorkspaceBytes);
    }

    internal static long GetKvCacheMemoryBytes(LocalModelRunRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        long keyBytes = BytesPerElement(recipe.KeyCachePrecision);
        long valueBytes = BytesPerElement(recipe.ValueCachePrecision);
        long bytesPerToken = SaturatingMultiply(
            SaturatingMultiply(recipe.FullAttentionLayerCount, recipe.KeyValueHeadCount),
            recipe.KeyValueHeadDimension);
        bytesPerToken = SaturatingMultiply(bytesPerToken, SaturatingAdd(keyBytes, valueBytes));
        return SaturatingMultiply(bytesPerToken, recipe.ContextTokens);
    }

    public static long GetEffectiveTotalMemoryBytes(GpuInfo gpu) =>
        gpu.GpuVisibleMemoryBytes is not > 0
            ? 0
            : SaturatingAdd(
                gpu.GpuVisibleMemoryBytes.Value,
                gpu.SharedGpuMemoryBytes is > 0 ? gpu.SharedGpuMemoryBytes.Value : 0);

    public static long? GetEffectiveFreeMemoryBytes(GpuInfo gpu)
    {
        if (gpu.FreeGpuVisibleMemoryBytes is not >= 0)
            return null;

        if (gpu.SharedGpuMemoryBytes is > 0 && gpu.FreeSharedGpuMemoryBytes is null)
            return null;

        return SaturatingAdd(
            gpu.FreeGpuVisibleMemoryBytes.Value,
            gpu.SharedGpuMemoryBytes is > 0 && gpu.FreeSharedGpuMemoryBytes is > 0
                ? gpu.FreeSharedGpuMemoryBytes.Value
                : 0);
    }

    private static bool IsStableGpuId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static long BytesPerElement(KvCachePrecision precision) => precision switch
    {
        KvCachePrecision.F16 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };

    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0
            ? 0
            : left > long.MaxValue / right
                ? long.MaxValue
                : left * right;

    public static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}
