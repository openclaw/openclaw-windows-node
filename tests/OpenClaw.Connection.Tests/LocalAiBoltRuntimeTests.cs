using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Connection.Tests;

public sealed partial class LocalAiPortLifecycleTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    public async Task BoltStartup_RejectsInsufficientOrUnknownBudgetWithoutChangingReceipt(long? commit)
    {
        using var temp = new TempDirectory("bolt-runtime-");
        var paths = await PrepareBoltInstallAsync(temp, 262144);
        string receipt = await File.ReadAllTextAsync(paths.ManifestPath);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, 28769);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events),
            new FakeLifecycle(events), modelFileVerifier: new AcceptingBoltVerifier(),
            hardwareProbe: new BoltHardwareProbe(commit));

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.DoesNotContain("start", events);
        Assert.Equal(receipt, await File.ReadAllTextAsync(paths.ManifestPath));
    }

    [Fact]
    public async Task BoltStartup_DoesNotDowngrade256KWhenOnly128KFits()
    {
        var model = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
        var minimum = LocalModelCatalog.GetProfiles(model).Single(p => p.ContextTokens == 131072);
        long commit = LocalInferenceEligibility.GetRequiredMemoryBytes(model, minimum);
        using var temp = new TempDirectory("bolt-runtime-pressure-");
        var paths = await PrepareBoltInstallAsync(temp, 262144);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        await using var runtime = CreateRuntime(paths, new FakeProcessHost(platform, events, 28769),
            platform, new FakeClient(events), new FakeLifecycle(events),
            modelFileVerifier: new AcceptingBoltVerifier(), hardwareProbe: new BoltHardwareProbe(commit));

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.DoesNotContain("start", events);
        Assert.Equal(262144, (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest.ContextLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoltStartupAndRestart_Retain128KWhenMoreMemoryIsAvailable(bool prerelease)
    {
        using var temp = new TempDirectory("bolt-runtime-retention-");
        var paths = await PrepareBoltInstallAsync(temp, 131072, prerelease);
        var original = (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest;
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var hardware = new BoltHardwareProbe(48L << 30);
        await using var runtime = CreateRuntime(paths, new FakeProcessHost(platform, events, 28769),
            platform, new FakeClient(events), new FakeLifecycle(events),
            modelFileVerifier: new AcceptingBoltVerifier(), hardwareProbe: hardware);

        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        Assert.Contains("ctx-size = 131072", (await File.ReadAllTextAsync(paths.RouterPresetPath)).Split(Environment.NewLine));
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.RestartAsync()).State);
        Assert.Equal(2, hardware.Calls);
        var current = (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest;
        Assert.Equal(131072, current.ContextLength);
        Assert.Equal(original.ModelId, current.ModelId);
        Assert.Equal(original.ModelAsset, current.ModelAsset);
        Assert.Equal(Assert.Single(original.AdditionalModelAssetsOrEmpty), Assert.Single(current.AdditionalModelAssetsOrEmpty));
    }

    [Fact]
    public async Task PrereleaseBolt_RejectsPublicDraftReceipt()
    {
        using var temp = new TempDirectory("bolt-mixed-receipt-");
        var paths = await PrepareBoltInstallAsync(temp, 131072, prerelease: true);
        var install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        var draft = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!.Recipe.DraftWeights!;
        var mixed = install with
        {
            Manifest = install.Manifest with
            {
                AdditionalModelAssets = [new LocalAiAssetReceipt
                {
                    FileName = Path.GetFileName(draft.RelativePath), SourceUrl = draft.DownloadUri.AbsoluteUri,
                    SizeBytes = draft.SizeBytes, Sha256 = draft.Sha256.Value,
                }],
            },
        };

        Assert.Throws<InvalidDataException>(() => LlamaServerRouterConfiguration.ResolveInstalledPlan(mixed));
    }

    private static async Task<LocalAiPaths> PrepareBoltInstallAsync(TempDirectory temp, int context, bool prerelease = false)
    {
        var paths = new LocalAiPaths(temp.Path);
        var model = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
        var weights = prerelease ? NemotronBoltPrereleaseFixture.Weights : model.Weights;
        var draft = prerelease ? NemotronBoltPrereleaseFixture.DraftWeights : model.Recipe.DraftWeights!;
        var runtime = LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, model.RuntimeFamily)!;
        var profile = LocalModelCatalog.GetProfiles(model).Single(p => p.ContextTokens == context);
        string cache = temp.Combine("hf");
        string Snapshot(PinnedArtifact artifact)
        {
            var source = (HuggingFaceRevisionSource)artifact.Source;
            Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(cache, source.RepositoryId,
                source.RevisionSha, artifact.RelativePath, out var path, out _, out var error), error);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "verified by test seam");
            return path;
        }
        static LocalAiAssetReceipt Receipt(PinnedArtifact artifact) => new()
        {
            FileName = Path.GetFileName(artifact.RelativePath), SourceUrl = artifact.DownloadUri.AbsoluteUri,
            SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256.Value,
        };
        var source = (HuggingFaceRevisionSource)weights.Source;
        var manifest = ValidManifest() with
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            EngineVersion = runtime.ReleaseTag, RuntimeId = runtime.Id, ModelCatalogId = model.Id,
            ModelAlias = model.Id, ModelId = source.RepositoryId + "@" + source.RevisionSha,
            RuntimeAssets = runtime.Artifacts.Select(Receipt).ToImmutableArray(),
            ModelAsset = Receipt(weights), ModelPath = Path.Combine("models", weights.RelativePath),
            ModelCacheRoot = cache, CachedModelPath = Snapshot(weights),
            AdditionalModelAssets = [Receipt(draft)],
            AdditionalModelPaths = [Snapshot(draft)],
            ContextLength = context, KeyCachePrecision = profile.KeyCachePrecision,
            ValueCachePrecision = profile.ValueCachePrecision, DraftKeyCachePrecision = profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = profile.DraftValueCachePrecision,
        };
        string executable = paths.ResolveContainedPath(manifest.ExecutablePath, "executable");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "test executable");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, LlamaRuntimeCatalog.ServerImplementationLibraryName), "test library");
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        return paths;
    }

    private sealed class AcceptingBoltVerifier : ILocalAiModelFileVerifier
    {
        public Task<LocalAiVerifiedModelLease?> TryOpenAsync(string cacheRoot, string candidatePath,
            long expectedSizeBytes, Sha256Digest expectedSha256, CancellationToken cancellationToken) =>
            Task.FromResult<LocalAiVerifiedModelLease?>(new(new MemoryStream(), candidatePath));
    }

    private sealed class BoltHardwareProbe(long? commit) : IHostHardwareProbe
    {
        public int Calls { get; private set; }
        public HostHardwareInfo Probe()
        {
            Calls++;
            return new(RuntimeArchitecture.Arm64, 48L << 30, 32L << 30,
                [new GpuInfo(GpuVendor.Nvidia, "NVIDIA RTX Spark N1X", 48L << 30, 48L << 30,
                    CudaMajorVersion: 13, StableId: "GPU-01234567-89ab-cdef-0123-456789abcdef")], false)
                { AvailableCommitMemoryBytes = commit };
        }
    }
}
