using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine;

[SupportedOSPlatform("windows")]
public static class TrayExecutableResolver
{
    private const string TrayExecutableName = "OpenClaw.Tray.WinUI.exe";

    public static string? Resolve(string? setupEngineBaseDirectory = null)
    {
        var candidatePath = GetCanonicalPath(setupEngineBaseDirectory);
        return File.Exists(candidatePath) ? candidatePath : null;
    }

    public static string GetCanonicalPath(string? setupEngineBaseDirectory = null)
    {
        setupEngineBaseDirectory ??= AppContext.BaseDirectory;

        return Path.GetFullPath(Path.Combine(setupEngineBaseDirectory, "..", TrayExecutableName));
    }
}
