namespace OpenClaw.SetupEngine;

using System.Security.Cryptography;

/// <summary>
/// Stages the app-shipped Visual C++ runtime beside the managed llama.cpp
/// executable. A downloaded child process runs outside the MSIX package graph,
/// so the package's VCLibs dependency cannot satisfy its native imports.
/// </summary>
internal sealed class LocalAiVcRuntimeStager
{
    internal const string PackagedRuntimeRelativeDirectory = "tools/local-ai-vc-runtime";

    internal static readonly string[] RequiredFiles =
    [
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
    ];

    private readonly string _applicationBaseDirectory;
    private readonly string? _systemRuntimeDirectory;

    public LocalAiVcRuntimeStager(string applicationBaseDirectory)
        : this(
            applicationBaseDirectory,
            OperatingSystem.IsWindows() ? Environment.SystemDirectory : null)
    {
    }

    internal LocalAiVcRuntimeStager(
        string applicationBaseDirectory,
        string? systemRuntimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory);
        _systemRuntimeDirectory = string.IsNullOrWhiteSpace(systemRuntimeDirectory)
            ? null
            : Path.GetFullPath(systemRuntimeDirectory);
    }

    public void Stage(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        string destinationDirectory = Path.GetFullPath(installDirectory);
        if (!Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("The llama-server runtime directory does not exist.");

        string sourceDirectory = ResolveSourceDirectory();
        foreach (string fileName in RequiredFiles)
            CopyRuntimeFile(sourceDirectory, destinationDirectory, fileName);
    }

    private string ResolveSourceDirectory()
    {
        string packagedDirectory = Path.Combine(
            _applicationBaseDirectory,
            PackagedRuntimeRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (ContainsRuntime(packagedDirectory))
            return packagedDirectory;
        if (ContainsRuntime(_applicationBaseDirectory))
            return _applicationBaseDirectory;
        if (_systemRuntimeDirectory is not null && ContainsRuntime(_systemRuntimeDirectory))
            return _systemRuntimeDirectory;

        throw new FileNotFoundException(
            "The app-local Visual C++ runtime payload required by llama-server is incomplete.");
    }

    private static bool ContainsRuntime(string directory) =>
        RequiredFiles.All(fileName => IsRegularFile(Path.Combine(directory, fileName)));

    private static bool IsRegularFile(string path) =>
        File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static void CopyRuntimeFile(
        string sourceDirectory,
        string destinationDirectory,
        string fileName)
    {
        string sourcePath = Path.Combine(sourceDirectory, fileName);
        string destinationPath = Path.Combine(destinationDirectory, fileName);
        if (File.Exists(destinationPath) &&
            (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The llama-server runtime file '{fileName}' is a reparse point.");
        }
        if (File.Exists(destinationPath) && FilesMatch(sourcePath, destinationPath))
            return;

        string temporaryPath = destinationPath + ".openclaw-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup must not replace the staging failure.
            }
        }
    }

    private static bool FilesMatch(string sourcePath, string destinationPath)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
            return false;

        using FileStream source = File.OpenRead(sourcePath);
        using FileStream destination = File.OpenRead(destinationPath);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(source),
            SHA256.HashData(destination));
    }
}
