namespace OpenClaw.SetupEngine;

internal static class DistroInstallPathPolicy
{
    public const string LegacyReplacementGuidance =
        "To replace an existing distro with an unsupported legacy name, rerun SetupEngine with --uninstall --confirm-destructive and the same distro name, then rerun setup with a supported name.";

    private const int MaxDistroNameLength = 64;
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    public static bool TryGetNewInstallPath(
        string localDataDir,
        string? distroName,
        out string installPath,
        out string error)
    {
        installPath = "";

        if (string.IsNullOrWhiteSpace(distroName))
        {
            error = "WSL distro name is required.";
            return false;
        }

        if (!IsValidDistroName(distroName))
        {
            error = $"Invalid WSL distro name '{distroName}'. Use 1-{MaxDistroNameLength} ASCII letters, digits, periods, underscores, or hyphens, starting and ending with a letter or digit.";
            return false;
        }

        return TryGetManagedInstallPath(localDataDir, distroName, out installPath, out error);
    }

    public static bool TryGetManagedInstallPath(
        string localDataDir,
        string? distroName,
        out string installPath,
        out string error)
    {
        installPath = "";

        if (!IsSafeManagedPathSegment(distroName, out error))
            return false;

        string localDataRoot;
        string wslRoot;
        try
        {
            localDataRoot = NormalizePath(localDataDir);
            wslRoot = NormalizePath(Path.Combine(localDataRoot, "wsl"));
            installPath = NormalizePath(Path.Combine(wslRoot, distroName!));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            installPath = "";
            error = $"Invalid WSL distro install path: {ex.Message}";
            return false;
        }

        if (!PathEquals(Path.GetDirectoryName(installPath), wslRoot))
        {
            error = $"WSL distro install path '{installPath}' must be an immediate child of '{wslRoot}'.";
            installPath = "";
            return false;
        }

        if (TryValidateAncestors(localDataRoot, wslRoot, out error))
            return true;

        installPath = "";
        return false;
    }

    public static string WithLegacyReplacementGuidance(string? distroName, string error)
        => IsLegacyTeardownOnlyName(distroName)
            ? $"{error} {LegacyReplacementGuidance}"
            : error;

    public static bool TryValidateDeleteTarget(
        string localDataDir,
        string? distroName,
        string candidatePath,
        out string deletePath,
        out string error)
    {
        if (!TryGetManagedInstallPath(localDataDir, distroName, out deletePath, out error))
            return false;

        string candidate;
        try
        {
            candidate = NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid WSL distro deletion path: {ex.Message}";
            return false;
        }

        if (!PathEquals(candidate, deletePath))
        {
            error = $"Refusing to delete WSL path '{candidate}'; expected the app-owned distro path '{deletePath}'.";
            return false;
        }

        return true;
    }

    private static bool TryValidateAncestors(string localDataRoot, string wslRoot, out string error)
    {
        string? current = wslRoot;
        while (current is not null)
        {
            try
            {
                if (Directory.Exists(current) &&
                    new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    error = $"Refusing to operate under '{current}' because it is a reparse point; remove it manually and retry setup.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = $"Cannot verify WSL ancestor directory '{current}': {ex.Message}";
                return false;
            }

            if (PathEquals(current, localDataRoot))
            {
                error = "";
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        error = $"WSL install path '{wslRoot}' is not contained within '{localDataRoot}'.";
        return false;
    }

    private static bool IsValidDistroName(string name)
        => name.Length <= MaxDistroNameLength &&
           char.IsAsciiLetterOrDigit(name[0]) &&
           char.IsAsciiLetterOrDigit(name[^1]) &&
           name.All(IsAllowedDistroNameCharacter);

    private static bool IsAllowedDistroNameCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-';

    private static bool IsLegacyTeardownOnlyName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           !IsValidDistroName(name) &&
           IsSafeManagedPathSegment(name, out _);

    private static bool IsSafeManagedPathSegment(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "WSL distro name is required.";
            return false;
        }

        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            name.EndsWith('.') ||
            name is "." or ".." ||
            Path.IsPathRooted(name) ||
            name.IndexOfAny(InvalidFileNameChars) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            IsReservedDeviceName(name))
        {
            error = $"Invalid managed WSL distro name '{name}'. The name must be one unambiguous Windows path segment.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0];
        return ReservedDeviceNames.Contains(stem);
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string? left, string right)
        => string.Equals(left, right, PathComparison);
}
