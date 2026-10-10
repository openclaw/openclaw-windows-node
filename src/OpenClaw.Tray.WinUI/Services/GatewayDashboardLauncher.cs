using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

/// <summary>Owns browser launch outcomes while the link service owns Dashboard URL/auth policy.</summary>
internal sealed class GatewayDashboardLauncher(
    Func<bool> ensureTunnel,
    Func<string?, GatewayDashboardLinkRequest?> resolveRequest,
    GatewayDashboardLinkService linkService,
    Func<string, Task<bool>> launchBrowser,
    Action<GatewayDashboardLinkResult> reportLinkFailure,
    Action reportFailure,
    Action? reportOpened = null,
    Action<string>? reportRevalidationWarning = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";

    public async Task<bool> OpenAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GatewayDashboardLinkRequest? request;
        try
        {
            if (!ensureTunnel())
                throw new InvalidOperationException("The Gateway tunnel is unavailable.");
            request = resolveRequest(path);
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            reportFailure();
            return false;
        }

        if (request is null)
        {
            reportFailure();
            return false;
        }

        return await PrepareAndLaunchAsync(request, validateBeforeLaunch: null, cancellationToken: cancellationToken);
    }

    internal Task<bool> OpenSavedAsync(
        GatewayDashboardLinkRequest request,
        Func<GatewayDashboardLinkResult, Task<bool>> validateBeforeLaunch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validateBeforeLaunch);
        return PrepareAndLaunchAsync(request, validateBeforeLaunch, cancellationToken);
    }

    private async Task<bool> PrepareAndLaunchAsync(
        GatewayDashboardLinkRequest request,
        Func<GatewayDashboardLinkResult, Task<bool>>? validateBeforeLaunch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await linkService.BuildAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.RevalidationError is not null)
            reportRevalidationWarning?.Invoke(result.RevalidationError);

        if (!result.Success)
        {
            reportLinkFailure(result);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.BrowserCredentialIncluded && validateBeforeLaunch is not null &&
            !await validateBeforeLaunch(result))
            return false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await launchBrowser(result.Url!))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include a credential-bearing URL. Do not log them.
            reportFailure();
            return false;
        }

        reportOpened?.Invoke();
        return true;
    }

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or FormatException or Win32Exception or COMException;
}
