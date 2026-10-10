using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.Shared.IO;
using OpenClaw.TestSupport;
using System.Security.Cryptography;

namespace OpenClaw.Shared.Tests;

public sealed class HuggingFaceHubCacheTests
{
    [Fact]
    public void ResolveCacheRoot_UsesHuggingFacePrecedence()
    {
        string resolved = HuggingFaceHubCache.ResolveCacheRoot(
            name => name switch
            {
                "HF_HUB_CACHE" => @"C:\explicit",
                "HUGGINGFACE_HUB_CACHE" => @"C:\legacy",
                "HF_HOME" => @"C:\home",
                "XDG_CACHE_HOME" => @"C:\xdg",
                _ => null,
            },
            @"C:\profile");

        Assert.Equal(@"C:\explicit", resolved);
    }

    [Fact]
    public void ResolveCacheRoot_FallsBackThroughLegacyHomeXdgAndProfile()
    {
        Assert.Equal(
            @"C:\legacy",
            HuggingFaceHubCache.ResolveCacheRoot(
                name => name == "HUGGINGFACE_HUB_CACHE" ? @"C:\legacy" : null,
                @"C:\profile"));
        Assert.Equal(
            Path.Combine(@"C:\home", "hub"),
            HuggingFaceHubCache.ResolveCacheRoot(
                name => name == "HF_HOME" ? @"C:\home" : null,
                @"C:\profile"));
        Assert.Equal(
            Path.Combine(@"C:\xdg", "huggingface", "hub"),
            HuggingFaceHubCache.ResolveCacheRoot(
                name => name == "XDG_CACHE_HOME" ? @"C:\xdg" : null,
                @"C:\profile"));
        Assert.Equal(
            Path.Combine(@"C:\profile", ".cache", "huggingface", "hub"),
            HuggingFaceHubCache.ResolveCacheRoot(_ => null, @"C:\profile"));
    }

    [Fact]
    public void ResolveCacheRoot_ExpandsUserProfilePrefix()
    {
        string resolved = HuggingFaceHubCache.ResolveCacheRoot(
            name => name == "HF_HUB_CACHE" ? @"~\hf-cache" : null,
            @"C:\profile");

        Assert.Equal(Path.Combine(@"C:\profile", "hf-cache"), resolved);
    }

    [Fact]
    public void ResolveCacheRoot_ExpandsWindowsEnvironmentVariables()
    {
        string resolved = HuggingFaceHubCache.ResolveCacheRoot(
            name => name == "HF_HUB_CACHE" ? @"%CACHE_ROOT%\hub" : null,
            @"C:\profile",
            value => value.Replace("%CACHE_ROOT%", @"D:\shared", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(@"D:\shared", "hub"), resolved);
    }

    [Fact]
    public void TryGetSnapshotPaths_SupportsSafeNestedRelativePath()
    {
        using var temp = new TempDirectory();

        bool success = HuggingFaceHubCache.TryGetSnapshotPaths(
            temp.Path,
            "unsloth/Qwen3.8-27B-GGUF",
            new string('a', 40),
            "weights/model.gguf",
            out string snapshotPath,
            out string partialPath,
            out string error);

        Assert.True(success, error);
        string expected = Path.Combine(
            temp.Path,
            "models--unsloth--Qwen3.8-27B-GGUF",
            "snapshots",
            new string('a', 40),
            "weights",
            "model.gguf");
        Assert.Equal(expected, snapshotPath);
        Assert.Equal(expected + ".partial", partialPath);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/CON")]
    [InlineData("owner/repo--other")]
    [InlineData(".owner/repo")]
    public void TryGetSnapshotPaths_RejectsUnsafeRepositoryId(string repositoryId)
    {
        bool success = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            repositoryId,
            new string('a', 40),
            "model.gguf",
            out string snapshotPath,
            out string partialPath,
            out string error);

        Assert.False(success);
        Assert.Empty(snapshotPath);
        Assert.Empty(partialPath);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void TryGetSnapshotPaths_RejectsUnpinnedRevision(string revision)
    {
        bool success = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            revision,
            "model.gguf",
            out _,
            out _,
            out string error);

        Assert.False(success);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("folder/../model.gguf")]
    [InlineData("folder//model.gguf")]
    [InlineData(@"folder\model.gguf")]
    [InlineData("/model.gguf")]
    [InlineData("folder/CON")]
    [InlineData("model.gguf:stream")]
    public void TryGetSnapshotPaths_RejectsUnsafeRelativePath(string relativePath)
    {
        bool success = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 40),
            relativePath,
            out string snapshotPath,
            out string partialPath,
            out string error);

        Assert.False(success);
        Assert.Empty(snapshotPath);
        Assert.Empty(partialPath);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetSnapshotPaths_ReservesComponentLengthForPartialSuffix()
    {
        string fileName = new('a', WindowsPathSafety.MaximumComponentLength - ".partial".Length + 1);

        bool success = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 40),
            fileName,
            out string snapshotPath,
            out string partialPath,
            out string error);

        Assert.False(success);
        Assert.Empty(snapshotPath);
        Assert.Empty(partialPath);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetBlobPath_UsesPinnedDigestWithoutTrustingExistingContent()
    {
        bool success = HuggingFaceHubCache.TryGetBlobPath(
            @"C:\cache",
            "owner/repo",
            new Sha256Digest(new string('b', 64)),
            out string blobPath,
            out string error);

        Assert.True(success, error);
        Assert.Equal(
            Path.Combine(
                @"C:\cache",
                "models--owner--repo",
                "blobs",
                new string('b', 64)),
            blobPath);
    }

    [Fact]
    public async Task TryOpenVerifiedCacheFileAsync_RequiresPinnedSizeAndDigest()
    {
        using var temp = new TempDirectory();
        byte[] content = "verified model"u8.ToArray();
        string snapshotPath = CreateSnapshotFile(temp.Path, content);
        var digest = new Sha256Digest(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

        await using FileStream? verified = await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            temp.Path,
            snapshotPath,
            content.Length,
            digest);
        Assert.NotNull(verified);
        Assert.Equal(0, verified.Position);
        Assert.Equal(content, await ReadAllBytesAsync(verified));

        Assert.Null(await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            temp.Path,
            snapshotPath,
            content.Length + 1,
            digest));
        Assert.Null(await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            temp.Path,
            snapshotPath,
            content.Length,
            new Sha256Digest(new string('b', 64))));
    }

    [Fact]
    public async Task TryOpenVerifiedCacheFileAsync_AcceptsVerifiedBlob()
    {
        using var temp = new TempDirectory();
        byte[] content = "verified blob"u8.ToArray();
        var digest = new Sha256Digest(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        Assert.True(HuggingFaceHubCache.TryGetBlobPath(
            temp.Path,
            "owner/repo",
            digest,
            out string blobPath,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, content);

        await using FileStream? verified = await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            temp.Path,
            blobPath,
            content.Length,
            digest);

        Assert.NotNull(verified);
    }

    [SymbolicLinkFact]
    public async Task SnapshotRead_AcceptsOnlySameRepositoryBlobSymlink()
    {
        using var cache = new TempDirectory();
        using var outside = new TempDirectory();
        byte[] content = "verified linked model"u8.ToArray();
        var digest = new Sha256Digest(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        string repository = Path.Combine(cache.Path, "models--owner--repo");
        string snapshots = Path.Combine(repository, "snapshots", new string('a', 40));
        string blobs = Path.Combine(repository, "blobs");
        string blobPath = Path.Combine(blobs, digest.Value);
        string snapshotPath = Path.Combine(snapshots, "model.gguf");
        Directory.CreateDirectory(snapshots);
        Directory.CreateDirectory(blobs);
        await File.WriteAllBytesAsync(blobPath, content);
        File.CreateSymbolicLink(snapshotPath, Path.GetRelativePath(snapshots, blobPath));

        await using VerifiedHuggingFaceCacheFile? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheEntryAsync(
            cache.Path,
            snapshotPath,
            content.Length,
            digest);
        Assert.NotNull(verified);
        Assert.Equal(
            WindowsPathSafety.NormalizePath(blobPath),
            verified.ResolvedPath,
            ignoreCase: true);

        File.Delete(snapshotPath);
        string outsidePath = Path.Combine(outside.Path, "model.gguf");
        await File.WriteAllBytesAsync(outsidePath, content);
        File.CreateSymbolicLink(snapshotPath, outsidePath);

        Assert.Null(await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            cache.Path,
            snapshotPath,
            content.Length,
            digest));
    }

    [SymbolicLinkFact]
    public async Task TryOpenVerifiedCacheFileAsync_RejectsReparsePointAncestor()
    {
        using var cache = new TempDirectory();
        using var outside = new TempDirectory();
        string repositoryLink = Path.Combine(cache.Path, "models--owner--repo");
        string outsideSnapshot = Path.Combine(
            outside.Path,
            "snapshots",
            new string('a', 40));
        Directory.CreateDirectory(outsideSnapshot);
        Directory.CreateSymbolicLink(repositoryLink, outside.Path);
        string candidate = Path.Combine(
            repositoryLink,
            "snapshots",
            new string('a', 40),
            "model.gguf");
        byte[] content = "untrusted model"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(outsideSnapshot, "model.gguf"), content);
        var digest = new Sha256Digest(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

        FileStream? verified = await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
            cache.Path,
            candidate,
            content.Length,
            digest);

        Assert.Null(verified);
    }

    private static string CreateSnapshotFile(string cacheRoot, byte[] content)
    {
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            "owner/repo",
            new string('a', 40),
            "model.gguf",
            out string snapshotPath,
            out _,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllBytes(snapshotPath, content);
        return snapshotPath;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

internal static class SymbolicLinkTestSupport
{
    private static readonly Lazy<bool> s_isAvailable = new(Probe, isThreadSafe: true);

    public static bool IsAvailable => s_isAvailable.Value;

    private static bool Probe()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"openclaw-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "target");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(directory, "link"), target);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed capability probe must not fail the test host.
            }
        }
    }
}

internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (!SymbolicLinkTestSupport.IsAvailable)
            Skip = "Creating symbolic links requires Windows Developer Mode or elevation.";
    }
}
