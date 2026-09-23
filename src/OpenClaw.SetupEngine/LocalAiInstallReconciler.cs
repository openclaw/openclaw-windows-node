using System.Collections.Immutable;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiReconcileResult(
    bool Reused,
    LocalAiResolvedInstall? ResolvedInstall,
    LlamaRuntimeInstallResult? RuntimeInstall,
    HuggingFaceModelInstallResult? ModelInstall,
    LocalAiResolvedInstall? OriginalInstall = null,
    ImmutableArray<HuggingFaceAdditionalAssetInstallResult>? AdditionalModelInstalls = null)
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

    /// <summary>
    /// Verifies one schema-5 additional model asset (a DFlash draft checkpoint,
    /// or an extra split-GGUF shard) still matches its pinned receipt in the
    /// shared hub cache. Additional assets have no legacy app-owned copy, so
    /// unlike <see cref="VerifyActiveAsync"/> there is no separate schema-3 path.
    /// </summary>
    Task<bool> VerifyAdditionalAssetAsync(
        LocalAiResolvedInstall install,
        string cachedAssetPath,
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
        if (!install.Manifest.UsesHubCache)
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
        if (!install.Manifest.UsesHubCache)
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

    public async Task<bool> VerifyAdditionalAssetAsync(
        LocalAiResolvedInstall install,
        string cachedAssetPath,
        PinnedArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    install.Manifest.ModelCacheRoot!,
                    cachedAssetPath,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
        return verified is not null;
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
        LocalAiResolvedInstall originalInstall = install;
        bool runtimeUpgradePending = ValidateRecipeMatch(install, plan, selectedGpuId, localDataDirectory);

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
        bool additionalAssetsAreValid = activeModelIsValid && await VerifyAdditionalAssetsAsync(
                install,
                plan.Model,
                cancellationToken)
            .ConfigureAwait(false);
        bool modelIsValid = activeModelIsValid && legacyModelIsValid && additionalAssetsAreValid;
        if (runtimeUpgradePending)
        {
            // The catalog moved to a newer pinned runtime. Drop only the runtime so the
            // acquirer installs the new one, and keep the verified model and its extra
            // assets so an upgrade does not re-download tens of GB. OriginalInstall lets
            // the manifest step replace the existing receipt in place.
            return new LocalAiReconcileResult(
                Reused: false,
                ResolvedInstall: null,
                RuntimeInstall: null,
                ModelInstall: modelIsValid ? CreateModelInstall(install, localDataDirectory) : null,
                OriginalInstall: originalInstall,
                AdditionalModelInstalls: modelIsValid ? CreateAdditionalModelInstalls(install) : null);
        }

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
                AdditionalModelInstalls: modelIsValid ? CreateAdditionalModelInstalls(install) : null);
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
            AdditionalModelInstalls: CreateAdditionalModelInstalls(install));
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
        if (!install.Manifest.UsesHubCache)
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

    /// <summary>
    /// Reconstructs the additional-asset install results a reused install's
    /// manifest already proves verified, so a caller that skips re-acquiring
    /// them (because <see cref="ReconcileAsync"/> just verified them) still has
    /// a populated <c>SetupContext.LocalAiAdditionalModelInstalls</c> to persist.
    /// </summary>
    private static ImmutableArray<HuggingFaceAdditionalAssetInstallResult> CreateAdditionalModelInstalls(
        LocalAiResolvedInstall install)
    {
        if (install.Manifest.AdditionalModelPaths.IsDefaultOrEmpty)
            return ImmutableArray<HuggingFaceAdditionalAssetInstallResult>.Empty;

        var builder = ImmutableArray.CreateBuilder<HuggingFaceAdditionalAssetInstallResult>(
            install.Manifest.AdditionalModelPathsOrEmpty.Length);
        foreach (string cachedAssetPath in install.Manifest.AdditionalModelPathsOrEmpty)
        {
            builder.Add(new HuggingFaceAdditionalAssetInstallResult(
                cachedAssetPath,
                install.Manifest.ModelCacheRoot!,
                HuggingFaceModelInstallDisposition.ReusedVerified,
                CreatedThisRun: false));
        }

        return builder.MoveToImmutable();
    }

    private async Task<bool> VerifyAdditionalAssetsAsync(
        LocalAiResolvedInstall install,
        LocalModelInfo model,
        CancellationToken cancellationToken)
    {
        ImmutableArray<PinnedArtifact> expected = LocalModelCatalog.AdditionalArtifacts(model);
        if (expected.IsEmpty)
            return install.Manifest.AdditionalModelAssetsOrEmpty.IsEmpty;
        if (install.Manifest.AdditionalModelPathsOrEmpty.Length != expected.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            if (!await _modelVerifier
                    .VerifyAdditionalAssetAsync(
                        install,
                        install.Manifest.AdditionalModelPathsOrEmpty[i],
                        expected[i],
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
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
        _ = ValidateRecipeMatch(migrated, plan, selectedGpuId, localDataDirectory);
        return migrated;
    }

    /// <summary>
    /// Validates the receipt against the runtime and recipe it actually recorded, and
    /// reports whether the catalog has since moved to a newer pinned runtime. A version
    /// difference is an expected upgrade, not a corrupt install, so it must not throw:
    /// throwing here ends setup with an uninstall instruction instead of upgrading.
    /// </summary>
    private static bool ValidateRecipeMatch(
        LocalAiResolvedInstall install,
        LocalInferencePlan plan,
        string selectedGpuId,
        string localDataDirectory)
    {
        LocalAiInstallManifest manifest = install.Manifest;
        LlamaRuntimeVariant installedRuntime =
            LlamaRuntimeCatalog.FindInstalled(manifest.RuntimeId) ?? plan.Runtime;
        bool runtimeUpgradePending =
            !string.Equals(installedRuntime.Id, plan.Runtime.Id, StringComparison.Ordinal);
        string expectedArchitecture = plan.Runtime.Architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
        };
        if (!string.Equals(manifest.EngineVersion, installedRuntime.ReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(manifest.Architecture, expectedArchitecture, StringComparison.Ordinal) ||
            !string.Equals(manifest.RuntimeId, installedRuntime.Id, StringComparison.Ordinal) ||
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

        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(installedRuntime);
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
            installedRuntime,
            plan.Model);

        bool modelPathMatches;
        if (manifest.UsesHubCache)
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

        return runtimeUpgradePending;
    }

    private static bool GpuIdsMatch(string persistedGpuId, string selectedGpuId) =>
        string.Equals(persistedGpuId, selectedGpuId, StringComparison.Ordinal) ||
        persistedGpuId.StartsWith("cuda:", StringComparison.Ordinal) &&
        string.Equals(persistedGpuId["cuda:".Length..], selectedGpuId, StringComparison.Ordinal);
}
