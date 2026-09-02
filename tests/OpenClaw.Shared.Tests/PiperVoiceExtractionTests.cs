using System.Diagnostics;
using System.Reflection;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class PiperVoiceExtractionTests
{
    [Fact]
    public async Task ExtractTarBz2Async_ExtractsArchiveWithWindowsTar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TemporaryDirectory();
        var packageDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "source", "package"));
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "destination"));
        var archivePath = Path.Combine(directory.Path, "voice.tar.bz2");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory.FullName, "voice.onnx"),
            "model-bytes");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory.FullName, "voice.onnx.json"),
            "{\"audio\":{\"sample_rate\":22050}}");
        await CreateTarBz2Async(
            archivePath,
            packageDirectory.Parent!.FullName,
            packageDirectory.Name);

        await PiperVoiceManager.ExtractTarBz2Async(
            archivePath,
            destinationDirectory.FullName,
            CancellationToken.None);

        Assert.Equal(
            "model-bytes",
            await File.ReadAllTextAsync(Path.Combine(destinationDirectory.FullName, "voice.onnx")));
        Assert.True(File.Exists(Path.Combine(destinationDirectory.FullName, "voice.onnx.json")));
    }

    [Fact]
    public async Task ExtractTarBz2Async_TimeoutIsBoundedAndKillsExtractor()
    {
        using var directory = new TemporaryDirectory();
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PiperVoiceManager.ExtractTarBz2Async(
                "fixture-hold",
                directory.Path,
                CancellationToken.None,
                FindTestHost(),
                TimeSpan.FromSeconds(2)));

        stopwatch.Stop();
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Piper timeout cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        await AssertFixtureExitedAsync(directory.Path);
    }

    [Fact]
    public async Task ExtractTarBz2Async_CancellationIsBoundedAndKillsExtractor()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var extraction = PiperVoiceManager.ExtractTarBz2Async(
            "fixture-hold",
            directory.Path,
            cancellation.Token,
            FindTestHost(),
            BoundedProcessWait.DefaultTimeout);
        await ReadFixturePidAsync(directory.Path);
        var stopwatch = Stopwatch.StartNew();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extraction);

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Piper cancellation cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        await AssertFixtureExitedAsync(directory.Path);
    }

    private static async Task CreateTarBz2Async(
        string archivePath,
        string sourceDirectory,
        string packageName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-cjf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(sourceDirectory);
        startInfo.ArgumentList.Add(packageName);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start tar archive fixture.");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(15));

        Assert.True(
            result.ExitCode == 0,
            $"tar fixture creation failed (exit {result.ExitCode}): {result.StandardError}");
    }

    private static async Task AssertFixtureExitedAsync(string directory)
    {
        var processId = await ReadFixturePidAsync(directory);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    private static async Task<int> ReadFixturePidAsync(string directory)
    {
        var pidPath = Path.Combine(directory, "fixture.pid");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!File.Exists(pidPath) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(File.Exists(pidPath), "Piper extractor fixture did not publish its PID.");
        return int.Parse(await File.ReadAllTextAsync(pidPath));
    }

    private static string FindTestHost()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var executableName = OperatingSystem.IsWindows()
            ? "OpenClaw.Shared.TestHost.exe"
            : "OpenClaw.Shared.TestHost";
        var hostPath = Path.Combine(
            current.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration,
            "net10.0",
            executableName);
        Assert.True(File.Exists(hostPath), $"Process test host was not built: {hostPath}");
        return hostPath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("openclaw-piper-extract-").FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
