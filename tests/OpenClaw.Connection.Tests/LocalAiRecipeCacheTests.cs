using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.Tests;

public class LocalAiRecipeCacheTests
{
    [Fact]
    public void Preset_UsesModelOwnedCacheAndDraftOffloadLimits()
    {
        LocalModelInfo original = LocalModelCatalog.Find(LocalModelCatalog.Qwen38_27B_DFlashModelId)!;
        LocalModelRunRecipe recipe = Recipe(0, 2, 99);
        LocalModelInfo model = original with { Recipe = recipe };

        string preset = LlamaServerRouterConfiguration.BuildPreset(model,
            LocalModelCatalog.GetProfiles(original)[0], @"C:\verified\target.gguf", @"C:\verified\draft.gguf");

        Assert.Contains("cache-ram = 0", preset.Split(Environment.NewLine));
        Assert.Contains("ctx-checkpoints = 2", preset.Split(Environment.NewLine));
        Assert.Contains("spec-draft-ngl = 99", preset.Split(Environment.NewLine));
    }

    [Fact]
    public void ExistingPreset_RetainsRuntimeCacheAndDraftOffloadDefaults()
    {
        LocalModelInfo model = LocalModelCatalog.Find(LocalModelCatalog.Qwen38_27B_DFlashModelId)!;

        string preset = LlamaServerRouterConfiguration.BuildPreset(model,
            LocalModelCatalog.GetProfiles(model)[0], @"C:\verified\target.gguf", @"C:\verified\draft.gguf");

        Assert.DoesNotContain("cache-ram =", preset);
        Assert.DoesNotContain("ctx-checkpoints =", preset);
        Assert.DoesNotContain("spec-draft-ngl =", preset);
    }

    [Theory]
    [InlineData(-1, 2, 99)]
    [InlineData(0, -1, 99)]
    [InlineData(0, 2, -1)]
    public void Recipe_RejectsNegativeAllocationLimits(int cacheMiB, int checkpoints, int draftGpuLayers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Recipe(cacheMiB, checkpoints, draftGpuLayers));
    }

    private static LocalModelRunRecipe Recipe(int cacheMiB, int checkpoints, int draftGpuLayers)
    {
        var original = LocalModelCatalog.Find(LocalModelCatalog.Qwen38_27B_DFlashModelId)!.Recipe;
        return new LocalModelRunRecipe(original.BatchTokens, original.MicroBatchTokens, original.ParallelRequests,
            original.FullAttentionLayerCount, original.KeyValueHeadCount, original.KeyValueHeadDimension,
            original.FlashAttention, original.OffloadAllLayers, original.SpeculativeDecoding,
            original.SpeculativeDraftMaxTokens, original.Sampling, original.DraftWeights,
            promptCacheMiB: cacheMiB, maximumContextCheckpoints: checkpoints, draftGpuLayers: draftGpuLayers);
    }
}
