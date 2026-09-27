using System.Net;

namespace OpenClaw.SetupEngine;

internal enum ProviderArtworkStatus { Loaded, Blocked, Network, Http, TooLarge, Unsupported, InvalidImage, TimedOut, Busy }
internal enum ProviderArtworkFormat { Png, Jpeg, Svg }
internal sealed record ProviderArtworkData(byte[] Bytes, ProviderArtworkFormat Format);
internal sealed record ProviderArtworkResult(ProviderArtworkStatus Status, ProviderArtworkData? Data = null);

/// <summary>
/// Setup-scoped, credential-free acquisition. Only validated encoded bytes are cached;
/// native decoded images are owned by their individual controls and released on unload.
/// </summary>
internal sealed class ProviderArtworkLoader : IDisposable
{
    internal const int MaxBytes = 256 * 1024;
    internal const int MaxCacheBytes = 2 * 1024 * 1024;
    internal const int MaxCacheEntries = 16;
    internal const int MaxConcurrentRequests = 4;
    internal const int MaxPendingRequests = 16;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(MaxConcurrentRequests);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private int _pending;
    private int _cacheBytes;
    private bool _disposed;
    private sealed record CacheEntry(ProviderArtworkData Data, DateTimeOffset Expires);

    internal ProviderArtworkLoader(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler ?? ProviderArtworkNetworkPolicy.CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        _timeout = timeout ?? RequestTimeout;
    }

    internal async Task<ProviderArtworkResult> LoadAsync(Uri uri, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!ProviderArtworkNetworkPolicy.TryGetUri(uri.OriginalString, out var validated))
            return new(ProviderArtworkStatus.Blocked);
        var key = validated.AbsoluteUri;
        if (TryGetCached(key) is { } cached)
            return new(ProviderArtworkStatus.Loaded, cached);
        if (Interlocked.Increment(ref _pending) > MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pending);
            return new(ProviderArtworkStatus.Busy);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(_timeout);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            if (TryGetCached(key) is { } afterWait)
                return new(ProviderArtworkStatus.Loaded, afterWait);
            var result = await FetchAsync(validated, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (result.Data is { } data)
                Cache(key, data);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            return new(ProviderArtworkStatus.TimedOut);
        }
        catch (HttpRequestException ex)
        {
            return new(ex is ProviderArtworkAddressException || ex.InnerException is ProviderArtworkAddressException
                ? ProviderArtworkStatus.Blocked : ProviderArtworkStatus.Network);
        }
        catch (IOException) { return new(ProviderArtworkStatus.Network); }
        finally
        {
            if (acquired)
                _requests.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task<ProviderArtworkResult> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("image/png, image/jpeg, image/svg+xml");
        // No gateway identity, referer, cookies, default credentials or auth retries.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            return new(ProviderArtworkStatus.Http);
        if (response.Content.Headers.ContentEncoding.Count > 0 ||
            response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() is not
                ("image/png" or "image/jpeg" or "image/svg+xml"))
            return new(ProviderArtworkStatus.Unsupported);
        if (response.Content.Headers.ContentLength > MaxBytes)
            return new(ProviderArtworkStatus.TooLarge);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (bytes.Length + read > MaxBytes)
                return new(ProviderArtworkStatus.TooLarge);
            bytes.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        return ProviderArtworkContent.Validate(bytes.ToArray(), response.Content.Headers.ContentType!.MediaType!);
    }

    private ProviderArtworkData? TryGetCached(string key)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var entry))
                return null;
            if (entry.Expires > DateTimeOffset.UtcNow)
                return entry.Data;
            _cache.Remove(key);
            _cacheBytes -= entry.Data.Bytes.Length;
            return null;
        }
    }

    private void Cache(string key, ProviderArtworkData data)
    {
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(key))
                return;
            while (_cache.Count >= MaxCacheEntries || _cacheBytes + data.Bytes.Length > MaxCacheBytes)
            {
                var oldest = _cache.MinBy(pair => pair.Value.Expires);
                _cache.Remove(oldest.Key);
                _cacheBytes -= oldest.Value.Data.Bytes.Length;
            }
            _cache.Add(key, new(data, DateTimeOffset.UtcNow.AddMinutes(5)));
            _cacheBytes += data.Bytes.Length;
        }
    }

    internal (int Entries, int Bytes) CacheSize { get { lock (_cacheLock) return (_cache.Count, _cacheBytes); } }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cache.Clear();
            _cacheBytes = 0;
        }
        _lifetime.Cancel();
        _http.Dispose();
        // In-flight work still owns linked registrations and releases the semaphore.
        // These managed gates are deliberately not disposed underneath it.
    }
}
