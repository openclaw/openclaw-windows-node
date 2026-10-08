namespace OpenClaw.Connection.Migration;

/// <summary>
/// Stable three- or four-part numeric releases and the repository's numeric
/// stable-correction form are comparable. Other prerelease and informational
/// versions cannot authorize an older uninstaller.
/// </summary>
public static class MigrationVersionPolicy
{
    public static bool TryParseReleaseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null)
            return false;

        if (TryParseNumericVersion(value, out version))
            return true;

        return TryParseStableCorrection(value, out version);
    }

    public static bool IsExecutableVersionCompatible(
        string releaseVersion, string? executableFileVersion, string? executableProductVersion)
    {
        if (!TryParseReleaseVersion(releaseVersion, out var release) ||
            !TryParseNumericVersion(executableFileVersion, out var executable))
            return false;

        if (!TryParseStableCorrection(releaseVersion, out _))
        {
            if (release != executable)
                return false;
            if (string.IsNullOrEmpty(executableProductVersion))
                return true;
            return TryGetProductRelease(executableProductVersion, out var baseProductRelease) &&
                !baseProductRelease.Contains('-') &&
                TryParseNumericVersion(baseProductRelease, out var baseProductVersion) &&
                baseProductVersion == release;
        }

        var baseVersion = new Version(release.Major, release.Minor, release.Build, 0);
        return executable == baseVersion &&
            TryGetProductRelease(executableProductVersion, out var productRelease) &&
            string.Equals(productRelease, releaseVersion, StringComparison.Ordinal);
    }

    private static bool TryParseStableCorrection(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var separator = value.LastIndexOf('-');
        if (separator <= 0 || value.IndexOf('-') != separator)
            return false;

        var correctionText = value.AsSpan(separator + 1);
        if (correctionText.Length == 0 || correctionText[0] == '0' ||
            correctionText.IndexOfAnyExcept("0123456789") >= 0 ||
            !int.TryParse(correctionText, out var correction))
            return false;

        var baseText = value[..separator];
        if (baseText.Count(character => character == '.') != 2 ||
            !TryParseNumericVersion(baseText, out var baseVersion))
            return false;

        version = new Version(baseVersion.Major, baseVersion.Minor, baseVersion.Build, correction);
        return true;
    }

    private static bool TryGetProductRelease(string? value, out string release)
    {
        release = "";
        if (string.IsNullOrEmpty(value))
            return false;

        var separator = value.IndexOf('+');
        if (separator < 0)
        {
            release = value;
            return true;
        }
        if (separator == 0 || separator != value.LastIndexOf('+'))
            return false;

        var metadata = value.AsSpan(separator + 1);
        if (metadata.Length == 0)
            return false;
        var identifierLength = 0;
        foreach (var character in metadata)
        {
            if (character == '.')
            {
                if (identifierLength == 0)
                    return false;
                identifierLength = 0;
                continue;
            }
            if (character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and not '-')
                return false;
            identifierLength++;
        }
        if (identifierLength == 0)
            return false;

        release = value[..separator];
        return true;
    }

    private static bool TryParseNumericVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null)
            return false;

        var parts = value.Split('.');
        if (parts.Length is not (3 or 4) ||
            parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9')) ||
            !Version.TryParse(value, out var parsed))
            return false;

        version = new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(parsed.Revision, 0));
        return true;
    }
}
