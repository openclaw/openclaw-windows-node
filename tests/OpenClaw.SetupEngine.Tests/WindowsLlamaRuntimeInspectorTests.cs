using System.ComponentModel;
using System.Diagnostics;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WindowsLlamaRuntimeInspectorTests
{
    private readonly ITestOutputHelper _output;

    public WindowsLlamaRuntimeInspectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
    public async Task InspectAsync_Win32StartFailure_ReturnsInvalidInspection()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory("openclaw-llama-inspector-");
        string executable = temp.Combine(LlamaRuntimeCatalog.ServerExecutableName);
        string[] requiredFiles =
        [
            executable,
            temp.Combine("ggml-cuda.dll"),
            temp.Combine("cudart64_13.dll"),
            temp.Combine("cublas64_13.dll"),
            temp.Combine("cublasLt64_13.dll"),
        ];

        foreach (string path in requiredFiles)
            await File.WriteAllTextAsync(path, "not a Windows executable");
        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
            await File.WriteAllTextAsync(temp.Combine(fileName), "not a Windows executable");

        Assert.All(requiredFiles, path =>
        {
            Assert.True(File.Exists(path));
            Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = temp.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--version");

        Win32Exception startException;
        using (var process = new Process { StartInfo = startInfo })
            startException = Assert.Throws<Win32Exception>(() => process.Start());

        var inspector = new WindowsLlamaRuntimeInspector();
        LlamaRuntimeInspection inspection = await inspector.InspectAsync(
            temp.Path,
            CancellationToken.None);

        Assert.False(inspection.IsValid);
        Assert.Null(inspection.VersionOutput);
        Assert.StartsWith("llama-server --version failed:", inspection.Error, StringComparison.Ordinal);
        Assert.Contains(startException.Message, inspection.Error, StringComparison.Ordinal);

        _output.WriteLine($"required_file_preconditions={requiredFiles.Length}");
        _output.WriteLine($"process_start_exception={startException.GetType().FullName}");
        _output.WriteLine($"inspection_is_valid={inspection.IsValid}");
        _output.WriteLine($"inspection_version_output={(inspection.VersionOutput is null ? "<null>" : "<present>")}");
        _output.WriteLine("inspection_error_prefix=llama-server --version failed:");
    }
}
