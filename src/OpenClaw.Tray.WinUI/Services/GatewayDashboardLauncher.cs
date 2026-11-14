using OpenClaw.SetupEngine;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

internal sealed class GatewayDashboardLauncher(
    Func<bool> ensureTunnel,
    Func<string?, GatewayDashboardLinkRequest?> resolveRequest,
    GatewayDashboardLinkService linkService,
    Func<string, Task<bool>> launchBrowser,
    Action<GatewayDashboardLinkResult> reportLinkFailure,
    Action<string?> reportFailure,
    Action? reportOpened = null,
    Action<string>? reportRevalidationWarning = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";

    public async Task<bool> OpenAsync(
        string? path = null,
        Action? authorizeDispatch = null,
        CancellationToken cancellationToken = default)
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
            reportFailure(null);
            return false;
        }

        if (request is null)
        {
            reportFailure(null);
            return false;
        }

        return await PrepareAndLaunchAsync(
            request,
            validateBeforeLaunch: null,
            authorizeDispatch,
            cancellationToken);
    }

    internal Task<bool> OpenSavedAsync(
        GatewayDashboardLinkRequest request,
        Func<GatewayDashboardLinkResult, Task<bool>> validateBeforeLaunch,
        CancellationToken cancellationToken = default,
        Action? authorizeDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validateBeforeLaunch);
        return PrepareAndLaunchAsync(request, validateBeforeLaunch, authorizeDispatch, cancellationToken);
    }

    private async Task<bool> PrepareAndLaunchAsync(
        GatewayDashboardLinkRequest request,
        Func<GatewayDashboardLinkResult, Task<bool>>? validateBeforeLaunch,
        Action? authorizeDispatch,
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
            reportFailure(result.Error);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.BrowserCredentialIncluded && validateBeforeLaunch is not null &&
            !await validateBeforeLaunch(result))
            return false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            authorizeDispatch?.Invoke();
            if (!await launchBrowser(result.Url!))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
        }
        catch (SetupNativeOwnershipException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include a credential-bearing URL. Do not log them.
            reportFailure(null);
            return false;
        }

        reportOpened?.Invoke();
        return true;
    }

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or FormatException or Win32Exception or COMException;
}
