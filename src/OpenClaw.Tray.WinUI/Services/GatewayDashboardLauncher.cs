using OpenClaw.Connection;
using OpenClawTray.Helpers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

/// <summary>Uses the normal Dashboard credential path without setup completion authority.</summary>
internal sealed class GatewayDashboardLauncher(
    Func<Task<bool>> ensureTunnel,
    Func<InteractiveGatewayCredential?> resolveCredential,
    Func<string, Task<bool>> launchBrowser,
    Action reportFailure,
    Action? reportOpened = null,
    Func<int?>? ownedLocalForwardPort = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";
    public async Task<bool> OpenAsync(string? path = null)
    {
        try
        {
            if (!await ensureTunnel())
                throw new InvalidOperationException("The Gateway tunnel is unavailable.");
            var credential = resolveCredential()
                ?? throw new InvalidOperationException("The Gateway credential is unavailable.");
            var gatewayUrl = ownedLocalForwardPort?.Invoke() is int localPort
                ? DashboardBrowserHandoff.ProjectOntoLocalForward(credential.GatewayUrl, localPort)
                : credential.GatewayUrl;
            var url = GatewayDashboardUrlBuilder.Build(gatewayUrl, path, credential.Token,
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
