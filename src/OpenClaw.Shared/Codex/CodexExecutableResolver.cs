using System.Collections.ObjectModel;
using System.Diagnostics;

namespace OpenClaw.Shared.Codex;

public sealed class CodexExecutableResolver
{
    private const string ExecutableName = "codex.exe";
    private readonly ICodexExecutablePlatform _platform;

    public CodexExecutableResolver()
        : this(new CurrentProcessCodexExecutablePlatform())
    {
    }

    internal CodexExecutableResolver(ICodexExecutablePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
    }

    public CodexLaunchPlan? Resolve()
    {
        var packagedAlias = GetPackagedAlias();
        if (packagedAlias is not null && IsExistingFile(packagedAlias, allowReparsePoint: true))
            return new CodexLaunchPlan(packagedAlias);

        if (string.IsNullOrWhiteSpace(_platform.PathEnvironment))
            return null;

        foreach (var pathEntry in _platform.PathEnvironment.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(pathEntry) || !_platform.IsPathFullyQualified(pathEntry))
                continue;

            var candidate = TryGetFullPath(Path.Combine(pathEntry, ExecutableName));
            if (candidate is not null && IsExistingFile(candidate, allowReparsePoint: false))
                return new CodexLaunchPlan(candidate);
        }

        return null;
    }

    private string? GetPackagedAlias()
    {
        if (string.IsNullOrWhiteSpace(_platform.LocalApplicationData)
            || !_platform.IsPathFullyQualified(_platform.LocalApplicationData))
        {
            return null;
        }

        return TryGetFullPath(Path.Combine(
            _platform.LocalApplicationData,
            "Microsoft",
            "WindowsApps",
            ExecutableName));
    }

    private string? TryGetFullPath(string path)
    {
        try
        {
            return _platform.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return null;
        }
    }

    private bool IsExistingFile(string path, bool allowReparsePoint)
    {
        if (!string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || !_platform.FileExists(path))
        {
            return false;
        }

        try
        {
            var attributes = _platform.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                   && (allowReparsePoint || (attributes & FileAttributes.ReparsePoint) == 0);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            return false;
        }
    }
}

public sealed class CodexLaunchPlan
{
    private static readonly IReadOnlyList<string> LaunchArguments =
        Array.AsReadOnly(["app-server", "--listen", "stdio://"]);

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal CodexLaunchPlan(string executablePath)
    {
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Arguments => LaunchArguments;

    public IReadOnlyDictionary<string, string> EnvironmentOverrides => EmptyEnvironment;

    public bool UseShellExecute => false;

    public bool RedirectStandardInput => true;

    public bool RedirectStandardOutput => true;

    public bool RedirectStandardError => true;

    public ProcessStartInfo CreateProcessStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = UseShellExecute,
            RedirectStandardInput = RedirectStandardInput,
            RedirectStandardOutput = RedirectStandardOutput,
            RedirectStandardError = RedirectStandardError,
        };

        foreach (var argument in Arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }
}

internal interface ICodexExecutablePlatform
{
    string? LocalApplicationData { get; }

    string? PathEnvironment { get; }

    string GetFullPath(string path);

    bool IsPathFullyQualified(string path);

    bool FileExists(string path);

    FileAttributes GetAttributes(string path);
}

internal sealed class CurrentProcessCodexExecutablePlatform : ICodexExecutablePlatform
{
    public string? LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? PathEnvironment => Environment.GetEnvironmentVariable("PATH");

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool IsPathFullyQualified(string path) => Path.IsPathFullyQualified(path);

    public bool FileExists(string path) => File.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}
