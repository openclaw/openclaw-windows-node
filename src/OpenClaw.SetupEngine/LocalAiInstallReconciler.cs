using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiReconcileResult(
    bool Reused,
    LocalAiResolvedInstall? ResolvedInstall,
    LlamaRuntimeInstallResult? RuntimeInstall,
    HuggingFaceModelInstallResult? ModelInstall,
    LocalAiResolvedInstall? OriginalInstall = null,
    LocalAiResolvedInstall? PendingReplacement = null)
{
    public static LocalAiReconcileResult NotInstalled { get; } =
        new(false, null, null, null);
}

internal interface ILocalAiModelFileVerifier
{
    Task<bool> VerifyActiveAsync(
        LocalAiResolvedInstall install,
        PinnedArtifact artifact,
        CancellationToken cancellationToken);

    Task<bool> VerifyLegacyCompatibilityAsync(
        LocalAiResolvedInstall install,
        LocalAiPaths paths,
        PinnedArtifact artifact,
        CancellationToken cancellationToken);
}

internal sealed class LocalAiModelFileVerifier : ILocalAiModelFileVerifier
{
    public async Task<bool> VerifyActiveAsync(
        LocalAiResolvedInstall install,
        PinnedArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (install.Manifest.SchemaVersion != LocalAiInstallManifest.HubCacheReceiptSchemaVersion)
        {
            if (!File.Exists(install.ModelPath))
                return false;
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

    public Task<bool> VerifyLegacyCompatibilityAsync(
        LocalAiResolvedInstall install,
        LocalAiPaths paths,
        PinnedArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (install.Manifest.SchemaVersion != LocalAiInstallManifest.HubCacheReceiptSchemaVersion)
            return Task.FromResult(true);

        string legacyModelPath = paths.ResolveContainedPath(
            install.Manifest.ModelPath,
            nameof(install.Manifest.ModelPath));
        if (!File.Exists(legacyModelPath))
            return Task.FromResult(false);
        return HuggingFaceModelInstaller.VerifyFileAsync(
            legacyModelPath,
            artifact,
            cancellationToken);
    }
}

/// <summary>
/// Reuses an installation only when its manifest, runtime, and model still match
/// the selected immutable recipe. Recovery may retain a matching receipt while
/// the individual acquirers repair incomplete runtime or model assets.
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
        IProgress<LocalAiModelMigrationProgress>? migrationProgress = null,
        bool allowIncompleteInstallation = false)
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
        LocalAiResolvedInstall originalInstall = install.Manifest.ReplacedManifest is { } replacedManifest
            ? manifestStore.ResolveAndValidate(replacedManifest)
            : install;
        LocalAiResolvedInstall? pendingReplacement = install.Manifest.ReplacedManifest is null ? null : install;
        bool replacingModel = !string.Equals(
            install.Manifest.ModelCatalogId,
            plan.Model.Id,
            StringComparison.Ordinal);
        if (install.Manifest.ReplacedManifest is not null && replacingModel)
        {
            throw new InvalidDataException(
                "Complete the pending Local AI model replacement before selecting another model.");
        }
        if (replacingModel)
        {
            if (!allowIncompleteInstallation)
            {
                throw new InvalidDataException(
                    "The existing managed Local AI installation does not match the selected runtime, GPU, and model recipe.");
            }

            ValidateReplacementSource(install, plan, selectedGpuId, localDataDirectory);
            LlamaRuntimeInspection replacementRuntime = await _runtimeInspector
                .InspectAsync(Path.GetDirectoryName(install.ExecutablePath)!, cancellationToken)
                .ConfigureAwait(false);
            return new LocalAiReconcileResult(
                Reused: false,
                ResolvedInstall: null,
                RuntimeInstall: replacementRuntime.IsValid ? CreateRuntimeInstall(install) : null,
                ModelInstall: null,
                OriginalInstall: originalInstall,
                PendingReplacement: pendingReplacement);
        }
        ValidateRecipeMatch(install, plan, selectedGpuId, localDataDirectory);

        bool migrateLegacyGpuId =
            !string.Equals(install.Manifest.SelectedGpuId, selectedGpuId, StringComparison.Ordinal) &&
            GpuIdsMatch(install.Manifest.SelectedGpuId, selectedGpuId);

        LlamaRuntimeInspection inspection = await _runtimeInspector
            .InspectAsync(Path.GetDirectoryName(install.ExecutablePath)!, cancellationToken)
            .ConfigureAwait(false);
        bool activeModelIsValid = await _modelVerifier
            .VerifyActiveAsync(install, plan.Model.Weights, cancellationToken)
            .ConfigureAwait(false);
        bool legacyModelIsValid = activeModelIsValid && await _modelVerifier
            .VerifyLegacyCompatibilityAsync(
                install,
                paths,
                plan.Model.Weights,
                cancellationToken)
            .ConfigureAwait(false);
        bool modelIsValid = activeModelIsValid && legacyModelIsValid;
        if (!inspection.IsValid || !modelIsValid)
        {
            if (!allowIncompleteInstallation)
            {
                throw new InvalidDataException(!inspection.IsValid
                    ? inspection.Error ?? "The managed llama-server runtime no longer passes validation."
                    : "The managed Local AI model no longer matches its pinned size and SHA-256 digest.");
            }

            if (modelIsValid)
            {
                install = await MigrateLegacyModelAsync(
                        install,
                        paths,
                        localDataDirectory,
                        plan,
                        selectedGpuId,
                        migrationProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new LocalAiReconcileResult(
                Reused: false,
                ResolvedInstall: null,
                RuntimeInstall: inspection.IsValid ? CreateRuntimeInstall(install) : null,
                ModelInstall: modelIsValid ? CreateModelInstall(install, localDataDirectory) : null,
                OriginalInstall: originalInstall,
                PendingReplacement: pendingReplacement);
        }

        install = await MigrateLegacyModelAsync(
                install,
                paths,
                localDataDirectory,
                plan,
                selectedGpuId,
                migrationProgress,
                cancellationToken)
            .ConfigureAwait(false);

        if (migrateLegacyGpuId)
        {
            LocalAiInstallManifest migratedManifest = install.Manifest with
            {
                SelectedGpuId = selectedGpuId,
            };
            await manifestStore.SaveAsync(migratedManifest, cancellationToken).ConfigureAwait(false);
            install = manifestStore.ResolveAndValidate(migratedManifest);
        }

        return new LocalAiReconcileResult(
            true,
            install,
            CreateRuntimeInstall(install),
            CreateModelInstall(install, localDataDirectory),
            OriginalInstall: allowIncompleteInstallation ? originalInstall : null,
            PendingReplacement: pendingReplacement);
    }

    private static void ValidateReplacementSource(
        LocalAiResolvedInstall install,
        LocalInferencePlan plan,
        string selectedGpuId,
        string localDataDirectory)
    {
        // Validate the existing receipt against its own catalog model before using it
        // as durable rollback provenance for the newly selected model.
        _ = LlamaServerRouterConfiguration.Build(new LocalAiPaths(localDataDirectory), install);

        string expectedArchitecture = plan.Runtime.Architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
        };
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
        if (!string.Equals(install.Manifest.RuntimeId, plan.Runtime.Id, StringComparison.Ordinal) ||
            !string.Equals(install.Manifest.Architecture, expectedArchitecture, StringComparison.Ordinal) ||
            !GpuIdsMatch(install.Manifest.SelectedGpuId, selectedGpuId) ||
            !LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths setupPaths,
                out _) ||
            !string.Equals(
                Path.GetDirectoryName(install.ExecutablePath),
                setupPaths.InstallDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The existing managed Local AI installation cannot reuse the selected runtime and GPU.");
        }
    }

    private static LlamaRuntimeInstallResult CreateRuntimeInstall(LocalAiResolvedInstall install) =>
        new(
            Path.GetDirectoryName(install.ExecutablePath)!,
            install.ExecutablePath,
            LlamaRuntimeInstallDisposition.ReusedVerified,
            CreatedThisRun: false,
            install.Manifest.RuntimeAssets
                .Select(asset => new LocalAiVerifiedArchive(
                    asset.FileName,
                    asset.SizeBytes,
                    asset.Sha256))
                .ToArray(),
            Rollback: null);

    private static HuggingFaceModelInstallResult CreateModelInstall(
        LocalAiResolvedInstall install,
        string localDataDirectory)
    {
        if (install.Manifest.SchemaVersion != LocalAiInstallManifest.HubCacheReceiptSchemaVersion)
        {
            return new HuggingFaceModelInstallResult(
                install.ModelPath,
                CacheRoot: null,
                HuggingFaceModelInstallDisposition.ReusedVerified,
                CreatedThisRun: false,
                install.ModelPath,
                LegacyCreatedThisRun: false);
        }

        return new HuggingFaceModelInstallResult(
            install.ModelPath,
            install.Manifest.ModelCacheRoot,
            HuggingFaceModelInstallDisposition.ReusedVerified,
            CreatedThisRun: false,
            new LocalAiPaths(localDataDirectory).ResolveContainedPath(
                install.Manifest.ModelPath,
                nameof(install.Manifest.ModelPath)),
            LegacyCreatedThisRun: false);
    }

    private async Task<LocalAiResolvedInstall> MigrateLegacyModelAsync(
        LocalAiResolvedInstall install,
        LocalAiPaths paths,
        string localDataDirectory,
        LocalInferencePlan plan,
        string selectedGpuId,
        IProgress<LocalAiModelMigrationProgress>? migrationProgress,
        CancellationToken cancellationToken)
    {
        if (install.Manifest.SchemaVersion != LocalAiInstallManifest.CurrentSchemaVersion)
            return install;

        string cacheRoot = _cacheRootResolver();
        if (!HuggingFaceModelInstaller.TryValidateCacheRootOwnershipBoundary(
                localDataDirectory,
                cacheRoot,
                out string cacheRootError))
        {
            throw new InvalidDataException(cacheRootError);
        }

        var migrationStore = new LocalAiManifestStore(paths, () => cacheRoot);
        LocalAiResolvedInstall migrated = await migrationStore
            .MigrateLegacyModelToHubCacheAsync(migrationProgress, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The Local AI installation manifest disappeared during cache migration.");
        ValidateRecipeMatch(migrated, plan, selectedGpuId, localDataDirectory);
        return migrated;
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
