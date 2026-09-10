using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.Shared.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        if (!IsCanonicalLegacyModelPath(
                paths,
                legacyInstall.ModelPath,
                provenance,
                manifest.ModelAsset.FileName))
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
        string temporaryFileName = $".openclaw-migration-{expectedSha256.Value}.partial";
        string destinationFileName = Path.GetFileName(cachedModelPath);
        SafeCacheDirectory? directory = null;
        CacheMigrationFile? temporary = null;
        FileStream? source = null;
        try
        {
            if (Directory.Exists(destinationDirectory))
            {
                directory = SafeCacheDirectory.OpenOrCreate(cacheRoot, destinationDirectory);
                temporary = await TryOpenVerifiedTemporaryAsync(
                        directory,
                        temporaryFileName,
                        manifest.ModelAsset,
                        expectedSha256,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (temporary is null)
            {
                source = await OpenLegacySourceIfAvailableAsync(
                        paths.ModelsDirectory,
                        legacyInstall.ModelPath,
                        manifest.ModelAsset,
                        expectedSha256,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (source is null)
                    return manifest;

                EnsureSufficientFreeSpace(destinationDirectory, manifest.ModelAsset.SizeBytes);
                if (directory is null)
                {
                    directory = SafeCacheDirectory.OpenOrCreate(cacheRoot, destinationDirectory);
                    temporary = await TryOpenVerifiedTemporaryAsync(
                            directory,
                            temporaryFileName,
                            manifest.ModelAsset,
                            expectedSha256,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (temporary is null)
            {
                temporary = directory!.CreateNew(temporaryFileName);
                await CopyVerifiedSourceAsync(
                        source!,
                        temporary.Stream,
                        manifest.ModelAsset.SizeBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                await temporary.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Stream.Flush(flushToDisk: true);
                if (!await IsVerifiedOpenCacheFileAsync(
                        temporary.Stream,
                        manifest.ModelAsset,
                        expectedSha256,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The copied Hugging Face cache model does not match its receipt.");
                }
            }

            try
            {
                await beforePromotion(cachedModelPath, cancellationToken).ConfigureAwait(false);
                temporary.Promote(destinationFileName);
            }
            catch (IOException ex)
            {
                temporary.Dispose();
                temporary = null;
                if (!PathEntryExists(cachedModelPath))
                {
                    throw new InvalidDataException(
                        "The Hugging Face cache migration temporary file could not be promoted safely.",
                        ex);
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

            directory!.RequirePromotedFile(temporary.Stream.SafeFileHandle, destinationFileName);
            temporary.Commit();
            return CreateMigratedManifest(manifest, cacheRoot, cachedModelPath);
        }
        finally
        {
            temporary?.Dispose();
            directory?.Dispose();
            if (source is not null)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<CacheMigrationFile?> TryOpenVerifiedTemporaryAsync(
        SafeCacheDirectory directory,
        string temporaryFileName,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        CacheMigrationFile? temporary = directory.TryOpenExisting(temporaryFileName);
        if (temporary is null)
            return null;
        try
        {
            if (await IsVerifiedOpenCacheFileAsync(
                    temporary.Stream,
                    receipt,
                    expectedSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return temporary;
            }
        }
        catch
        {
            temporary.Dispose();
            throw;
        }

        temporary.Dispose();
        return null;
    }

    private static async Task<FileStream?> OpenLegacySourceIfAvailableAsync(
        string modelsDirectory,
        string legacyModelPath,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        FileStream? source = OpenContainedReadFile(modelsDirectory, legacyModelPath);
        if (source is null)
            return null;

        return await OpenVerifiedLegacySourceAsync(
                source,
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
        LocalAiManifestStore.HuggingFaceModelProvenance provenance,
        string fileName)
    {
        string[] repositorySegments = provenance.RepositoryId.Split('/');
        try
        {
            string expected = WindowsPathSafety.NormalizePath(
                Path.Combine(
                    [
                        paths.ModelsDirectory,
                        repositorySegments[0],
                        repositorySegments[1],
                        provenance.Revision,
                        fileName,
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
        FileStream stream,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
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

    private static async Task<bool> IsVerifiedOpenCacheFileAsync(
        FileStream stream,
        LocalAiAssetReceipt receipt,
        Sha256Digest expectedSha256,
        IProgress<LocalAiModelMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (stream.Length != receipt.SizeBytes)
            return false;

        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long completed = 0;
        Report(
            progress,
            LocalAiModelMigrationPhase.VerifyingCacheCopy,
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
                LocalAiModelMigrationPhase.VerifyingCacheCopy,
                completed,
                receipt.SizeBytes);
        }

        bool verified = completed == receipt.SizeBytes &&
            CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(expectedSha256.Value));
        stream.Position = 0;
        return verified;
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
        string capacityPath = NormalizeCapacityPath(
            FindExistingDirectory(destinationDirectory));
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

    private static string NormalizeCapacityPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root is not null &&
            WindowsPathSafety.PathEquals(
                WindowsPathSafety.NormalizePath(path),
                WindowsPathSafety.NormalizePath(root))
            ? WindowsPathSafety.EnsureTrailingDirectorySeparator(root)
            : path;
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

    /// <summary>
    /// Roots every cache mutation in directory handles whose identities are checked
    /// against the configured cache root, so path swaps cannot redirect file writes.
    /// </summary>
    private sealed class SafeCacheDirectory : IDisposable
    {
        private readonly SafeFileHandle _cacheRootHandle;
        private readonly SafeFileHandle _directoryHandle;
        private readonly string _destinationPath;
        private readonly bool _isNetworkCache;

        private SafeCacheDirectory(
            SafeFileHandle cacheRootHandle,
            SafeFileHandle directoryHandle,
            string destinationPath,
            bool isNetworkCache)
        {
            _cacheRootHandle = cacheRootHandle;
            _directoryHandle = directoryHandle;
            _destinationPath = destinationPath;
            _isNetworkCache = isNetworkCache;
        }

        public static SafeCacheDirectory OpenOrCreate(
            string cacheRoot,
            string destinationDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Local AI Hugging Face cache migration requires Windows handle-relative file operations.");
            }

            string normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            string normalizedDestination = WindowsPathSafety.NormalizePath(destinationDirectory);
            if (!WindowsPathSafety.IsStrictDescendant(normalizedDestination, normalizedRoot))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration destination escaped its cache root.");
            }
            if (File.Exists(normalizedRoot))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache root is an existing file.");
            }

            SafeFileHandle? rootHandle = null;
            SafeFileHandle? directoryHandle = null;
            try
            {
                rootHandle = OpenAbsoluteDirectory(normalizedRoot, create: true);
                if (rootHandle is null)
                    throw new InvalidDataException("The Hugging Face cache root could not be created.");
                directoryHandle = rootHandle;
                string[] segments = Path.GetRelativePath(normalizedRoot, normalizedDestination).Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string segment in segments)
                {
                    SafeFileHandle next = OpenOrCreateRelativeDirectory(directoryHandle, segment);
                    if (!ReferenceEquals(directoryHandle, rootHandle))
                        directoryHandle.Dispose();
                    directoryHandle = next;
                }

                var result = new SafeCacheDirectory(
                    rootHandle,
                    directoryHandle,
                    normalizedDestination,
                    normalizedRoot.StartsWith(@"\\", StringComparison.Ordinal));
                result.RequireDirectoryInsideCacheRoot();
                rootHandle = null;
                directoryHandle = null;
                return result;
            }
            finally
            {
                rootHandle?.Dispose();
                directoryHandle?.Dispose();
            }
        }

        public CacheMigrationFile? TryOpenExisting(string fileName)
        {
            ValidateFileName(fileName);
            int status = OpenRelativeFile(
                _directoryHandle,
                fileName,
                GenericRead | DeleteAccess | SynchronizeAccess,
                FileOpen,
                out SafeFileHandle? handle);
            if (status != 0)
            {
                int error = checked((int)RtlNtStatusToDosError(status));
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;
                throw CreateNativeIOException(
                    "The Hugging Face cache migration temporary file could not be opened safely.",
                    error);
            }

            var file = new CacheMigrationFile(handle!, FileAccess.Read, this);
            try
            {
                BY_HANDLE_FILE_INFORMATION info = GetHandleInformation(handle!);
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    info.NumberOfLinks != 1)
                {
                    file.Delete();
                    file.Dispose();
                    return null;
                }

                RequireContainedFile(handle!, fileName);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public CacheMigrationFile CreateNew(string fileName)
        {
            ValidateFileName(fileName);
            int status = OpenRelativeFile(
                _directoryHandle,
                fileName,
                GenericRead | GenericWrite | DeleteAccess | SynchronizeAccess,
                FileCreate,
                out SafeFileHandle? handle);
            if (status != 0)
            {
                throw CreateNativeIOException(
                    "The Hugging Face cache migration temporary file could not be claimed safely.",
                    checked((int)RtlNtStatusToDosError(status)));
            }

            var file = new CacheMigrationFile(handle!, FileAccess.ReadWrite, this);
            try
            {
                RequireContainedFile(handle!, fileName);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void RequirePromotedFile(SafeFileHandle handle, string fileName)
        {
            RequireContainedFile(handle, fileName);
            using SafeFileHandle namedDirectory = OpenDirectory(
                _destinationPath,
                openReparsePoint: true);
            BY_HANDLE_FILE_INFORMATION held = GetHandleInformation(_directoryHandle);
            BY_HANDLE_FILE_INFORMATION named = GetHandleInformation(namedDirectory);
            if ((named.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                held.VolumeSerialNumber != named.VolumeSerialNumber ||
                held.FileIndexHigh != named.FileIndexHigh ||
                held.FileIndexLow != named.FileIndexLow)
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration destination directory changed during promotion.");
            }
        }

        public void Rename(SafeFileHandle handle, string fileName)
        {
            ValidateFileName(fileName);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + sizeof(int);
            byte[] nameBytes = Encoding.Unicode.GetBytes(fileName);
            IntPtr buffer = Marshal.AllocHGlobal(nameOffset + nameBytes.Length);
            try
            {
                for (int index = 0; index < nameOffset; index++)
                    Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteIntPtr(
                    buffer,
                    rootOffset,
                    _isNetworkCache ? IntPtr.Zero : _directoryHandle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
                int status = NtSetInformationFile(
                    handle,
                    out _,
                    buffer,
                    (uint)(nameOffset + nameBytes.Length),
                    FileRenameInformation);
                if (status != 0)
                {
                    throw CreateNativeIOException(
                        "The Hugging Face cache migration temporary file could not be promoted safely.",
                        checked((int)RtlNtStatusToDosError(status)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            _directoryHandle.Dispose();
            _cacheRootHandle.Dispose();
        }

        private void RequireContainedFile(SafeFileHandle handle, string fileName)
        {
            RequireDirectoryInsideCacheRoot();
            string finalDirectory = GetFinalPathFromHandle(_directoryHandle);
            string finalFile = GetFinalPathFromHandle(handle);
            string expected = WindowsPathSafety.NormalizePath(Path.Combine(finalDirectory, fileName));
            if (!WindowsPathSafety.PathEquals(finalFile, expected))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration file resolved outside its validated destination directory.");
            }
        }

        private void RequireDirectoryInsideCacheRoot()
        {
            string finalRoot = GetFinalPathFromHandle(_cacheRootHandle);
            string finalDirectory = GetFinalPathFromHandle(_directoryHandle);
            if (!WindowsPathSafety.IsStrictDescendant(finalDirectory, finalRoot))
            {
                throw new InvalidDataException(
                    "The Hugging Face cache migration destination directory resolved outside its cache root.");
            }
        }

        private static SafeFileHandle OpenDirectory(string path, bool openReparsePoint)
        {
            uint flags = FileFlagBackupSemantics;
            if (openReparsePoint)
                flags |= FileFlagOpenReparsePoint;
            SafeFileHandle handle = CreateFileW(
                path,
                GenericRead,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw CreateNativeIOException(
                    $"The Hugging Face cache directory '{path}' could not be opened safely.",
                    Marshal.GetLastWin32Error());
            }

            return handle;
        }

        private static SafeFileHandle OpenOrCreateRelativeDirectory(
            SafeFileHandle parent,
            string segment) =>
            OpenRelativeDirectory(parent, segment, create: true);

        internal static SafeFileHandle OpenRelativeDirectory(
            SafeFileHandle parent,
            string segment,
            bool create) =>
            TryOpenRelativeDirectory(parent, segment, create)
            ?? throw new DirectoryNotFoundException(
                "The Hugging Face cache migration directory does not exist.");

        internal static SafeFileHandle? TryOpenRelativeDirectory(
            SafeFileHandle parent,
            string segment,
            bool create)
        {
            ValidateFileName(segment);
            int status = OpenRelative(
                parent,
                segment,
                DirectoryAccess | SynchronizeAccess,
                FileShare.ReadWrite | FileShare.Delete,
                create ? FileOpenIf : FileOpen,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                out SafeFileHandle? handle);
            if (status != 0)
            {
                int error = checked((int)RtlNtStatusToDosError(status));
                if (!create && error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;
                throw CreateNativeIOException(
                    "The Hugging Face cache migration directory could not be opened or created safely.",
                    error);
            }

            BY_HANDLE_FILE_INFORMATION info = GetHandleInformation(handle!);
            if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                handle!.Dispose();
                throw new InvalidDataException(
                    "The Hugging Face cache migration destination contains a reparse point.");
            }
            return handle!;
        }

        internal static SafeFileHandle? OpenAbsoluteDirectory(string path, bool create)
        {
            string normalizedPath = WindowsPathSafety.NormalizePath(path);
            string pathRoot = Path.GetPathRoot(normalizedPath)
                ?? throw new InvalidDataException("The Windows path has no volume root.");
            SafeFileHandle root = OpenDirectory(pathRoot, openReparsePoint: true);
            if (WindowsPathSafety.PathEquals(normalizedPath, pathRoot))
                return root;

            SafeFileHandle current = root;
            try
            {
                foreach (string segment in Path.GetRelativePath(pathRoot, normalizedPath).Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    SafeFileHandle? next = TryOpenRelativeDirectory(current, segment, create);
                    if (next is null)
                    {
                        if (!ReferenceEquals(current, root))
                            current.Dispose();
                        root.Dispose();
                        return null;
                    }
                    if (!ReferenceEquals(current, root))
                        current.Dispose();
                    current = next;
                }

                if (ReferenceEquals(current, root))
                    return root;
                root.Dispose();
                return current;
            }
            catch
            {
                if (!ReferenceEquals(current, root))
                    current.Dispose();
                root.Dispose();
                throw;
            }
        }

        private static void ValidateFileName(string fileName)
        {
            if (!WindowsPathSafety.IsSafeSegment(fileName))
                throw new InvalidDataException("The Hugging Face cache migration file name is unsafe.");
        }
    }

    private static FileStream? OpenContainedReadFile(
        string containedRoot,
        string candidatePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Local AI Hugging Face cache migration requires Windows handle-relative file operations.");
        }

        string normalizedRoot = WindowsPathSafety.NormalizePath(containedRoot);
        string normalizedCandidate = WindowsPathSafety.NormalizePath(candidatePath);
        if (!WindowsPathSafety.IsStrictDescendant(normalizedCandidate, normalizedRoot))
            throw new InvalidDataException("The legacy Local AI model escaped its managed model root.");

        using SafeFileHandle? root = SafeCacheDirectory.OpenAbsoluteDirectory(
            normalizedRoot,
            create: false);
        if (root is null)
            return null;
        string relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        SafeFileHandle directory = root;
        try
        {
            for (int index = 0; index < segments.Length - 1; index++)
            {
                SafeFileHandle? next = SafeCacheDirectory.TryOpenRelativeDirectory(
                    directory,
                    segments[index],
                    create: false);
                if (next is null)
                    return null;
                if (!ReferenceEquals(directory, root))
                    directory.Dispose();
                directory = next;
            }

            int status = OpenRelative(
                directory,
                segments[^1],
                GenericRead | SynchronizeAccess,
                FileShare.Read,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                out SafeFileHandle? handle);
            if (status != 0)
            {
                int error = checked((int)RtlNtStatusToDosError(status));
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;
                throw CreateNativeIOException(
                    "The legacy Local AI model could not be opened safely for cache migration.",
                    error);
            }

            try
            {
                BY_HANDLE_FILE_INFORMATION info = GetHandleInformation(handle!);
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    info.NumberOfLinks != 1 ||
                    !WindowsPathSafety.PathEquals(
                        GetFinalPathFromHandle(handle!),
                        normalizedCandidate))
                {
                    throw new InvalidDataException(
                        "The legacy Local AI model path is not a safely owned regular file.");
                }

                return new FileStream(
                    handle!,
                    FileAccess.Read,
                    CopyBufferSize,
                    isAsync: false);
            }
            catch
            {
                handle!.Dispose();
                throw;
            }
        }
        finally
        {
            if (!ReferenceEquals(directory, root))
                directory.Dispose();
        }
    }

    private sealed class CacheMigrationFile : IAsyncDisposable, IDisposable
    {
        private readonly SafeCacheDirectory _directory;
        private bool _deleteOnDispose = true;
        private bool _disposed;

        public CacheMigrationFile(
            SafeFileHandle handle,
            FileAccess access,
            SafeCacheDirectory directory)
        {
            _directory = directory;
            Stream = new FileStream(handle, access, CopyBufferSize, isAsync: false);
        }

        public FileStream Stream { get; }

        public void Promote(string destinationFileName) =>
            _directory.Rename(Stream.SafeFileHandle, destinationFileName);

        public void Commit() => _deleteOnDispose = false;

        public void Delete()
        {
            if (_disposed || !_deleteOnDispose)
                return;

            SetDeleteDisposition(Stream.SafeFileHandle);
            _deleteOnDispose = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            try
            {
                if (_deleteOnDispose)
                    SetDeleteDisposition(Stream.SafeFileHandle);
            }
            finally
            {
                _deleteOnDispose = false;
                _disposed = true;
                Stream.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static int OpenRelativeFile(
        SafeFileHandle directoryHandle,
        string fileName,
        uint desiredAccess,
        uint createDisposition,
        out SafeFileHandle? handle) =>
        OpenRelative(
            directoryHandle,
            fileName,
            desiredAccess,
            shareAccess: 0,
            createDisposition,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            out handle);

    // NtCreateFile is used with RootDirectory so creation/opening remains bound to
    // the validated directory object even if its pathname is concurrently replaced.
    private static int OpenRelative(
        SafeFileHandle directoryHandle,
        string fileName,
        uint desiredAccess,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions,
        out SafeFileHandle? handle)
    {
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(fileName);
        IntPtr unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        try
        {
            var unicodeString = new UNICODE_STRING
            {
                Length = checked((ushort)(fileName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((fileName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);
            var objectAttributes = new OBJECT_ATTRIBUTES
            {
                Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectCaseInsensitive,
            };
            int status = NtCreateFile(
                out IntPtr rawHandle,
                desiredAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                (uint)FileAttributes.Normal,
                (uint)shareAccess,
                createDisposition,
                createOptions,
                IntPtr.Zero,
                0);
            handle = status == 0
                ? new SafeFileHandle(rawHandle, ownsHandle: true)
                : null;
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void SetDeleteDisposition(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfo,
                    buffer,
                    1))
            {
                throw CreateNativeIOException(
                    "The Hugging Face cache migration temporary file could not be removed safely.",
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static BY_HANDLE_FILE_INFORMATION GetHandleInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {
            throw CreateNativeIOException(
                "The Hugging Face cache migration handle could not be inspected safely.",
                Marshal.GetLastWin32Error());
        }
        return info;
    }

    private static string GetFinalPathFromHandle(SafeFileHandle handle)
    {
        int capacity = 512;
        while (capacity <= 32_768)
        {
            var builder = new StringBuilder(capacity);
            uint length = GetFinalPathNameByHandleW(
                handle,
                builder,
                (uint)builder.Capacity,
                0);
            if (length == 0)
            {
                throw CreateNativeIOException(
                    "The Hugging Face cache migration handle path could not be resolved safely.",
                    Marshal.GetLastWin32Error());
            }
            if (length < builder.Capacity)
                return WindowsPathSafety.NormalizePath(NormalizeFinalPath(builder.ToString()));
            capacity = checked((int)length + 1);
        }

        throw new IOException("A resolved Hugging Face cache migration path exceeded the supported length.");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUncPrefix.Length..];
        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static IOException CreateNativeIOException(string message, int error) =>
        new($"{message} {new Win32Exception(error).Message} (Win32 error {error}).");

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint DirectoryAccess = 0x00000081;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInformation = 10;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IO_STATUS_BLOCK ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out BY_HANDLE_FILE_INFORMATION fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
