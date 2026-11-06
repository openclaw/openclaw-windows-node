using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class FileWorkerManifestTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task ReleaseManifestRejectsUnsignedWorkerBeforeWritingDigests(string rid)
    {
        using var directory = new TempDirectory();
        File.WriteAllBytes(directory.Combine("OpenClaw.FileWorker.exe"), Image());
        File.WriteAllBytes(directory.Combine("OpenClaw.FileWorker.dll"), Image());
        File.WriteAllBytes(directory.Combine("coreclr.dll"), Image());
        File.WriteAllText(directory.Combine("OpenClaw.FileWorker.runtimeconfig.json"), "{}");
        var (exitCode, diagnostics) = await RunManifest(directory.Path, rid, requireSigned: true);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("before manifest generation", diagnostics);
        Assert.False(File.Exists(directory.Combine("payload-manifest.json")));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "VCRUNTIME140.dll")]
    [InlineData(true, "MSVCP140.dll")]
    public async Task ManifestRejectsDirectAndDelayedSpeechRuntimeDependencies(bool delayed, string? dependency)
    {
        using var directory = new TempDirectory();
        File.WriteAllBytes(directory.Combine("OpenClaw.FileWorker.exe"), Image());
        File.WriteAllBytes(directory.Combine("coreclr.dll"), Image());
        File.WriteAllText(directory.Combine("OpenClaw.FileWorker.runtimeconfig.json"), "{}");
        if (dependency is not null) File.WriteAllBytes(directory.Combine("dependency.dll"), Image(dependency, delayed));
        var (exitCode, diagnostics) = await RunManifest(directory.Path, "win-arm64");
        var manifest = directory.Combine("payload-manifest.json");
        if (dependency is not null)
        {
            Assert.NotEqual(0, exitCode);
            Assert.Contains("unexpectedly depends on an external VC runtime", diagnostics);
            Assert.False(File.Exists(manifest));
        }
        else
        {
            Assert.True(exitCode == 0, diagnostics);
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            Assert.Equal("win-arm64", document.RootElement.GetProperty("rid").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("files").EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task ShippingManifestRemovesSymbolsIncludingStalePublishFiles(string rid)
    {
        using var directory = new TempDirectory();
        var runtimeFiles = new[] { "OpenClaw.FileWorker.exe", "coreclr.dll", "dependency.dll", "OpenClaw.FileWorker.runtimeconfig.json" };
        foreach (var name in runtimeFiles)
            File.WriteAllBytes(directory.Combine(name), name.EndsWith(".json") ? "{}"u8.ToArray() : Image());
        File.WriteAllText(directory.Combine("OpenClaw.FileWorker.pdb"), "current symbols");
        File.WriteAllText(directory.Combine("obsolete.PDB"), "stale symbols");

        var (exitCode, diagnostics) = await RunManifest(directory.Path, rid);
        Assert.True(exitCode == 0, diagnostics);
        var manifestPath = directory.Combine("payload-manifest.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        using var document = JsonDocument.Parse(manifestBytes);
        Assert.Equal(rid, document.RootElement.GetProperty("rid").GetString());
        var files = document.RootElement.GetProperty("files");
        Assert.Equal(runtimeFiles.Order(), files.EnumerateObject().Select(file => file.Name).Order());
        Assert.Equal(runtimeFiles.Append("payload-manifest.json").Order(),
            Directory.GetFiles(directory.Path).Select(Path.GetFileName).Order());
        foreach (var name in runtimeFiles)
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(directory.Combine(name)))).ToLowerInvariant(),
                files.GetProperty(name).GetString());

        // Publish can reuse a directory and reintroduce symbols before manifest generation.
        File.WriteAllText(directory.Combine("OpenClaw.FileWorker.pdb"), "republished symbols");
        (exitCode, diagnostics) = await RunManifest(directory.Path, rid);
        Assert.True(exitCode == 0, diagnostics);
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));
        Assert.False(File.Exists(directory.Combine("OpenClaw.FileWorker.pdb")));
    }

    private static async Task<(int ExitCode, string Diagnostics)> RunManifest(string directory, string rid, bool requireSigned = false)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "scripts", "Write-FileWorkerManifest.ps1"),
            "-PayloadDirectory", directory, "-RuntimeIdentifier", rid, "-ProductVersion", "fixture"
        }) start.ArgumentList.Add(argument);
        if (requireSigned) start.ArgumentList.Add("-RequireSignedWorker");
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output + await error);
    }

    // Minimal PE32+ import-table fixture. It is parsed as data, never loaded or executed.
    private static byte[] Image(string? dependency = null, bool delayed = false)
    {
        var bytes = new byte[1024];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5a4d);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3c), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20b);
        const int section = 0x98 + 240;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 8), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 16), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 20), 0x200);
        if (dependency is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 112 + (delayed ? 13 : 1) * 8), 0x1000);
            if (delayed) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200 + (delayed ? 4 : 12)), 0x1100);
            Encoding.ASCII.GetBytes(dependency).CopyTo(bytes, 0x300);
        }
        return bytes;
    }
}
