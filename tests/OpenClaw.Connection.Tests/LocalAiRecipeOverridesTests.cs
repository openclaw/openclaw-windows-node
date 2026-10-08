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
    [InlineData("[*]\nmodel = x", "line 2: 'model' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nctx-size = 4096", "'ctx-size' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nchat-template-file = x", "'chat-template-file' is not an allowed llama-server tuning option")]
    [InlineData("[*]\ntools = all", "'tools' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nmcp-servers-json = {}", "'mcp-servers-json' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nlookup-cache-dynamic = x", "'lookup-cache-dynamic' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nno-webui = false", "'no-webui' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nspec-draft-hf = org/model", "'spec-draft-hf' is not an allowed llama-server tuning option")]
    [InlineData("[*]\ndocker-repo = ai/model", "'docker-repo' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nkv-unified-per-slot = 4096", "'kv-unified-per-slot' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nparallel = 4", "'parallel' is not an allowed llama-server tuning option")]
    [InlineData("[*]\nb = 2048", "'b' is not an allowed llama-server tuning option")]
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

    /// <summary>
    /// llama.cpp registers aliases well beyond the two-character ones (-ag is --agent, -hft is
    /// --hf-token) and its preset parser (common/preset.cpp) resolves every alias, so deny rules
    /// keyed on names are bypassable. Only canonical allowlisted tuning keys may pass, under any
    /// spelling, and a no- prefix is honored only for options with a real negated form.
    /// </summary>
    [Theory]
    [InlineData("[*]\nno-ag = false")]
    [InlineData("[*]\nag = true")]
    [InlineData("[*]\nagent = true")]
    [InlineData("[*]\nhft = secret-token")]
    [InlineData("[*]\nhf-token = secret-token")]
    public async Task Build_RejectsAliasesAndDeniedCanonicalNames(string content)
    {
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => BuildWithOverridesAsync(content));

        Assert.StartsWith($"{LocalAiRecipeOverrides.FileName} line 2: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not an allowed llama-server tuning option", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>no-</c> prefix is honored only for options llama.cpp defines a negated form for;
    /// negating anything else (including a leading dash) is rejected.
    /// </summary>
    [Theory]
    [InlineData("[*]\nno-temp = 0.5", "cannot be negated")]
    [InlineData("[*]\nno-batch-size = 2048", "cannot be negated")]
    [InlineData("[*]\n-no-ag = false", "is not a llama-server option name")]
    public async Task Build_RejectsInvalidNegationForms(string content, string expected)
    {
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => BuildWithOverridesAsync(content));

        Assert.StartsWith($"{LocalAiRecipeOverrides.FileName} line 2: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[*]\nno-context-shift = true")]
    [InlineData("[*]\nno-kv-offload = true")]
    [InlineData("[*]\ntemp = 0.5")]
    public async Task Build_AcceptsNegatableAndCanonicalTuningKeys(string content)
    {
        LlamaServerRouterLaunchPlan launch = await BuildWithOverridesAsync(content);

        Assert.NotEmpty(launch.AppliedOverrides);
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
