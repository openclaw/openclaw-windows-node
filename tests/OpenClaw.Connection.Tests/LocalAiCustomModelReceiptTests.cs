using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed partial class LocalAiPortLifecycleTests
{
    private const string CustomRevision = "50968a4468ef4233ed78cd7c3de230dd1d61a56b";

    private static readonly LocalCustomModelRecipe CustomRecipe = new(
        "unsloth/Qwen3-0.6B-GGUF", CustomRevision, "Qwen3-0.6B-Q4_K_M.gguf", 396_705_472,
        new string('c', 64), "Qwen3-0.6B-Q4_K_M", "qwen3", 28, 8, 128, 40_960);

    private static LocalAiInstallManifest CustomManifest(LocalAiPaths paths)
    {
        LocalModelInfo model = LocalCustomModel.ToModelInfo(CustomRecipe);
        string cacheRoot = Path.Combine(paths.LocalDataDirectory, "hf-cache");
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(cacheRoot, CustomRecipe.RepositoryId, CustomRevision,
            CustomRecipe.FilePath, out string cachedPath, out _, out string error), error);
        return ValidManifest() with
        {
            SchemaVersion = LocalAiInstallManifest.CustomModelSchemaVersion,
            ModelCatalogId = model.Id,
            ModelAlias = model.Id,
            ModelId = $"{CustomRecipe.RepositoryId}@{CustomRevision}",
            ModelPath = Path.Combine("models", CustomRecipe.FilePath),
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedPath,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = CustomRecipe.FilePath,
                SourceUrl = model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = CustomRecipe.SizeBytes,
                Sha256 = CustomRecipe.Sha256,
            },
            CustomModel = CustomRecipe,
            ContextLength = 40_960,
            KeyCachePrecision = KvCachePrecision.F16,
            ValueCachePrecision = KvCachePrecision.F16,
            DraftKeyCachePrecision = KvCachePrecision.F16,
            DraftValueCachePrecision = KvCachePrecision.F16,
        };
    }

    [Fact]
    public async Task CustomModelReceipt_RoundTripsAndLaunchesWithoutSpeculativeDraft()
    {
        using var temp = new TempDirectory("local-ai-custom-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest manifest = CustomManifest(paths);

        await store.SaveAsync(manifest);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;

        Assert.Equal(CustomRecipe, install.Manifest.CustomModel);
        LocalInferencePlan plan = LlamaServerRouterConfiguration.ResolveInstalledPlan(install);
        Assert.Equal(manifest.ModelCatalogId, plan.Model.Id);
        Assert.Equal("ctx-40960-f16", plan.Profile.Id);
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, install with
        {
            Endpoint = new Uri("http://127.0.0.1:28765/v1"),
        });
        Assert.Contains("ctx-size = 40960", launch.PresetContent);
        Assert.DoesNotContain("spec-type", launch.PresetContent);
        Assert.Equal($"llamacpp/{manifest.ModelAlias}", LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
    }

    [Fact]
    public void CustomModelReceipt_RejectsTamperedIdentityAndRecipeOnCatalogSchema()
    {
        using var temp = new TempDirectory("local-ai-custom-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest manifest = CustomManifest(paths);

        Assert.Throws<InvalidDataException>(() => store.ResolveAndValidate(manifest with { ModelAlias = "hf-0000000000000000" }));
        Assert.Throws<InvalidDataException>(() => store.ResolveAndValidate(manifest with
        {
            CustomModel = CustomRecipe with { SizeBytes = CustomRecipe.SizeBytes + 1 },
        }));
        Assert.Throws<InvalidDataException>(() => store.ResolveAndValidate(manifest with
        {
            SchemaVersion = LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
        }));
        Assert.Throws<InvalidDataException>(() => store.ResolveAndValidate(manifest with { CustomModel = null }));
    }

    [Fact]
    public async Task ReplaceInstall_WithdrawsWithOldReceiptThenCommitsNewModel()
    {
        using var temp = new TempDirectory("local-ai-replace-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_795), platform, new FakeClient(events), lifecycle);
        LocalAiInstallManifest replacement = CustomManifest(paths);

        LocalAiRuntimeSnapshot stopped = await runtime.ReplaceInstallAsync(replacement);

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(replacement.ModelCatalogId, stopped.ModelId);
        Assert.Contains("quiesce:Teardown", events);
        LocalAiResolvedInstall saved = (await new LocalAiManifestStore(paths).LoadAsync())!;
        Assert.Equal(CustomRecipe, saved.Manifest.CustomModel);
        Assert.Null(saved.Manifest.Endpoint);
    }

    [Fact]
    public async Task ReplaceInstall_KeepsReceiptWhenOldRouteCannotBeWithdrawn()
    {
        using var temp = new TempDirectory("local-ai-replace-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        byte[] before = await File.ReadAllBytesAsync(paths.ManifestPath);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events) { FailQuiesce = true };
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_796), platform, new FakeClient(events), lifecycle);

        await Assert.ThrowsAsync<LocalAiModelSwitchException>(() => runtime.ReplaceInstallAsync(CustomManifest(paths)));

        Assert.Equal(before, await File.ReadAllBytesAsync(paths.ManifestPath));
    }

    [Fact]
    public async Task ReplaceInstall_ManifestSaveFailureSettlesBindingBackToPreviousModel()
    {
        using var temp = new TempDirectory("local-ai-replace-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        string previousAlias = (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest.ModelAlias;
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        // The rebind lands, then the manifest commit fails, as when the process is interrupted.
        var lifecycle = new FakeLifecycle(events) { OnRebind = () => File.SetAttributes(paths.ManifestPath, FileAttributes.ReadOnly) };
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_797), platform, new FakeClient(events), lifecycle);
        LocalAiInstallManifest replacement = CustomManifest(paths);
        try
        {
            byte[] before = await File.ReadAllBytesAsync(paths.ManifestPath);
            await Assert.ThrowsAsync<LocalAiModelSwitchException>(() => runtime.ReplaceInstallAsync(replacement));

            Assert.Equal(before, await File.ReadAllBytesAsync(paths.ManifestPath));
            Assert.Equal(
                [$"rebind:{replacement.ModelAlias}", $"settle:{previousAlias}"],
                lifecycle.Rebinds.SkipWhile(e => !e.StartsWith("rebind:", StringComparison.Ordinal)));
        }
        finally
        {
            File.SetAttributes(paths.ManifestPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SetGatewayFallback_RejectedLifecycleLeavesManifestAndRetryRepairsBoth()
    {
        using var temp = new TempDirectory("local-ai-fallback-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        byte[] before = await File.ReadAllBytesAsync(paths.ManifestPath);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events) { FallbackException = new InvalidOperationException("pending") };
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_798), platform, new FakeClient(events), lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetGatewayFallbackModelAsync("openai/gpt-6"));
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.ManifestPath));

        lifecycle.FallbackException = null;
        await runtime.SetGatewayFallbackModelAsync("openai/gpt-6");
        // A same-value retry still reaches the lifecycle, so a binding left behind is repaired.
        await runtime.SetGatewayFallbackModelAsync("openai/gpt-6");

        Assert.Equal("openai/gpt-6", (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest.GatewayFallbackModel);
        Assert.Equal(3, events.Count(e => e == "fallback:openai/gpt-6"));
    }
}
