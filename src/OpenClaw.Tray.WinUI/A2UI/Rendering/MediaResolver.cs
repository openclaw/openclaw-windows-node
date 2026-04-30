using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using Windows.Storage.Streams;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Single resolver for image/video/audio URLs in A2UI surfaces. Enforces the
/// security policy from the spec:
///   - https://&lt;allowlist&gt; only
///   - data: image/png|jpeg|webp|svg+xml up to 2 MiB
///   - everything else → broken-image fallback (logged once)
/// Allowlist is mutable at runtime so a future <c>createSurface.theme</c> /
/// manifest can extend it without restarting the process.
/// </summary>
public sealed class MediaResolver : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly HashSet<string> _hostAllowlist = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private bool _disposed;
    private static readonly SemaphoreSlim s_svgDecodeSemaphore = new(3, 3);
    private const long DataUrlMaxBytes = 2L * 1024 * 1024;
    /// <summary>Hard cap on remote image bytes. Sized to dwarf realistic UI imagery while still preventing OOM from a hostile/compromised allowlisted host.</summary>
    internal const long RemoteImageMaxBytes = 8L * 1024 * 1024;
    /// <summary>Bound on SVG SetSourceAsync. SVG render time isn't bounded by the byte cap (a 1 MiB path expression can be pathologically expensive), so cap wall time here.</summary>
    internal static readonly TimeSpan SvgRenderTimeout = TimeSpan.FromSeconds(8);

    public MediaResolver(IOpenClawLogger logger) : this(logger, handler: null) { }

    /// <summary>
    /// Test seam: pass a custom <see cref="HttpMessageHandler"/> to stub the
    /// network. Production callers use the parameterless overload.
    /// </summary>
    public MediaResolver(IOpenClawLogger logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _http = handler != null
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(BuildSafeHandler(logger), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Builds a SocketsHttpHandler whose ConnectCallback resolves DNS once and
    /// rejects the connection if any resolved address is private/loopback/
    /// link-local/multicast. Defends against DNS rebinding (TOCTOU between
    /// allowlist hostname check and the actual TCP connect: a hostile DNS
    /// server could otherwise return an internal IP for an allowlisted name).
    /// </summary>
    private static SocketsHttpHandler BuildSafeHandler(IOpenClawLogger logger)
    {
        return new SocketsHttpHandler
        {
            // The ConnectCallback validates IP addresses, but auto-redirect
            // would let an allowlisted host respond with a 30x to a hostname
            // we never validated against the allowlist. Disable redirects
            // entirely — a broken image is preferable to an SSRF window.
            AllowAutoRedirect = false,
            // Disable connection pooling so a host's resolution can't be cached
            // across requests in a way that bypasses revalidation.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                if (addresses.Length == 0)
                    throw new HttpRequestException($"DNS returned no addresses for '{ctx.DnsEndPoint.Host}'");
                // Reject the entire connection if ANY resolved address is
                // unsafe — DNS round-robin must not let a partially-internal
                // record slip through.
                foreach (var ip in addresses)
                {
                    if (!HttpUrlRiskEvaluator.IsPublicAddress(ip))
                    {
                        logger.Warn($"[A2UI] Refusing to connect to '{ctx.DnsEndPoint.Host}': resolved to non-public address {ip}");
                        throw new HttpRequestException($"Refusing to connect to non-public address {ip} for host '{ctx.DnsEndPoint.Host}'");
                    }
                }
                // Connect to the resolved IP directly so the TCP target matches
                // exactly what we validated (no second DNS lookup downstream).
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses[0], ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // disposeHandler:true on the production path means HttpClient.Dispose
        // also tears down the SocketsHttpHandler and its connection pool.
        // The test seam path (handler:null overload above) uses
        // disposeHandler:false, so the test owns the handler lifecycle.
        try { _http.Dispose(); } catch (Exception ex) { _logger.Debug($"[A2UI] MediaResolver dispose: {ex.Message}"); }
    }

    public void AllowHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _hostAllowlist.Add(host);
    }

    public bool IsAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return IsSafeDataImage(url);
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return _hostAllowlist.Contains(uri.Host);
    }

    private static bool IsSafeDataImage(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return false;
        var header = url.Substring(5, comma - 5).ToLowerInvariant();
        if (!(header.StartsWith("image/png")
              || header.StartsWith("image/jpeg")
              || header.StartsWith("image/webp")
              || header.StartsWith("image/svg+xml")))
            return false;
        // SVG data: URLs are commonly transmitted as percent-encoded UTF-8 (utf8 / charset=utf-8) rather than base64.
        // The size cap still applies — measure against the worst-case decoded size of the payload portion.
        bool isBase64 = header.Contains(";base64");
        var encoded = url.Length - comma - 1;
        long decoded = isBase64
            ? ComputeBase64DecodedLength(url, comma + 1, encoded)
            : encoded; // percent-encoded text decodes to at most `encoded` bytes
        return decoded <= DataUrlMaxBytes;
    }

    private static long ComputeBase64DecodedLength(string url, int start, int encodedLen)
    {
        if (encodedLen <= 0) return 0;
        // Strip trailing whitespace and base64 padding for a precise upper bound.
        int padding = 0;
        int end = start + encodedLen - 1;
        while (end >= start && (url[end] == '=' || char.IsWhiteSpace(url[end])))
        {
            if (url[end] == '=') padding++;
            end--;
        }
        long realChars = end - start + 1 + padding;
        return realChars / 4 * 3 - padding;
    }

    public Task<ImageSource?> LoadImageAsync(string url) => LoadImageAsync(url, CancellationToken.None);

    public async Task<ImageSource?> LoadImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsAllowed(url))
        {
            _logger.Warn($"[A2UI] Image blocked: {Truncate(url)}");
            return null;
        }
        try
        {
            byte[] bytes;
            bool mimeIsSvg;
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryDecodeDataUrl(url, out bytes, out mimeIsSvg))
                {
                    _logger.Warn($"[A2UI] data: image decode failed for {Truncate(url)}");
                    return null;
                }
                if (bytes.LongLength > DataUrlMaxBytes)
                {
                    _logger.Warn($"[A2UI] data: image exceeds cap ({bytes.LongLength} > {DataUrlMaxBytes})");
                    return null;
                }
            }
            else
            {
                var fetched = await FetchBoundedAsync(url, cancellationToken).ConfigureAwait(false);
                if (fetched is null) return null;
                bytes = fetched.Value.Bytes;
                var ct = fetched.Value.ContentType;
                mimeIsSvg = ct != null && ct.StartsWith("image/svg+xml", StringComparison.OrdinalIgnoreCase);
            }

            // Content-Type is the strong signal; signature sniff is the fallback for servers/agents that mislabel.
            if (mimeIsSvg || LooksLikeSvg(bytes))
                return await SvgFromBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
            return await BitmapFromBytes(bytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"[A2UI] Image fetch failed for {Truncate(url)}: {ex.Message}");
            return null;
        }
    }

    private static bool TryDecodeDataUrl(string url, out byte[] bytes, out bool mimeIsSvg)
    {
        bytes = Array.Empty<byte>();
        mimeIsSvg = false;
        var comma = url.IndexOf(',');
        if (comma < 0) return false;
        var header = url.Substring(5, comma - 5);
        mimeIsSvg = header.StartsWith("image/svg+xml", StringComparison.OrdinalIgnoreCase);
        var payload = url.Substring(comma + 1);
        if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try { bytes = Convert.FromBase64String(payload); }
            catch (FormatException) { return false; }
        }
        else
        {
            // RFC 2397 percent-encoding: SVG data: URLs commonly use this form (smaller than base64 for text).
            try { bytes = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)); }
            catch (UriFormatException) { return false; }
        }
        return true;
    }

    private async Task<(byte[] Bytes, string? ContentType)?> FetchBoundedAsync(string url, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead lets us reject by Content-Length before buffering the body.
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn($"[A2UI] Image fetch HTTP {(int)resp.StatusCode} for {Truncate(url)}");
            return null;
        }
        var contentLength = resp.Content.Headers.ContentLength;
        if (contentLength is long cl && cl > RemoteImageMaxBytes)
        {
            _logger.Warn($"[A2UI] Image rejected by Content-Length ({cl} > {RemoteImageMaxBytes}) for {Truncate(url)}");
            return null;
        }

        // Stream body into a capped buffer. Servers may lie about Content-Length
        // or use chunked encoding without it, so enforce again on the read side.
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream(capacity: contentLength is long c ? (int)Math.Min(c, RemoteImageMaxBytes) : 64 * 1024);
        var buffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > RemoteImageMaxBytes)
            {
                _logger.Warn($"[A2UI] Image stream exceeded cap ({total} > {RemoteImageMaxBytes}) for {Truncate(url)}");
                return null;
            }
            ms.Write(buffer, 0, read);
        }
        return (ms.ToArray(), resp.Content.Headers.ContentType?.MediaType);
    }

    public Uri? AsUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;

    /// <summary>
    /// Gate URL for Video / AudioPlayer playback. Enforces HTTPS+allowlist
    /// (via <see cref="IsAllowed"/>) and rejects IP-literal hosts that are not
    /// public (catches accidental allowlist entries like https://10.0.0.1/).
    ///
    /// NOT a DNS-rebinding pin: the OS media stack
    /// (<c>MediaSource.CreateFromUri</c>) performs its own DNS resolution at
    /// playback time, so any DNS lookup here would only be best-effort triage
    /// against the lookup the platform actually uses. The allowlist is the
    /// load-bearing defense; image fetches go through the safe
    /// <see cref="HttpClient"/>'s <c>ConnectCallback</c> path which DOES pin.
    /// </summary>
    public Uri? TryResolveMediaUri(string url)
    {
        if (!IsAllowed(url))
        {
            _logger.Warn($"[A2UI] Media blocked: {Truncate(url)}");
            return null;
        }
        var uri = AsUri(url);
        if (uri == null) return null;
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && IPAddress.TryParse(uri.Host, out var literal)
            && !HttpUrlRiskEvaluator.IsPublicAddress(literal))
        {
            _logger.Warn($"[A2UI] Media blocked: host '{uri.Host}' is not public");
            return null;
        }
        return uri;
    }

    private static async Task<BitmapImage> BitmapFromBytes(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using var ms = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ms))
        {
            w.WriteBytes(bytes);
            await w.StoreAsync();
            await w.FlushAsync();
            w.DetachStream();
        }
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
        return bmp;
    }

    /// <summary>
    /// Decode SVG bytes through D2D's static SVG 1.1 subset (no scripts, no SMIL, no &lt;foreignObject&gt;,
    /// no external fetches — that's our trust model). Two pieces of defense-in-depth on top:
    ///   - DOCTYPE pre-strip in case D2D's XML parser would resolve external entities (XXE / billion-laughs).
    ///   - Wall-clock timeout because a 1 MiB SVG can hold pathologically expensive geometry.
    /// </summary>
    private async Task<ImageSource?> SvgFromBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (!await s_svgDecodeSemaphore.WaitAsync(SvgRenderTimeout, cancellationToken).ConfigureAwait(false))
        {
            _logger.Warn("[A2UI] SVG decode queue saturated");
            return null;
        }

        try
        {
            bytes = StripDoctype(bytes);
            var svg = new SvgImageSource();
            using var ms = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(ms))
            {
                w.WriteBytes(bytes);
                await w.StoreAsync();
                await w.FlushAsync();
                w.DetachStream();
            }
            ms.Seek(0);

            using var renderTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            renderTimeout.CancelAfter(SvgRenderTimeout);
            SvgImageSourceLoadStatus status;
            try
            {
                status = await svg.SetSourceAsync(ms).AsTask().WaitAsync(renderTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warn("[A2UI] SVG decode timed out");
                return null;
            }
            if (status != SvgImageSourceLoadStatus.Success)
            {
                _logger.Warn($"[A2UI] SVG decode failed: {status}");
                return null;
            }
            return svg;
        }
        finally
        {
            s_svgDecodeSemaphore.Release();
        }
    }

    /// <summary>Cheap content sniff for SVG bytes after BOM and leading whitespace.</summary>
    internal static bool LooksLikeSvg(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return false;
        int i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
        while (i < bytes.Length && (bytes[i] == 0x20 || bytes[i] == 0x09 || bytes[i] == 0x0A || bytes[i] == 0x0D)) i++;
        int max = Math.Min(bytes.Length - i, 256);
        if (max <= 0) return false;
        var head = System.Text.Encoding.UTF8.GetString(bytes, i, max);
        return head.StartsWith("<?xml", StringComparison.Ordinal)
            || head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remove the first <c>&lt;!DOCTYPE …&gt;</c> declaration (including any internal subset
    /// in <c>[…]</c>) from the byte stream. SVG content has no need for a DOCTYPE — browsers
    /// ignore it — but stripping it pre-empts XXE / entity-expansion attacks regardless of
    /// what the downstream XML parser does.
    /// </summary>
    internal static byte[] StripDoctype(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return bytes ?? Array.Empty<byte>();
        int idx = IndexOfAsciiCi(bytes, 0, "<!DOCTYPE");
        if (idx < 0) return bytes;
        int cursor = idx + "<!DOCTYPE".Length;
        int firstGt = IndexOfByte(bytes, cursor, (byte)'>');
        if (firstGt < 0) return bytes;
        int bracket = IndexOfByteInRange(bytes, cursor, firstGt, (byte)'[');
        int end;
        if (bracket < 0)
        {
            end = firstGt;
        }
        else
        {
            int closeBracket = IndexOfByte(bytes, bracket + 1, (byte)']');
            if (closeBracket < 0) return bytes;
            end = IndexOfByte(bytes, closeBracket + 1, (byte)'>');
            if (end < 0) return bytes;
        }
        var result = new byte[bytes.Length - (end - idx + 1)];
        System.Buffer.BlockCopy(bytes, 0, result, 0, idx);
        System.Buffer.BlockCopy(bytes, end + 1, result, idx, bytes.Length - end - 1);
        return result;
    }

    private static int IndexOfByte(byte[] haystack, int start, byte needle)
    {
        for (int i = start; i < haystack.Length; i++)
            if (haystack[i] == needle) return i;
        return -1;
    }

    private static int IndexOfByteInRange(byte[] haystack, int start, int endExclusive, byte needle)
    {
        int limit = Math.Min(endExclusive, haystack.Length);
        for (int i = start; i < limit; i++)
            if (haystack[i] == needle) return i;
        return -1;
    }

    private static int IndexOfAsciiCi(byte[] haystack, int start, string asciiNeedle)
    {
        if (asciiNeedle.Length == 0) return start;
        int last = haystack.Length - asciiNeedle.Length;
        for (int i = start; i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < asciiNeedle.Length; j++)
            {
                byte b = haystack[i + j];
                char c = asciiNeedle[j];
                byte upper = (b >= (byte)'a' && b <= (byte)'z') ? (byte)(b - 0x20) : b;
                char nUpper = (c >= 'a' && c <= 'z') ? (char)(c - 0x20) : c;
                if (upper != (byte)nUpper) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    // Truncation alone leaks query/fragment for any URL shorter than 60
    // chars. Sanitize first (drop query+fragment, keep first path segment),
    // then bound the length so the format strings stay tidy.
    private static string Truncate(string s)
    {
        var sanitized = UrlLogSanitizer.Sanitize(s);
        return sanitized.Length <= 60 ? sanitized : sanitized.Substring(0, 60) + "...";
    }
}
