using OpenClaw.Connection;
using OpenClawTray.Helpers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

/// <summary>Uses the normal Dashboard credential path without setup completion authority.</summary>
internal sealed class GatewayDashboardLauncher(
    Func<bool> ensureTunnel,
    Func<InteractiveGatewayCredential?> resolveCredential,
    Func<string, Task<bool>> launchBrowser,
    Action reportFailure,
    Action? reportOpened = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";
    public async Task<bool> OpenAsync(string? path = null)
    {
        try
        {
            if (!ensureTunnel())
                throw new InvalidOperationException("The Gateway tunnel is unavailable.");
            var credential = resolveCredential()
                ?? throw new InvalidOperationException("The Gateway credential is unavailable.");
            var url = GatewayDashboardUrlBuilder.Build(credential.GatewayUrl, path, credential.Token,
                !credential.IsBootstrapToken && credential.Source == CredentialResolver.SourceSharedGatewayToken);
            if (!await launchBrowser(url))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include the full credential-bearing URL. Do not log them.
            reportFailure();
            return false;
        }
        reportOpened?.Invoke();
        return true;
    }

    public async Task<bool> OpenPreparedAsync(
        string url,
        Func<Task<bool>>? confirmReady = null,
        string? tlsHost = null,
        string? originKey = null)
    {
        // The ownership check that built this URL can be stale by the time the
        // browser starts. Refuse the launch when that check no longer holds.
        if (confirmReady is not null && !await confirmReady())
            return false;

        // The browser receives a loopback page with no credential. The credential
        // is released only when that page is requested and ownership still holds.
        var launchUrl = confirmReady is null
            ? url
            : DashboardCredentialHandoff.Start(confirmReady, url, tlsHost, originKey);
        return await OpenUrlAsync(launchUrl);
    }

    private async Task<bool> OpenUrlAsync(string url)
    {
        try
        {
            if (!await launchBrowser(url))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include the full credential-bearing URL. Do not log them.
            reportFailure();
            return false;
        }

        reportOpened?.Invoke();
        return true;
    }

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception or COMException;
}
