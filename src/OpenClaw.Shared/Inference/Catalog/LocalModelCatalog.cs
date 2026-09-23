using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Key/value cache storage precision passed to llama-server.</summary>
public enum KvCachePrecision
{
    F16 = 0,
    Q8_0 = 1,
}

/// <summary>Speculative decoding implementation used by a model recipe.</summary>
public enum SpeculativeDecodingMode
{
    DraftMtp = 0,
    /// <summary>No speculative decoding; the target model runs standalone.</summary>
    None = 1,
    /// <summary>Draft-flash decoding using a separate, independently pinned draft checkpoint.</summary>
    DraftDFlash = 2,
}

/// <summary>Sampling values recommended for the model's thinking mode.</summary>
public sealed record ModelSamplingPreset(
    double Temperature,
    int TopK,
    double TopP,
    double MinP,
    double RepetitionPenalty,
    double PresencePenalty);

/// <summary>Model-owned llama-server settings that affect capacity or output behavior.</summary>
public sealed record LocalModelRunRecipe
{
    public LocalModelRunRecipe(
        int batchTokens,
        int microBatchTokens,
        int parallelRequests,
        int fullAttentionLayerCount,
        int keyValueHeadCount,
        int keyValueHeadDimension,
        bool flashAttention,
        bool offloadAllLayers,
        SpeculativeDecodingMode speculativeDecoding,
        int speculativeDraftMaxTokens,
        ModelSamplingPreset sampling,
        PinnedArtifact? draftWeights = null)
    {
        if (speculativeDecoding == SpeculativeDecodingMode.DraftDFlash && draftWeights is null)
            throw new ArgumentException("Draft-flash decoding requires a pinned draft checkpoint.", nameof(draftWeights));
        if (speculativeDecoding != SpeculativeDecodingMode.DraftDFlash && draftWeights is not null)
            throw new ArgumentException("Only draft-flash decoding uses a separate draft checkpoint.", nameof(draftWeights));
        if (batchTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchTokens));
        if (microBatchTokens <= 0 || microBatchTokens > batchTokens)
            throw new ArgumentOutOfRangeException(nameof(microBatchTokens));
        if (parallelRequests <= 0)
            throw new ArgumentOutOfRangeException(nameof(parallelRequests));
        if (fullAttentionLayerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(fullAttentionLayerCount));
        if (keyValueHeadCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(keyValueHeadCount));
        if (keyValueHeadDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(keyValueHeadDimension));
        if (speculativeDraftMaxTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(speculativeDraftMaxTokens));
        ArgumentNullException.ThrowIfNull(sampling);

        BatchTokens = batchTokens;
        MicroBatchTokens = microBatchTokens;
        ParallelRequests = parallelRequests;
        FullAttentionLayerCount = fullAttentionLayerCount;
        KeyValueHeadCount = keyValueHeadCount;
        KeyValueHeadDimension = keyValueHeadDimension;
        FlashAttention = flashAttention;
        OffloadAllLayers = offloadAllLayers;
        SpeculativeDecoding = speculativeDecoding;
        SpeculativeDraftMaxTokens = speculativeDraftMaxTokens;
        Sampling = sampling;
        DraftWeights = draftWeights;
    }

    public int BatchTokens { get; }
    public int MicroBatchTokens { get; }
    public int ParallelRequests { get; }
    public int FullAttentionLayerCount { get; }
    public int KeyValueHeadCount { get; }
    public int KeyValueHeadDimension { get; }
    public bool FlashAttention { get; }
    public bool OffloadAllLayers { get; }
    public SpeculativeDecodingMode SpeculativeDecoding { get; }
    public int SpeculativeDraftMaxTokens { get; }
    public ModelSamplingPreset Sampling { get; }
    /// <summary>The independently pinned draft checkpoint, set only for <see cref="SpeculativeDecodingMode.DraftDFlash"/>.</summary>
    public PinnedArtifact? DraftWeights { get; }
}

/// <summary>A downloadable GGUF model and its deterministic llama-server recipe.</summary>
public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    string Family,
    string Quantization,
    PinnedArtifact Weights,
    LocalModelRunRecipe Recipe,
    bool IsDefault,
    bool IsExplicitAlternative,
    bool SupportsVision,
    int RecommendationPriority = 0);

/// <summary>The capacity-sensitive settings selected for one model launch.</summary>
public sealed record LocalInferenceRunProfile
{
    public LocalInferenceRunProfile(
        string id,
        int contextTokens,
        KvCachePrecision keyCachePrecision,
        KvCachePrecision valueCachePrecision,
        KvCachePrecision draftKeyCachePrecision,
        KvCachePrecision draftValueCachePrecision,
        long runtimeWorkspaceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (contextTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        if (runtimeWorkspaceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(runtimeWorkspaceBytes));

        Id = id;
        ContextTokens = contextTokens;
        KeyCachePrecision = keyCachePrecision;
        ValueCachePrecision = valueCachePrecision;
        DraftKeyCachePrecision = draftKeyCachePrecision;
        DraftValueCachePrecision = draftValueCachePrecision;
        RuntimeWorkspaceBytes = runtimeWorkspaceBytes;
    }

    public string Id { get; }
    public int ContextTokens { get; }
    public KvCachePrecision KeyCachePrecision { get; }
    public KvCachePrecision ValueCachePrecision { get; }
    public KvCachePrecision DraftKeyCachePrecision { get; }
    public KvCachePrecision DraftValueCachePrecision { get; }
    public long RuntimeWorkspaceBytes { get; }
}

/// <summary>Immutable Hugging Face model pins offered by the Windows local inference flow.</summary>
public static class LocalModelCatalog
{
    public const string Qwen38_27BModelId = "qwen3.8-27b-mtp-ud-q4-k-m";
    public const string Qwen35BModelId = "qwen3.6-35b-a3b-mtp-q4-k-m";
    public const string Qwen27BModelId = "qwen3.6-27b-mtp-q4-k-m";
    /// <summary>
    /// Retired from new installs. Retained only so an already-installed managed
    /// Qwen3.5 9B receipt keeps resolving and launching across upgrade.
    /// </summary>
    public const string Qwen9BModelId = "qwen3.5-9b-mtp-q4-k-m";
    /// <summary>RTX Spark 48GB-SKU recipe. Never offered on the generic dGPU path; see <c>RtxSparkInferenceSelector</c>.</summary>
    public const string Qwen35B_IQ4XSModelId = "qwen3.6-35b-a3b-mtp-ud-iq4-xs";
    /// <summary>RTX Spark 128GB-SKU default recipe. Never offered on the generic dGPU path; see <c>RtxSparkInferenceSelector</c>.</summary>
    public const string Qwen38_27B_DFlashModelId = "qwen3.8-27b-dflash-ud-q4-k-m";
    public const int NativeContextTokens = 262_144;
    public const int IntermediateContextTokens = 196_608;
    public const int ReducedContextTokens = 131_072;
    public const int MinimumContextTokens = 65_536;
    /// <summary>RTX Spark 48GB-SKU context tier (98,304 tokens); see <see cref="Qwen35B_IQ4XSModelId"/>.</summary>
    public const int RtxSpark48GbContextTokens = 98_304;

    // Measured-conservative allowances for compute buffers, recurrent state,
    // CUDA graphs, allocator alignment, and miscellaneous backend allocations.
    // These buffers shrink with context size; the tiers retain at least about
    // 0.5 GiB of guard over the corresponding RTX 5090 peak measurements.
    public const long RuntimeWorkspaceReserveBytes = 8L * 1024 * 1024 * 1024;
    public const long IntermediateContextWorkspaceReserveBytes = 7L * 1024 * 1024 * 1024;
    public const long ReducedContextWorkspaceReserveBytes = 5L * 1024 * 1024 * 1024;
    public const long MinimumContextWorkspaceReserveBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HuggingFaceRevisionSource s_qwen38_27BSource = new(
        "unsloth/Qwen3.8-27B-GGUF",
        "313447f257f7ebde0b968e4778feef774546ed81");

    private static readonly HuggingFaceRevisionSource s_qwen35BSource = new(
        "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
        "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d");

    private static readonly HuggingFaceRevisionSource s_qwen27BSource = new(
        "unsloth/Qwen3.6-27B-MTP-GGUF",
        "5cb35eb3dcbf52dbce5f87dbc64df6aaffadcace");

    private static readonly HuggingFaceRevisionSource s_qwen9BSource = new(
        "unsloth/Qwen3.5-9B-MTP-GGUF",
        "9716a636ee4bddc3fed678220b7a33dd2a4160ae");

    private static readonly HuggingFaceRevisionSource s_qwen38_27BDFlashDraftSource = new(
        "z-lab/Qwen3.8-27B-DFlash2-GGUF",
        "2d9571f8ce46e151f61c6499c99dee6079e1d610");

    private static readonly ReadOnlyCollection<LocalModelInfo> s_models = Array.AsReadOnly(
        new[]
        {
            new LocalModelInfo(
                Qwen38_27BModelId,
                "Qwen3.8 27B (UD-Q4_K_M)",
                "Qwen3.8",
                "UD-Q4_K_M",
                ModelArtifact(
                    Qwen38_27BModelId,
                    s_qwen38_27BSource,
                    "Qwen3.8-27B-UD-Q4_K_M.gguf",
                    16_464_440_224,
                    "322e194ff79741c7baa497c240f677f54b201b0efab44ca8e50f122b39123482"),
                Recipe(
                    fullAttentionLayerCount: 16,
                    keyValueHeadCount: 4,
                    temperature: 1.0),
                IsDefault: true,
                IsExplicitAlternative: false,
                SupportsVision: false,
                RecommendationPriority: 400),
            new LocalModelInfo(
                Qwen35BModelId,
                "Qwen3.6 35B-A3B (UD-Q4_K_M)",
                "Qwen3.6",
                "Q4_K_M",
                ModelArtifact(
                    Qwen35BModelId,
                    s_qwen35BSource,
                    "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                    22_663_387_424,
                    "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b"),
                Recipe(
                    fullAttentionLayerCount: 10,
                    keyValueHeadCount: 2,
                    temperature: 0.6),
                IsDefault: false,
                IsExplicitAlternative: true,
                SupportsVision: false,
                RecommendationPriority: 300),
            new LocalModelInfo(
                Qwen27BModelId,
                "Qwen3.6 27B (Q4_K_M)",
                "Qwen3.6",
                "Q4_K_M",
                ModelArtifact(
                    Qwen27BModelId,
                    s_qwen27BSource,
                    "Qwen3.6-27B-Q4_K_M.gguf",
                    17_106_773_120,
                    "a7cbd3ecc0e3f9b333edee61ae66bc87ed713c5d49587a8355814722ed329e0f"),
                Recipe(
                    fullAttentionLayerCount: 16,
                    keyValueHeadCount: 4,
                    temperature: 1.0),
                IsDefault: false,
                IsExplicitAlternative: true,
                SupportsVision: false,
                RecommendationPriority: 200),
            // RTX Spark SKU recipes below. RecommendationPriority: 0 keeps them
            // out of the generic dGPU default pick; only RtxSparkInferenceSelector
            // offers them, keyed off the detected Spark unified-memory SKU.
            new LocalModelInfo(
                Qwen35B_IQ4XSModelId,
                "Qwen3.6 35B-A3B (UD-IQ4_XS)",
                "Qwen3.6",
                "UD-IQ4_XS",
                ModelArtifact(
                    Qwen35B_IQ4XSModelId,
                    s_qwen35BSource,
                    "Qwen3.6-35B-A3B-UD-IQ4_XS.gguf",
                    18_209_036_576,
                    "df27a780435b7b45c2597536112ea3cb091f8544c3d0c3318d9f4258b31f7adf"),
                Recipe(
                    fullAttentionLayerCount: 10,
                    keyValueHeadCount: 2,
                    temperature: 0.6,
                    speculativeDraftMaxTokens: 2),
                IsDefault: false,
                IsExplicitAlternative: false,
                SupportsVision: false,
                RecommendationPriority: 0),
            new LocalModelInfo(
                Qwen38_27B_DFlashModelId,
                "Qwen3.8 27B (UD-Q4_K_M, DFlash)",
                "Qwen3.8",
                "UD-Q4_K_M",
                ModelArtifact(
                    Qwen38_27B_DFlashModelId,
                    s_qwen38_27BSource,
                    "Qwen3.8-27B-UD-Q4_K_M.gguf",
                    16_464_440_224,
                    "322e194ff79741c7baa497c240f677f54b201b0efab44ca8e50f122b39123482"),
                Recipe(
                    fullAttentionLayerCount: 16,
                    keyValueHeadCount: 4,
                    temperature: 1.0,
                    batchTokens: 4_096,
                    microBatchTokens: 512,
                    speculativeDecoding: SpeculativeDecodingMode.DraftDFlash,
                    speculativeDraftMaxTokens: 7,
                    draftWeights: ModelArtifact(
                        "qwen3.8-27b-dflash2-q4-k-m",
                        s_qwen38_27BDFlashDraftSource,
                        "Qwen3.8-27B-DFlash2-Q4_K_M.gguf",
                        1_143_006_816,
                        "1a25c56858e1ebe93f2718ac1d49d1151f9323325c1bbfd6209370f4db131ebd")),
                IsDefault: false,
                IsExplicitAlternative: false,
                SupportsVision: false,
                RecommendationPriority: 0),
        });

    // Retired from new installs and never offered, recommended, or selectable.
    // These entries exist only so an existing managed installation keeps
    // resolving its own pinned receipt and launching after upgrade. Pins are
    // reproduced exactly as they were installed; nothing is remapped.
    private static readonly ReadOnlyCollection<LocalModelInfo> s_legacyModels = Array.AsReadOnly(
        new[]
        {
            new LocalModelInfo(
                Qwen9BModelId,
                "Qwen3.5 9B (Q4_K_M)",
                "Qwen3.5",
                "Q4_K_M",
                ModelArtifact(
                    Qwen9BModelId,
                    s_qwen9BSource,
                    "Qwen3.5-9B-Q4_K_M.gguf",
                    5_868_826_976,
                    "e8dd94817e95d6c0939102049d068418269978377b13616c4726235e232841fe"),
                Recipe(
                    fullAttentionLayerCount: 8,
                    keyValueHeadCount: 4,
                    temperature: 1.0),
                IsDefault: false,
                IsExplicitAlternative: false,
                SupportsVision: false,
                RecommendationPriority: 0),
        });

    private static readonly IReadOnlyDictionary<string, ReadOnlyCollection<LocalInferenceRunProfile>>
        s_profilesByModel = s_models
            .Select(model => (model, profiles: Array.AsReadOnly(
                string.Equals(model.Id, Qwen35B_IQ4XSModelId, StringComparison.Ordinal)
                    ? CreateRtxSpark48GbProfiles(model)
                    : CreateProfiles(model))))
            .Concat(s_legacyModels
                .Select(model => (model, profiles: Array.AsReadOnly(CreateLegacyProfiles(model)))))
            .ToDictionary(
                entry => entry.model.Id,
                entry => entry.profiles,
                StringComparer.OrdinalIgnoreCase);

    private static readonly ReadOnlyCollection<LocalModelInfo> s_explicitAlternatives =
        Array.AsReadOnly(s_models.Where(model => model.IsExplicitAlternative).ToArray());

    public static IReadOnlyList<LocalModelInfo> Models => s_models;

    public static LocalModelInfo Default => s_models.Single(model => model.IsDefault);

    public static IReadOnlyList<LocalModelInfo> ExplicitAlternatives => s_explicitAlternatives;

    public static IReadOnlyList<LocalInferenceRunProfile> GetProfiles(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return s_profilesByModel.TryGetValue(model.Id, out ReadOnlyCollection<LocalInferenceRunProfile>? profiles)
            ? profiles
            : throw new ArgumentException("The model is not part of the local inference catalog.", nameof(model));
    }

    public static LocalInferenceRunProfile? FindProfile(LocalModelInfo model, string? profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? null
            : GetProfiles(model).SingleOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public static LocalInferenceRunProfile? FindProfile(
        LocalModelInfo model,
        int contextTokens,
        KvCachePrecision keyCachePrecision,
        KvCachePrecision valueCachePrecision,
        KvCachePrecision draftKeyCachePrecision,
        KvCachePrecision draftValueCachePrecision) =>
        GetProfiles(model).SingleOrDefault(profile =>
            profile.ContextTokens == contextTokens &&
            profile.KeyCachePrecision == keyCachePrecision &&
            profile.ValueCachePrecision == valueCachePrecision &&
            profile.DraftKeyCachePrecision == draftKeyCachePrecision &&
            profile.DraftValueCachePrecision == draftValueCachePrecision);

    public static string ToLlamaServerCacheType(KvCachePrecision precision) => precision switch
    {
        KvCachePrecision.F16 => "f16",
        KvCachePrecision.Q8_0 => "q8_0",
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };

    public static string ToDisplayCacheType(KvCachePrecision precision) => precision switch
    {
        KvCachePrecision.F16 => "F16",
        KvCachePrecision.Q8_0 => "Q8_0",
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };

    public static LocalModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : s_models.SingleOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a model that an existing installation receipt may reference,
    /// including retired entries that are no longer offered for new installs.
    /// Use this only on installed-receipt validation, launch, and display
    /// paths. Selection, recommendation, and eligibility must keep using
    /// <see cref="Find"/> and <see cref="Models"/> so retired models are never
    /// offered again.
    /// </summary>
    public static LocalModelInfo? FindInstalled(string? id) =>
        Find(id) ??
        (string.IsNullOrWhiteSpace(id)
            ? null
            : s_legacyModels.SingleOrDefault(model =>
                string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when the id resolves only to a retired catalog entry.</summary>
    public static bool IsLegacy(string? id) => Find(id) is null && FindInstalled(id) is not null;

    /// <summary>
    /// The recipe's additional pinned artifacts beyond its primary weights, in the
    /// fixed order every acquirer, manifest, and launch path must agree on. Today that
    /// is the DFlash draft checkpoint, when the recipe pins one.
    /// </summary>
    public static ImmutableArray<PinnedArtifact> AdditionalArtifacts(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Recipe.DraftWeights is { } draftWeights
            ? [draftWeights]
            : ImmutableArray<PinnedArtifact>.Empty;
    }

    private static LocalInferenceRunProfile[] CreateProfiles(LocalModelInfo model) =>
    [
        Profile(model, NativeContextTokens, KvCachePrecision.F16),
        Profile(model, NativeContextTokens, KvCachePrecision.Q8_0),
        Profile(model, IntermediateContextTokens, KvCachePrecision.F16),
        Profile(model, IntermediateContextTokens, KvCachePrecision.Q8_0),
        Profile(model, ReducedContextTokens, KvCachePrecision.F16),
        Profile(model, ReducedContextTokens, KvCachePrecision.Q8_0),
        Profile(model, MinimumContextTokens, KvCachePrecision.F16),
        Profile(model, MinimumContextTokens, KvCachePrecision.Q8_0),
    ];

    // A retired model can only ever have been installed under the single
    // pre-profile recipe: native context with F16 KV. Exposing exactly that
    // profile keeps the existing receipt launchable while any other
    // combination still fails receipt validation instead of being remapped.
    private static LocalInferenceRunProfile[] CreateLegacyProfiles(LocalModelInfo model) =>
    [
        Profile(model, NativeContextTokens, KvCachePrecision.F16),
    ];

    // The RTX Spark 48GB-SKU recipe launches at a single fixed context/KV
    // tier (98,304 tokens, F16 KV) rather than the shared cross-product of
    // tiers other models expose.
    private static LocalInferenceRunProfile[] CreateRtxSpark48GbProfiles(LocalModelInfo model) =>
    [
        Profile(model, RtxSpark48GbContextTokens, KvCachePrecision.F16),
    ];

    private static LocalInferenceRunProfile Profile(
        LocalModelInfo model,
        int contextTokens,
        KvCachePrecision precision)
    {
        long workspaceBytes = contextTokens switch
        {
            NativeContextTokens => RuntimeWorkspaceReserveBytes,
            IntermediateContextTokens => IntermediateContextWorkspaceReserveBytes,
            ReducedContextTokens => ReducedContextWorkspaceReserveBytes,
            RtxSpark48GbContextTokens => ReducedContextWorkspaceReserveBytes,
            MinimumContextTokens => MinimumContextWorkspaceReserveBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(contextTokens)),
        };
        return new LocalInferenceRunProfile(
            $"ctx-{contextTokens}-{ToLlamaServerCacheType(precision)}",
            contextTokens,
            precision,
            precision,
            precision,
            precision,
            workspaceBytes);
    }

    private static PinnedArtifact ModelArtifact(
        string id,
        HuggingFaceRevisionSource source,
        string fileName,
        long sizeBytes,
        string sha256) =>
        new(
            id,
            ArtifactRole.ModelWeights,
            source,
            fileName,
            sizeBytes,
            new Sha256Digest(sha256),
            LocalInferenceCatalogProvenance.NvidiaCair);

    private static LocalModelRunRecipe Recipe(
        int fullAttentionLayerCount,
        int keyValueHeadCount,
        double temperature,
        int batchTokens = 4_096,
        int microBatchTokens = 4_096,
        SpeculativeDecodingMode speculativeDecoding = SpeculativeDecodingMode.DraftMtp,
        int speculativeDraftMaxTokens = 3,
        PinnedArtifact? draftWeights = null) =>
        new(
            batchTokens: batchTokens,
            microBatchTokens: microBatchTokens,
            parallelRequests: 1,
            fullAttentionLayerCount: fullAttentionLayerCount,
            keyValueHeadCount: keyValueHeadCount,
            keyValueHeadDimension: 256,
            flashAttention: true,
            offloadAllLayers: true,
            speculativeDecoding: speculativeDecoding,
            speculativeDraftMaxTokens: speculativeDraftMaxTokens,
            sampling: new ModelSamplingPreset(
                Temperature: temperature,
                TopK: 20,
                TopP: 0.95,
                MinP: 0.0,
                RepetitionPenalty: 1.0,
                PresencePenalty: 0.0),
            draftWeights: draftWeights);
}
