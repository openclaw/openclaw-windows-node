using System.Collections.ObjectModel;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Key/value cache storage precision passed to llama-server.</summary>
public enum KvCachePrecision
{
    F16 = 0,
}

/// <summary>Speculative decoding implementation used by a model recipe.</summary>
public enum SpeculativeDecodingMode
{
    DraftMtp = 0,
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
        int contextTokens,
        KvCachePrecision keyCachePrecision,
        KvCachePrecision valueCachePrecision,
        int batchTokens,
        int microBatchTokens,
        int parallelRequests,
        int fullAttentionLayerCount,
        int keyValueHeadCount,
        int keyValueHeadDimension,
        long runtimeWorkspaceBytes,
        bool flashAttention,
        bool offloadAllLayers,
        SpeculativeDecodingMode speculativeDecoding,
        int speculativeDraftMaxTokens,
        ModelSamplingPreset sampling)
    {
        if (contextTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
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
        if (runtimeWorkspaceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(runtimeWorkspaceBytes));
        if (speculativeDraftMaxTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(speculativeDraftMaxTokens));
        ArgumentNullException.ThrowIfNull(sampling);

        ContextTokens = contextTokens;
        KeyCachePrecision = keyCachePrecision;
        ValueCachePrecision = valueCachePrecision;
        BatchTokens = batchTokens;
        MicroBatchTokens = microBatchTokens;
        ParallelRequests = parallelRequests;
        FullAttentionLayerCount = fullAttentionLayerCount;
        KeyValueHeadCount = keyValueHeadCount;
        KeyValueHeadDimension = keyValueHeadDimension;
        RuntimeWorkspaceBytes = runtimeWorkspaceBytes;
        FlashAttention = flashAttention;
        OffloadAllLayers = offloadAllLayers;
        SpeculativeDecoding = speculativeDecoding;
        SpeculativeDraftMaxTokens = speculativeDraftMaxTokens;
        Sampling = sampling;
    }

    public int ContextTokens { get; }
    public KvCachePrecision KeyCachePrecision { get; }
    public KvCachePrecision ValueCachePrecision { get; }
    public int BatchTokens { get; }
    public int MicroBatchTokens { get; }
    public int ParallelRequests { get; }
    public int FullAttentionLayerCount { get; }
    public int KeyValueHeadCount { get; }
    public int KeyValueHeadDimension { get; }
    public long RuntimeWorkspaceBytes { get; }
    public bool FlashAttention { get; }
    public bool OffloadAllLayers { get; }
    public SpeculativeDecodingMode SpeculativeDecoding { get; }
    public int SpeculativeDraftMaxTokens { get; }
    public ModelSamplingPreset Sampling { get; }
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
    bool SupportsVision);

/// <summary>Immutable Hugging Face model pins offered by the Windows local inference flow.</summary>
public static class LocalModelCatalog
{
    public const string Qwen35BModelId = "qwen3.6-35b-a3b-mtp-q4-k-m";
    public const string Qwen27BModelId = "qwen3.6-27b-mtp-q4-k-m";
    public const string Qwen9BModelId = "qwen3.5-9b-mtp-q4-k-m";
    public const int NativeContextTokens = 262_144;

    // The pinned 262K MTP recipe also allocates draft KV, compute buffers,
    // recurrent state, and backend workspace beyond weights and primary KV.
    public const long RuntimeWorkspaceReserveBytes = 8L * 1024 * 1024 * 1024;

    private static readonly HuggingFaceRevisionSource s_qwen35BSource = new(
        "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
        "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d");

    private static readonly HuggingFaceRevisionSource s_qwen27BSource = new(
        "unsloth/Qwen3.6-27B-MTP-GGUF",
        "5cb35eb3dcbf52dbce5f87dbc64df6aaffadcace");

    private static readonly HuggingFaceRevisionSource s_qwen9BSource = new(
        "unsloth/Qwen3.5-9B-MTP-GGUF",
        "9716a636ee4bddc3fed678220b7a33dd2a4160ae");

    private static readonly ReadOnlyCollection<LocalModelInfo> s_models = Array.AsReadOnly(
        new[]
        {
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
                IsDefault: true,
                IsExplicitAlternative: false,
                SupportsVision: false),
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
                SupportsVision: false),
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
                IsExplicitAlternative: true,
                SupportsVision: false),
        });

    private static readonly ReadOnlyCollection<LocalModelInfo> s_explicitAlternatives =
        Array.AsReadOnly(s_models.Where(model => model.IsExplicitAlternative).ToArray());

    public static IReadOnlyList<LocalModelInfo> Models => s_models;

    public static LocalModelInfo Default => s_models.Single(model => model.IsDefault);

    public static IReadOnlyList<LocalModelInfo> ExplicitAlternatives => s_explicitAlternatives;

    public static LocalModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : s_models.SingleOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

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
        double temperature) =>
        new(
            contextTokens: NativeContextTokens,
            keyCachePrecision: KvCachePrecision.F16,
            valueCachePrecision: KvCachePrecision.F16,
            batchTokens: 4_096,
            microBatchTokens: 4_096,
            parallelRequests: 1,
            fullAttentionLayerCount: fullAttentionLayerCount,
            keyValueHeadCount: keyValueHeadCount,
            keyValueHeadDimension: 256,
            runtimeWorkspaceBytes: RuntimeWorkspaceReserveBytes,
            flashAttention: true,
            offloadAllLayers: true,
            speculativeDecoding: SpeculativeDecodingMode.DraftMtp,
            speculativeDraftMaxTokens: 3,
            sampling: new ModelSamplingPreset(
                Temperature: temperature,
                TopK: 20,
                TopP: 0.95,
                MinP: 0.0,
                RepetitionPenalty: 1.0,
                PresencePenalty: 0.0));
}
