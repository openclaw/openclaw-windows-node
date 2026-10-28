namespace OpenClaw.Shared.Inference.Catalog;

public static partial class LocalModelCatalog
{
    // Existing receipts keep their exact prerelease pins until explicitly reinstalled.
    // This recipe is never offered for fresh acquisition.
    private static readonly LocalModelInfo s_prereleaseNemotronBolt = CreateNemotronBolt(prerelease: true);

    private static LocalModelInfo CreateNemotronBolt(bool prerelease = false) => new(
        NemotronBoltModelId,
        "Nemotron 3.5 Bolt 74B-A9B (IQ2, LiLiCorr)",
        "Nemotron 3.5",
        "IQ2_XXS",
        new PinnedArtifact(
            NemotronBoltModelId,
            ArtifactRole.ModelWeights,
            new HuggingFaceRevisionSource(
                prerelease ? "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-GGUF"
                    : "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-GGUF",
                prerelease ? "06c7fae4665f61a83319ad98e2b00a2824a5f390" : "e0fecb69cb3e5e4c5e8a056fa2aec98c78e91246"),
            prerelease ? "super3.5-vl.gguf" : "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB.gguf",
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
                    prerelease ? "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-LiLiCorr-GGUF"
                        : "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr-GGUF",
                    prerelease ? "77844a4db96a62c0a6e14247af855b8ae8e2f89b" : "182d2987939e12f458298b5d5f6ef98912f4ad6c"),
                prerelease ? "originalLi5_final_step353859_NVFP4.gguf" : "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr.gguf",
                prerelease ? 998_242_176 : 998_242_560,
                new Sha256Digest(prerelease ? "9c4033517096cfca23ad9450044186204ad1ab35efa77e058870f6f61fc245e0"
                    : "080e1ea41c9562a7804dbf30c6fbd4d17e216c6b8ff666f88e6edb1816010194")),
            draftAttentionLayerCount: 5,
            // Saved prompt snapshots and serialized recurrent checkpoints can
            // exceed the KV cache itself when agent conversations switch.
            promptCacheMiB: 0,
            maximumContextCheckpoints: 2,
            draftGpuLayers: 99,
            // The prerelease pins never shipped a projector, so their receipts stay text-only.
            visionProjector: prerelease ? null : new PinnedArtifact(
                "nemotron-3.5-bolt-vision-projector",
                ArtifactRole.ModelWeights,
                new HuggingFaceRevisionSource(
                    "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-GGUF",
                    "e0fecb69cb3e5e4c5e8a056fa2aec98c78e91246"),
                "mmproj-NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB.gguf",
                1_733_692_736,
                new Sha256Digest("0ee891eadc7bf0cae6289b249c74eb1ef420534351f6ef8b64d49f0ca6a926b0"))),
        IsDefault: false,
        IsExplicitAlternative: true,
        SupportsVision: !prerelease,
        RecommendationPriority: 0,
        RuntimeFamily: LlamaRuntimeCatalog.NemotronBoltRuntimeFamily);

    private static LocalInferenceRunProfile[] CreateNemotronBoltProfiles() =>
    [
        NemotronBoltProfile(NativeContextTokens, 5L * 512 * 1024 * 1024),
        NemotronBoltProfile(ReducedContextTokens, 2L * 1024 * 1024 * 1024),
    ];

    private static LocalInferenceRunProfile NemotronBoltProfile(int contextTokens, long workspaceBytes) => new(
        $"ctx-{contextTokens}-q8_0-draft-f16",
        contextTokens,
        KvCachePrecision.Q8_0,
        KvCachePrecision.Q8_0,
        KvCachePrecision.F16,
        KvCachePrecision.F16,
        // Reserve for compute buffers and checkpoints above files and KV: 128K with vision needs about 28 GB.
        workspaceBytes);
}
