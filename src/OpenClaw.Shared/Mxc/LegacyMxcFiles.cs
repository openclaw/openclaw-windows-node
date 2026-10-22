namespace OpenClaw.Shared.Mxc;

/// <summary>Exact obsolete npm payload cleanup for unpackaged upgrades, including copy-over ZIP updates.</summary>
public static class LegacyMxcFiles
{
    public static IReadOnlyList<string> RelativePaths { get; } =
    [
        @"tools\mxc\x64\wxc-exec.exe",
        @"tools\mxc\x64\wslcsdk.dll",
        @"tools\mxc\arm64\wxc-exec.exe",
        @"tools\mxc\arm64\wslcsdk.dll",
    ];

    public static IReadOnlyList<string> Cleanup(string appBase, bool isPackaged = false)
    {
        if (isPackaged) return [];
        var warnings = new List<string>();
        var root = Path.GetFullPath(appBase);
        foreach (var relative in RelativePaths)
        {
            var path = Path.Combine(root, relative);
            try
            {
                RequireOrdinaryAncestors(path);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"{relative}: {ex.GetType().Name}"); }
        }
        foreach (var relative in new[] { @"tools\mxc\x64", @"tools\mxc\arm64", @"tools\mxc" })
        {
            var path = Path.Combine(root, relative);
            try
            {
                RequireOrdinaryAncestors(path);
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"{relative}: {ex.GetType().Name}"); }
        }
        return warnings;
    }

    private static void RequireOrdinaryAncestors(string path)
    {
        for (var current = path; !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Obsolete payload cleanup refuses reparse traversal.");
    }
}
