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

    [Fact]
    public async Task BoltStartupAndRestart_Retain128KWhenMoreMemoryIsAvailable()
    {
        using var temp = new TempDirectory("bolt-runtime-retention-");
        var paths = await PrepareBoltInstallAsync(temp, 131072);
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
        Assert.Equal(131072, (await new LocalAiManifestStore(paths).LoadAsync())!.Manifest.ContextLength);
    }

    private static async Task<LocalAiPaths> PrepareBoltInstallAsync(TempDirectory temp, int context)
    {
        var paths = new LocalAiPaths(temp.Path);
        var model = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
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
        var source = (HuggingFaceRevisionSource)model.Weights.Source;
        var manifest = ValidManifest() with
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            EngineVersion = runtime.ReleaseTag, RuntimeId = runtime.Id, ModelCatalogId = model.Id,
            ModelAlias = model.Id, ModelId = source.RepositoryId + "@" + source.RevisionSha,
            RuntimeAssets = runtime.Artifacts.Select(Receipt).ToImmutableArray(),
            ModelAsset = Receipt(model.Weights), ModelPath = Path.Combine("models", model.Weights.RelativePath),
            ModelCacheRoot = cache, CachedModelPath = Snapshot(model.Weights),
            AdditionalModelAssets = [Receipt(model.Recipe.DraftWeights!)],
            AdditionalModelPaths = [Snapshot(model.Recipe.DraftWeights!)],
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
