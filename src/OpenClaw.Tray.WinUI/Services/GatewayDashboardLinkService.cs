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
    internal const string NoBrowserCompatibleCredentialKey =
        "DashboardLink_NoBrowserCompatibleCredential";
    internal const string RevalidationFailedKey =
        "DashboardLink_TailscaleRevalidationFailed";

    private readonly Func<string, CancellationToken, Task<bool>> _revalidateTailscaleAuth;
    private readonly Func<string, string> _localize;

    public GatewayDashboardLinkService(
        Func<string, CancellationToken, Task<bool>> revalidateTailscaleAuth,
        Func<string, string> localize)
    {
        _revalidateTailscaleAuth = revalidateTailscaleAuth
            ?? throw new ArgumentNullException(nameof(revalidateTailscaleAuth));
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
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
                revalidationError = _localize(RevalidationFailedKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.TailscaleGatewayId) &&
            !trustTailscaleAuth &&
            (!request.AppendBrowserCredential || string.IsNullOrWhiteSpace(request.BrowserCredential)))
        {
            return new GatewayDashboardLinkResult(
                Url: null,
                TrustTailscaleAuth: false,
                Error: _localize(NoBrowserCompatibleCredentialKey),
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
