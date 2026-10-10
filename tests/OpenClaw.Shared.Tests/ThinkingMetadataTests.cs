using System.Collections.Immutable;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class ThinkingMetadataTests
{
    private static ThinkingContext Read(string json, ThinkingContext? previous = null)
    {
        using var document = JsonDocument.Parse(json);
        return ThinkingMetadata.MergeSession(document.RootElement, previous);
    }

    [Theory]
    [InlineData("""{"thinkingDefault":"off"}""")]
    [InlineData("""{"thinkingLevels":[]}""")]
    [InlineData("""{"thinkingLevels":[{"id":"Future/Exact","label":"Provider choice"}],"thinkingDefault":"Future/Exact"}""")]
    [InlineData("""{"thinkingOptions":["off","high"],"thinkingDefault":"high"}""")]
    public void RepeatedMetadata_HasValueEqualityAndMatchingHashes(string json)
    {
        var first = Read(json);
        var repeated = Read(json);

        Assert.Equal(first.Profile, repeated.Profile);
        Assert.Equal(first, repeated);
        Assert.Equal(first.GetHashCode(), repeated.GetHashCode());
        Assert.Single(new HashSet<ThinkingProfile> { first.Profile!, repeated.Profile! });
    }

    [Theory]
    [InlineData("""{"thinkingLevels":[{"id":"low","label":"Low"},{"id":"high","label":"Deep"}],"thinkingDefault":"low"}""")]
    [InlineData("""{"thinkingLevels":[{"id":"LOW","label":"Low"},{"id":"high","label":"High"}],"thinkingDefault":"low"}""")]
    [InlineData("""{"thinkingLevels":[{"id":"high","label":"High"},{"id":"low","label":"Low"}],"thinkingDefault":"low"}""")]
    [InlineData("""{"thinkingLevels":[{"id":"low","label":"Low"},{"id":"high","label":"High"}],"thinkingDefault":"high"}""")]
    [InlineData("""{"thinkingLevels":[{"id":"low","label":"Low"},{"id":"high","label":"High"}]}""")]
    [InlineData("""{"thinkingLevels":[],"thinkingDefault":"low"}""")]
    [InlineData("""{"thinkingDefault":"low"}""")]
    public void ProfileEquality_PreservesLabelsIdsOrderDefaultAndPresence(string changed)
    {
        var original = Read("""
            {"thinkingLevels":[{"id":"low","label":"Low"},{"id":"high","label":"High"}],"thinkingDefault":"low"}
            """);
        var current = Read(changed);

        Assert.NotEqual(original.Profile, current.Profile);
        Assert.NotEqual(original, current);
        Assert.Equal(2, new HashSet<ThinkingProfile> { original.Profile!, current.Profile! }.Count);
    }

    [Fact]
    public void ProfileEquality_DistinguishesUnknownUninitializedAndAdvertisedEmptyLevels()
    {
        var unknown = new ThinkingProfile();
        var uninitialized = new ThinkingProfile(default(ImmutableArray<ThinkingLevelOption>));
        var empty = new ThinkingProfile([]);

        Assert.Equal(3, new HashSet<ThinkingProfile> { unknown, uninitialized, empty }.Count);
        Assert.Equal(uninitialized, new ThinkingProfile(default(ImmutableArray<ThinkingLevelOption>)));
        Assert.NotEqual(unknown, empty);
        Assert.False(unknown.Equals(null));
    }

    [Theory]
    [InlineData("on", "low")]
    [InlineData("none", "off")]
    [InlineData(" auto ", "adaptive")]
    [InlineData("Extra_High", "xhigh")]
    [InlineData("think-hard", "low")]
    [InlineData("ultrathink", "high")]
    [InlineData("think", "minimal")]
    [InlineData(" MED ", "medium")]
    [InlineData(" Future-Mode ", "future-mode")]
    public void LegacyAliases_UseOnlyPublicNormalization(string raw, string expected)
    {
        var context = Read(JsonSerializer.Serialize(new { thinkingOptions = new[] { raw } }));
        var option = Assert.Single(context.Profile!.Levels!.Value);
        Assert.Equal(expected, option.Id);
        Assert.Equal(raw, option.Label);
    }

    [Fact]
    public void StructuredLevels_PreserveOrderLabelsIdsAndDefaultWithoutLegacyFallback()
    {
        var context = Read("""
            {"thinkingLevels":[{"id":"Future/Exact","label":"Provider mode"},{"id":"off","label":"Disabled"}],
             "thinkingOptions":["low"],"thinkingDefault":"Future/Exact","agentRuntime":{"id":"native"}}
            """);
        Assert.Equal(["Future/Exact", "off"], context.Profile!.Levels!.Value.Select(level => level.Id));
        Assert.Equal(["Provider mode", "Disabled"], context.Profile.Levels.Value.Select(level => level.Label));
        Assert.Equal("Future/Exact", context.Profile.Default);
        Assert.Equal("native", context.Identity.RuntimeId);
        Assert.Empty(Read("""{"thinkingLevels":[],"thinkingOptions":["low"]}""").Profile!.Levels!.Value);
        Assert.Null(Read("""{"reasoning":true}""").Profile);
        Assert.Null(Read("""{"reasoning":false}""").Profile);
        Assert.Null(Read("""{"thinkingDefault":"off"}""").Profile!.Levels);
    }

    [Fact]
    public void OmissionPreservesPreparedProfileButExplicitArrayReplacesTheWholeProfile()
    {
        var previous = Read("""
            {"model":"m","modelProvider":"p","agentRuntime":{"id":"r"},
             "thinkingLevels":[{"id":"high","label":"High"}],"thinkingDefault":"high"}
            """);
        var sparse = Read("""{"thinkingDefault":"off"}""", previous);
        Assert.Equal("high", Assert.Single(sparse.Profile!.Levels!.Value).Id);
        Assert.Equal("off", sparse.Profile.Default);
        Assert.Null(sparse.Identity.Model);
        Assert.Null(sparse.Identity.Provider);
        Assert.Null(Read("""{"model":"other"}""", sparse).Profile);
        var empty = Read("""{"thinkingLevels":[]}""", previous);
        Assert.Empty(empty.Profile!.Levels!.Value);
        Assert.Null(empty.Profile.Default);
        Assert.Null(Read("{}", Read("""{"thinkingDefault":"high"}""")).Profile);
    }

    [Theory]
    [InlineData("""{"model":"other"}""")]
    [InlineData("""{"modelProvider":"other"}""")]
    [InlineData("""{"agentRuntime":{"id":"other"}}""")]
    [InlineData("""{"model":null}""")]
    [InlineData("""{"modelProvider":null}""")]
    [InlineData("""{"agentRuntime":null}""")]
    [InlineData("""{"sessionId":"other"}""")]
    [InlineData("""{"agentId":"other"}""")]
    public void IdentityAndScopeChanges_DoNotRetainAnotherProfile(string incoming)
    {
        var previous = Read("""
            {"model":"m","modelProvider":"p","agentRuntime":{"id":"r"},"sessionId":"s","agentId":"a",
             "thinkingLevels":[{"id":"off","label":"Off"}]}
            """);
        Assert.Null(Read(incoming, previous).Profile);
        Assert.Null(Read(incoming, Read("{}", previous)).Profile);
    }

    [Fact]
    public void ProfileRefresh_RetainsPreviouslyKnownIdentityWithoutFillingRawIdentity()
    {
        var context = Read("""{"model":"model-a","modelProvider":"provider","agentRuntime":{"id":"runtime-a"}}""");
        context = Read("{}", context);
        context = Read("""{"thinkingLevels":[{"id":"high","label":"High"}]}""", context);

        Assert.Equal(new ThinkingIdentity(), context.Identity);
        Assert.Null(Read("""{"model":"model-b"}""", context).Profile);
        Assert.Equal(new ThinkingIdentity("provider", "model-a", "runtime-a"), context.ProfileIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateIdentity_IsRetainedAcrossOmissionAndIdentityFreeProfileRefresh(bool refreshProfile)
    {
        var context = Read("""{"thinkingLevels":[{"id":"high","label":"High"}]}""");
        context = Read("""{"model":"model-a","modelProvider":"provider","agentRuntime":{"id":"runtime-a"}}""", context);
        context = Read("{}", context);
        if (refreshProfile)
            context = Read("""{"thinkingLevels":[{"id":"high","label":"High"}]}""", context);

        var changed = Read("""{"model":"model-b","modelProvider":"provider","agentRuntime":{"id":"runtime-b"}}""", context);

        Assert.Null(changed.Profile);
        Assert.Equal(new ThinkingIdentity("provider", "model-a", "runtime-a"), context.ProfileIdentity);
        Assert.Equal(new ThinkingIdentity(), context.Identity);
        Assert.Equal("high", Assert.Single(context.Profile!.Levels!.Value).Id);
    }

    [Theory]
    [InlineData("""{"model":"model-b"}""")]
    [InlineData("""{"modelProvider":"other-provider"}""")]
    [InlineData("""{"agentRuntime":{"id":"runtime-b"}}""")]
    public void PiecemealIdentity_AccumulatesOnlyInternalProvenance(string changed)
    {
        var context = Read("""{"thinkingLevels":[{"id":"high","label":"High"}]}""");
        context = Read("""{"model":"model-a"}""", context);
        context = Read("""{"modelProvider":"provider"}""", context);
        context = Read("""{"agentRuntime":{"id":"runtime-a"}}""", context);
        context = Read("""{"thinkingLevels":[]}""", context);
        context = Read("{}", context);

        Assert.Null(Read(changed, context).Profile);
        Assert.Equal(new ThinkingIdentity("provider", "model-a", "runtime-a"), context.ProfileIdentity);
        Assert.Equal(new ThinkingIdentity(), context.Identity);
        Assert.Empty(context.Profile!.Levels!.Value);
    }

    [Fact]
    public void ExplicitProfileForNewIdentity_ReplacesInsteadOfInheritingOldProvenance()
    {
        var previous = Read("""
            {"model":"model-a","modelProvider":"provider","agentRuntime":{"id":"runtime-a"},
             "thinkingLevels":[{"id":"high","label":"High"}],"thinkingDefault":"high"}
            """);
        var current = Read("""{"model":"model-b","thinkingLevels":[{"id":"future","label":"Future"}]}""", previous);

        Assert.Equal(new ThinkingIdentity(Model: "model-b"), current.ProfileIdentity);
        Assert.Equal("future", Assert.Single(current.Profile!.Levels!.Value).Id);
        Assert.Null(current.Profile.Default);
    }

    [Fact]
    public void CatalogParsingAndMerging_KeepPreparedMetadataWithoutUsingReasoningAsPolicy()
    {
        using var json = JsonDocument.Parse("""
            {"models":[
              {"id":"m","provider":"p","reasoning":false,"agentRuntime":{"id":"r"},
               "thinkingLevels":[{"id":"high","label":"Required"}],"thinkingDefault":"high"},
              {"id":"unknown","provider":"p","reasoning":true},
              {"id":"off-only","provider":"p","thinkingLevels":[{"id":"off","label":"Off"}]}]}
            """);
        var catalog = OpenClawGatewayClient.ParseModelsListPayload(json.RootElement);
        var configured = new ModelsListInfo { Models = [new ModelInfo { Id = "p/m" }] };
        var model = OpenClawGatewayClient.MergeModelCatalog(configured, catalog).Models[0];
        Assert.False(model.Reasoning);
        Assert.Equal(new ThinkingIdentity("p", "m", "r"), model.ThinkingContext!.Identity);
        Assert.Equal("high", Assert.Single(model.ThinkingContext.Profile!.Levels!.Value).Id);
        Assert.Null(catalog.Models[1].ThinkingContext!.Profile);
        Assert.Equal("off", Assert.Single(catalog.Models[2].ThinkingContext!.Profile!.Levels!.Value).Id);
        configured.Models[0].ThinkingContext = new(new("p", "m", "other"));
        Assert.Null(OpenClawGatewayClient.MergeModelCatalog(configured, catalog).Models[0].ThinkingContext!.Profile);
        configured.Models[0].ThinkingContext = new(new("p", "m", "r"), new([]));
        Assert.Empty(OpenClawGatewayClient.MergeModelCatalog(configured, catalog).Models[0].ThinkingContext!.Profile!.Levels!.Value);
    }
}
