using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiRecipeOverridesTests
{
    [Fact]
    public async Task Build_MergesAllModelsThenModelSectionIntoGeneratedPreset()
    {
        LlamaServerRouterLaunchPlan launch = await BuildWithOverridesAsync(
            "[*]\n" +
            "batch-size = 2048\n" +
            "ubatch-size = 1024\n" +
            $"[{LocalModelCatalog.Qwen35BModelId}]\n" +
            "ubatch-size = 512\n" +
            "cache-reuse = 256\n" +
            "[some-other-model]\n" +
            "batch-size = 1\n");

        string[] preset = launch.PresetContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("batch-size = 2048", preset);
        Assert.Contains("ubatch-size = 512", preset);
        Assert.DoesNotContain("batch-size = 4096", preset);
        Assert.DoesNotContain("batch-size = 1", preset);
        Assert.DoesNotContain("ubatch-size = 1024", preset);
        Assert.True(
            Array.IndexOf(preset, "batch-size = 2048") < Array.IndexOf(preset, "flash-attn = on"),
            "An overridden generated key keeps its original position.");
        Assert.Equal("cache-reuse = 256", preset[^1]);
        Assert.Equal(
            ["batch-size", "ubatch-size", "cache-reuse"],
            launch.AppliedOverrides.Select(entry => entry.Key));
    }

    [Fact]
    public async Task Build_EmptyValueRemovesGeneratedKey()
    {
        LlamaServerRouterLaunchPlan launch = await BuildWithOverridesAsync(
            "[*]\nspec-draft-backend-sampling =\n");

        Assert.DoesNotContain(
            launch.PresetContent.Split(Environment.NewLine),
            line => line.StartsWith("spec-draft-backend-sampling", StringComparison.Ordinal));
        Assert.Null(Assert.Single(launch.AppliedOverrides).Value);
    }

    [Theory]
    [InlineData("[*]\nmodel = x", "line 2: 'model' cannot be overridden")]
    [InlineData("[*]\nctx-size = 4096", "'ctx-size' cannot be overridden")]
    [InlineData("[*]\nchat-template-file = x", "'chat-template-file' cannot be overridden")]
    [InlineData("[*]\ntools = all", "'tools' cannot be overridden")]
    [InlineData("[*]\nmcp-servers-json = {}", "'mcp-servers-json' cannot be overridden")]
    [InlineData("[*]\nlookup-cache-dynamic = x", "'lookup-cache-dynamic' cannot be overridden")]
    [InlineData("[*]\nno-webui = false", "'no-webui' cannot be overridden")]
    [InlineData("[*]\nspec-draft-hf = org/model", "'spec-draft-hf' cannot be overridden")]
    [InlineData("[*]\ndocker-repo = ai/model", "'docker-repo' cannot be overridden")]
    [InlineData("[*]\nkv-unified-per-slot = 4096", "'kv-unified-per-slot' cannot be overridden")]
    [InlineData("[*]\nparallel = 4", "'parallel' cannot be overridden")]
    [InlineData("[*]\nb = 2048", "short alias")]
    [InlineData("batch-size = 1", "line 1: add a [*] or [model-id] section")]
    [InlineData("[*]\nbatch-size = 1\nbatch-size = 2", "line 3: 'batch-size' is set more than once in [*]")]
    [InlineData("[*]\nbatch-size = 1\rmodel = x", "line 2: the value for 'batch-size' contains a control character")]
    public async Task Build_RejectsInvalidOverrideFile(string content, string expected)
    {
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => BuildWithOverridesAsync(content));

        Assert.StartsWith($"{LocalAiRecipeOverrides.FileName} line ", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static async Task<LlamaServerRouterLaunchPlan> BuildWithOverridesAsync(string content)
    {
        using var temp = new TempDirectory("local-ai-overrides-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LocalAiPortLifecycleTests.ValidManifest());
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        await File.WriteAllTextAsync(paths.RecipeOverridesPath, content);
        return LlamaServerRouterConfiguration.Build(paths, saved);
    }
}
