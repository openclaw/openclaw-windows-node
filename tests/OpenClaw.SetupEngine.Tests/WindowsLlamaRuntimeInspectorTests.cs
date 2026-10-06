using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WindowsLlamaRuntimeInspectorTests
{
    [Fact]
    public void VcRuntimeStager_PrefersPackagedPayload_AndStagesEveryImportedDll()
    {
        using var app = new TempDirectory("openclaw-llama-vc-app-");
        using var install = new TempDirectory("openclaw-llama-vc-install-");
        string packaged = Path.Combine(
            app.Path,
            LocalAiVcRuntimeStager.PackagedRuntimeRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(packaged);

        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(app.Path, fileName), "root-old");
            File.WriteAllText(Path.Combine(packaged, fileName), "packaged-current");
        }

        new LocalAiVcRuntimeStager(app.Path).Stage(install.Path);

        Assert.All(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => Assert.Equal(
                "packaged-current",
                File.ReadAllText(Path.Combine(install.Path, fileName))));

        string unchanged = Path.Combine(install.Path, LocalAiVcRuntimeStager.RequiredFiles[0]);
        DateTime preservedWriteTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(unchanged, preservedWriteTime);
        new LocalAiVcRuntimeStager(app.Path).Stage(install.Path);
        Assert.Equal(preservedWriteTime, File.GetLastWriteTimeUtc(unchanged));
    }

    [Fact]
    public void VcRuntimeStager_UsesSystemRuntime_WhenUnpackagedArm64OutputHasNoPayload()
    {
        using var app = new TempDirectory("openclaw-llama-vc-app-");
        using var system = new TempDirectory("openclaw-llama-vc-system-");
        using var install = new TempDirectory("openclaw-llama-vc-install-");

        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
            File.WriteAllText(Path.Combine(system.Path, fileName), "system-current");

        new LocalAiVcRuntimeStager(app.Path, system.Path).Stage(install.Path);

        Assert.All(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => Assert.Equal(
                "system-current",
                File.ReadAllText(Path.Combine(install.Path, fileName))));
    }

    [Fact]
    public void VcRuntimeStager_DefaultWindowsFallback_StagesInstalledSystemRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var app = new TempDirectory("openclaw-llama-vc-app-");
        using var install = new TempDirectory("openclaw-llama-vc-install-");
        Assert.All(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, fileName))));

        new LocalAiVcRuntimeStager(app.Path).Stage(install.Path);

        Assert.All(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => Assert.Equal(
                File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, fileName)),
                File.ReadAllBytes(Path.Combine(install.Path, fileName))));
    }

    [Fact]
    public void VcRuntimeStager_IncompletePayload_FailsClosed()
    {
        using var app = new TempDirectory("openclaw-llama-vc-app-");
        using var install = new TempDirectory("openclaw-llama-vc-install-");
        File.WriteAllText(Path.Combine(app.Path, "msvcp140.dll"), "partial");

        FileNotFoundException failure = Assert.Throws<FileNotFoundException>(
            () => new LocalAiVcRuntimeStager(app.Path, systemRuntimeDirectory: null).Stage(install.Path));

        Assert.Contains("payload required by llama-server is incomplete", failure.Message);
        Assert.DoesNotContain(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => File.Exists(Path.Combine(install.Path, fileName)));
    }

    [Fact]
    public async Task InspectAsync_MissingImplementationLibrary_ReturnsInvalidWithoutLaunching()
    {
        using var temp = new TempDirectory("openclaw-llama-inspector-");
        LlamaRuntimeVariant runtime = CreateRuntime(
            Architecture.X64,
            (LlamaRuntimeCatalog.ServerExecutableName, "launcher"),
            (LlamaRuntimeCatalog.ServerImplementationLibraryName, "implementation"));
        await File.WriteAllTextAsync(temp.Combine(LlamaRuntimeCatalog.ServerExecutableName), "launcher");

        var inspector = new WindowsLlamaRuntimeInspector();
        LlamaRuntimeInspection inspection = await inspector.InspectAsync(
            temp.Path,
            runtime,
            CancellationToken.None);

        Assert.False(inspection.IsValid);
        Assert.Contains(LlamaRuntimeCatalog.ServerImplementationLibraryName, inspection.Error);
    }

    [Fact]
    public async Task InspectAsync_CompletePinnedPayload_DoesNotExecuteLauncher()
    {
        using var temp = new TempDirectory("openclaw-llama-inspector-");
        LlamaRuntimeVariant runtime = CreateRuntime(
            Architecture.X64,
            (LlamaRuntimeCatalog.ServerExecutableName, "not an executable"),
            (LlamaRuntimeCatalog.ServerImplementationLibraryName, "not a library"));
        await WriteRuntimeAsync(temp, runtime);

        LlamaRuntimeInspection inspection = await new WindowsLlamaRuntimeInspector()
            .InspectAsync(
                temp.Path,
                runtime,
                CancellationToken.None);

        Assert.True(inspection.IsValid, inspection.Error);
        Assert.Null(inspection.Error);
    }

    [Theory]
    [InlineData(Architecture.X64, "ggml-cpu-x64.dll")]
    [InlineData(Architecture.Arm64, "ggml-cpu.dll")]
    public async Task InspectAsync_MissingArchitectureBackend_ReturnsInvalid(
        Architecture architecture,
        string missingFile)
    {
        using var temp = new TempDirectory("openclaw-llama-inspector-");
        LlamaRuntimeVariant runtime = CreateRuntime(
            architecture,
            (LlamaRuntimeCatalog.ServerExecutableName, "launcher"),
            (missingFile, "backend"));
        await File.WriteAllTextAsync(temp.Combine(LlamaRuntimeCatalog.ServerExecutableName), "launcher");

        LlamaRuntimeInspection inspection = await new WindowsLlamaRuntimeInspector()
            .InspectAsync(temp.Path, runtime, CancellationToken.None);

        Assert.False(inspection.IsValid);
        Assert.Contains(missingFile, inspection.Error);
    }

    [Fact]
    public async Task InspectAsync_ReplacedFileWithSameSize_ReturnsInvalid()
    {
        using var temp = new TempDirectory("openclaw-llama-inspector-");
        LlamaRuntimeVariant runtime = CreateRuntime(
            Architecture.X64,
            (LlamaRuntimeCatalog.ServerExecutableName, "expected"));
        await File.WriteAllTextAsync(temp.Combine(LlamaRuntimeCatalog.ServerExecutableName), "replaced");

        LlamaRuntimeInspection inspection = await new WindowsLlamaRuntimeInspector()
            .InspectAsync(temp.Path, runtime, CancellationToken.None);

        Assert.False(inspection.IsValid);
        Assert.Contains("SHA-256", inspection.Error);
    }

    private static async Task WriteRuntimeAsync(TempDirectory temp, LlamaRuntimeVariant runtime)
    {
        foreach (LlamaRuntimeFile file in runtime.RequiredFiles)
        {
            string content = file.FileName == LlamaRuntimeCatalog.ServerExecutableName
                ? "not an executable"
                : "not a library";
            await File.WriteAllTextAsync(temp.Combine(file.FileName), content);
        }
        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
            await File.WriteAllTextAsync(temp.Combine(fileName), "runtime");
    }

    private static LlamaRuntimeVariant CreateRuntime(
        Architecture architecture,
        params (string FileName, string Content)[] files)
    {
        var source = new GitHubReleaseSource("owner/repo", "v1", new string('b', 40));
        var artifactHash = new Sha256Digest(new string('a', 64));
        return new LlamaRuntimeVariant(
            $"test-{architecture.ToString().ToLowerInvariant()}",
            architecture,
            new Version(13, 4),
            [
                new PinnedArtifact("runtime", ArtifactRole.RuntimeBinary, source, "runtime.zip", 1, artifactHash),
                new PinnedArtifact("dependency", ArtifactRole.RuntimeDependency, source, "dependency.zip", 1, artifactHash),
            ],
            requiredFiles: files.Select(file =>
            {
                byte[] content = Encoding.UTF8.GetBytes(file.Content);
                return new LlamaRuntimeFile(
                    file.FileName,
                    content.Length,
                    new Sha256Digest(Convert.ToHexStringLower(SHA256.HashData(content))));
            }).ToArray());
    }
}
