namespace OpenClaw.Shared.Inference.Catalog;

public static partial class LocalModelCatalog
{
    private static LocalModelInfo CreateNemotronBolt() => new(
        NemotronBoltModelId,
        "Nemotron 3.5 Bolt 74B-A9B (IQ2, LiLiCorr)",
        "Nemotron 3.5",
        "IQ2_XXS",
        new PinnedArtifact(
            NemotronBoltModelId,
            ArtifactRole.ModelWeights,
            new HuggingFaceRevisionSource(
                "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-GGUF",
                "e0fecb69cb3e5e4c5e8a056fa2aec98c78e91246"),
            "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB.gguf",
            22_018_326_528,
            new Sha256Digest("91d63363e782c8a3d5f4be42816eba8e0d42dd2530bd07a7d0d2dc440599af75")),
        new LocalModelRunRecipe(
            batchTokens: 2_048,
            microBatchTokens: 512,
            parallelRequests: 1,
            fullAttentionLayerCount: 8,
            keyValueHeadCount: 2,
            keyValueHeadDimension: 128,
            flashAttention: true,
            offloadAllLayers: true,
            speculativeDecoding: SpeculativeDecodingMode.DraftDFlash,
            speculativeDraftMaxTokens: 7,
            sampling: new ModelSamplingPreset(1.0, 40, 0.95, 0.05, 1.0, 0.0),
            draftWeights: new PinnedArtifact(
                "nemotron-3.5-bolt-lilicorr-nvfp4",
                ArtifactRole.ModelWeights,
                new HuggingFaceRevisionSource(
                    "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr-GGUF",
                    "182d2987939e12f458298b5d5f6ef98912f4ad6c"),
                "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr.gguf",
                998_242_560,
                new Sha256Digest("080e1ea41c9562a7804dbf30c6fbd4d17e216c6b8ff666f88e6edb1816010194")),
            draftAttentionLayerCount: 5,
            // Saved prompt snapshots and serialized recurrent checkpoints can
            // exceed the KV cache itself when agent conversations switch.
            promptCacheMiB: 0,
            maximumContextCheckpoints: 2,
            draftGpuLayers: 99),
        IsDefault: false,
        IsExplicitAlternative: true,
        SupportsVision: false,
        RecommendationPriority: 0,
        RuntimeFamily: LlamaRuntimeCatalog.NemotronBoltRuntimeFamily);

    private static LocalInferenceRunProfile[] CreateNemotronBoltProfiles() =>
    [
        NemotronBoltProfile(NativeContextTokens, 12L * 1024 * 1024 * 1024),
        NemotronBoltProfile(ReducedContextTokens, 8L * 1024 * 1024 * 1024),
    ];

    private static LocalInferenceRunProfile NemotronBoltProfile(int contextTokens, long workspaceBytes) => new(
        $"ctx-{contextTokens}-q8_0-draft-f16",
        contextTokens,
        KvCachePrecision.Q8_0,
        KvCachePrecision.Q8_0,
        KvCachePrecision.F16,
        KvCachePrecision.F16,
        // RTX Spark N1X near-full prefill, generation, and tool/stream trials
        // peaked at 30.30 GB (128K) and 34.94 GB (256K) of Windows commit.
        // These reserves cover recurrent/compute/checkpoint state and retain
        // at least 2 GiB above the larger measured peak at each context.
        workspaceBytes);
}
