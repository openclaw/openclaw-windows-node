using System.Net;
using System.Text;

namespace OpenClaw.Shared.Dashboards;

public enum BoardWidgetDocumentFailure
{
    /// <summary>The view ticket was rejected (HTTP 401): refetch the board for a new ticket.</summary>
    Unauthorized,
    /// <summary>Timeout, 408, or 5xx: retry later.</summary>
    Unavailable,
    /// <summary>Any other rejection or an oversized document.</summary>
    Rejected,
}

public sealed class BoardWidgetDocumentException(BoardWidgetDocumentFailure failure, string message)
    : Exception(message)
{
    public BoardWidgetDocumentFailure Failure { get; } = failure;
}

/// <summary>
/// Fetches ticketed widget HTML from the active Gateway, like upstream
/// <c>BoardWidgetSandboxHost.fetchDocument</c>. The URL must already have
/// passed <see cref="BoardWidgetUrlPolicy.TryResolveFrameUrl"/>. Redirects are
/// not followed so a document can never come from another origin.
/// </summary>
public sealed class BoardWidgetDocumentLoader : IDisposable
{
    /// <summary>Upstream <c>WIDGET_HTML_MAX_UTF8_BYTES</c>.</summary>
    public const int MaxDocumentBytes = 10 * 1024 * 1024;

    /// <summary>Default terminal deadline, matching ordinary Gateway reads.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_BOARD_WIDGET_LOAD_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? seconds
            : 30);

    private readonly HttpClient _http;

    public BoardWidgetDocumentLoader(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<string> LoadAsync(Uri documentUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, documentUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true, NoCache = true };
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BoardWidgetDocumentException(BoardWidgetDocumentFailure.Unavailable, "widget content request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new BoardWidgetDocumentException(BoardWidgetDocumentFailure.Unavailable, $"widget content request failed: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new BoardWidgetDocumentException(
                    response.StatusCode == HttpStatusCode.Unauthorized ? BoardWidgetDocumentFailure.Unauthorized
                        : status == 408 || status >= 500 ? BoardWidgetDocumentFailure.Unavailable
                        : BoardWidgetDocumentFailure.Rejected,
                    $"widget content request failed ({status})");
            }
            if (response.Content.Headers.ContentLength is > MaxDocumentBytes)
                throw TooLarge();

            await using var stream = await ReadBodyAsync(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (await ReadBodyAsync(() => stream.ReadAsync(chunk, timeout.Token).AsTask(), cancellationToken).ConfigureAwait(false) is var read and > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes)
                    throw TooLarge();
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>Maps a dropped connection or deadline during the body read to a retryable failure.</summary>
    private static async Task<T> ReadBodyAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BoardWidgetDocumentException(BoardWidgetDocumentFailure.Unavailable, "widget content request timed out");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new BoardWidgetDocumentException(BoardWidgetDocumentFailure.Unavailable, $"widget content request failed: {ex.Message}");
        }
    }

    private static BoardWidgetDocumentException TooLarge() =>
        new(BoardWidgetDocumentFailure.Rejected, "widget HTML exceeds 10 MiB; regenerate a smaller widget");

    public void Dispose() => _http.Dispose();
}
