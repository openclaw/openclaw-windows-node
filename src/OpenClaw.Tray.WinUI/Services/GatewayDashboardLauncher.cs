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
    Action? reportOpened = null,
    Func<bool>? isPersonalMode = null,
    Func<string?, Task<bool>>? openPersonalDashboard = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";
    public async Task<bool> OpenAsync(string? path = null)
    {
        try
        {
            if (isPersonalMode?.Invoke() == true)
            {
                if (openPersonalDashboard is null || !await openPersonalDashboard(path))
                    throw new InvalidOperationException("Personal Gateway identity is unavailable.");
                reportOpened?.Invoke();
                return true;
            }
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

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception or COMException;
}
