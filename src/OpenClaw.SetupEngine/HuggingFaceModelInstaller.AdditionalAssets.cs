using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Acquisition for additional model assets (a DFlash draft checkpoint)
/// verified into the same standard Hugging Face hub cache <see cref="HuggingFaceModelInstaller.InstallAsync"/> uses for the
/// primary weights. Reuses the same resumable download, hash verification,
/// and safe-cache-directory promotion helpers; the one thing it deliberately
/// omits is the legacy app-owned compatibility copy, since every recipe that
/// pins an additional asset is new -- there is no pre-hub-cache install of
/// it to stay compatible with.
/// </summary>
internal sealed partial class HuggingFaceModelInstaller
{
    public async Task<HuggingFaceAdditionalAssetInstallResult> InstallAdditionalAssetAsync(
        string localDataDirectory,
        PinnedArtifact artifact,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Role != ArtifactRole.ModelWeights || artifact.Source is not HuggingFaceRevisionSource source)
        {
            throw new HuggingFaceModelInstallException(
                "An additional Local AI model asset must be an immutable Hugging Face weights artifact.");
        }

        string cacheRoot = _cacheRootResolver();
        if (!TryValidateCacheRootOwnershipBoundary(localDataDirectory, cacheRoot, out string cacheRootError))
            throw new HuggingFaceModelInstallException(cacheRootError);
        if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                cacheRoot,
                source.RepositoryId,
                source.RevisionSha,
                artifact.RelativePath,
                out string modelPath,
                out string partialPath,
                out string pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        if (Directory.Exists(modelPath))
            throw new HuggingFaceModelInstallException("The managed Local AI model path is an existing directory.");
        if (Directory.Exists(partialPath))
            throw new HuggingFaceModelInstallException("The managed Local AI partial model path is an existing directory.");

        await using (FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    cacheRoot,
                    modelPath,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    new VerificationProgress(this, progress, artifact.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (verified is not null)
            {
                return new HuggingFaceAdditionalAssetInstallResult(
                    modelPath, cacheRoot, HuggingFaceModelInstallDisposition.ReusedVerified, CreatedThisRun: false);
            }
        }

        if (PathEntryExists(modelPath))
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face cache destination '{modelPath}' is unsafe or does not match " +
                "the pinned model. Remove it manually and retry setup.");
        }

        string destinationDirectory = Path.GetDirectoryName(modelPath)
            ?? throw new HuggingFaceModelInstallException(
                "The Hugging Face cache destination has no parent directory.");
        string destinationFileName = Path.GetFileName(modelPath);
        string partialFileName = Path.GetFileName(partialPath);
        LocalAiManifestMigration.SafeCacheDirectory? directory = null;
        LocalAiManifestMigration.CacheMigrationFile? partial = null;
        bool partialExistedBeforeInstall = false;
        HuggingFaceModelInstallDisposition disposition = HuggingFaceModelInstallDisposition.Downloaded;
        try
        {
            directory = LocalAiManifestMigration.SafeCacheDirectory.OpenOrCreate(cacheRoot, destinationDirectory);
            partial = directory.TryOpenExisting(partialFileName);
            partialExistedBeforeInstall = partial is not null;

            bool partialVerified = partial is not null &&
                partial.Stream.Length == artifact.SizeBytes &&
                await VerifyOpenFileAsync(partial.Stream, artifact, progress, cancellationToken).ConfigureAwait(false);
            if (partial is not null && partial.Stream.Length >= artifact.SizeBytes && !partialVerified)
            {
                partial.Stream.SetLength(0);
                partial.Stream.Position = 0;
            }

            if (!partialVerified)
            {
                partial ??= directory.CreateNew(partialFileName);
                if (partial.Stream.Length == 0 &&
                    await TryCopyVerifiedBlobAsync(
                            cacheRoot, source.RepositoryId, artifact, partial.Stream, progress, cancellationToken)
                        .ConfigureAwait(false))
                {
                    disposition = HuggingFaceModelInstallDisposition.ReusedVerified;
                }
                else
                {
                    await DownloadAndVerifyAsync(artifact, partial.Stream, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalAiManifestMigration.CacheMigrationFile activePartial = partial
                ?? throw new HuggingFaceModelInstallException("The Hugging Face cache partial was not created.");
            if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                    cacheRoot,
                    source.RepositoryId,
                    source.RevisionSha,
                    artifact.RelativePath,
                    out string revalidatedModelPath,
                    out string revalidatedPartialPath,
                    out pathError) ||
                !string.Equals(modelPath, revalidatedModelPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(partialPath, revalidatedPartialPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new HuggingFaceModelInstallException(
                    string.IsNullOrWhiteSpace(pathError)
                        ? "The Local AI model paths changed before promotion."
                        : pathError);
            }

            if (!await VerifyOpenFileAsync(activePartial.Stream, artifact, progress, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new HuggingFaceModelInstallException(
                    "The Hugging Face cache partial does not match the pinned model.");
            }

            try
            {
                activePartial.Promote(destinationFileName);
            }
            catch (IOException ex)
            {
                if (partialExistedBeforeInstall)
                    activePartial.Commit();
                activePartial.Dispose();
                partial = null;
                await using FileStream? winner =
                    await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                            cacheRoot,
                            modelPath,
                            artifact.SizeBytes,
                            artifact.Sha256,
                            new VerificationProgress(this, progress, artifact.SizeBytes),
                            cancellationToken)
                        .ConfigureAwait(false);
                if (winner is null)
                {
                    throw new HuggingFaceModelInstallException(
                        "The Hugging Face cache destination changed before promotion.", ex);
                }

                return new HuggingFaceAdditionalAssetInstallResult(
                    modelPath, cacheRoot, HuggingFaceModelInstallDisposition.ReusedVerified, CreatedThisRun: false);
            }

            if (partialExistedBeforeInstall)
                activePartial.Commit();
            directory.RequirePromotedFile(activePartial.Stream.SafeFileHandle, destinationFileName);
            activePartial.Commit();
            return new HuggingFaceAdditionalAssetInstallResult(modelPath, cacheRoot, disposition, CreatedThisRun: true);
        }
        catch (OperationCanceledException)
        {
            partial?.Commit();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException)
        {
            partial?.Commit();
            throw;
        }
        catch (HuggingFaceModelInstallException) when (partialExistedBeforeInstall)
        {
            partial?.Commit();
            throw;
        }
        finally
        {
            partial?.Dispose();
            directory?.Dispose();
        }
    }
}
