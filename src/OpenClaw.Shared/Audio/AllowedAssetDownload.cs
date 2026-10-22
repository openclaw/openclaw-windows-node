using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

internal static class AllowedAssetDownload
{
    internal const int MaximumRedirects = 5;

    internal static HttpClient CreateClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout
        };

    internal static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var current = new Uri(url);
        ValidateDownloadUri(current, initialRequest: true);
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            var response = await client.GetAsync(
                    current,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect == MaximumRedirects)
                throw new InvalidOperationException("The model download exceeded the redirect limit.");

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            ValidateDownloadUri(next, initialRequest: false);
            current = next;
        }

        throw new InvalidOperationException("The model download exceeded the redirect limit.");
    }

    internal static void ValidateDownloadUri(Uri uri, bool initialRequest)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("The model download URI must be credential-free HTTPS.");
        }

        var allowed = string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            (!initialRequest &&
             (uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
            throw new InvalidOperationException("The model download redirected to an untrusted host.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
