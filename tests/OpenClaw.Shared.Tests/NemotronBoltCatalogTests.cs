using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class NemotronBoltCatalogTests
{
    [Fact]
    public void Bolt_ResolvesPrereleasePinsOnlyForTheirInstalledSource()
    {
        var model = Assert.IsType<LocalModelInfo>(LocalModelCatalog.FindInstalled(
            "nemotron-3.5-bolt-74b-a9b-iq2-lilicorr", NemotronBoltPrereleaseFixture.Weights.DownloadUri.AbsoluteUri));

        Assert.Equal(NemotronBoltPrereleaseFixture.Weights, model.Weights);
        Assert.Equal(NemotronBoltPrereleaseFixture.DraftWeights, model.Recipe.DraftWeights);
        Assert.DoesNotContain(model, LocalModelCatalog.Models);
        Assert.DoesNotContain(model, LocalModelCatalog.ExplicitAlternatives);
        Assert.NotEqual(model, LocalModelCatalog.Find(model.Id));
        Assert.Same(LocalModelCatalog.Find(model.Id), LocalModelCatalog.FindInstalled(model.Id));
        Assert.Same(LocalModelCatalog.Find(model.Id), LocalModelCatalog.FindInstalled(model.Id, "https://example.invalid/model.gguf"));
        Assert.Equal(new[] { 262_144, 131_072 }, LocalModelCatalog.GetProfiles(model).Select(p => p.ContextTokens));
    }

    [Fact]
    public void Bolt_PinsIndependentlyVerifiedTargetAndDrafter()
    {
        var model = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId));
        AssertArtifact(model.Weights, "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-GGUF",
            "e0fecb69cb3e5e4c5e8a056fa2aec98c78e91246", "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB.gguf", 22_018_326_528,
            "91d63363e782c8a3d5f4be42816eba8e0d42dd2530bd07a7d0d2dc440599af75");
        var draft = Assert.IsType<PinnedArtifact>(model.Recipe.DraftWeights);
        AssertArtifact(draft, "nvidia/NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr-GGUF",
            "182d2987939e12f458298b5d5f6ef98912f4ad6c", "NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-LiLiCorr.gguf", 998_242_560,
            "080e1ea41c9562a7804dbf30c6fbd4d17e216c6b8ff666f88e6edb1816010194");
        Assert.Equal(23_016_569_088, LocalModelCatalog.TotalDownloadSizeBytes(model));
        Assert.Equal(draft, Assert.Single(LocalModelCatalog.AdditionalArtifacts(model)));
        Assert.False(model.IsDefault);
        Assert.True(model.IsExplicitAlternative);
        Assert.False(model.SupportsVision);
        Assert.Equal(0, model.RecommendationPriority);
    }

    [Fact]
    public void Bolt_UsesQualifiedGeometrySamplingAndBoundedSnapshots()
    {
        var recipe = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)).Recipe;
        Assert.Equal(SpeculativeDecodingMode.DraftDFlash, recipe.SpeculativeDecoding);
        Assert.Equal((8, 2, 128, 5), (recipe.FullAttentionLayerCount, recipe.KeyValueHeadCount,
            recipe.KeyValueHeadDimension, recipe.DraftAttentionLayerCount));
        Assert.Equal((2_048, 512, 1, 7), (recipe.BatchTokens, recipe.MicroBatchTokens,
            recipe.ParallelRequests, recipe.SpeculativeDraftMaxTokens));
        Assert.Equal((0, 2, 99), (recipe.PromptCacheMiB, recipe.MaximumContextCheckpoints, recipe.DraftGpuLayers));
        Assert.Equal(new ModelSamplingPreset(1.0, 40, 0.95, 0.05, 1.0, 0.0), recipe.Sampling);
        Assert.True(recipe.FlashAttention);
        Assert.True(recipe.OffloadAllLayers);
    }

    [Fact]
    public void Bolt_OffersOnlyQualified128KAnd256KProfiles()
    {
        var model = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId));
        var profiles = LocalModelCatalog.GetProfiles(model);
        Assert.Equal(new[] { 262_144, 131_072 }, profiles.Select(p => p.ContextTokens));
        foreach (var profile in profiles)
        {
            Assert.Equal(KvCachePrecision.Q8_0, profile.KeyCachePrecision);
            Assert.Equal(KvCachePrecision.Q8_0, profile.ValueCachePrecision);
            Assert.Equal(KvCachePrecision.F16, profile.DraftKeyCachePrecision);
            Assert.Equal(KvCachePrecision.F16, profile.DraftValueCachePrecision);
            Assert.Same(profile, LocalModelCatalog.FindProfile(model, profile.Id));
            Assert.Same(profile, LocalModelCatalog.FindProfile(model, profile.ContextTokens,
                profile.KeyCachePrecision, profile.ValueCachePrecision, profile.DraftKeyCachePrecision,
                profile.DraftValueCachePrecision));
            long measuredPeak = profile.ContextTokens == 262_144 ? 34_944_585_728 : 30_304_747_520;
            Assert.True(LocalInferenceEligibility.GetRequiredMemoryBytes(model, profile) >= measuredPeak + 2L * 1024 * 1024 * 1024);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Bolt_NeverBecomesDefaultOrFallback(long gib)
    {
        var hardware = new HostHardwareInfo(RuntimeArchitecture.Arm64, 200L * 1024 * 1024 * 1024,
            200L * 1024 * 1024 * 1024,
            new[] { new GpuInfo(GpuVendor.Nvidia, "NVIDIA arbitrary adapter", gib * 1024 * 1024 * 1024,
                gib * 1024 * 1024 * 1024, CudaMajorVersion: 13, StableId: "GPU-default") }, false);
        var result = LocalInferenceSelector.Select(hardware);
        Assert.NotNull(result.Plan);
        Assert.NotEqual(LocalModelCatalog.NemotronBoltModelId, result.Plan.Model.Id);
        Assert.NotEqual(LlamaRuntimeCatalog.NemotronBoltArm64RuntimeId, result.Plan.Runtime.Id);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, LocalModelCatalog.Default.Id);
    }

    private static void AssertArtifact(PinnedArtifact artifact, string repo, string revision, string file, long bytes, string sha)
    {
        Assert.Equal(repo, artifact.Source.RepositoryId);
        Assert.Equal(revision, artifact.Source.ImmutableRevision);
        Assert.Equal(file, artifact.RelativePath);
        Assert.Equal(bytes, artifact.SizeBytes);
        Assert.Equal(sha, artifact.Sha256.Value);
        Assert.Null(artifact.CatalogProvenance);
    }
}
