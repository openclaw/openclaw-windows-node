using System;

namespace OpenClawTray.Helpers;

/// <summary>
/// Detects whether the app is running as an MSIX-packaged app or unpackaged.
/// </summary>
internal static class PackageHelper
{
    private static readonly Lazy<string?> CurrentPackageVersion = new(DetectPackageVersion);

    /// <summary>
    /// Returns true if the app is running with package identity (MSIX).
    /// </summary>
    public static bool IsPackaged => PackageVersion is not null;

    /// <summary>
    /// Returns the four-part Windows package identity version, or null when unpackaged.
    /// </summary>
    public static string? PackageVersion => CurrentPackageVersion.Value;

    private static string? DetectPackageVersion()
    {
        try
        {
            // Package.Current throws if not running in a packaged context
            var package = global::Windows.ApplicationModel.Package.Current;
            var version = package.Id.Version;
            return FormattableString.Invariant(
                $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
        }
        catch
        {
            return null;
        }
    }
}
