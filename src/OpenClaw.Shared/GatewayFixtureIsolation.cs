namespace OpenClaw.Shared;

/// <summary>
/// Validates the explicit, browse-only Gateway fixture context without performing IO.
/// An isolated data directory alone does not enable fixture mode.
/// </summary>
/// <remarks>
/// The launcher owns directory creation, unique run ownership, installed-profile and
/// reparse-point checks, and cleanup. This guard prevents missing or ambiguous overrides
/// from falling back to installed state; it is not an OS sandbox.
/// </remarks>
public static class GatewayFixtureIsolation
{
    public const string ModeEnvironmentVariable = "OPENCLAW_GATEWAY_FIXTURE";
    public const string DataDirectoryEnvironmentVariable = "OPENCLAW_TRAY_DATA_DIR";
    public const string LocalDataDirectoryEnvironmentVariable = "OPENCLAW_TRAY_LOCAL_DATA_DIR";
    public const string LocalAppDataDirectoryEnvironmentVariable = "OPENCLAW_TRAY_LOCALAPPDATA_DIR";

    private static readonly GatewayFixtureIsolationContext _disabled = new(false, null, null);

    /// <summary>Throws on an invalid explicit fixture context instead of treating it as ordinary mode.</summary>
    public static bool IsEnabled => Get().IsEnabled;

    /// <summary>
    /// Reads the current process environment, or a launcher's child-environment snapshot.
    /// Returns canonical absolute roots only for an explicitly enabled, valid fixture.
    /// </summary>
    /// <exception cref="InvalidOperationException">An explicit fixture context is invalid.</exception>
    public static GatewayFixtureIsolationContext Get(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        if (!string.Equals(getEnvironmentVariable(ModeEnvironmentVariable), "1", StringComparison.Ordinal))
            return _disabled;

        var dataDirectory = ValidateAbsoluteDirectory(
            getEnvironmentVariable(DataDirectoryEnvironmentVariable), DataDirectoryEnvironmentVariable);
        var localDataDirectory = ValidateAbsoluteDirectory(
            getEnvironmentVariable(LocalDataDirectoryEnvironmentVariable), LocalDataDirectoryEnvironmentVariable);

        // SetupEngine gives this legacy override precedence over LOCAL_DATA_DIR.
        // Never accept a context whose validated root would not actually be used.
        if (!string.IsNullOrEmpty(getEnvironmentVariable(LocalAppDataDirectoryEnvironmentVariable)))
        {
            throw new InvalidOperationException(
                $"Gateway fixture mode requires {LocalAppDataDirectoryEnvironmentVariable} to be cleared.");
        }

        return new GatewayFixtureIsolationContext(true, dataDirectory, localDataDirectory);
    }

    private static string ValidateAbsoluteDirectory(string? value, string variableName)
    {
        const string requirement = "to contain a nonempty, valid absolute directory path.";
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"Gateway fixture mode requires {variableName} {requirement}");

        try
        {
            if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("Invalid path characters.");

            var fullPath = Path.GetFullPath(value);
            var rootLength = Path.GetPathRoot(fullPath)!.Length;
            foreach (var segment in fullPath[rootLength..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Invalid directory characters.");
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Gateway fixture mode requires {variableName} {requirement}", ex);
        }
    }
}

/// <summary>A validated fixture-mode snapshot. Disabled snapshots do not resolve installed paths.</summary>
public sealed record GatewayFixtureIsolationContext(
    bool IsEnabled,
    string? DataDirectory,
    string? LocalDataDirectory);
