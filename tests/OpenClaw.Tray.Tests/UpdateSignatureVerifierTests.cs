using System.IO.Compression;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateSignatureVerifierTests
{
    [Theory]
    [InlineData("CN=Scott Hanselman", true)]
    [InlineData("O=Scott Hanselman, CN=Scott Hanselman, C=US", true)]
    [InlineData("CN=Scott Hanselman Evil, O=Scott Hanselman, C=US", false)]
    [InlineData("CN=Someone Else, O=Scott Hanselman, C=US", false)]
    public void PublisherSubjectMatches_RequiresPinnedCommonName(string subject, bool expected)
    {
        Assert.Equal(expected, UpdateSignatureVerifier.PublisherSubjectMatches(subject));
    }

    [Fact]
    public void VerifyUpdatePackage_ZipVerifiesTrayExecutable()
    {
        var zipPath = CreatePackageZip(includeExecutable: true);
        var verifiedPath = string.Empty;

        var result = UpdateSignatureVerifier.VerifyUpdatePackage(zipPath, path =>
        {
            verifiedPath = path;
            return new UpdateSignatureVerificationResult(true, "ok", UpdateSignatureVerifier.PinnedPublisherSubject);
        });

        Assert.True(result.IsTrusted);
        Assert.Equal(UpdateSignatureVerifier.UpdateExecutableName, Path.GetFileName(verifiedPath));
    }

    [Fact]
    public void VerifyUpdatePackage_ZipWithoutTrayExecutableFails()
    {
        var zipPath = CreatePackageZip(includeExecutable: false);

        var result = UpdateSignatureVerifier.VerifyUpdatePackage(
            zipPath,
            _ => new UpdateSignatureVerificationResult(true, "should not be called"));

        Assert.False(result.IsTrusted);
        Assert.Contains(UpdateSignatureVerifier.UpdateExecutableName, result.Message);
    }

    private static string CreatePackageZip(bool includeExecutable)
    {
        var tempDir = Directory.CreateTempSubdirectory("OpenClawUpdateSignatureTests");
        var zipPath = Path.Combine(tempDir.FullName, "update.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(includeExecutable
            ? $"publish/{UpdateSignatureVerifier.UpdateExecutableName}"
            : "publish/other.txt");

        using var writer = new StreamWriter(entry.Open());
        writer.Write("test");

        return zipPath;
    }
}
