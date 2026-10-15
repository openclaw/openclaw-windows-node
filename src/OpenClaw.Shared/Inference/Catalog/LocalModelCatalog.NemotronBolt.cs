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
                "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-GGUF",
                "06c7fae4665f61a83319ad98e2b00a2824a5f390"),
            "super3.5-vl.gguf",
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
                    "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-LiLiCorr-GGUF",
                    "77844a4db96a62c0a6e14247af855b8ae8e2f89b"),
                "originalLi5_final_step353859_NVFP4.gguf",
                998_242_176,
                new Sha256Digest("9c4033517096cfca23ad9450044186204ad1ab35efa77e058870f6f61fc245e0")),
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
