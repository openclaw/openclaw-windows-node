using System;
using System.Threading.Tasks;
using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

/// <summary>
/// MSIX-only update path. When the tray is running as a packaged app the
/// canonical non-Store auto-update channel is an <c>.appinstaller</c> file
/// hosted at a stable URL (see <c>installer/openclaw-companion.appinstaller.template</c>
/// and <c>docs/RELEASING.md</c>). Windows AppInstaller polls that URL on
/// launch automatically via the <c>OnLaunch</c> settings embedded in the
/// AppInstaller XML. This service exposes the *manual* path the user takes
/// when they click "Check for updates" — it asks PackageManager to apply
/// whatever the AppInstaller URL currently advertises, which is the same
/// machinery the OnLaunch poll uses but bypasses the 24 h check window.
///
/// This service is only invoked when <see cref="PackageHelper.IsPackaged"/>
/// is true. The unpackaged dev / debug path is intentionally unchanged
/// (it short-circuits via the <c>#if DEBUG</c> in <c>App.xaml.cs</c>; once
/// Updatum and Inno are sunset in Track 3, the unpackaged update path
/// disappears entirely).
/// </summary>
internal static class AppInstallerUpdateService
{
    /// <summary>
    /// Stable URL of the AppInstaller XML on GitHub Pages. The CI release job
    /// (.github/workflows/ci.yml, step "Render AppInstaller") publishes both a
    /// per-tag file and this stable <c>latest.appinstaller</c> alias; embedded
    /// installs poll this URL according to the OnLaunch settings.
    /// </summary>
    public const string LatestAppInstallerUri =
        "https://openclaw.github.io/openclaw-windows-node/latest.appinstaller";

    /// <summary>
    /// Reflects the outcome of <see cref="TryApplyUpdateAsync"/> so the caller
    /// can surface a meaningful status to the user without coupling to WinRT.
    /// </summary>
    public enum UpdateOutcome
    {
        /// <summary>An upgrade was queued; the tray will be restarted by Windows.</summary>
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
    /// Asks Windows to fetch and apply the MSIX advertised at the AppInstaller URL.
    /// Returns when Windows has accepted (or rejected) the request; the actual
    /// upgrade may continue asynchronously and finishes by restarting the app
    /// (PackageManager invokes <c>ForceApplicationShutdown</c> so we don't fight
    /// the OS for the package container).
    /// </summary>
    public static async Task<UpdateResult> TryApplyUpdateAsync(string? appInstallerUri = null)
    {
        if (!PackageHelper.IsPackaged)
        {
            return new UpdateResult(UpdateOutcome.NotPackaged,
                "AppInstallerUpdateService called from an unpackaged process. " +
                "The unpackaged update path uses Updatum (see App.xaml.cs); " +
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
            var deploymentOperation = manager.AddPackageByAppInstallerFileAsync(
                uri,
                global::Windows.Management.Deployment.AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
                manager.GetDefaultPackageVolume());

            var result = await deploymentOperation.AsTask();

            if (result.IsRegistered)
            {
                // Successfully registered the new version. Windows will restart
                // the app per the ForceTargetAppShutdown flag.
                return new UpdateResult(UpdateOutcome.UpdateQueued,
                    "Update applied; Windows will restart the app.");
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
