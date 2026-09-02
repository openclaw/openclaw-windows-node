using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiReconcileResult(
    bool Reused,
    LocalAiResolvedInstall? ResolvedInstall,
    LlamaRuntimeInstallResult? RuntimeInstall,
    HuggingFaceModelInstallResult? ModelInstall,
    LocalAiResolvedInstall? ReplacedInstall)
{
    public static LocalAiReconcileResult NotInstalled { get; } = new(false, null, null, null, null);
}

internal interface ILocalAiModelFileVerifier
{
    Task<bool> VerifyAsync(string path, PinnedArtifact artifact, CancellationToken cancellationToken);
}

internal sealed class LocalAiModelFileVerifier : ILocalAiModelFileVerifier
{
    public Task<bool> VerifyAsync(
        string path,
        PinnedArtifact artifact,
        CancellationToken cancellationToken) =>
        HuggingFaceModelInstaller.VerifyFileAsync(path, artifact, cancellationToken);
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

    public LocalAiInstallReconciler()
        : this(new WindowsLlamaRuntimeInspector(), new LocalAiModelFileVerifier())
    {
    }

    internal LocalAiInstallReconciler(
        ILlamaRuntimeInspector runtimeInspector,
        ILocalAiModelFileVerifier modelVerifier)
    {
        _runtimeInspector = runtimeInspector ?? throw new ArgumentNullException(nameof(runtimeInspector));
        _modelVerifier = modelVerifier ?? throw new ArgumentNullException(nameof(modelVerifier));
    }

    public async Task<LocalAiReconcileResult> ReconcileAsync(
        string localDataDirectory,
        LocalInferencePlan plan,
        string selectedGpuId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedGpuId);

        var paths = new LocalAiPaths(localDataDirectory);
        LocalAiResolvedInstall? install = await new LocalAiManifestStore(paths)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (install is null)
            return LocalAiReconcileResult.NotInstalled;

        ValidateRuntimeRecipeMatch(install, plan, localDataDirectory);

        LlamaRuntimeInspection inspection = await _runtimeInspector
            .InspectAsync(Path.GetDirectoryName(install.ExecutablePath)!, cancellationToken)
            .ConfigureAwait(false);
        if (!inspection.IsValid)
        {
            throw new InvalidDataException(
                inspection.Error ?? "The managed llama-server runtime no longer passes validation.");
        }

        if (!string.Equals(
                install.Manifest.ModelCatalogId,
                plan.Model.Id,
                StringComparison.Ordinal))
        {
            return new LocalAiReconcileResult(
                false,
                null,
                CreateRuntimeInstall(install),
                null,
                install);
        }

        ValidateSelectedModelMatch(install, plan, selectedGpuId, localDataDirectory);

        if (!await _modelVerifier
                .VerifyAsync(install.ModelPath, plan.Model.Weights, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The managed Local AI model no longer matches its pinned size and SHA-256 digest.");
        }

        var modelInstall = new HuggingFaceModelInstallResult(
            install.ModelPath,
            HuggingFaceModelInstallDisposition.ReusedVerified,
            CreatedThisRun: false);
        return new LocalAiReconcileResult(true, install, CreateRuntimeInstall(install), modelInstall, null);
    }

    private static LlamaRuntimeInstallResult CreateRuntimeInstall(LocalAiResolvedInstall install) =>
        new(
            Path.GetDirectoryName(install.ExecutablePath)!,
            install.ExecutablePath,
            LlamaRuntimeInstallDisposition.ReusedVerified,
            CreatedThisRun: false,
            install.Manifest.RuntimeAssets
                .Select(asset => new LocalAiVerifiedArchive(asset.FileName, asset.SizeBytes, asset.Sha256))
                .ToArray(),
            Rollback: null);

    private static void ValidateRuntimeRecipeMatch(
        LocalAiResolvedInstall install,
        LocalInferencePlan plan,
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
            manifest.RuntimeAssets.Length != plan.Runtime.Artifacts.Count ||
            plan.Runtime.Artifacts.Any(artifact =>
                !manifest.RuntimeAssets.Any(receipt => RuntimeReceiptMatches(receipt, artifact))))
        {
            throw new InvalidDataException(
                "The existing managed Local AI installation does not match the selected runtime recipe.");
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
    }

    private static void ValidateSelectedModelMatch(
        LocalAiResolvedInstall install,
        LocalInferencePlan plan,
        string selectedGpuId,
        string localDataDirectory)
    {
        if (!string.Equals(install.Manifest.SelectedGpuId, selectedGpuId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The existing managed Local AI installation does not match the selected GPU.");
        }

        // This performs the complete catalog receipt comparison, including
        // runtime and model URLs, sizes, hashes, revision, alias, and context.
        _ = LlamaServerRouterConfiguration.Build(new LocalAiPaths(localDataDirectory), install);

        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
        if (!LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths setupPaths,
                out string error))
        {
            throw new InvalidDataException(error);
        }

        if (plan.Model.Weights.Source is not HuggingFaceRevisionSource source ||
            !LocalAiPathPolicy.TryGetModelPaths(
                setupPaths,
                source.RepositoryId,
                source.RevisionSha,
                plan.Model.Weights.RelativePath,
                out string expectedModelPath,
                out _,
                out error) ||
            !string.Equals(install.ModelPath, expectedModelPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "The managed model path does not match the selected catalog recipe."
                    : error);
        }
    }

    private static bool RuntimeReceiptMatches(LocalAiAssetReceipt receipt, PinnedArtifact artifact) =>
        string.Equals(receipt.FileName, Path.GetFileName(artifact.RelativePath), StringComparison.Ordinal) &&
        string.Equals(receipt.SourceUrl, artifact.DownloadUri.AbsoluteUri, StringComparison.Ordinal) &&
        receipt.SizeBytes == artifact.SizeBytes &&
        string.Equals(receipt.Sha256, artifact.Sha256.Value, StringComparison.Ordinal);
}
