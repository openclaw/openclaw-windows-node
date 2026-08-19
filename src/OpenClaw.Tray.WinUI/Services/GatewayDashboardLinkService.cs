using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

public sealed record GatewayDashboardLinkRequest(
    string GatewayUrl,
    string? Path,
    string? BrowserCredential,
    bool AppendBrowserCredential,
    string? TailscaleGatewayId = null);

public sealed record GatewayDashboardLinkResult(
    string? Url,
    bool TrustTailscaleAuth,
    string? Error = null,
    string? RevalidationError = null)
{
    public bool Success => Url is not null && Error is null;
}

/// <summary>
/// Owns dashboard-link authentication policy while callers retain UI and MCP side effects.
/// </summary>
public sealed class GatewayDashboardLinkService
{
    internal const string NoBrowserCompatibleCredential =
        "No browser-compatible gateway credential is available";
    internal const string RevalidationFailed =
        "Tailscale dashboard authentication revalidation failed";

    private readonly Func<string, CancellationToken, Task<bool>> _revalidateTailscaleAuth;

    public GatewayDashboardLinkService(
        Func<string, CancellationToken, Task<bool>> revalidateTailscaleAuth)
    {
        _revalidateTailscaleAuth = revalidateTailscaleAuth
            ?? throw new ArgumentNullException(nameof(revalidateTailscaleAuth));
    }

    public async Task<GatewayDashboardLinkResult> BuildAsync(
        GatewayDashboardLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        var trustTailscaleAuth = false;
        string? revalidationError = null;

        if (!string.IsNullOrWhiteSpace(request.TailscaleGatewayId))
        {
            try
            {
                trustTailscaleAuth = await _revalidateTailscaleAuth(
                        request.TailscaleGatewayId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                revalidationError = RevalidationFailed;
            }
        }

        if (!trustTailscaleAuth &&
            (!request.AppendBrowserCredential || string.IsNullOrWhiteSpace(request.BrowserCredential)))
        {
            return new GatewayDashboardLinkResult(
                Url: null,
                TrustTailscaleAuth: false,
                Error: NoBrowserCompatibleCredential,
                RevalidationError: revalidationError);
        }

        var url = GatewayDashboardUrlBuilder.Build(
            request.GatewayUrl,
            request.Path,
            request.BrowserCredential,
            request.AppendBrowserCredential && !trustTailscaleAuth,
            trustTailscaleAuth);

        return new GatewayDashboardLinkResult(
            url,
            trustTailscaleAuth,
            RevalidationError: revalidationError);
    }
}
