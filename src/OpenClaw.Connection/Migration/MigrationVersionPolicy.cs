namespace OpenClaw.Connection.Migration;

/// <summary>
/// Only stable, three- or four-part numeric release versions are comparable.
/// Prerelease and informational versions cannot authorize an older uninstaller.
/// </summary>
public static class MigrationVersionPolicy
{
    public static bool TryParseReleaseVersion(string? value, out Version version)
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
