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
