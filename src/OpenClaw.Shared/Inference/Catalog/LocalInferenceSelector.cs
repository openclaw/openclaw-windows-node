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
    /// <summary>RTX Spark detected, but this memory SKU has no recommended local model.</summary>
    NotRecommendedForSku = 4,
}

/// <summary>Whether a caller accepted the catalog default or named a model explicitly.</summary>
public enum LocalInferenceModelSelectionOrigin
{
    Default = 0,
    Explicit = 1,
}

/// <summary>A complete, immutable native inference choice.</summary>
/// <param name="BoundGpuStableId">
/// The adapter this recipe was chosen for, when the choice is only valid on that
/// adapter. RTX Spark recipes come from a fixed per-device SKU table, so the recipe
/// and the GPU that runs it must be the same adapter; eligibility restricts its
/// candidates to this id. Null means any qualifying NVIDIA GPU may run the plan,
/// which is the generic discrete-GPU behavior.
/// </param>
public sealed record LocalInferencePlan(
    LlamaRuntimeVariant Runtime,
    LocalModelInfo Model,
    LocalInferenceRunProfile Profile,
    LocalInferenceModelSelectionOrigin ModelSelectionOrigin,
    string? BoundGpuStableId = null);

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
/// pairings are not part of qualification for discrete GPUs -- the one
/// deliberate exception is RTX Spark's default pick, routed through
/// <see cref="RtxSparkInferenceSelector"/> because its unified-memory SKU
/// cannot be identified by capacity fit-testing alone (see NVIDIA's fixed
/// SKU-to-recipe table). An explicitly requested model ID uses the same
/// capacity fit-test on every GPU, except when the request is the Spark SKU's
/// own recommendation round-tripped through setup, which keeps that SKU's
/// pinned profile and adapter binding.
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
        LocalInferenceRunProfile profile;
        LocalInferenceModelSelectionOrigin modelSelectionOrigin;
        string? boundGpuStableId = null;
        GpuInfo? sparkGpu = hardware.NvidiaGpus.FirstOrDefault(
            gpu => gpu.IsRtxSpark && LocalInferenceQualificationPolicy.HasCompleteFacts(gpu));
        var sparkPick = sparkGpu is null
            ? null
            : RtxSparkInferenceSelector.SelectDefault(sparkGpu);
        if (string.IsNullOrWhiteSpace(requestedModelId))
        {
            if (sparkPick is not null)
            {
                // Bind the plan to this adapter: the SKU table answers "what should
                // THIS Spark run", so the recipe is only valid on the Spark that
                // produced it, never on some other GPU eligibility might rank higher.
                (model, profile) = sparkPick.Value;
                boundGpuStableId = sparkGpu!.StableId;
            }
            else
            {
                // Either no Spark, or a Spark SKU with no recommended model. In the
                // latter case the Spark is excluded rather than failing the whole
                // host, so a discrete GPU alongside it can still qualify normally.
                HostHardwareInfo genericHardware = sparkGpu is null
                    ? hardware
                    : hardware with
                    {
                        Gpus = hardware.Gpus.Where(gpu => !gpu.IsRtxSpark).ToArray(),
                    };
                if (!genericHardware.HasNvidiaGpu)
                {
                    return LocalInferenceSelectionResult.Unsupported(
                        LocalInferenceSelectionFailureCode.NotRecommendedForSku);
                }

                (model, profile) = SelectDefaultModelAndProfile(genericHardware, runtime);
            }

            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Default;
        }
        else
        {
            model = LocalModelCatalog.Find(requestedModelId);
            if (model is null)
                return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.UnknownModel);
            if (sparkPick is { } recommended &&
                string.Equals(recommended.Model.Id, model.Id, StringComparison.OrdinalIgnoreCase))
            {
                // Setup persists the recommended model id and passes it back here, so
                // the SKU's own recommendation arrives as an explicit request. Re-deriving
                // its profile through the generic fit-test would silently discard the
                // pinned profile the SKU table specifies (the 64 GB tier's reduced context
                // is not the largest that merely fits) and drop the adapter binding.
                // A request for any other model is a real user override and still uses
                // the generic fit-test below.
                profile = recommended.Profile;
                boundGpuStableId = sparkGpu!.StableId;
            }
            else
            {
                profile = SelectBestFittingProfile(hardware, runtime, model) ??
                    LocalModelCatalog.GetProfiles(model)[^1];
            }

            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Explicit;
        }

        return LocalInferenceSelectionResult.Selected(
            new LocalInferencePlan(runtime, model, profile, modelSelectionOrigin, boundGpuStableId));
    }

    private static (LocalModelInfo Model, LocalInferenceRunProfile Profile) SelectDefaultModelAndProfile(
        HostHardwareInfo hardware,
        LlamaRuntimeVariant runtime)
    {
        // A priority-0, explicit-alternative model (currently only the
        // experimental 96GB Flash-Next recipe) is offered solely by
        // RtxSparkInferenceSelector for its one intended SKU, never picked as
        // a generic dGPU default or fallback -- excluded here so a large
        // enough non-Spark GPU (or a Spark GPU IsRtxSpark fails to detect)
        // can't land on it by tie-break/fallback ordering. Priority-0 models
        // that aren't explicit alternatives (the other Spark-only recipes)
        // keep their existing, unrelated reachability.
        IEnumerable<LocalModelInfo> genericDefaultCandidates = LocalModelCatalog.Models
            .Where(model => model.RecommendationPriority > 0 || !model.IsExplicitAlternative);
        foreach (LocalModelInfo candidate in genericDefaultCandidates
                     .OrderByDescending(model => model.RecommendationPriority)
                     .ThenByDescending(LocalModelCatalog.TotalDownloadSizeBytes))
        {
            LocalInferenceRunProfile? profile = SelectBestFittingProfile(hardware, runtime, candidate);
            if (profile is not null)
                return (candidate, profile);
        }

        LocalModelInfo fallback = genericDefaultCandidates
            .OrderBy(LocalModelCatalog.TotalDownloadSizeBytes)
            .First();
        return (fallback, LocalModelCatalog.GetProfiles(fallback)[^1]);
    }

    private static LocalInferenceRunProfile? SelectBestFittingProfile(
        HostHardwareInfo hardware,
        LlamaRuntimeVariant runtime,
        LocalModelInfo model) =>
        LocalModelCatalog.GetProfiles(model).FirstOrDefault(profile =>
            hardware.NvidiaGpus.Any(gpu =>
                LocalInferenceQualificationPolicy.HasRuntimePrerequisites(gpu, runtime) &&
                LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu) >=
                    LocalInferenceQualificationPolicy.GetRequiredMemoryBytes(model, profile)));
}

internal static class LocalInferenceQualificationPolicy
{
    public static bool HasCompleteFacts(GpuInfo gpu) =>
        IsStableGpuId(gpu.StableId) &&
        gpu.GpuVisibleMemoryBytes is > 0 &&
        gpu.CudaMajorVersion is not null;

    public static bool HasRuntimePrerequisites(GpuInfo gpu, LlamaRuntimeVariant runtime)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(runtime);
        return HasCompleteFacts(gpu) &&
            gpu.CudaMajorVersion >= runtime.CudaVersion.Major;
    }

    public static long GetRequiredMemoryBytes(
        LocalModelInfo model,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        long weightsBytes = SaturatingAdd(
            model.Weights.SizeBytes,
            model.Recipe.DraftWeights?.SizeBytes ?? 0);
        return SaturatingAdd(
            SaturatingAdd(
                SaturatingAdd(weightsBytes, GetKvCacheMemoryBytes(model.Recipe, profile)),
                GetDraftKvCacheMemoryBytes(model.Recipe, profile)),
            profile.RuntimeWorkspaceBytes);
    }

    internal static long GetKvCacheMemoryBytes(
        LocalModelRunRecipe recipe,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(profile);
        long vectorsPerTypePerToken = SaturatingMultiply(
            recipe.FullAttentionLayerCount,
            recipe.KeyValueHeadCount);
        long bytesPerToken = SaturatingAdd(
            SaturatingMultiply(
                vectorsPerTypePerToken,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.KeyCachePrecision)),
            SaturatingMultiply(
                vectorsPerTypePerToken,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.ValueCachePrecision)));
        return SaturatingMultiply(bytesPerToken, profile.ContextTokens);
    }

    internal static long GetDraftKvCacheMemoryBytes(
        LocalModelRunRecipe recipe,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(profile);

        if (recipe.SpeculativeDecoding == SpeculativeDecodingMode.None)
            return 0;

        // The pinned Qwen MTP and DFlash draft artifacts contain one draft
        // attention layer with the same KV head count and head dimension as
        // the target model.
        long bytesPerToken = SaturatingAdd(
            SaturatingMultiply(
                recipe.KeyValueHeadCount,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.DraftKeyCachePrecision)),
            SaturatingMultiply(
                recipe.KeyValueHeadCount,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.DraftValueCachePrecision)));
        return SaturatingMultiply(bytesPerToken, profile.ContextTokens);
    }

    public static long GetEffectiveTotalMemoryBytes(GpuInfo gpu) =>
        gpu.GpuVisibleMemoryBytes is > 0 ? gpu.GpuVisibleMemoryBytes.Value : 0;

    public static long? GetEffectiveFreeMemoryBytes(GpuInfo gpu) =>
        gpu.FreeGpuVisibleMemoryBytes is >= 0 ? gpu.FreeGpuVisibleMemoryBytes.Value : null;

    private static bool IsStableGpuId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static long EncodedBytes(long elementCount, KvCachePrecision precision) => precision switch
    {
        KvCachePrecision.F16 => SaturatingMultiply(elementCount, 2),
        KvCachePrecision.Q8_0 => SaturatingMultiply((SaturatingAdd(elementCount, 31)) / 32, 34),
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
