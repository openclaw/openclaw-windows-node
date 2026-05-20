using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

/// <summary>
/// MSIX-only update path. When the tray is running as a packaged app the
/// canonical non-Store auto-update channel is an <c>.appinstaller</c> file
/// hosted at a stable URL (see <c>installer/openclaw-companion.appinstaller.template</c>
/// and <c>docs/RELEASING.md</c>). Windows AppInstaller polls that URL via
/// its background task and applies package registration when the app is not
/// in use. This service exposes the manual path the user takes when they
/// click "Check for updates" without making force-shutdown the default.
///
/// This service is only invoked when <see cref="PackageHelper.IsPackaged"/>
/// is true. The unpackaged dev / debug path is intentionally unchanged
/// (it stamps status in <c>App.xaml.cs</c> and does not try to self-update).
/// </summary>
internal static class AppInstallerUpdateService
{
    /// <summary>
    /// Stable x64 URL of the AppInstaller XML on GitHub Pages.
    /// </summary>
    public const string LatestX64AppInstallerUri =
        "https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller";

    /// <summary>
    /// Stable ARM64 URL of the AppInstaller XML on GitHub Pages.
    /// </summary>
    public const string LatestArm64AppInstallerUri =
        "https://openclaw.github.io/openclaw-windows-node/openclaw-arm64.appinstaller";

    public static string LatestAppInstallerUri =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? LatestArm64AppInstallerUri
            : LatestX64AppInstallerUri;

    /// <summary>
    /// Reflects the outcome of <see cref="TryApplyUpdateAsync"/> so the caller
    /// can surface a meaningful status to the user without coupling to WinRT.
    /// </summary>
    public enum UpdateOutcome
    {
        /// <summary>Windows accepted the update request; registration may complete after restart.</summary>
        UpdateQueued,
        /// <summary>No newer version is currently published at the AppInstaller URL.</summary>
        NoUpdateAvailable,
        /// <summary>The call ran but Windows reported a non-fatal failure (e.g. network).</summary>
        Failed,
        /// <summary>Caller invoked the service from an unpackaged process (programming error).</summary>
        NotPackaged
    }

    public record UpdateResult(UpdateOutcome Outcome, string? DetailMessage);

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

        var uri = new Uri(appInstallerUri ?? LatestAppInstallerUri, UriKind.Absolute);

        try
        {
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
                manager.GetDefaultPackageVolume());

            var result = await deploymentOperation.AsTask();

            if (result.IsRegistered)
            {
                return new UpdateResult(UpdateOutcome.UpdateQueued,
                    forceRestart
                        ? "Update applied; Windows will restart the app."
                        : "Update accepted; restart OpenClaw when convenient to finish.");
            }

            // ExtendedErrorCode is the canonical "why didn't it install" surface.
            // 0x80073D02 (E_PACKAGES_IN_USE) is the typical "no update available"
            // shape from AppInstaller; treat unknown failures as Failed not
            // NoUpdateAvailable so the UI never silently lies about being up to date.
            var hr = (uint)result.ExtendedErrorCode.HResult;
            return hr switch
            {
                0x80073D02 => new UpdateResult(UpdateOutcome.NoUpdateAvailable,
                    "Already on the latest version published at the AppInstaller URL."),
                _ => new UpdateResult(UpdateOutcome.Failed,
                    $"PackageManager reported HRESULT 0x{hr:X8}: {result.ErrorText}")
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult(UpdateOutcome.Failed, ex.Message);
        }
    }
}
