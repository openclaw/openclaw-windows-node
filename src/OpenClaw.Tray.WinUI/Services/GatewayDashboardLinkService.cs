using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

// Builds Dashboard links with current Tailscale trust and credential requirements.
// Usage:
//   var result = await links.BuildAsync(request, cancellationToken);
/// <summary>Inputs used to build a Gateway Dashboard link.</summary>
public sealed record GatewayDashboardLinkRequest(
    string GatewayUrl,
    string? Path,
    string? BrowserCredential,
    bool AppendBrowserCredential,
    string? TailscaleGatewayId = null);

/// <summary>The Dashboard link and credential or revalidation result.</summary>
public sealed record GatewayDashboardLinkResult(
    string? Url,
    bool TrustTailscaleAuth,
    string? Error = null,
    string? RevalidationError = null,
    bool BrowserCredentialIncluded = false)
{
    public bool Success => Url is not null && Error is null;
}

/// <summary>Builds Dashboard URLs, revalidating trust when a Tailscale Gateway ID is supplied.</summary>
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

    /// <summary>Revalidates Tailscale trust when applicable and builds a browser-compatible URL.</summary>
    /// <param name="request">The Gateway URL, path, and available browser credential.</param>
    /// <param name="cancellationToken">Cancels trust revalidation.</param>
    /// <returns>The built link or the reason a compatible link could not be created.</returns>
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

        if (!GatewayDashboardUrlBuilder.TryBuild(
                request.GatewayUrl,
                request.Path,
                request.BrowserCredential,
                request.AppendBrowserCredential && !trustTailscaleAuth,
                out var url,
                out var urlError,
                trustTailscaleAuth))
        {
            return new GatewayDashboardLinkResult(
                Url: null,
                TrustTailscaleAuth: trustTailscaleAuth,
                Error: urlError,
                RevalidationError: revalidationError);
        }

        var browserCredentialIncluded = GatewayDashboardUrlBuilder.HasSharedTokenFragment(url);

        return new GatewayDashboardLinkResult(
            url,
            trustTailscaleAuth,
            RevalidationError: revalidationError,
            BrowserCredentialIncluded: browserCredentialIncluded);
    }
}
