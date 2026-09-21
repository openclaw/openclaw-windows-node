using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;

namespace OpenClaw.Connection;

/// <summary>Holds the verified archive against replacement until the installer has consumed it.</summary>
public sealed class VerifiedWindowsUpdatePackage : IDisposable
{
    private readonly FileStream _archive;

    internal VerifiedWindowsUpdatePackage(FileStream archive)
    {
        _archive = archive;
        FilePath = archive.Name;
    }

    public string FilePath { get; }

    public void Dispose() => _archive.Dispose();
}

public static class WindowsUpdatePackageVerifier
{
    // These are the owned payload files signed by the release workflow. Vendor
    // files retain their publishers; the release digest covers the whole archive.
    private static readonly string[] OwnedBinaries =
    [
        "OpenClaw.Tray.WinUI.exe", "OpenClaw.Tray.WinUI.dll", "OpenClaw.Chat.dll",
        "OpenClaw.Connection.dll", "OpenClaw.SetupEngine.UI.dll", "OpenClaw.SetupEngine.dll",
        "OpenClaw.Shared.dll", "OpenClawTray.FunctionalUI.dll"
    ];

    public static Task<VerifiedWindowsUpdatePackage> VerifyAsync(
        string path, long expectedSize, string? expectedDigest, CancellationToken cancellationToken = default) =>
        Task.Run(() => Verify(path, expectedSize, expectedDigest,
            WindowsAuthenticodeVerifier.VerifyOpenClawSignedFile, cancellationToken), cancellationToken);

    public static void ValidateReleaseDigest([NotNull] string? digest)
    {
        if (string.IsNullOrEmpty(digest) ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 ||
            !digest[7..].All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("The release asset has no supported SHA-256 digest.");
        }
    }

    internal static VerifiedWindowsUpdatePackage Verify(
        string path,
        long expectedSize,
        string? expectedDigest,
        Func<string, AuthenticodeTrustResult> verifySignature,
        CancellationToken cancellationToken = default)
    {
        ValidateReleaseDigest(expectedDigest);

        // Updatum opens the same path with FileShare.Read. Keep this handle open
        // through that read so verification cannot be separated from the bytes used.
        var archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedSize <= 0 || archiveStream.Length != expectedSize)
                throw new InvalidDataException("The update download does not match the release asset size.");
            var digest = Convert.ToHexString(SHA256.HashData(archiveStream));
            if (!string.Equals(digest, expectedDigest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update download does not match the release SHA-256 digest.");

            archiveStream.Position = 0;
            var verificationDirectory = Directory.CreateTempSubdirectory("openclaw-update-verification-");
            try
            {
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
                // Match Updatum's Windows extraction, including duplicate entries
                // and path aliases: signatures must cover the final extracted files.
                archive.ExtractToDirectory(verificationDirectory.FullName, overwriteFiles: true);
                foreach (var name in OwnedBinaries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var binaryPath = Path.Combine(verificationDirectory.FullName, name);
                    if (!File.Exists(binaryPath))
                        throw new InvalidDataException($"The update is missing the required binary {name}.");
                    var signature = verifySignature(binaryPath);
                    if (!signature.IsTrusted)
                        throw new InvalidDataException($"Update verification rejected {name}: {signature.Detail}");
                }
            }
            finally
            {
                // The installer can terminate this process. Remove verifier copies
                // before handoff instead of depending on a later finally block.
                verificationDirectory.Delete(recursive: true);
            }
            return new VerifiedWindowsUpdatePackage(archiveStream);
        }
        catch
        {
            archiveStream.Dispose();
            throw;
        }
    }
}
