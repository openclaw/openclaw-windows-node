using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiReconcileResult(
    bool Reused,
    LocalAiResolvedInstall? ResolvedInstall,
    LlamaRuntimeInstallResult? RuntimeInstall,
    HuggingFaceModelInstallResult? ModelInstall)
{
    public static LocalAiReconcileResult NotInstalled { get; } = new(false, null, null, null);
}

internal interface ILocalAiModelFileVerifier
{
    Task<bool> VerifyAsync(
        LocalAiResolvedInstall install,
        PinnedArtifact artifact,
        CancellationToken cancellationToken);
}

internal sealed class LocalAiModelFileVerifier : ILocalAiModelFileVerifier
{
    public async Task<bool> VerifyAsync(
        LocalAiResolvedInstall install,
        PinnedArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (install.Manifest.SchemaVersion != LocalAiInstallManifest.HubCacheReceiptSchemaVersion)
        {
            return await HuggingFaceModelInstaller
                .VerifyFileAsync(install.ModelPath, artifact, cancellationToken)
                .ConfigureAwait(false);
        }

        await using FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    install.Manifest.ModelCacheRoot!,
                    install.ModelPath,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
        return verified is not null;
    }
}

/// <summary>
/// Reuses only an installation claimed by a complete manifest that still
/// matches the selected immutable catalog recipe and passes on-disk checks.
/// Unclaimed paths remain the responsibility of the individual acquirers.
/// </summary>
internal sealed class LocalAiInstallReconciler
{
    private readonly ILlamaRuntimeInspector _runtimeInspector;
    private readonly ILocalAiModelFileVerifier _modelVerifier;
    private readonly Func<string> _cacheRootResolver;

    public LocalAiInstallReconciler()
        : this(
            new WindowsLlamaRuntimeInspector(),
            new LocalAiModelFileVerifier(),
            HuggingFaceHubCache.ResolveCacheRoot)
    {
    }

    internal LocalAiInstallReconciler(
        ILlamaRuntimeInspector runtimeInspector,
        ILocalAiModelFileVerifier modelVerifier,
        Func<string>? cacheRootResolver = null)
    {
        _runtimeInspector = runtimeInspector ?? throw new ArgumentNullException(nameof(runtimeInspector));
        _modelVerifier = modelVerifier ?? throw new ArgumentNullException(nameof(modelVerifier));
        _cacheRootResolver = cacheRootResolver ?? HuggingFaceHubCache.ResolveCacheRoot;
    }

    public async Task<LocalAiReconcileResult> ReconcileAsync(
        string localDataDirectory,
        LocalInferencePlan plan,
        string selectedGpuId,
        CancellationToken cancellationToken,
        IProgress<LocalAiModelMigrationProgress>? migrationProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedGpuId);

        var paths = new LocalAiPaths(localDataDirectory);
        var manifestStore = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall? install = await manifestStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (install is null)
            return LocalAiReconcileResult.NotInstalled;
        ValidateRecipeMatch(install, plan, selectedGpuId, localDataDirectory);
        if (install.Manifest.SchemaVersion == LocalAiInstallManifest.CurrentSchemaVersion)
        {
            string cacheRoot = _cacheRootResolver();
            if (!HuggingFaceModelInstaller.TryValidateCacheRootOwnershipBoundary(
                    localDataDirectory,
                    cacheRoot,
                    out string cacheRootError))
            {
                throw new InvalidDataException(cacheRootError);
            }

            var migrationStore = new LocalAiManifestStore(paths, () => cacheRoot);
            install = await migrationStore
                .MigrateLegacyModelToHubCacheAsync(migrationProgress, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The Local AI installation manifest disappeared during cache migration.");
            ValidateRecipeMatch(install, plan, selectedGpuId, localDataDirectory);
        }

        bool migrateLegacyGpuId =
            !string.Equals(install.Manifest.SelectedGpuId, selectedGpuId, StringComparison.Ordinal) &&
            GpuIdsMatch(install.Manifest.SelectedGpuId, selectedGpuId);

        LlamaRuntimeInspection inspection = await _runtimeInspector
            .InspectAsync(Path.GetDirectoryName(install.ExecutablePath)!, cancellationToken)
            .ConfigureAwait(false);
        if (!inspection.IsValid)
        {
            throw new InvalidDataException(
                inspection.Error ?? "The managed llama-server runtime no longer passes validation.");
        }

        if (!await _modelVerifier
                .VerifyAsync(install, plan.Model.Weights, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The managed Local AI model no longer matches its pinned size and SHA-256 digest.");
        }

        if (migrateLegacyGpuId)
        {
            LocalAiInstallManifest migratedManifest = install.Manifest with
            {
                SelectedGpuId = selectedGpuId,
            };
            await manifestStore.SaveAsync(migratedManifest, cancellationToken).ConfigureAwait(false);
            install = manifestStore.ResolveAndValidate(migratedManifest);
        }

        IReadOnlyList<LocalAiVerifiedArchive> verifiedArchives = install.Manifest.RuntimeAssets
            .Select(asset => new LocalAiVerifiedArchive(asset.FileName, asset.SizeBytes, asset.Sha256))
            .ToArray();
        var runtimeInstall = new LlamaRuntimeInstallResult(
            Path.GetDirectoryName(install.ExecutablePath)!,
            install.ExecutablePath,
            LlamaRuntimeInstallDisposition.ReusedVerified,
            CreatedThisRun: false,
            verifiedArchives,
            Rollback: null);
        var modelInstall = new HuggingFaceModelInstallResult(
            install.ModelPath,
            install.Manifest.ModelCacheRoot,
            HuggingFaceModelInstallDisposition.ReusedVerified,
            CreatedThisRun: false,
            new LocalAiPaths(localDataDirectory).ResolveContainedPath(
                install.Manifest.ModelPath,
                nameof(install.Manifest.ModelPath)),
            LegacyCreatedThisRun: false);
        return new LocalAiReconcileResult(true, install, runtimeInstall, modelInstall);
    }

    private static void ValidateRecipeMatch(
        LocalAiResolvedInstall install,
        LocalInferencePlan plan,
        string selectedGpuId,
        string localDataDirectory)
    {
        LocalAiInstallManifest manifest = install.Manifest;
        string expectedArchitecture = plan.Runtime.Architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
        };
        if (!string.Equals(manifest.EngineVersion, LlamaRuntimeCatalog.ReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(manifest.Architecture, expectedArchitecture, StringComparison.Ordinal) ||
            !string.Equals(manifest.RuntimeId, plan.Runtime.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.ModelCatalogId, plan.Model.Id, StringComparison.Ordinal) ||
            manifest.ContextLength != plan.Profile.ContextTokens ||
            manifest.KeyCachePrecision != plan.Profile.KeyCachePrecision ||
            manifest.ValueCachePrecision != plan.Profile.ValueCachePrecision ||
            manifest.DraftKeyCachePrecision != plan.Profile.DraftKeyCachePrecision ||
            manifest.DraftValueCachePrecision != plan.Profile.DraftValueCachePrecision ||
            !GpuIdsMatch(manifest.SelectedGpuId, selectedGpuId))
        {
            throw new InvalidDataException(
                "The existing managed Local AI installation does not match the selected runtime, GPU, and model recipe.");
        }

        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
        if (!LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths setupPaths,
                out string error) ||
            !string.Equals(
                Path.GetDirectoryName(install.ExecutablePath),
                setupPaths.InstallDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "The managed llama-server path does not match the selected catalog recipe."
                    : error);
        }

        if (plan.Model.Weights.Source is not HuggingFaceRevisionSource source)
        {
            throw new InvalidDataException(
                "The managed model does not have immutable Hugging Face provenance.");
        }
        LlamaServerRouterConfiguration.ValidateArtifactReceipts(
            manifest,
            plan.Runtime,
            plan.Model);

        bool modelPathMatches;
        if (manifest.SchemaVersion == LocalAiInstallManifest.HubCacheReceiptSchemaVersion)
        {
            modelPathMatches =
                !string.IsNullOrWhiteSpace(manifest.ModelCacheRoot) &&
                HuggingFaceHubCache.TryGetSnapshotPaths(
                    manifest.ModelCacheRoot,
                    source.RepositoryId,
                    source.RevisionSha,
                    plan.Model.Weights.RelativePath,
                    out string expectedModelPath,
                    out _,
                    out error) &&
                string.Equals(
                    install.ModelPath,
                    expectedModelPath,
                    StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            modelPathMatches =
                LocalAiPathPolicy.TryGetModelPaths(
                    setupPaths,
                    source.RepositoryId,
                    source.RevisionSha,
                    plan.Model.Weights.RelativePath,
                    out string expectedModelPath,
                    out _,
                    out error) &&
                string.Equals(
                    install.ModelPath,
                    expectedModelPath,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (!modelPathMatches)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "The managed model path does not match the selected catalog recipe."
                    : error);
        }
    }

    private static bool GpuIdsMatch(string persistedGpuId, string selectedGpuId) =>
        string.Equals(persistedGpuId, selectedGpuId, StringComparison.Ordinal) ||
        persistedGpuId.StartsWith("cuda:", StringComparison.Ordinal) &&
        string.Equals(persistedGpuId["cuda:".Length..], selectedGpuId, StringComparison.Ordinal);
}
