using OpenClaw.Shared.Inference.Catalog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine;

internal enum LlamaRuntimeInstallDisposition
{
    Installed,
    ReusedVerified,
}

internal sealed record LlamaRuntimeInstallResult(
    string InstallDirectory,
    string ExecutablePath,
    LlamaRuntimeInstallDisposition Disposition,
    bool CreatedThisRun,
    IReadOnlyList<LocalAiVerifiedArchive> VerifiedArchives,
    LocalAiArtifactRollbackMetadata? Rollback);

internal sealed record LlamaRuntimeInspection(bool IsValid, string? VersionOutput, string? Error);

internal interface ILlamaRuntimeInspector
{
    Task<LlamaRuntimeInspection> InspectAsync(string installDirectory, CancellationToken cancellationToken);
}

internal interface ILlamaRuntimeAcquirer
{
    Task<LlamaRuntimeInstallResult> InstallAsync(
        string localDataDirectory,
        LlamaRuntimeVariant runtime,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken);

    void RemoveInstalledRuntime(string localDataDirectory, LlamaRuntimeInstallResult install);
}

internal sealed class LlamaRuntimeInstaller : ILlamaRuntimeAcquirer
{
    private const int MaximumDeleteAttempts = 8;
    private readonly LocalAiArtifactInstaller _artifactInstaller;
    private readonly ILlamaRuntimeInspector _inspector;

    public LlamaRuntimeInstaller(HttpClient httpClient)
        : this(new LocalAiArtifactInstaller(httpClient), new WindowsLlamaRuntimeInspector())
    {
    }

    internal LlamaRuntimeInstaller(
        LocalAiArtifactInstaller artifactInstaller,
        ILlamaRuntimeInspector inspector)
    {
        _artifactInstaller = artifactInstaller ?? throw new ArgumentNullException(nameof(artifactInstaller));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public event EventHandler<LocalAiArtifactInstallProgress>? ProgressChanged
    {
        add => _artifactInstaller.ProgressChanged += value;
        remove => _artifactInstaller.ProgressChanged -= value;
    }

    public async Task<LlamaRuntimeInstallResult> InstallAsync(
        string localDataDirectory,
        LlamaRuntimeVariant runtime,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        LocalAiComponentIdentity component = Component(runtime);
        if (!LocalAiPathPolicy.TryResolve(localDataDirectory, component, out LocalAiSetupPaths paths, out string pathError))
            throw new LocalAiArtifactInstallException(pathError);

        if (Directory.Exists(paths.InstallDirectory) || File.Exists(paths.InstallDirectory))
        {
            if (!LocalAiPathPolicy.TryDeleteManagedTree(
                    localDataDirectory,
                    paths.InstallDirectory,
                    allowRoot: false,
                    out string cleanupError))
            {
                throw new LocalAiArtifactInstallException(
                    $"An unclaimed llama-server runtime could not be removed safely: {cleanupError}");
            }
        }

        IReadOnlyList<LocalAiPinnedArchive> archives = runtime.Artifacts
            .Select(artifact => new LocalAiPinnedArchive(
                artifact.RelativePath,
                artifact.DownloadUri,
                artifact.SizeBytes,
                artifact.Sha256.Value))
            .ToArray();
        LocalAiArtifactInstallResult installed = await _artifactInstaller.InstallAsync(
                localDataDirectory,
                component,
                archives,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            LlamaRuntimeInspection inspection = await _inspector.InspectAsync(
                    installed.InstallDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!inspection.IsValid)
            {
                throw new LocalAiArtifactInstallException(
                    inspection.Error ?? "The installed llama-server runtime did not pass validation.");
            }

            return new LlamaRuntimeInstallResult(
                installed.InstallDirectory,
                Path.Combine(installed.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName),
                LlamaRuntimeInstallDisposition.Installed,
                CreatedThisRun: true,
                installed.VerifiedArchives,
                installed.Rollback);
        }
        catch
        {
            DeleteCreatedInstall(localDataDirectory, installed.Rollback.CreatedDirectory);
            throw;
        }
    }

    internal static LocalAiComponentIdentity Component(LlamaRuntimeVariant runtime) =>
        new(
            "llama-server",
            LlamaRuntimeCatalog.ReleaseTag,
            runtime.Architecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => throw new InvalidOperationException("The llama-server runtime architecture is unsupported."),
            });

    public void RemoveInstalledRuntime(string localDataDirectory, LlamaRuntimeInstallResult install)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (!install.CreatedThisRun || install.Rollback is null)
            return;

        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                localDataDirectory,
                install.Rollback.CreatedDirectory,
                out string deletePath,
                out string error))
        {
            throw new InvalidDataException(error);
        }

        if ((Directory.Exists(deletePath) || File.Exists(deletePath)) &&
            !LocalAiPathPolicy.TryDeleteManagedTree(
                localDataDirectory,
                deletePath,
                allowRoot: false,
                out string cleanupError))
        {
            throw new InvalidDataException(cleanupError);
        }
    }

    internal static void DeleteDirectoryWithRetry(
        string deletePath,
        Action<string>? delete = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deletePath);
        delete ??= path => Directory.Delete(path, recursive: true);
        delay ??= Thread.Sleep;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                delete(deletePath);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt < MaximumDeleteAttempts)
            {
                int delayMilliseconds = Math.Min(100 << (attempt - 1), 1_000);
                delay(TimeSpan.FromMilliseconds(delayMilliseconds));
            }
        }
    }

    private static void DeleteCreatedInstall(string localDataDirectory, string createdDirectory)
    {
        try
        {
            if (LocalAiPathPolicy.TryDeleteManagedTree(
                    localDataDirectory,
                    createdDirectory,
                    allowRoot: false,
                    out _))
                return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not replace the validation failure.
        }
    }
}

internal sealed class WindowsLlamaRuntimeInspector : ILlamaRuntimeInspector
{
    private static readonly string[] RequiredFiles =
    [
        LlamaRuntimeCatalog.ServerExecutableName,
        "ggml-cuda.dll",
        "cudart64_13.dll",
        "cublas64_13.dll",
        "cublasLt64_13.dll",
    ];

    public async Task<LlamaRuntimeInspection> InspectAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        foreach (string fileName in RequiredFiles)
        {
            string path = Path.Combine(installDirectory, fileName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new LlamaRuntimeInspection(false, null, $"The llama-server runtime is missing required file '{fileName}'.");
        }

        string executable = Path.Combine(installDirectory, LlamaRuntimeCatalog.ServerExecutableName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new LlamaRuntimeInspection(false, null, "llama-server --version did not start.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string output = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
                (await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
                return new LlamaRuntimeInspection(false, output, "llama-server --version returned a nonzero exit code.");
            return ValidateVersionOutput(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            return new LlamaRuntimeInspection(false, null, "llama-server --version timed out.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new LlamaRuntimeInspection(false, null, $"llama-server --version failed: {exception.Message}");
        }
    }

    internal static LlamaRuntimeInspection ValidateVersionOutput(string output)
    {
        bool buildMatches = output.Contains("build 10488", StringComparison.OrdinalIgnoreCase);
        bool commitMatches = output.Contains(
            LlamaRuntimeCatalog.ReleaseCommitSha[..9],
            StringComparison.OrdinalIgnoreCase);
        return buildMatches && commitMatches
            ? new LlamaRuntimeInspection(true, output, null)
            : new LlamaRuntimeInspection(
                false,
                output,
                "llama-server did not report the pinned b10488 build and source commit.");
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup during cancellation or timeout.
        }
    }
}
