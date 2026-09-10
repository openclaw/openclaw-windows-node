using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// Resolves and validates paths in the standard Hugging Face hub cache layout.
/// </summary>
/// <remarks>
/// Read and mutation validation are intentionally separate. Reads may follow a
/// standard snapshot symlink into the same repository's blobs directory. This type
/// deliberately does not expose a validate-then-mutate API because returning a path
/// cannot make a later filesystem mutation race-free. Existing files are never
/// considered reusable based on their path alone. Call
/// <see cref="TryOpenVerifiedCacheFileAsync"/> with pinned size and SHA-256 evidence.
/// </remarks>
public static class HuggingFaceHubCache
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int HashBufferSize = 1024 * 1024;
    private const string HubCacheEnvironmentVariable = "HF_HUB_CACHE";
    private const string LegacyHubCacheEnvironmentVariable = "HUGGINGFACE_HUB_CACHE";
    private const string HubHomeEnvironmentVariable = "HF_HOME";
    private const string XdgCacheHomeEnvironmentVariable = "XDG_CACHE_HOME";

    private enum CacheEntryKind
    {
        Blob,
        Snapshot,
    }

    /// <summary>
    /// Resolves the cache root using Hugging Face precedence: <c>HF_HUB_CACHE</c>,
    /// <c>HUGGINGFACE_HUB_CACHE</c>, <c>HF_HOME\hub</c>,
    /// <c>XDG_CACHE_HOME\huggingface\hub</c>, then
    /// <c>%USERPROFILE%\.cache\huggingface\hub</c>.
    /// </summary>
    public static string ResolveCacheRoot() =>
        ResolveCacheRoot(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.ExpandEnvironmentVariables);

    internal static string ResolveCacheRoot(
        Func<string, string?> getEnvironmentVariable,
        string userProfile,
        Func<string, string>? expandEnvironmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        expandEnvironmentVariables ??= Environment.ExpandEnvironmentVariables;

        string? explicitCache = NullIfWhiteSpace(getEnvironmentVariable(HubCacheEnvironmentVariable))
            ?? NullIfWhiteSpace(getEnvironmentVariable(LegacyHubCacheEnvironmentVariable));
        if (explicitCache is not null)
            return NormalizeConfiguredPath(explicitCache, userProfile, expandEnvironmentVariables);

        string? hubHome = NullIfWhiteSpace(getEnvironmentVariable(HubHomeEnvironmentVariable));
        if (hubHome is not null)
            return WindowsPathSafety.NormalizePath(
                Path.Combine(
                    NormalizeConfiguredPath(hubHome, userProfile, expandEnvironmentVariables),
                    "hub"));

        string? xdgCacheHome = NullIfWhiteSpace(getEnvironmentVariable(XdgCacheHomeEnvironmentVariable));
        if (xdgCacheHome is not null)
        {
            return WindowsPathSafety.NormalizePath(
                Path.Combine(
                    NormalizeConfiguredPath(xdgCacheHome, userProfile, expandEnvironmentVariables),
                    "huggingface",
                    "hub"));
        }

        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("The user profile directory is unavailable.");

        return WindowsPathSafety.NormalizePath(
            Path.Combine(userProfile, ".cache", "huggingface", "hub"));
    }

    /// <summary>
    /// Computes the snapshot file path and its same-directory <c>.partial</c>
    /// download path for one immutable repository revision.
    /// </summary>
    public static bool TryGetSnapshotPaths(
        string cacheRoot,
        string repositoryId,
        string revision,
        string relativePath,
        out string snapshotPath,
        out string partialPath,
        out string error)
    {
        snapshotPath = "";
        partialPath = "";

        if (!TryNormalizeRoot(cacheRoot, out string normalizedRoot, out error) ||
            !TryValidateRepositoryId(repositoryId, out string owner, out string repository, out error) ||
            !TryValidateRevision(revision, out error) ||
            !TryGetRelativePathSegments(relativePath, out string[] relativeSegments, out error))
        {
            return false;
        }

        try
        {
            string repositoryDirectory = Path.Combine(
                normalizedRoot,
                $"models--{owner}--{repository}");
            string snapshotDirectory = Path.Combine(
                repositoryDirectory,
                "snapshots",
                revision);
            snapshotPath = WindowsPathSafety.NormalizePath(
                Path.Combine([snapshotDirectory, .. relativeSegments]));
            partialPath = WindowsPathSafety.NormalizePath(snapshotPath + ".partial");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            snapshotPath = "";
            partialPath = "";
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(snapshotPath, normalizedRoot) ||
            !WindowsPathSafety.IsStrictDescendant(partialPath, normalizedRoot))
        {
            snapshotPath = "";
            partialPath = "";
            error = "The Hugging Face snapshot path escaped the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Computes the content-addressed blob path used by Hugging Face for an LFS
    /// artifact. The file at this path still requires pinned integrity verification.
    /// </summary>
    public static bool TryGetBlobPath(
        string cacheRoot,
        string repositoryId,
        Sha256Digest sha256,
        out string blobPath,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        blobPath = "";

        if (!TryNormalizeRoot(cacheRoot, out string normalizedRoot, out error) ||
            !TryValidateRepositoryId(repositoryId, out string owner, out string repository, out error))
        {
            return false;
        }

        try
        {
            blobPath = WindowsPathSafety.NormalizePath(
                Path.Combine(
                    normalizedRoot,
                    $"models--{owner}--{repository}",
                    "blobs",
                    sha256.Value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            blobPath = "";
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(blobPath, normalizedRoot))
        {
            blobPath = "";
            error = "The Hugging Face blob path escaped the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Opens a snapshot or blob entry for a caller that will verify its pinned
    /// integrity. Standard snapshot symlinks are accepted only when the opened handle
    /// resolves into the same repository's blobs directory.
    /// </summary>
    internal static bool TryOpenReadPath(
        string cacheRoot,
        string candidatePath,
        out FileStream? stream,
        out string validatedPath,
        out string error)
    {
        stream = null;
        validatedPath = "";

        if (!TryNormalizeCacheEntry(
                cacheRoot,
                candidatePath,
                out string normalizedRoot,
                out string normalizedPath,
                out CacheEntryKind entryKind,
                out string repositoryDirectory,
                out error) ||
            !TryRejectReparsePointDescendants(
                normalizedRoot,
                normalizedPath,
                includeTarget: false,
                out error))
        {
            return false;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(normalizedPath);
            stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                HashBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            string finalPath = GetFinalPathFromHandle(stream.SafeFileHandle, normalizedPath);
            string finalRoot = GetFinalDirectoryPath(normalizedRoot);
            if (!WindowsPathSafety.IsStrictDescendant(finalPath, finalRoot))
            {
                error = $"Hugging Face cache path '{normalizedPath}' resolves outside the hub cache root.";
                stream.Dispose();
                stream = null;
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 &&
                !TryValidateSnapshotLink(
                    normalizedRoot,
                    normalizedPath,
                    finalRoot,
                    finalPath,
                    entryKind,
                    repositoryDirectory,
                    out error))
            {
                stream.Dispose();
                stream = null;
                return false;
            }

            validatedPath = normalizedPath;
            error = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException)
        {
            stream?.Dispose();
            stream = null;
            error = $"Cannot safely open Hugging Face hub cache path '{normalizedPath}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns an open stream only when a safely opened snapshot or blob matches the
    /// pinned size and SHA-256 digest. The stream is rewound to position zero so a
    /// caller can copy from the same verified handle, including across volumes.
    /// The caller owns the returned stream.
    /// </summary>
    public static async Task<FileStream?> TryOpenVerifiedCacheFileAsync(
        string cacheRoot,
        string candidatePath,
        long expectedSizeBytes,
        Sha256Digest expectedSha256,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes), "Expected size must be positive.");
        ArgumentNullException.ThrowIfNull(expectedSha256);

        if (!TryOpenReadPath(
                cacheRoot,
                candidatePath,
                out FileStream? stream,
                out _,
                out _))
        {
            return null;
        }

        bool returnStream = false;
        try
        {
            FileStream verifiedStream = stream!;
            if (verifiedStream.Length != expectedSizeBytes)
                return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[HashBufferSize];
            long completed = 0;
            progress?.Report(completed);
            while (true)
            {
                int read = await verifiedStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                completed += read;
                progress?.Report(completed);
            }

            if (completed != expectedSizeBytes ||
                !CryptographicOperations.FixedTimeEquals(
                    hash.GetHashAndReset(),
                    Convert.FromHexString(expectedSha256.Value)))
            {
                return null;
            }

            verifiedStream.Position = 0;
            returnStream = true;
            return verifiedStream;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (!returnStream)
                await stream!.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryValidateSnapshotLink(
        string normalizedRoot,
        string normalizedPath,
        string finalRoot,
        string finalPath,
        CacheEntryKind entryKind,
        string repositoryDirectory,
        out string error)
    {
        if (entryKind != CacheEntryKind.Snapshot ||
            new FileInfo(normalizedPath).LinkTarget is null)
        {
            error = $"Refusing to read '{normalizedPath}' because it is an unsupported reparse point.";
            return false;
        }

        string blobsDirectory = WindowsPathSafety.NormalizePath(
            Path.Combine(repositoryDirectory, "blobs"));
        if (!Directory.Exists(blobsDirectory))
        {
            error = "The Hugging Face snapshot link has no repository blobs directory.";
            return false;
        }

        if (!TryRejectReparsePointDescendants(
                normalizedRoot,
                blobsDirectory,
                includeTarget: true,
                out error))
        {
            return false;
        }

        string finalBlobsDirectory = GetFinalDirectoryPath(blobsDirectory);
        if (!WindowsPathSafety.IsStrictDescendant(finalBlobsDirectory, finalRoot) ||
            !WindowsPathSafety.IsStrictDescendant(finalPath, finalBlobsDirectory))
        {
            error = $"Hugging Face snapshot link '{normalizedPath}' does not resolve into its repository blobs directory.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryNormalizeCacheEntry(
        string cacheRoot,
        string candidatePath,
        out string normalizedRoot,
        out string normalizedPath,
        out CacheEntryKind entryKind,
        out string repositoryDirectory,
        out string error)
    {
        entryKind = default;
        repositoryDirectory = "";
        if (!TryNormalizeContainedPath(
                cacheRoot,
                candidatePath,
                out normalizedRoot,
                out normalizedPath,
                out error))
        {
            return false;
        }

        string[] segments = Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !TryValidateRepositoryDirectoryName(segments[0]))
        {
            error = "The path is not a valid Hugging Face repository cache entry.";
            return false;
        }

        repositoryDirectory = WindowsPathSafety.NormalizePath(
            Path.Combine(normalizedRoot, segments[0]));
        if (string.Equals(segments[1], "blobs", StringComparison.Ordinal) &&
            segments.Length == 3 &&
            WindowsPathSafety.IsSafeSegment(segments[2]))
        {
            entryKind = CacheEntryKind.Blob;
            error = "";
            return true;
        }

        if (string.Equals(segments[1], "snapshots", StringComparison.Ordinal) &&
            segments.Length >= 4 &&
            PinnedArtifactValidation.IsLowerHex(segments[2], 40) &&
            segments.Skip(3).All(WindowsPathSafety.IsSafeSegment))
        {
            entryKind = CacheEntryKind.Snapshot;
            error = "";
            return true;
        }

        error = "The path is not a valid Hugging Face blob or pinned snapshot entry.";
        return false;
    }

    private static bool TryNormalizeContainedPath(
        string cacheRoot,
        string candidatePath,
        out string normalizedRoot,
        out string normalizedPath,
        out string error)
    {
        normalizedRoot = "";
        normalizedPath = "";
        if (!TryNormalizeRoot(cacheRoot, out normalizedRoot, out error))
            return false;

        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath))
        {
            error = "The Hugging Face cache path must be fully qualified.";
            return false;
        }

        try
        {
            normalizedPath = WindowsPathSafety.NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(normalizedPath, normalizedRoot))
        {
            error = $"Hugging Face cache path '{normalizedPath}' is not contained within the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryNormalizeRoot(
        string cacheRoot,
        out string normalizedRoot,
        out string error)
    {
        normalizedRoot = "";
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Path.IsPathFullyQualified(cacheRoot))
        {
            error = "The Hugging Face hub cache root must be fully qualified.";
            return false;
        }

        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateRepositoryId(
        string repositoryId,
        out string owner,
        out string repository,
        out string error)
    {
        owner = "";
        repository = "";
        string[] segments = repositoryId?.Split('/') ?? [];
        if (repositoryId is null ||
            repositoryId.Length > 96 ||
            segments.Length != 2 ||
            segments.Any(segment => !IsValidRepositorySegment(segment)))
        {
            error = "A Hugging Face repository id must contain exactly two safe path segments.";
            return false;
        }

        owner = segments[0];
        repository = segments[1];
        error = "";
        return true;
    }

    private static bool IsValidRepositorySegment(string segment) =>
        WindowsPathSafety.IsSafeSegment(segment) &&
        segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
        segment[0] is not '-' and not '.' &&
        segment[^1] is not '-' and not '.' &&
        !segment.Contains("--", StringComparison.Ordinal) &&
        !segment.Contains("..", StringComparison.Ordinal);

    private static bool TryValidateRepositoryDirectoryName(string value)
    {
        const string prefix = "models--";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string[] segments = value[prefix.Length..].Split(
            "--",
            StringSplitOptions.None);
        return segments.Length == 2 &&
            segments.All(IsValidRepositorySegment) &&
            $"{segments[0]}/{segments[1]}".Length <= 96;
    }

    private static bool TryValidateRevision(string revision, out string error)
    {
        if (!PinnedArtifactValidation.IsLowerHex(revision, 40))
        {
            error = "A Hugging Face revision must contain exactly 40 lowercase hexadecimal characters.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryGetRelativePathSegments(
        string relativePath,
        out string[] segments,
        out string error)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.StartsWith('/') ||
            relativePath.EndsWith('/'))
        {
            error = "The Hugging Face artifact path must be a normalized relative path.";
            return false;
        }

        segments = relativePath.Split('/');
        if (segments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)))
        {
            segments = [];
            error = "The Hugging Face artifact path contains an unsafe Windows path segment.";
            return false;
        }

        if (segments[^1].Length + ".partial".Length > WindowsPathSafety.MaximumComponentLength)
        {
            segments = [];
            error = "The Hugging Face artifact file name is too long for its partial download suffix.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryRejectReparsePointDescendants(
        string normalizedRoot,
        string normalizedPath,
        bool includeTarget,
        out string error)
    {
        string[] segments = Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int count = includeTarget ? segments.Length : Math.Max(0, segments.Length - 1);
        string current = normalizedRoot;
        for (int index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!TryRejectReparsePoint(current, out error))
                return false;
        }

        error = "";
        return true;
    }

    private static bool TryRejectReparsePoint(string path, out string error)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                error = $"Refusing to operate on '{path}' because it is a reparse point.";
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            error = $"Cannot verify Hugging Face hub cache path '{path}': {ex.Message}";
            return false;
        }

        error = "";
        return true;
    }

    private static string GetFinalDirectoryPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? resolved = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return WindowsPathSafety.NormalizePath(resolved?.FullName ?? path);
        }

        using SafeFileHandle handle = CreateFileW(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Cannot open directory '{path}' (Win32 error {Marshal.GetLastWin32Error()}).");

        return GetFinalPathFromHandle(handle, path);
    }

    private static string GetFinalPathFromHandle(SafeFileHandle handle, string fallbackPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? resolved = new FileInfo(fallbackPath).ResolveLinkTarget(returnFinalTarget: true);
            return WindowsPathSafety.NormalizePath(resolved?.FullName ?? fallbackPath);
        }

        int capacity = 512;
        while (capacity <= 32_768)
        {
            var builder = new StringBuilder(capacity);
            uint length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
            {
                throw new IOException(
                    $"Cannot resolve a Hugging Face cache handle (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (length < builder.Capacity)
                return WindowsPathSafety.NormalizePath(NormalizeFinalPath(builder.ToString()));

            capacity = checked((int)length + 1);
        }

        throw new IOException("A resolved Hugging Face cache path exceeded the supported length.");
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

    private static string NormalizeConfiguredPath(
        string path,
        string userProfile,
        Func<string, string> expandEnvironmentVariables)
    {
        string expanded = path;
        if (path == "~" ||
            path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            path.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userProfile))
                throw new InvalidOperationException("The user profile directory is unavailable.");

            expanded = path.Length == 1
                ? userProfile
                : Path.Combine(userProfile, path[2..]);
        }

        expanded = expandEnvironmentVariables(expanded);
        return WindowsPathSafety.NormalizePath(expanded);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
