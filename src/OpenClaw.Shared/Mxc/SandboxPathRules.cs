namespace OpenClaw.Shared.Mxc;

/// <summary>One specificity rule for the SDK projection and the fixed worker's data-only view.</summary>
public static class SandboxPathRules
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Within(string path, string boundary) =>
        path.Equals(boundary, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static T? Effective<T>(string path, IEnumerable<(string Path, T Access)> boundaries) where T : struct
    {
        path = Normalize(path);
        T? result = null;
        var length = -1;
        foreach (var (boundary, access) in boundaries)
        {
            var normalized = Normalize(boundary);
            if (normalized.Length >= length && Within(path, normalized))
            {
                length = normalized.Length;
                result = access;
            }
        }
        return result;
    }
}
