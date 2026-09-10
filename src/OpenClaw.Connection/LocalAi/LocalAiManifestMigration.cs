using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.Shared.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenClaw.Connection.LocalAi;

/// <summary>
/// Copies schema-3 model weights into a standard Hugging Face snapshot path and
/// records the verified destination without changing the active legacy model path.
/// </summary>
internal static class LocalAiManifestMigration
{
    private const int CopyBufferSize = 1024 * 1024;

    public static async Task<LocalAiInstallManifest> MigrateAsync(
        LocalAiPaths paths,
        LocalAiResolvedInstall legacyInstall,
        string cacheRoot,
        IProgress<LocalAiModelMigrationProgress>? progress,
        Func<string, CancellationToken, Task> beforePromotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(legacyInstall);
        if (legacyInstall.Manifest.SchemaVersion != LocalAiInstallManifest.CurrentSchemaVersion)
            return legacyInstall.Manifest;

        LocalAiInstallManifest manifest = legacyInstall.Manifest;
        LocalAiManifestStore.HuggingFaceModelProvenance provenance =
            GetProvenance(manifest);
        if (!IsCanonicalLegacyModelPath(paths, legacyInstall.ModelPath, provenance))
            return manifest;

        var expectedSha256 = new Sha256Digest(manifest.ModelAsset.Sha256);
        if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                cacheRoot,
                provenance.RepositoryId,
                provenance.Revision,
                provenance.RelativePath,
                out string cachedModelPath,
                out _,
                out string pathError))
        {
            throw new InvalidDataException(pathError);
        }

        if (Directory.Exists(cachedModelPath))
        {
            throw new InvalidDataException(
                "The Hugging Face cache migration destination is an existing directory.");
        }

        if (PathEntryExists(cachedModelPath))
        {
            await RequireVerifiedCacheDestinationAsync(
                    cacheRoot,
                    cachedModelPath,
                    manifest.ModelAsset,
                    expectedSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            return CreateMigratedManifest(manifest, cacheRoot, cachedModelPath);
        }

        string destinationDirectory = Path.GetDirectoryName(cachedModelPath)
            ?? throw new InvalidDataException("The Hugging Face cache migration destination has no parent directory.");
        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".openclaw-migration-{expectedSha256.Value}.partial");
        bool ownsTemporary = false;
        try
        {
            bool hasVerifiedTemporary = await IsVerifiedCacheFileAsync(
                    cacheRoot,
                    temporaryPath,
                    manifest.ModelAsset,
                    expectedSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            await using FileStream? source = hasVerifiedTemporary
                ? null
                : await OpenLegacySourceIfAvailableAsync(
                        legacyInstall.ModelPath,
                        manifest.ModelAsset,
                        expectedSha256,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!hasVerifiedTemporary && source is null)
                return manifest;

            EnsureSafeDestinationDirectory(cacheRoot, destinationDirectory);
            if (!hasVerifiedTemporary)
            {
                RemoveUnverifiedTemporaryEntry(temporaryPath);
                EnsureSufficientFreeSpace(
                    destinationDirectory,
                    manifest.ModelAsset.SizeBytes);
                await using (FileStream destination = OpenOwnedTemporaryFile(
                    temporaryPath,
                    out ownsTemporary))
                {
                    await CopyVerifiedSourceAsync(
                            source!,
                            destination,
                            manifest.ModelAsset.SizeBytes,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }

                await RequireVerifiedCacheDestinationAsync(
                        cacheRoot,
                        temporaryPath,
                        manifest.ModelAsset,
                        expectedSha256,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await beforePromotion(cachedModelPath, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, cachedModelPath, overwrite: false);
                ownsTemporary = false;
            }
            catch (IOException)
            {
                if (!PathEntryExists(cachedModelPath))
                    throw;
            }

            await RequireVerifiedCacheDestinationAsync(
                    cacheRoot,
                    cachedModelPath,
                    manifest.ModelAsset,
                    expectedSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return CreateMigratedManifest(manifest, cacheRoot, cachedModelPath);
        }
        finally
        {
            if (ownsTemporary)
                DeleteTemporaryCreatedThisRun(temporaryPath);
        }
    }

    private static async Task<FileStream?> OpenLegacySourceIfAvailableAsync(
        string legacyModelPath,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(legacyModelPath))
            throw new InvalidDataException("The legacy Local AI model path is an existing directory.");
        if (!PathEntryExists(legacyModelPath))
            return null;

        return await OpenVerifiedLegacySourceAsync(
                legacyModelPath,
                receipt,
                expectedSha256,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static LocalAiManifestStore.HuggingFaceModelProvenance GetProvenance(
        LocalAiInstallManifest manifest)
    {
        int separator = manifest.ModelId.LastIndexOf('@');
        string repositoryId = manifest.ModelId[..separator];
        string revision = manifest.ModelId[(separator + 1)..];
        var source = new Uri(manifest.ModelAsset.SourceUrl, UriKind.Absolute);
        string prefix = $"/{repositoryId}/resolve/{revision}/";
        string relativePath = Uri.UnescapeDataString(source.AbsolutePath)[prefix.Length..];
        return new(repositoryId, revision, relativePath);
    }

    private static bool IsCanonicalLegacyModelPath(
        LocalAiPaths paths,
        string modelPath,
        LocalAiManifestStore.HuggingFaceModelProvenance provenance)
    {
        string[] repositorySegments = provenance.RepositoryId.Split('/');
        string[] relativeSegments = provenance.RelativePath.Split('/');
        try
        {
            string expected = WindowsPathSafety.NormalizePath(
                Path.Combine(
                    [
                        paths.ModelsDirectory,
                        repositorySegments[0],
                        repositorySegments[1],
                        provenance.Revision,
                        .. relativeSegments,
                    ]));
            return WindowsPathSafety.PathEquals(modelPath, expected);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static LocalAiInstallManifest CreateMigratedManifest(
        LocalAiInstallManifest manifest,
        string cacheRoot,
        string cachedModelPath) =>
        manifest with
        {
            SchemaVersion = LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
            ModelCacheRoot = WindowsPathSafety.NormalizePath(cacheRoot),
            CachedModelPath = WindowsPathSafety.NormalizePath(cachedModelPath),
        };

    private static async Task<FileStream> OpenVerifiedLegacySourceAsync(
        string sourcePath,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidDataException(
                "The legacy Local AI model could not be opened safely for cache migration.",
                ex);
        }

        bool verified = false;
        try
        {
            if (stream.Length != receipt.SizeBytes)
                throw new InvalidDataException("The legacy Local AI model size does not match its receipt.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferSize];
            long completed = 0;
            Report(
                progress,
                LocalAiModelMigrationPhase.VerifyingLegacyModel,
                completed,
                receipt.SizeBytes);
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                completed += read;
                Report(
                    progress,
                    LocalAiModelMigrationPhase.VerifyingLegacyModel,
                    completed,
                    receipt.SizeBytes);
            }

            if (completed != receipt.SizeBytes ||
                !CryptographicOperations.FixedTimeEquals(
                    hash.GetHashAndReset(),
                    Convert.FromHexString(expectedSha256.Value)))
            {
                throw new InvalidDataException(
                    "The legacy Local AI model SHA-256 digest does not match its receipt.");
            }

            stream.Position = 0;
            verified = true;
            return stream;
        }
        finally
        {
            if (!verified)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RequireVerifiedCacheDestinationAsync(
        string cacheRoot,
        string candidatePath,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    cacheRoot,
                    candidatePath,
                    receipt.SizeBytes,
                    expectedSha256,
                    new CacheVerificationProgress(progress, receipt.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);
        if (verified is null)
        {
            throw new InvalidDataException(
                "The Hugging Face cache migration destination is unsafe or does not match its receipt.");
        }
    }

    private static async Task<bool> IsVerifiedCacheFileAsync(
        string cacheRoot,
        string candidatePath,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!PathEntryExists(candidatePath))
            return false;

        await using FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    cacheRoot,
                    candidatePath,
                    receipt.SizeBytes,
                    expectedSha256,
                    new CacheVerificationProgress(progress, receipt.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);
        return verified is not null;
    }

    private static async Task CopyVerifiedSourceAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long completed = 0;
        Report(progress, LocalAiModelMigrationPhase.CopyingToCache, completed, totalBytes);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            completed += read;
            Report(progress, LocalAiModelMigrationPhase.CopyingToCache, completed, totalBytes);
        }

        if (completed != totalBytes)
            throw new InvalidDataException("The verified legacy Local AI model changed during cache migration.");
    }

    private static void EnsureSufficientFreeSpace(
        string destinationDirectory,
        long requiredBytes)
    {
        string capacityPath = FindExistingDirectory(destinationDirectory);
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceEx(
                    capacityPath,
                    out ulong availableBytes,
                    out _,
                    out _))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache volume capacity could not be inspected safely.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (availableBytes < (ulong)requiredBytes)
            {
                throw new InvalidDataException(
                    "The Hugging Face cache volume does not have enough free space for the Local AI model migration.");
            }
            return;
        }

        try
        {
            string root = Path.GetPathRoot(capacityPath)
                ?? throw new InvalidDataException(
                    "The Hugging Face cache volume capacity could not be inspected safely.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                throw new InvalidDataException(
                    "The Hugging Face cache volume capacity could not be inspected safely.");
            }
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new InvalidDataException(
                    "The Hugging Face cache volume does not have enough free space for the Local AI model migration.");
            }
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            throw new InvalidDataException(
                "The Hugging Face cache volume capacity could not be inspected safely.",
                ex);
        }
    }

    private static string FindExistingDirectory(string path)
    {
        string? current = path;
        while (current is not null && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);
        return current
            ?? throw new InvalidDataException(
                "The Hugging Face cache volume capacity could not be inspected safely.");
    }

    private static void Report(
        IProgress<LocalAiModelMigrationProgress>? progress,
        LocalAiModelMigrationPhase phase,
        long completedBytes,
        long totalBytes) =>
        progress?.Report(new LocalAiModelMigrationProgress(
            phase,
            completedBytes,
            totalBytes));

    private static FileStream OpenOwnedTemporaryFile(
        string temporaryPath,
        out bool ownsTemporary)
    {
        ownsTemporary = false;
        try
        {
            if (Directory.Exists(temporaryPath))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration temporary path is an existing directory.");
            }

            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            ownsTemporary = true;
            return stream;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "The Hugging Face cache migration temporary file could not be claimed safely.",
                ex);
        }
    }

    private static void RemoveUnverifiedTemporaryEntry(string temporaryPath)
    {
        try
        {
            if (Directory.Exists(temporaryPath))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration temporary path is an existing directory.");
            }

            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "The Hugging Face cache migration temporary file could not be replaced safely.",
                ex);
        }
    }

    private static void EnsureSafeDestinationDirectory(
        string cacheRoot,
        string destinationDirectory)
    {
        string normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
        string normalizedDestination = WindowsPathSafety.NormalizePath(destinationDirectory);
        if (!WindowsPathSafety.IsStrictDescendant(normalizedDestination, normalizedRoot))
            throw new InvalidDataException("The Hugging Face cache migration destination escaped its cache root.");

        EnsureDirectory(normalizedRoot, rejectReparsePoint: false);
        string current = normalizedRoot;
        foreach (string segment in Path.GetRelativePath(normalizedRoot, normalizedDestination).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureDirectory(current, rejectReparsePoint: true);
        }
    }

    private static void EnsureDirectory(string path, bool rejectReparsePoint)
    {
        try
        {
            if (File.Exists(path))
                throw new InvalidDataException($"The Hugging Face cache path '{path}' is an existing file.");

            Directory.CreateDirectory(path);
            if (rejectReparsePoint &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The Hugging Face cache path '{path}' is a reparse point.");
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                $"The Hugging Face cache path '{path}' could not be created safely.",
                ex);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            throw new InvalidDataException(
                $"The Hugging Face cache migration destination '{path}' could not be inspected safely.",
                ex);
        }
    }

    private static void DeleteTemporaryCreatedThisRun(string temporaryPath)
    {
        try
        {
            if (!File.Exists(temporaryPath))
                return;
            if ((File.GetAttributes(temporaryPath) & FileAttributes.ReparsePoint) != 0)
                return;
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            // A failed cleanup must not replace the migration result.
        }
    }

    private sealed class CacheVerificationProgress(
        IProgress<LocalAiModelMigrationProgress>? progress,
        long totalBytes) : IProgress<long>
    {
        public void Report(long value) =>
            LocalAiManifestMigration.Report(
                progress,
                LocalAiModelMigrationPhase.VerifyingCacheCopy,
                value,
                totalBytes);
    }
}
