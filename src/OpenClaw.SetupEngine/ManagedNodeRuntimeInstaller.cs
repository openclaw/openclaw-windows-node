using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine;

internal sealed record ManagedNodePackage(
    string Version,
    string Architecture,
    Uri DownloadUri,
    string Sha256);

internal static class ManagedNodeRuntimeInstaller
{
    internal static ManagedNodePackage ResolvePackage(Architecture architecture)
    {
        var architectureName = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Managed Node is not available for Windows architecture '{architecture}'."),
        };
        var hash = architecture == Architecture.Arm64
            ? GatewayLkgVersion.ManagedNodeArm64Sha256
            : GatewayLkgVersion.ManagedNodeX64Sha256;
        var fileName = $"node-v{GatewayLkgVersion.ManagedNodeVersion}-win-{architectureName}.zip";
        return new ManagedNodePackage(
            GatewayLkgVersion.ManagedNodeVersion,
            architectureName,
            new Uri($"https://nodejs.org/dist/v{GatewayLkgVersion.ManagedNodeVersion}/{fileName}"),
            hash);
    }

    internal static async Task<StepResult> EnsureInstalledAsync(
        SetupContext ctx,
        CancellationToken ct,
        Func<Uri, Stream, CancellationToken, Task>? download = null,
        ManagedNodePackage? packageOverride = null)
    {
        var nodePath = GatewayCliRunner.GetManagedNativeNodePath(ctx.LocalDataDir);
        var npmCliPath = GatewayCliRunner.GetManagedNativeNpmCliPath(ctx.LocalDataDir);
        if (File.Exists(nodePath) && File.Exists(npmCliPath))
            return StepResult.Ok("Managed Node runtime already installed");

        var package = packageOverride ?? ResolvePackage(RuntimeInformation.OSArchitecture);
        var setupTemp = Path.Combine(ctx.LocalDataDir, "setup-temp");
        var workDirectory = Path.Combine(setupTemp, $"node-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(workDirectory, "node.zip");
        var extractDirectory = Path.Combine(workDirectory, "extract");
        var nodeDirectory = GatewayCliRunner.GetManagedNativeNodeDirectory(ctx.LocalDataDir);
        try
        {
            Directory.CreateDirectory(workDirectory);
            await using (var destination = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                if (download is null)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    using var response = await http.GetAsync(
                        package.DownloadUri,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await source.CopyToAsync(destination, ct);
                }
                else
                {
                    await download(package.DownloadUri, destination, ct);
                }
            }

            if (!VerifySha256(archivePath, package.Sha256))
                return StepResult.Fail("Managed Node archive failed SHA-256 verification.");

            Directory.CreateDirectory(extractDirectory);
            ZipFile.ExtractToDirectory(archivePath, extractDirectory);
            var extractedRoot = Directory.EnumerateDirectories(extractDirectory).SingleOrDefault()
                ?? extractDirectory;
            if (!File.Exists(Path.Combine(extractedRoot, "node.exe")))
                return StepResult.Fail("Managed Node archive did not contain node.exe.");

            AtomicFile.DeleteDirectoryStrict(nodeDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(nodeDirectory)!);
            AtomicFile.MoveDirectory(extractedRoot, nodeDirectory);
            if (!File.Exists(nodePath) || !File.Exists(npmCliPath))
                return StepResult.Fail("Managed Node extraction did not produce the expected npm runtime.");

            ctx.Logger.Info($"Installed app-private Node {package.Version} ({package.Architecture})");
            return StepResult.Ok($"Managed Node {package.Version} installed");
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            return StepResult.Fail($"Managed Node installation failed: {ex.Message}");
        }
        finally
        {
            AtomicFile.DeleteDirectoryStrict(workDirectory);
        }
    }

    internal static bool VerifySha256(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedHash));
    }
}
