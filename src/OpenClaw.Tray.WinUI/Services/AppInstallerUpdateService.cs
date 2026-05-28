using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

/// <summary>
/// MSIX packaged update path. When the tray is running as a packaged app the
/// canonical non-Store auto-update channel is an <c>.appinstaller</c> file
/// hosted at a stable URL (see <c>installer/openclaw-companion.appinstaller.template</c>
/// and <c>docs/RELEASING.md</c>). Windows AppInstaller polls that URL via
/// its background task and applies package registration when the app is not
/// in use. This service exposes the manual path the user takes when they
/// click "Check for updates" without making force-shutdown the default.
///
/// This service is only invoked when <see cref="PackageHelper.IsPackaged"/>
/// is true. The unpackaged installer/portable path continues to use Updatum.
/// </summary>
internal static class AppInstallerUpdateService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Stable x64 URL of the AppInstaller XML in the Windows repo.
    /// </summary>
    public const string LatestX64AppInstallerUri =
        "https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller";

    /// <summary>
    /// Stable ARM64 URL of the AppInstaller XML in the Windows repo.
    /// </summary>
    public const string LatestArm64AppInstallerUri =
        "https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-arm64.appinstaller";

    public static string LatestAppInstallerUri =>
        ResolveAppInstallerUri() ?? ArchitectureFallbackAppInstallerUri;

    internal static string ArchitectureFallbackAppInstallerUri =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? LatestArm64AppInstallerUri
            : LatestX64AppInstallerUri;

    /// <summary>
    /// Reflects the outcome of <see cref="TryApplyUpdateAsync"/> so the caller
    /// can surface a meaningful status to the user without coupling to WinRT.
    /// </summary>
    public enum UpdateOutcome
    {
        /// <summary>A newer version is advertised by the AppInstaller feed.</summary>
        UpdateAvailable,
        /// <summary>Windows accepted the update request; registration may complete after restart.</summary>
        UpdateQueued,
        /// <summary>An update is available but Windows needs OpenClaw to exit before registration can finish.</summary>
        UpdatePendingRestart,
        /// <summary>No newer version is currently published at the AppInstaller URL.</summary>
        NoUpdateAvailable,
        /// <summary>The call ran but Windows reported a non-fatal failure (e.g. network).</summary>
        Failed,
        /// <summary>Caller invoked the service from an unpackaged process (programming error).</summary>
        NotPackaged
    }

    public record UpdateResult(UpdateOutcome Outcome, string? DetailMessage);

    internal const int HResultPackagesInUse = unchecked((int)0x80073D02);
    internal const int HResultPackageAlreadyExists = unchecked((int)0x80073CFB);

    /// <summary>
    /// Reads the hosted AppInstaller XML and compares its version with the
    /// installed package version without staging or registering any package.
    /// </summary>
    public static async Task<UpdateResult> CheckForUpdateAsync(
        string? appInstallerUri = null,
        HttpClient? httpClient = null)
    {
        if (!PackageHelper.IsPackaged)
        {
            return new UpdateResult(UpdateOutcome.NotPackaged,
                "AppInstallerUpdateService called from an unpackaged process. " +
                "branch on PackageHelper.IsPackaged before invoking this service.");
        }

        var resolvedUri = ResolveAppInstallerUri(appInstallerUri);
        if (resolvedUri is null)
        {
            return new UpdateResult(UpdateOutcome.Failed,
                $"No AppInstaller update feed is configured for package identity '{TryGetCurrentPackageName() ?? "unknown"}'.");
        }

        var uri = new Uri(resolvedUri, UriKind.Absolute);

        try
        {
            var client = httpClient ?? SharedHttpClient;
            var xml = await client.GetStringAsync(uri);
            var identityResult = ClassifyPublishedIdentity(ParseAppInstallerPackageName(xml));
            if (identityResult is not null)
                return identityResult;

            var publishedVersion = ParseAppInstallerVersion(xml);
            var currentVersion = GetCurrentPackageVersion();
            return ClassifyPublishedVersion(currentVersion, publishedVersion);
        }
        catch (Exception ex)
        {
            return new UpdateResult(UpdateOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Asks Windows to fetch the MSIX advertised at the AppInstaller URL.
    /// By default this does not force-close the tray; callers that expose an
    /// explicit "Update now and restart" affordance may opt in to force restart.
    /// </summary>
    public static async Task<UpdateResult> TryApplyUpdateAsync(
        string? appInstallerUri = null,
        bool forceRestart = false)
    {
        if (!PackageHelper.IsPackaged)
        {
            return new UpdateResult(UpdateOutcome.NotPackaged,
                "AppInstallerUpdateService called from an unpackaged process. " +
                "branch on PackageHelper.IsPackaged before invoking this service.");
        }

        var resolvedUri = ResolveAppInstallerUri(appInstallerUri);
        if (resolvedUri is null)
        {
            return new UpdateResult(UpdateOutcome.Failed,
                $"No AppInstaller update feed is configured for package identity '{TryGetCurrentPackageName() ?? "unknown"}'.");
        }

        var uri = new Uri(resolvedUri, UriKind.Absolute);

        try
        {
            var client = SharedHttpClient;
            var xml = await client.GetStringAsync(uri);
            var identityResult = ClassifyPublishedIdentity(ParseAppInstallerPackageName(xml));
            if (identityResult is not null)
                return identityResult;

            // Late-bind PackageManager so the file compiles on unpackaged test
            // builds that don't actually link against Windows.Management.Deployment.
            // Same global:: prefix dance as AutoStartManager — `Windows` resolves
            // to OpenClawTray.Windows here otherwise.
            var manager = new global::Windows.Management.Deployment.PackageManager();
            var options = forceRestart
                ? global::Windows.Management.Deployment.AddPackageByAppInstallerOptions.ForceTargetAppShutdown
                : global::Windows.Management.Deployment.AddPackageByAppInstallerOptions.None;
            var deploymentOperation = manager.AddPackageByAppInstallerFileAsync(
                uri,
                options,
                ResolveCurrentPackageVolume(manager));

            var result = await deploymentOperation.AsTask();
            return ClassifyDeploymentResult(
                result.IsRegistered,
                result.ExtendedErrorCode?.HResult ?? 0,
                result.ErrorText,
                forceRestart);
        }
        catch (Exception ex)
        {
            return new UpdateResult(UpdateOutcome.Failed, ex.Message);
        }
    }

    internal static UpdateResult ClassifyDeploymentResult(
        bool isRegistered,
        int hResult,
        string? errorText,
        bool forceRestart)
    {
        if (isRegistered)
        {
            return new UpdateResult(UpdateOutcome.UpdateQueued,
                forceRestart
                    ? "Update applied; Windows will restart the app."
                    : "Update accepted; restart OpenClaw when convenient to finish.");
        }

        return hResult switch
        {
            HResultPackagesInUse => new UpdateResult(UpdateOutcome.UpdatePendingRestart,
                "An update is available, but OpenClaw is running. Close and reopen OpenClaw to finish installing it."),
            HResultPackageAlreadyExists => new UpdateResult(UpdateOutcome.NoUpdateAvailable,
                "Already on the latest version published at the AppInstaller URL."),
            0 => new UpdateResult(UpdateOutcome.Failed,
                $"PackageManager did not register the package and did not report an HRESULT: {errorText ?? "no error text"}"),
            _ => new UpdateResult(UpdateOutcome.Failed,
                $"PackageManager reported HRESULT 0x{unchecked((uint)hResult):X8}: {errorText}")
        };
    }

    internal static string? ResolveAppInstallerUri(string? appInstallerUri = null)
    {
        if (!string.IsNullOrWhiteSpace(appInstallerUri))
            return appInstallerUri;

        return TryGetRegisteredAppInstallerUri() ?? TryGetChannelFallbackAppInstallerUri();
    }

    private static string? TryGetRegisteredAppInstallerUri()
    {
        if (!PackageHelper.IsPackaged)
            return null;

        try
        {
            var uri = global::Windows.ApplicationModel.Package.Current
                .GetAppInstallerInfo()
                ?.Uri
                ?.AbsoluteUri is { Length: > 0 } uriText
                    ? new Uri(uriText, UriKind.Absolute)
                    : null;
            if (uri is null)
                return null;

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return uri.AbsoluteUri;

            Logger.Warn($"Ignoring non-HTTP AppInstaller source '{uri}'. Falling back to configured architecture feed.");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Logger.Warn($"Failed to read package AppInstaller source; falling back to architecture feed: {ex.Message}");
            return null;
        }
    }

    private static string? TryGetChannelFallbackAppInstallerUri()
    {
        var packageName = TryGetCurrentPackageName();
        if (string.IsNullOrWhiteSpace(packageName) ||
            string.Equals(packageName, "OpenClaw.Companion", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureFallbackAppInstallerUri;
        }

        if (packageName.StartsWith("OpenClaw.Companion.", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"No fallback AppInstaller feed is configured for package identity '{packageName}'.");
            return null;
        }

        return ArchitectureFallbackAppInstallerUri;
    }

    private static string? TryGetCurrentPackageName()
    {
        if (!PackageHelper.IsPackaged)
            return null;

        try
        {
            return global::Windows.ApplicationModel.Package.Current.Id.Name;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Logger.Warn($"Failed to read package identity name: {ex.Message}");
            return null;
        }
    }

    private static UpdateResult? ClassifyPublishedIdentity(string? publishedPackageName)
    {
        var currentPackageName = TryGetCurrentPackageName();
        if (string.IsNullOrWhiteSpace(currentPackageName) ||
            string.IsNullOrWhiteSpace(publishedPackageName) ||
            string.Equals(currentPackageName, publishedPackageName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new UpdateResult(UpdateOutcome.Failed,
            $"AppInstaller feed package identity '{publishedPackageName}' does not match installed package identity '{currentPackageName}'.");
    }

    internal static UpdateResult ClassifyPublishedVersion(Version currentVersion, Version publishedVersion)
    {
        if (publishedVersion.CompareTo(currentVersion) > 0)
        {
            return new UpdateResult(UpdateOutcome.UpdateAvailable,
                $"Version {publishedVersion} is available. Windows AppInstaller will install it in the background when possible.");
        }

        return new UpdateResult(UpdateOutcome.NoUpdateAvailable,
            $"Already on version {currentVersion}; latest published version is {publishedVersion}.");
    }

    internal static Version ParseAppInstallerVersion(string appInstallerXml)
    {
        var doc = XDocument.Parse(appInstallerXml);
        var mainPackage = doc.Root is null
            ? null
            : doc.Root.Elements().SingleOrDefault(element => element.Name.LocalName == "MainPackage");
        var versionText = (string?)mainPackage?.Attribute("Version");
        if (!Version.TryParse(versionText, out var version) || version.Revision < 0)
            throw new FormatException("AppInstaller MainPackage Version must be a four-part version.");

        return version;
    }

    internal static string? ParseAppInstallerPackageName(string appInstallerXml)
    {
        var doc = XDocument.Parse(appInstallerXml);
        var mainPackage = doc.Root is null
            ? null
            : doc.Root.Elements().SingleOrDefault(element => element.Name.LocalName == "MainPackage");
        return (string?)mainPackage?.Attribute("Name");
    }

    private static Version GetCurrentPackageVersion()
    {
        var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
        return new Version(version.Major, version.Minor, version.Build, version.Revision);
    }

    private static global::Windows.Management.Deployment.PackageVolume ResolveCurrentPackageVolume(
        global::Windows.Management.Deployment.PackageManager manager)
    {
        var fallback = manager.GetDefaultPackageVolume();

        try
        {
            var installedPath = global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            foreach (var volume in manager.FindPackageVolumes())
            {
                if (PathIsUnderRoot(installedPath, volume.MountPoint))
                    return volume;
            }
        }
        catch (COMException ex)
        {
            LogPackageVolumeFallback(ex);
        }
        catch (InvalidOperationException ex)
        {
            LogPackageVolumeFallback(ex);
        }
        catch (IOException ex)
        {
            LogPackageVolumeFallback(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogPackageVolumeFallback(ex);
        }

        return fallback;
    }

    private static void LogPackageVolumeFallback(Exception ex) =>
        Logger.Warn($"Failed to resolve current package volume; falling back to default volume: {ex.Message}");

    private static bool PathIsUnderRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
