using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// The single mapping from a verified runtime, model acquisition, and selected plan to an
/// installation receipt. Setup and the in-place model switcher both write receipts through here.
/// </summary>
internal static class LocalAiManifestFactory
{
    public static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
    };

    public static LocalAiInstallManifest Create(
        LocalAiPaths paths,
        LocalInferencePlan plan,
        string gpuId,
        int requestedPort,
        string engineVersion,
        string runtimeId,
        string architecture,
        string executablePath,
        ImmutableArray<LocalAiAssetReceipt> runtimeAssets,
        HuggingFaceModelInstallResult modelInstall,
        ImmutableArray<HuggingFaceAdditionalAssetInstallResult> additionalInstalls)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(modelInstall);
        if (plan.Model.Weights.Source is not HuggingFaceRevisionSource modelSource)
            throw new InvalidDataException("The selected Local AI model does not have immutable Hugging Face provenance.");
        if (string.IsNullOrWhiteSpace(modelInstall.LegacyModelPath) || string.IsNullOrWhiteSpace(modelInstall.CacheRoot))
            throw new InvalidDataException("The Local AI model acquisition did not produce a verified cache receipt.");

        ImmutableArray<PinnedArtifact> additionalArtifacts = LocalModelCatalog.AdditionalArtifacts(plan.Model);
        if (additionalArtifacts.Length != additionalInstalls.Length)
            throw new InvalidDataException("The Local AI installation receipt requires a completed additional-asset acquisition step.");

        var additionalModelAssets = ImmutableArray.CreateBuilder<LocalAiAssetReceipt>(additionalArtifacts.Length);
        var additionalModelPaths = ImmutableArray.CreateBuilder<string>(additionalArtifacts.Length);
        for (int i = 0; i < additionalArtifacts.Length; i++)
        {
            PinnedArtifact artifact = additionalArtifacts[i];
            additionalModelAssets.Add(new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            });
            additionalModelPaths.Add(additionalInstalls[i].ModelPath);
        }

        return new LocalAiInstallManifest
        {
            SchemaVersion = plan.Model.CustomRecipe is not null
                ? LocalAiInstallManifest.CustomModelSchemaVersion
                : additionalArtifacts.IsEmpty
                    ? LocalAiInstallManifest.HubCacheReceiptSchemaVersion
                    : LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            EngineVersion = engineVersion,
            Architecture = architecture,
            RuntimeId = runtimeId,
            ModelCatalogId = plan.Model.Id,
            SelectedGpuId = gpuId,
            ExecutablePath = executablePath,
            RuntimeAssets = runtimeAssets,
            ModelPath = Path.GetRelativePath(paths.RootDirectory, modelInstall.LegacyModelPath),
            ModelCacheRoot = modelInstall.CacheRoot,
            CachedModelPath = modelInstall.ModelPath,
            ModelId = $"{modelSource.RepositoryId}@{modelSource.RevisionSha}",
            ModelAlias = plan.Model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(plan.Model.Weights.RelativePath),
                SourceUrl = plan.Model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = plan.Model.Weights.SizeBytes,
                Sha256 = plan.Model.Weights.Sha256.Value,
            },
            CustomModel = plan.Model.CustomRecipe,
            // Leave these at their unset default (not an explicitly-built empty
            // array) when there is nothing to add, so schema-4 manifests omit
            // them from JSON entirely -- see the properties' remarks.
            AdditionalModelAssets = additionalArtifacts.IsEmpty ? default : additionalModelAssets.MoveToImmutable(),
            AdditionalModelPaths = additionalArtifacts.IsEmpty ? default : additionalModelPaths.MoveToImmutable(),
            RequestedPort = requestedPort,
            Endpoint = null,
            ContextLength = plan.Profile.ContextTokens,
            KeyCachePrecision = plan.Profile.KeyCachePrecision,
            ValueCachePrecision = plan.Profile.ValueCachePrecision,
            DraftKeyCachePrecision = plan.Profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = plan.Profile.DraftValueCachePrecision,
        };
    }

    public static ImmutableArray<LocalAiAssetReceipt> BuildRuntimeReceipts(
        LlamaRuntimeVariant runtime,
        LlamaRuntimeInstallResult install)
    {
        if (install.VerifiedArchives.Count != runtime.Artifacts.Count)
            throw new InvalidDataException("The installed llama-server archive receipt set is incomplete.");

        var receipts = ImmutableArray.CreateBuilder<LocalAiAssetReceipt>(runtime.Artifacts.Count);
        foreach (PinnedArtifact artifact in runtime.Artifacts)
        {
            string fileName = Path.GetFileName(artifact.RelativePath);
            LocalAiVerifiedArchive verified = install.VerifiedArchives.SingleOrDefault(
                candidate => string.Equals(candidate.FileName, fileName, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' is missing.");
            if (verified.SizeBytes != artifact.SizeBytes ||
                !string.Equals(verified.Sha256, artifact.Sha256.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' does not match its pin.");
            }

            receipts.Add(new LocalAiAssetReceipt
            {
                FileName = fileName,
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = verified.SizeBytes,
                Sha256 = verified.Sha256,
            });
        }

        return receipts.MoveToImmutable();
    }
}
