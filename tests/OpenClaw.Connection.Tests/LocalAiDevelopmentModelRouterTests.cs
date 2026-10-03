using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Guard-on tests mutate a process-wide environment variable, so they run alone
/// after every parallel collection has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalAiDevelopmentModelRouterCollection
{
    public const string Name = "Local AI development model router";
}

[Collection(LocalAiDevelopmentModelRouterCollection.Name)]
public sealed class LocalAiDevelopmentModelRouterTests
{
    /// <summary>
    /// A 9B receipt recorded under a reduced development profile launches only in a
    /// process that sees the guard; elsewhere it fails receipt validation instead of being remapped.
    /// </summary>
    [Fact]
    public async Task Router_LaunchesDevelopmentQwen9BReducedProfileOnlyWithGuard()
    {
        using var temp = new TempDirectory("local-ai-dev-model-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LocalAiPortLifecycleTests.LegacyQwen9BManifest() with
        {
            ContextLength = LocalModelCatalog.ReducedContextTokens,
        });
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        using var env = new EnvironmentScope(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, "1");

        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        string[] preset = launch.PresetContent.Split(Environment.NewLine);
        Assert.Contains("ctx-size = 131072", preset);
        Assert.Contains("cache-type-k = f16", preset);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, launch.ModelAlias);

        env.Set(LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable, null);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LlamaServerRouterConfiguration.Build(paths, saved));
        Assert.Contains("qualified catalog profile", error.Message, StringComparison.Ordinal);
    }
}
