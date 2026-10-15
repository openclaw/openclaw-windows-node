namespace OpenClaw.SetupEngine.UI;

// Prospective cross-package setup paths must retain the legacy mapping even before
// state/config/workspace targets exist. This is not an existing-log path resolver.
internal static class NativeGatewayDataPathMapper
{
    public static string Resolve(string path, string? pfn, string localAppData, string roaming)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (pfn == null)
            return path;

        var packageRoot = Path.Combine(localAppData, "Packages", pfn);

        // Already inside the package container — nothing to translate.
        if (path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            return path;

        if (TryStrip(path, roaming, out var roamingRest))
            return Path.Combine(packageRoot, "LocalCache", "Roaming", roamingRest);

        if (TryStrip(path, localAppData, out var localRest))
            return Path.Combine(packageRoot, "LocalCache", "Local", localRest);

        return path;
    }

    private static bool TryStrip(string path, string prefix, out string rest)
    {
        rest = string.Empty;
        if (string.IsNullOrEmpty(prefix))
            return false;

        var sep = Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix + sep, StringComparison.OrdinalIgnoreCase))
            return false;

        rest = path.Substring(prefix.Length + 1);
        return true;
    }

}
