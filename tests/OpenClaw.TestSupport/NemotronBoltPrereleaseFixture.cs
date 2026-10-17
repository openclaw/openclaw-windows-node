using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.TestSupport;

/// <summary>Fixed artifact pins from Bolt receipts written before the public release.</summary>
public static class NemotronBoltPrereleaseFixture
{
    public static PinnedArtifact Weights { get; } = new(
        "nemotron-3.5-bolt-74b-a9b-iq2-lilicorr",
        ArtifactRole.ModelWeights,
        new HuggingFaceRevisionSource(
            "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-GGUF",
            "06c7fae4665f61a83319ad98e2b00a2824a5f390"),
        "super3.5-vl.gguf",
        22_018_326_528,
        new Sha256Digest("91d63363e782c8a3d5f4be42816eba8e0d42dd2530bd07a7d0d2dc440599af75"));

    public static PinnedArtifact DraftWeights { get; } = new(
        "nemotron-3.5-bolt-lilicorr-nvfp4",
        ArtifactRole.ModelWeights,
        new HuggingFaceRevisionSource(
            "NVIDIA-Nemotron/Nemotron-3.5-Bolt-74B-A9B-IQ2-LiLiCorr-GGUF",
            "77844a4db96a62c0a6e14247af855b8ae8e2f89b"),
        "originalLi5_final_step353859_NVFP4.gguf",
        998_242_176,
        new Sha256Digest("9c4033517096cfca23ad9450044186204ad1ab35efa77e058870f6f61fc245e0"));
}
