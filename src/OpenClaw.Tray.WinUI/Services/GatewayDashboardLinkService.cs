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
    internal const string TailscaleFallbackUnavailable =
        "Tailscale authentication is unavailable and no approved browser credential is available";

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
            catch (Exception ex)
            {
                revalidationError = ex.Message;
            }

            if (!trustTailscaleAuth && !request.AppendBrowserCredential)
            {
                return new GatewayDashboardLinkResult(
                    Url: null,
                    TrustTailscaleAuth: false,
                    Error: TailscaleFallbackUnavailable,
                    RevalidationError: revalidationError);
            }
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
