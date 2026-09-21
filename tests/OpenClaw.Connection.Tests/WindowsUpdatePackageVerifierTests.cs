using System.IO.Compression;
using System.Security.Cryptography;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class WindowsUpdatePackageVerifierTests
{
    private static readonly string[] OwnedBinaries =
    [
        "OpenClaw.Tray.WinUI.exe", "OpenClaw.Tray.WinUI.dll", "OpenClaw.Chat.dll",
        "OpenClaw.Connection.dll", "OpenClaw.SetupEngine.UI.dll", "OpenClaw.SetupEngine.dll",
        "OpenClaw.Shared.dll", "OpenClawTray.FunctionalUI.dll"
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:abcd")]
    [InlineData("sha256:abcd")]
    public void UnsupportedDigest_IsRejectedBeforeReadingTheDownload(string? digest)
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsUpdatePackageVerifier.Verify("missing.zip", 1, digest, _ =>
                throw new InvalidOperationException("Signature verification must not run.")));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    public void DownloadMismatch_IsRejectedBeforeExtracting(string mismatch)
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp);
        var size = new FileInfo(path).Length;
        var digest = Digest(path);
        if (mismatch == "size") size++;
        else digest = "sha256:" + new string('0', 64);

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsUpdatePackageVerifier.Verify(path, size, digest, _ =>
                throw new InvalidOperationException("Signature verification must not run.")));
        Assert.Contains(mismatch == "size" ? "size" : "SHA-256", error.Message);
        AssertArchiveUnlocked(path);
    }

    [Fact]
    public void VerifiedArchive_IsReadableButCannotBeChangedUntilLeaseIsDisposed()
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp, extra: ("vendor.dll", "third-party"));
        var inspected = new List<string>();
        string? verificationDirectory = null;
        using (var lease = WindowsUpdatePackageVerifier.Verify(path, new FileInfo(path).Length, Digest(path), file =>
        {
            verificationDirectory = Path.GetDirectoryName(file);
            inspected.Add(Path.GetFileName(file));
            Assert.Equal("owned", File.ReadAllText(file));
            return AuthenticodeTrustResult.Trusted();
        }))
        {
            Assert.Equal(Path.GetFullPath(path), lease.FilePath);
            Assert.Equal(OwnedBinaries.Order(), inspected.Order());
            Assert.False(Directory.Exists(verificationDirectory));
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(9, archive.Entries.Count);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write).Dispose());
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        AssertArchiveUnlocked(path);
    }

    [Theory]
    [InlineData("OpenClaw.Tray.WinUI.exe")]
    [InlineData("./OpenClaw.Tray.WinUI.exe")]
    [InlineData("openclaw.tray.winui.exe")]
    public void FinalExtractedBytes_AreVerifiedAfterDuplicateOrAliasedEntries(string entryName)
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp, extra: (entryName, "modified"));
        string? verificationDirectory = null;

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsUpdatePackageVerifier.Verify(path, new FileInfo(path).Length, Digest(path), file =>
            {
                verificationDirectory = Path.GetDirectoryName(file);
                return File.ReadAllText(file) == "owned"
                    ? AuthenticodeTrustResult.Trusted()
                    : AuthenticodeTrustResult.Rejected("signature mismatch");
            }));

        Assert.Contains("OpenClaw.Tray.WinUI.exe", error.Message);
        Assert.Contains("signature mismatch", error.Message);
        Assert.False(Directory.Exists(verificationDirectory));
        AssertArchiveUnlocked(path);
    }

    [Fact]
    public void MissingOwnedBinary_IsRejectedAndVerificationCopiesAreRemoved()
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp, omitLast: true);
        string? verificationDirectory = null;

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsUpdatePackageVerifier.Verify(path, new FileInfo(path).Length, Digest(path), file =>
            {
                verificationDirectory = Path.GetDirectoryName(file);
                return AuthenticodeTrustResult.Trusted();
            }));

        Assert.Contains(OwnedBinaries[^1], error.Message);
        Assert.False(Directory.Exists(verificationDirectory));
        AssertArchiveUnlocked(path);
    }

    [Fact]
    public void TraversalEntry_IsRejectedBeforeAnySignatureChecks()
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp, extra: ("../outside.txt", "outside"));
        Assert.Throws<IOException>(() =>
            WindowsUpdatePackageVerifier.Verify(path, new FileInfo(path).Length, Digest(path), _ =>
                throw new InvalidOperationException("Signature verification must not run.")));
        AssertArchiveUnlocked(path);
    }

    [Fact]
    public void CancelledVerification_ReleasesTheArchive()
    {
        using var temp = new TempDirectory("openclaw-update-test-");
        var path = WriteArchive(temp);
        Assert.Throws<OperationCanceledException>(() =>
            WindowsUpdatePackageVerifier.Verify(path, new FileInfo(path).Length, Digest(path), _ =>
                throw new InvalidOperationException("Signature verification must not run."),
                new CancellationToken(canceled: true)));
        AssertArchiveUnlocked(path);
    }

    [Fact]
    public void FoundationPolicy_RejectsTrustedMicrosoftAndUnsignedFiles()
    {
        var microsoftFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe");
        var microsoftTrust = WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(microsoftFile);
        Assert.True(microsoftTrust.IsTrusted, microsoftTrust.Detail);

        var wrongPublisher = WindowsAuthenticodeVerifier.VerifyOpenClawSignedFile(microsoftFile);
        Assert.False(wrongPublisher.IsTrusted);
        Assert.Contains("not OpenClaw Foundation", wrongPublisher.Detail);

        var unsigned = WindowsAuthenticodeVerifier.VerifyOpenClawSignedFile(
            typeof(WindowsUpdatePackageVerifierTests).Assembly.Location);
        Assert.False(unsigned.IsTrusted);
        Assert.Contains("Authenticode verification failed", unsigned.Detail);
    }

    private static string WriteArchive(
        TempDirectory temp, bool omitLast = false, (string Name, string Content)? extra = null)
    {
        var path = temp.Combine("update.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in omitLast ? OwnedBinaries[..^1] : OwnedBinaries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("owned");
        }
        if (extra is { } entry)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open());
            writer.Write(entry.Content);
        }
        return path;
    }

    private static string Digest(string path) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AssertArchiveUnlocked(string path)
    {
        using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
