using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests;

public class LocalModelDraftCapacityTests
{
    [Theory]
    [InlineData(KvCachePrecision.F16, 671_088_640)]
    [InlineData(KvCachePrecision.Q8_0, 356_515_840)]
    public void DraftCache_AccountsForEveryAttentionLayer(KvCachePrecision precision, long expectedBytes)
    {
        LocalModelRunRecipe recipe = Recipe(draftAttentionLayerCount: 5);
        var profile = new LocalInferenceRunProfile("draft-capacity", 131_072,
            KvCachePrecision.Q8_0, KvCachePrecision.Q8_0, precision, precision, 1);

        Assert.Equal(expectedBytes, LocalInferenceQualificationPolicy.GetDraftKvCacheMemoryBytes(recipe, profile));
    }

    [Fact]
    public void ExistingRecipes_RetainSingleLayerDraftCapacity()
    {
        string[] existingModels = [LocalModelCatalog.Qwen38_27BModelId, LocalModelCatalog.Qwen35BModelId,
            LocalModelCatalog.Qwen27BModelId, LocalModelCatalog.Qwen38_27B_DFlashModelId];
        Assert.All(existingModels, id => Assert.Equal(1, LocalModelCatalog.Find(id)!.Recipe.DraftAttentionLayerCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Recipe_RejectsInvalidDraftLayerCount(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Recipe(count));
    }

    private static LocalModelRunRecipe Recipe(int draftAttentionLayerCount) => new(
        batchTokens: 2048,
        microBatchTokens: 512,
        parallelRequests: 1,
        fullAttentionLayerCount: 8,
        keyValueHeadCount: 2,
        keyValueHeadDimension: 128,
        flashAttention: true,
        offloadAllLayers: true,
        speculativeDecoding: SpeculativeDecodingMode.DraftDFlash,
        speculativeDraftMaxTokens: 7,
        sampling: new ModelSamplingPreset(1, 40, .95, .05, 1, 0),
        draftWeights: LocalModelCatalog.Find(LocalModelCatalog.Qwen38_27B_DFlashModelId)!.Recipe.DraftWeights,
        draftAttentionLayerCount: draftAttentionLayerCount);
}
