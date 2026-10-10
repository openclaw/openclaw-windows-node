namespace OpenClaw.Shared.IO;

/// <summary>
/// Shared Windows path primitives for callers that apply their own ownership and
/// reparse-point policies.
/// </summary>
public static class WindowsPathSafety
{
    public const int MaximumComponentLength = 255;

    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Returns a fully qualified path without a trailing directory separator.</summary>
    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Compares normalized Windows paths using ordinal, case-insensitive semantics.</summary>
    public static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, PathComparison);

    /// <summary>Returns whether a normalized path equals or is nested beneath a normalized root.</summary>
    public static bool IsSameOrDescendant(string candidate, string root) =>
        PathEquals(candidate, root) || IsStrictDescendant(candidate, root);

    /// <summary>Returns whether a normalized path is nested beneath a normalized root.</summary>
    public static bool IsStrictDescendant(string candidate, string root) =>
        candidate.StartsWith(EnsureTrailingDirectorySeparator(root), PathComparison);

    public static string EnsureTrailingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>Returns whether a value is safe to use as one Windows path segment.</summary>
    public static bool IsSafeSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumComponentLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value is not "." and not ".." &&
        !value.EndsWith('.') &&
        !Path.IsPathRooted(value) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar) &&
        !IsWindowsDeviceName(value);

    /// <summary>Returns whether a segment names a reserved Windows device.</summary>
    public static bool IsWindowsDeviceName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        string baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDevice(baseName, "COM") ||
               IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
}
