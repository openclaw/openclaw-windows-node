// Final hand-off of a credential-bearing Gateway Dashboard URL to Windows.
//
// Usage (App.CreateDashboardLauncher):
//   var browser = new DashboardBrowser(
//       async url => await Launcher.LaunchUriAsync(new Uri(url)), Logger.Info);
//   new GatewayDashboardLauncher(..., browser.OpenAsync, ...);
//
// Ordinary runs pass the URL straight to Windows. Gateway fixture runs never reach a real
// browser: the launch is logged with a redacted URL and reported as opened, so full-app
// tests can observe that dispatch happened (or did not) without leaking the fixture token.
// A fixture can also ask for one simulated failure to exercise the visible Retry path.

using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Opens a Dashboard URL for <see cref="GatewayDashboardLauncher"/>. Outside fixture mode this is
/// a pass-through. In fixture mode it only ever suppresses or fails a launch; it never opens
/// anything and cannot widen what the launcher has already authorized.
/// </summary>
internal sealed class DashboardBrowser(
    Func<string, Task<bool>> launchWindowsBrowser,
    Action<string> log,
    Func<string, string?>? getEnvironmentVariable = null)
{
    /// <summary>
    /// Fixture-only. When this file exists in the fixture data directory, the next Dashboard
    /// launch fails once and the file is consumed.
    /// </summary>
    internal const string SimulatedFailureMarkerFileName = "dashboard-browser-failure.once";

    /// <summary>Fixture-mode log prefixes. Full-app tests wait for them, so keep them shared.</summary>
    internal const string SuppressedLaunchLogPrefix = "Gateway fixture mode: suppressed Dashboard browser launch: ";
    internal const string SimulatedFailureLogPrefix = "Gateway fixture mode: simulated Dashboard browser failure: ";

    private static readonly object SimulatedFailureGate = new();

    /// <summary>Returns false when the browser could not be opened.</summary>
    public async Task<bool> OpenAsync(string url)
    {
        var fixture = GatewayFixtureIsolation.Get(getEnvironmentVariable);
        if (!fixture.IsEnabled)
            return await launchWindowsBrowser(url);

        // The URL carries the Gateway credential. Log only its origin and first path segment.
        var target = UrlLogSanitizer.Sanitize(url);
        if (TryConsumeSimulatedFailure(fixture.DataDirectory!))
        {
            log(SimulatedFailureLogPrefix + target);
            return false;
        }

        log(SuppressedLaunchLogPrefix + target);
        return true;
    }

    private static bool TryConsumeSimulatedFailure(string dataDirectory)
    {
        var marker = Path.Combine(dataDirectory, SimulatedFailureMarkerFileName);
        try
        {
            // Launches can overlap; checking and deleting under one lock keeps the failure once-only.
            lock (SimulatedFailureGate)
            {
                if (!File.Exists(marker))
                    return false;
                File.Delete(marker);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
