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
    [InlineData(262144, 131072, StepOutcome.FailedTerminal, false, true)]
    [InlineData(131072, 262144, StepOutcome.Success, false, true)]
    [InlineData(262144, 131072, StepOutcome.FailedTerminal, true, true)]
    [InlineData(131072, 262144, StepOutcome.Success, true, true)]
    [InlineData(262144, 131072, StepOutcome.FailedTerminal, false, false)]
    [InlineData(131072, 262144, StepOutcome.Success, false, false)]
    [InlineData(262144, 131072, StepOutcome.FailedTerminal, true, false)]
    [InlineData(131072, 262144, StepOutcome.Success, true, false)]
    [InlineData(262144, 262144, StepOutcome.Success, true, false)]
    public async Task InstalledReceipt_KeepsItsProfileAcrossMemoryChanges(int installedContext,
        int fittingContext, StepOutcome expected, bool prerelease, bool withUiReceiptMarker)
    {
        using var temp = new TempDirectory();
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var model = LocalModelCatalog.Find(LocalModelCatalog.NemotronBoltModelId)!;
        var weights = prerelease ? NemotronBoltPrereleaseFixture.Weights : model.Weights;
        var draft = prerelease ? NemotronBoltPrereleaseFixture.DraftWeights : model.Recipe.DraftWeights!;
        // Prerelease receipts predate the vision projector and stay drafter-only.
        var additional = prerelease ? ImmutableArray.Create(draft) : LocalModelCatalog.AdditionalArtifacts(model);
        var runtime = LlamaRuntimeCatalog.Find(RuntimeArchitecture.Arm64, model.RuntimeFamily)!;
        var profile = LocalModelCatalog.GetProfiles(model).Single(p => p.ContextTokens == installedContext);
        var source = (HuggingFaceRevisionSource)weights.Source;
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
        Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, LlamaRuntimeInstaller.Component(runtime),
            out var setupPaths, out var pathError), pathError);
        var manifest = new LocalAiInstallManifest
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            Architecture = "arm64", EngineVersion = runtime.ReleaseTag, RuntimeId = runtime.Id,
            ModelCatalogId = model.Id, ModelAlias = model.Id, SelectedGpuId = "GPU-spark",
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory,
                Path.Combine(setupPaths.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName)),
            RuntimeAssets = runtime.Artifacts.Select(Receipt).ToImmutableArray(),
            ModelPath = Path.Combine("models", weights.RelativePath),
            ModelId = source.RepositoryId + "@" + source.RevisionSha, ModelAsset = Receipt(weights),
            ModelCacheRoot = cache, CachedModelPath = Snapshot(weights),
            AdditionalModelAssets = additional.Select(Receipt).ToImmutableArray(),
            AdditionalModelPaths = additional.Select(Snapshot).ToImmutableArray(),
            ContextLength = installedContext, KeyCachePrecision = profile.KeyCachePrecision,
            ValueCachePrecision = profile.ValueCachePrecision, DraftKeyCachePrecision = profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = profile.DraftValueCachePrecision,
        };
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        string before = await File.ReadAllTextAsync(paths.ManifestPath);
        var config = new SetupConfig();
        config.LocalAi.Enabled = true;
        config.LocalAi.SelectedModelId = model.Id;
        if (withUiReceiptMarker)
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
        Assert.Equal(weights, context.LocalAiEligibility.Plan.Model.Weights);
        Assert.Equal(draft, context.LocalAiEligibility.Plan.Model.Recipe.DraftWeights);
        Assert.Equal(profile.Id, config.LocalAi.SelectedProfileId);

        if (expected == StepOutcome.Success)
        {
            var recovery = await new LocalAiInstallReconciler(new IncompleteRuntimeInspector(),
                    new AcceptingModelVerifier())
                .ReconcileAsync(temp.Path, context.LocalAiEligibility.Plan, "GPU-spark", CancellationToken.None,
                    allowIncompleteInstallation: true);

            Assert.False(recovery.Reused);
            Assert.Null(recovery.RuntimeInstall);
            Assert.Equal(manifest.ModelAsset, recovery.OriginalInstall!.Manifest.ModelAsset);
            Assert.Equal(manifest.CachedModelPath, recovery.ModelInstall!.ModelPath);
            Assert.Equal(manifest.AdditionalModelPathsOrEmpty,
                recovery.AdditionalModelInstalls!.Value.Select(install => install.ModelPath));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(paths.ManifestPath));
    }

    private sealed class IncompleteRuntimeInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(string installDirectory, LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken) => Task.FromResult(new LlamaRuntimeInspection(false, "Missing runtime"));
    }

    private sealed class AcceptingModelVerifier : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyActiveAsync(LocalAiResolvedInstall install, PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> VerifyLegacyCompatibilityAsync(LocalAiResolvedInstall install, LocalAiPaths paths,
            PinnedArtifact artifact, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> VerifyAdditionalAssetAsync(LocalAiResolvedInstall install, string cachedAssetPath,
            PinnedArtifact artifact, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
