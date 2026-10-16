using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using OpenClaw.Connection.LocalAi;
using System.Collections.Immutable;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.SetupEngine.Tests;

public class NemotronBoltSetupAdmissionTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    public async Task Preflight_RejectsBoltWithoutCurrent128KFitAndPreservesSelection(long? commit)
    {
        using var temp = new TempDirectory();
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var config = new SetupConfig();
        config.LocalAi.Enabled = true;
        config.LocalAi.SelectedModelId = LocalModelCatalog.NemotronBoltModelId;
        config.LocalAi.SelectedProfileId = "ctx-262144-q8_0-draft-f16";
        var context = new SetupContext(config, logger, new TransactionJournal(filePath: null),
            new CommandRunner(logger), CancellationToken.None, temp.Path, temp.Path);
        var hardware = new HostHardwareInfo(RuntimeArchitecture.Arm64, 48L << 30, 32L << 30,
            [new GpuInfo(GpuVendor.Nvidia, "NVIDIA RTX Spark N1X", 48L << 30, 48L << 30,
                CudaMajorVersion: 13, StableId: "GPU-spark")], false)
            { AvailableCommitMemoryBytes = commit };

        var result = await new PreflightLocalAiHardwareStep(new FixedProbe(hardware)).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(LocalModelCatalog.NemotronBoltModelId, config.LocalAi.SelectedModelId);
        Assert.Equal("ctx-262144-q8_0-draft-f16", config.LocalAi.SelectedProfileId);
        Assert.False(Directory.Exists(new OpenClaw.Connection.LocalAi.LocalAiPaths(temp.Path).RootDirectory));
    }

    private sealed class FixedProbe(HostHardwareInfo hardware) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => hardware;
    }

    [Theory]
    [InlineData(262144, 131072, StepOutcome.FailedTerminal)]
    [InlineData(131072, 262144, StepOutcome.Success)]
    public async Task InstalledReceipt_KeepsItsProfileAcrossMemoryChanges(int installedContext,
        int fittingContext, StepOutcome expected)
    {
        using var temp = new TempDirectory();
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var model = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
        var runtime = LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, model.RuntimeFamily)!;
        var profile = LocalModelCatalog.GetProfiles(model).Single(p => p.ContextTokens == installedContext);
        var source = (HuggingFaceRevisionSource)model.Weights.Source;
        string cache = Path.Combine(temp.Path, "hf");
        string Snapshot(PinnedArtifact artifact)
        {
            var origin = (HuggingFaceRevisionSource)artifact.Source;
            Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(cache, origin.RepositoryId, origin.RevisionSha,
                artifact.RelativePath, out var path, out _, out var error), error);
            return path;
        }
        static LocalAiAssetReceipt Receipt(PinnedArtifact artifact) => new()
        {
            FileName = Path.GetFileName(artifact.RelativePath), SourceUrl = artifact.DownloadUri.AbsoluteUri,
            SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256.Value,
        };
        var paths = new LocalAiPaths(temp.Path);
        var manifest = new LocalAiInstallManifest
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            Architecture = "arm64", EngineVersion = runtime.ReleaseTag, RuntimeId = runtime.Id,
            ModelCatalogId = model.Id, ModelAlias = model.Id, SelectedGpuId = "GPU-spark",
            ExecutablePath = Path.Combine("engines", "bolt", LlamaRuntimeCatalog.ServerExecutableName),
            RuntimeAssets = runtime.Artifacts.Select(Receipt).ToImmutableArray(),
            ModelPath = Path.Combine("models", model.Weights.RelativePath),
            ModelId = source.RepositoryId + "@" + source.RevisionSha, ModelAsset = Receipt(model.Weights),
            ModelCacheRoot = cache, CachedModelPath = Snapshot(model.Weights),
            AdditionalModelAssets = [Receipt(model.Recipe.DraftWeights!)],
            AdditionalModelPaths = [Snapshot(model.Recipe.DraftWeights!)],
            ContextLength = installedContext, KeyCachePrecision = profile.KeyCachePrecision,
            ValueCachePrecision = profile.ValueCachePrecision, DraftKeyCachePrecision = profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = profile.DraftValueCachePrecision,
        };
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        string before = await File.ReadAllTextAsync(paths.ManifestPath);
        var config = new SetupConfig();
        config.LocalAi.Enabled = true;
        config.LocalAi.SelectedModelId = model.Id;
        config.LocalAi.InstalledReceiptModelId = model.Id;
        config.LocalAi.SelectedProfileId = profile.Id;
        var context = new SetupContext(config, logger, new TransactionJournal(filePath: null),
            new CommandRunner(logger), CancellationToken.None, temp.Path, temp.Path);
        var fitting = LocalModelCatalog.GetProfiles(model).Single(p => p.ContextTokens == fittingContext);
        var hardware = new HostHardwareInfo(RuntimeArchitecture.Arm64, 48L << 30, 32L << 30,
            [new GpuInfo(GpuVendor.Nvidia, "NVIDIA RTX Spark N1X", 48L << 30, 48L << 30,
                CudaMajorVersion: 13, StableId: "GPU-spark")], false)
            { AvailableCommitMemoryBytes = LocalInferenceEligibility.GetRequiredMemoryBytes(model, fitting) };

        var result = await new PreflightLocalAiHardwareStep(new FixedProbe(hardware)).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(profile.Id, context.LocalAiEligibility!.Plan!.Profile.Id);
        Assert.Equal(profile.Id, config.LocalAi.SelectedProfileId);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.ManifestPath));
    }
}
