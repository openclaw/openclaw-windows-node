using OpenClaw.Shared.Inference.Catalog;
using System.ComponentModel;
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
    LocalAiArtifactRollbackMetadata? Rollback,
    int ReusedCachedArchiveCount = 0);

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
    private readonly LocalAiVcRuntimeStager? _vcRuntimeStager;

    public LlamaRuntimeInstaller(HttpClient httpClient)
        : this(
            new LocalAiArtifactInstaller(httpClient),
            new WindowsLlamaRuntimeInspector(),
            new LocalAiVcRuntimeStager(AppContext.BaseDirectory))
    {
    }

    internal LlamaRuntimeInstaller(
        LocalAiArtifactInstaller artifactInstaller,
        ILlamaRuntimeInspector inspector)
        : this(artifactInstaller, inspector, vcRuntimeStager: null)
    {
    }

    internal LlamaRuntimeInstaller(
        LocalAiArtifactInstaller artifactInstaller,
        ILlamaRuntimeInspector inspector,
        LocalAiVcRuntimeStager? vcRuntimeStager)
    {
        _artifactInstaller = artifactInstaller ?? throw new ArgumentNullException(nameof(artifactInstaller));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _vcRuntimeStager = vcRuntimeStager;
    }

    public event EventHandler<LocalAiArtifactInstallProgress>? ProgressChanged
    {
        add => _artifactInstaller.ProgressChanged += value;
        remove => _artifactInstaller.ProgressChanged -= value;
    }

    /// <summary>
    /// Installs the pinned llama-server runtime and returns it only after it passes
    /// executable inspection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A leftover runtime directory at the target path that no receipt claims is removed
    /// first. The pinned archives are then installed through
    /// <see cref="LocalAiArtifactInstaller.InstallAsync"/>, which may reuse verified
    /// archives from the cache.
    /// </para>
    /// <para>
    /// Cache retention is committed only after inspection accepts the runtime and
    /// cancellation is checked. If inspection rejects the runtime or the install is
    /// cancelled, the promoted directory is deleted and the archive cache keeps its
    /// previous complete sets.
    /// </para>
    /// </remarks>
    /// <param name="localDataDirectory">App-owned local data root that contains the Local AI tree and the archive cache.</param>
    /// <param name="runtime">Catalog runtime variant whose artifacts are installed.</param>
    /// <param name="progress">Optional per-phase progress observer, in addition to <see cref="ProgressChanged"/>.</param>
    /// <param name="cancellationToken">Cancels acquisition and inspection.</param>
    /// <returns>
    /// The accepted install, created this run. The caller owns its rollback directory and
    /// should remove it through <see cref="RemoveInstalledRuntime"/>.
    /// </returns>
    /// <exception cref="LocalAiArtifactInstallException">
    /// The path is unsafe, a leftover runtime cannot be removed safely, acquisition fails,
    /// or inspection rejects the runtime.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
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
            _vcRuntimeStager?.Stage(installed.InstallDirectory);
            LlamaRuntimeInspection inspection = await _inspector.InspectAsync(
                    installed.InstallDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!inspection.IsValid)
            {
                throw new LocalAiArtifactInstallException(
                    inspection.Error ?? "The installed llama-server runtime did not pass validation.");
            }

            // Commit cache retention only once the runtime is accepted and the caller has
            // not cancelled, so a rejected install never evicts older cached runtime sets.
            cancellationToken.ThrowIfCancellationRequested();
            _artifactInstaller.CommitArchiveCacheSet(localDataDirectory, archives);

            return new LlamaRuntimeInstallResult(
                installed.InstallDirectory,
                Path.Combine(installed.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName),
                LlamaRuntimeInstallDisposition.Installed,
                CreatedThisRun: true,
                installed.VerifiedArchives,
                installed.Rollback,
                installed.ReusedCachedArchiveCount);
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
            runtime.ReleaseTag,
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
        .. LocalAiVcRuntimeStager.RequiredFiles,
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
            process.Start();
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return new LlamaRuntimeInspection(false, null, $"llama-server --version failed: {exception.Message}");
        }
    }

    internal static LlamaRuntimeInspection ValidateVersionOutput(string output)
    {
        bool buildMatches = output.Contains(
            $"build {LlamaRuntimeCatalog.ReleaseTag[1..]}",
            StringComparison.OrdinalIgnoreCase);
        bool commitMatches = output.Contains(
            LlamaRuntimeCatalog.ReleaseCommitSha[..9],
            StringComparison.OrdinalIgnoreCase);
        return buildMatches && commitMatches
            ? new LlamaRuntimeInspection(true, output, null)
            : new LlamaRuntimeInspection(
                false,
                output,
                $"llama-server did not report the pinned {LlamaRuntimeCatalog.ReleaseTag} build and source commit.");
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
