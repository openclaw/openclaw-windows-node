using System.Net;
using System.Text;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    internal const string ControlUiAssetTimeoutEnvironmentVariable = "OPENCLAW_CONTROL_UI_ASSET_TIMEOUT_MS";
    internal const int DefaultControlUiAssetTimeoutMs = 15_000;

    /// <summary>Deadline for one Control UI file, response headers and body together.</summary>
    internal TimeSpan ControlUiAssetTimeout { get; set; } = ResolveControlUiAssetTimeout();

    private static TimeSpan ResolveControlUiAssetTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable(ControlUiAssetTimeoutEnvironmentVariable), out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(DefaultControlUiAssetTimeoutMs);

    /// <summary>
    /// Reads one static Control UI file over the connection's HTTP base. The Gateway serves these files without request
    /// authentication, so no credential is sent; redirects are not followed (a redirect, for example to a sign-in page,
    /// is a failure). The URI must stay on the connection's origin and under its mount. A result from an earlier
    /// connection generation is discarded.
    /// </summary>
    public async Task<string?> ReadControlUiTextAsync(
        string path, string mediaType, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0 || !TryCaptureMediaLease(out var lease) ||
            !TryResolveControlUiUri(lease.HttpBaseUri, path, out var uri))
            return null;

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
            linkedCancellation.CancelAfter(ControlUiAssetTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(mediaType);
            using var response = await _assistantMediaHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                _logger.Info($"Control UI file unavailable ({(int)response.StatusCode}).");
                return null;
            }
            var bytes = await ReadBoundedAsync(response.Content, maxBytes, linkedCancellation.Token).ConfigureAwait(false);
            return bytes is not null && IsCurrentMediaLease(lease) ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A timeout surfaces as cancellation without the caller's token; the connection's own cancellation lands here too.
            _logger.Warn($"Control UI file request failed ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>
    /// Resolves a Control UI path (relative to the mount, or root-absolute as index.html serves it) against the HTTP
    /// base, which is the mount. Rejects anything that would leave the origin or the mount.
    /// </summary>
    internal static bool TryResolveControlUiUri(Uri httpBaseUri, string path, out Uri uri)
    {
        uri = null!;
        if (path.Contains('\\') || path.Contains("..", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains(':') || path.Contains('?') || path.Contains('#'))
            return false;
        var root = new UriBuilder(httpBaseUri) { Query = string.Empty, Fragment = string.Empty };
        if (!root.Path.EndsWith('/'))
            root.Path += "/";
        var mount = root.Uri;
        if (!Uri.TryCreate(mount, path, out var candidate) ||
            !string.Equals(candidate.GetLeftPart(UriPartial.Authority), mount.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
            !candidate.AbsolutePath.StartsWith(mount.AbsolutePath, StringComparison.Ordinal))
            return false;
        uri = candidate;
        return true;
    }
}
