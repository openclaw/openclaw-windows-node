using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiPortHandoffTests
{
    [Fact]
    public async Task Preflight_AutomaticPortRemainsZeroForChildOwnedBind()
    {
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true, Port = 0 });
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(CreateSparkHardware()));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(0, context.LocalAiPort);
    }

    [Fact]
    public async Task Preflight_RejectsReservedPort80()
    {
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true, Port = 80 });
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(CreateSparkHardware()));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("80", result.Message, StringComparison.Ordinal);
        Assert.Null(context.LocalAiPort);
    }

    [Fact]
    public async Task Preflight_StopsBeforeAnyDownloadWhenCapacityIsUnknown()
    {
        // A GPU whose dedicated-memory bound could not be resolved has no
        // trustworthy admission capacity, so setup must stop before downloading.
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true, Port = 0 });
        var step = new PreflightLocalAiHardwareStep(
            new FakeHardwareProbe(CreateIncompleteFactsHardware()));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains(
            nameof(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete),
            result.Message,
            StringComparison.Ordinal);
        Assert.Null(context.LocalAiPort);
    }

    [Fact]
    public async Task PersistStep_RecordsRequestButNotEndpointBeforeHealth()
    {
        using var temp = new TempDirectory("local-ai-handoff-");
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true, Port = 0 },
            temp.Path);
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiPort = 0;
        context.LocalAiRuntimeInstall = RuntimeInstall(temp.Path);
        context.LocalAiModelInstall = ModelInstall(temp.Path, context.LocalAiEligibility.Plan!.Model);

        StepResult result = await new PersistLocalAiManifestStep().ExecuteAsync(
            context,
            CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(new LocalAiPaths(temp.Path)).LoadAsync();
        Assert.NotNull(saved);
        Assert.Equal(0, saved.Manifest.RequestedPort);
        Assert.Null(saved.Endpoint);
        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, saved.Manifest.SchemaVersion);
        Assert.Equal(context.LocalAiModelInstall.CacheRoot, saved.Manifest.ModelCacheRoot);
        Assert.Equal(context.LocalAiModelInstall.ModelPath, saved.Manifest.CachedModelPath);
        Assert.Equal(context.LocalAiModelInstall.ModelPath, saved.ModelPath);
        Assert.NotEqual(saved.Manifest.ModelPath, saved.Manifest.CachedModelPath);
        Assert.Equal(
            context.LocalAiModelInstall.LegacyModelPath,
            new LocalAiPaths(temp.Path).ResolveContainedPath(
                saved.Manifest.ModelPath,
                nameof(saved.Manifest.ModelPath)));
    }

    [Fact]
    public async Task PersistStep_CarriesDeterministicallySelectedGpuIntoRouterEnvironment()
    {
        using var temp = new TempDirectory("local-ai-handoff-");
        SetupContext context = CreateContext(
            new LocalAiConfig
            {
                Enabled = true,
                Port = 0,
                SelectedModelId = LocalModelCatalog.Qwen38_27BModelId,
            },
            temp.Path);
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(
            CreateMultiGpuHardware(),
            context.Config.LocalAi.SelectedModelId);
        context.LocalAiPort = 0;
        context.LocalAiRuntimeInstall = RuntimeInstall(temp.Path);
        context.LocalAiModelInstall = ModelInstall(temp.Path, context.LocalAiEligibility.Plan!.Model);

        StepResult result = await new PersistLocalAiManifestStep().ExecuteAsync(
            context,
            CancellationToken.None);
        var paths = new LocalAiPaths(temp.Path);
        LocalAiResolvedInstall saved = (await new LocalAiManifestStore(paths).LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal("GPU-a", context.LocalAiEligibility.SelectedGpu?.StableId);
        Assert.Equal("GPU-a", saved.Manifest.SelectedGpuId);
        Assert.Equal("GPU-a", launch.Environment["CUDA_VISIBLE_DEVICES"]);
    }

    private static SetupContext CreateContext(LocalAiConfig localAi, string? localDataDirectory = null)
    {
        var config = new SetupConfig { LocalAi = localAi };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private static HostHardwareInfo CreateSparkHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                GpuVisibleMemoryBytes: 25_702_694_912,
                FreeGpuVisibleMemoryBytes: 25_702_694_912,
                DriverVersion: "616.00",
                CudaMajorVersion: 13,
                StableId: "GPU-SPARK"),
        ],
        VulkanAvailable: false);

    private static HostHardwareInfo CreateIncompleteFactsHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                CudaMajorVersion: 13,
                StableId: "GPU-SPARK"),
        ],
        VulkanAvailable: false);

    private static HostHardwareInfo CreateMultiGpuHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA tie GPU z",
                GpuVisibleMemoryBytes: 32L * 1024 * 1024 * 1024,
                FreeGpuVisibleMemoryBytes: 24L * 1024 * 1024 * 1024,
                DriverVersion: "616.00",
                CudaMajorVersion: 13,
                StableId: "GPU-z"),
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA tie GPU a",
                GpuVisibleMemoryBytes: 32L * 1024 * 1024 * 1024,
                FreeGpuVisibleMemoryBytes: 24L * 1024 * 1024 * 1024,
                DriverVersion: "616.00",
                CudaMajorVersion: 13,
                StableId: "GPU-a"),
        ],
        VulkanAvailable: false);

    private static LlamaRuntimeInstallResult RuntimeInstall(string localDataDirectory)
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(Architecture.Arm64)!;
        return new(
            Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server"),
            Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server", "llama-server.exe"),
            LlamaRuntimeInstallDisposition.Installed,
            CreatedThisRun: true,
            VerifiedArchives: runtime.Artifacts.Select(artifact => new LocalAiVerifiedArchive(
                Path.GetFileName(artifact.RelativePath),
                artifact.SizeBytes,
                artifact.Sha256.Value)).ToArray(),
            Rollback: new LocalAiArtifactRollbackMetadata(
                Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server")));
    }

    private static HuggingFaceModelInstallResult ModelInstall(
        string localDataDirectory,
        LocalModelInfo model)
    {
        string cacheRoot = Path.Combine(localDataDirectory, "hf-cache");
        HuggingFaceRevisionSource source = Assert.IsType<HuggingFaceRevisionSource>(
            model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out string modelPath,
            out _,
            out string error), error);
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(
            LlamaRuntimeCatalog.Find(Architecture.Arm64)!);
        Assert.True(LocalAiPathPolicy.TryResolve(
            localDataDirectory,
            component,
            out LocalAiSetupPaths paths,
            out error), error);
        Assert.True(LocalAiPathPolicy.TryGetModelPaths(
            paths,
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out string legacyModelPath,
            out _,
            out error), error);
        return new(
            modelPath,
            cacheRoot,
            HuggingFaceModelInstallDisposition.Downloaded,
            CreatedThisRun: true,
            legacyModelPath,
            LegacyCreatedThisRun: true);
    }

    private sealed class FakeHardwareProbe(HostHardwareInfo hardware) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => hardware;
    }
}
