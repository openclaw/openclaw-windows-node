using System.Net;
using System.Net.Http.Headers;

namespace OpenClaw.SetupEngine;

/// <summary>
/// The Hugging Face HTTPS policy shared by model downloads and Hub metadata reads: the
/// first request must target huggingface.co, redirects may only reach huggingface.co or
/// hf.co subdomains, and transient statuses surface as retryable failures.
/// </summary>
internal static class HuggingFaceHttp
{
    private const int MaximumRedirects = 5;

    public static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        long resumeOffset,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUri(initialUri, initialRequest: true);
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (resumeOffset > 0)
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            Uri observed = response.RequestMessage?.RequestUri ?? current;
            ValidateDownloadUri(observed, initialRequest: false);
            if (!IsRedirect(response.StatusCode))
            {
                if (IsTransientStatus(response.StatusCode))
                {
                    int statusCode = (int)response.StatusCode;
                    string reason = response.StatusCode.ToString();
                    response.Dispose();
                    throw new TransientHuggingFaceModelInstallException(
                        $"The Hugging Face download returned transient HTTP status {statusCode} ({reason}).");
                }

                return response;
            }

            if (redirect == MaximumRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(observed, response.Headers.Location);
            response.Dispose();
            ValidateDownloadUri(next, initialRequest: false);
            current = next;
        }

        throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
    }

    public static void ValidateDownloadUri(Uri uri, bool initialRequest)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new HuggingFaceModelInstallException("The model download URI must be credential-free HTTPS.");
        }

        bool allowed = string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            (!initialRequest &&
             (uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
            throw new HuggingFaceModelInstallException("The model download redirected to an untrusted host.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is >= 500 and <= 599;
}
