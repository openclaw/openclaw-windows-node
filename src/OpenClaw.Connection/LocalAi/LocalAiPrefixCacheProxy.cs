// Loopback HTTP proxy between the Gateway and the app-owned llama-server router. Every
// request is forwarded unchanged and streamed back as it arrives. The one exception is
// POST /v1/chat/completions carrying a leading system prefix while no other POST is in
// flight: before forwarding, the proxy makes that prefix resident in slot 0, either by
// restoring a saved file or by running the prefix alone and saving the slot (the prefill is
// work the real request would have done anyway). Any failure in that preparation is logged
// and the real request is forwarded regardless.
//
// With one slot, a restore or warm replaces whatever the slot holds, so preparation only
// runs when nothing is in flight; a busy proxy forwards without touching the slot.
//
// Usage:
//   var proxy = new LocalAiPrefixCacheProxy(options, logger);
//   Uri published = proxy.Start(); // http://127.0.0.1:<port>/v1
//   await proxy.DisposeAsync();
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClaw.Connection.LocalAi;

internal sealed record LocalAiPrefixCacheProxyOptions
{
    /// <summary>The router's base address, for example http://127.0.0.1:18803/.</summary>
    public required Uri Upstream { get; init; }
    public required string StoreDirectory { get; init; }
    /// <summary>Everything outside the prompt that makes a saved state valid: model file, context, cache types, engine.</summary>
    public required string ProfileFingerprint { get; init; }
    public int Slots { get; init; } = 1;
    /// <summary>The router's model id, which slot calls must name in their body. Needed for the startup restore.</summary>
    public string? ModelName { get; init; }
    /// <summary>
    /// Restores the newest saved prefix into every slot as soon as the proxy starts, so the first chat of
    /// a session finds it loaded. The restore loads the model on the router if it is not loaded yet.
    /// </summary>
    public bool RestoreNewestOnStart { get; init; }
    /// <summary>The app's own credential, used only for that app-initiated startup restore, never for a client's request.</summary>
    public Func<string?>? GetApiKey { get; init; }
    public int MinimumPrefixCharacters { get; init; } = 2048;
    /// <summary>Bounds warm-and-save work when the system prompt changes on every turn.</summary>
    public TimeSpan MinimumSaveInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

internal sealed class LocalAiPrefixCacheProxy : IAsyncDisposable
{
    private const string ChatPath = "/v1/chat/completions";
    private const int MaxBufferedBodyBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> s_hopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer",
        "Upgrade", "Host", "Content-Length", "Expect",
    };

    // Request fields that limit or constrain generation, which the one-token warm must not inherit.
    // tool_choice is handled separately: "none" and "auto" change how tools are rendered, so they stay.
    private static readonly string[] s_warmRemovedKeys =
    [
        "stream_options", "max_completion_tokens", "n_predict", "n", "logprobs", "top_logprobs",
        "response_format", "grammar", "json_schema",
    ];

    private readonly LocalAiPrefixCacheProxyOptions _options;
    private readonly IOpenClawLogger _logger;
    private readonly HttpClient _http;
    private readonly LlamaServerSlotClient _slots;
    private readonly LocalAiPrefixCacheStore _store;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private Task? _acceptLoop;
    private Task? _startupRestore;
    private int _inFlight;
    private string? _residentKey;
    private long? _lastSaveTimestamp;

    public LocalAiPrefixCacheProxy(LocalAiPrefixCacheProxyOptions options, IOpenClawLogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _slots = new LlamaServerSlotClient(_http, options.Upstream, _logger);
        _store = new LocalAiPrefixCacheStore(options.StoreDirectory);
    }

    /// <summary>The published address: http://127.0.0.1:&lt;port&gt;/v1, valid after <see cref="Start"/>.</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>Binds <paramref name="port"/>, or any free loopback port when it is zero.</summary>
    public Uri Start(int port = 0)
    {
        for (int attempt = 0; ; attempt++)
        {
            int bindPort = port != 0 ? port : FindFreeLoopbackPort();
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{bindPort}/");
                _listener.Start();
                Endpoint = new Uri($"http://127.0.0.1:{bindPort}/v1");
                break;
            }
            catch (HttpListenerException) when (port == 0 && attempt < 20)
            {
                // Another process took the probed port before we bound it; pick again.
            }
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
        if (_options.RestoreNewestOnStart && _options.ModelName is not null)
            _startupRestore = Task.Run(RestoreNewestAsync);
        return Endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        if (_startupRestore is not null)
            await _startupRestore.ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_handlers.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Streams still open after the grace period are dropped with the HttpClient.
        }
        _http.Dispose();
        _gate.Dispose();
        _stop.Dispose();
    }

    private static int FindFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            Task handler = HandleAsync(context);
            _handlers[handler] = 0;
            _ = handler.ContinueWith(done => _handlers.TryRemove(done, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        bool counted = false;
        try
        {
            byte[]? body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            if (body is null)
            {
                await WriteErrorAsync(context, HttpStatusCode.RequestEntityTooLarge, "request body too large").ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "POST")
            {
                // Every POST takes the gate so preparation never races a request that is
                // about to start using the slot.
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    if (_inFlight == 0)
                        await PrepareAsync(context.Request, body).ConfigureAwait(false);
                    else
                        InvalidateResidency();
                    Interlocked.Increment(ref _inFlight);
                    counted = true;
                }
                finally
                {
                    _gate.Release();
                }
            }
            await ForwardAsync(context, body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            await WriteErrorAsync(context, HttpStatusCode.ServiceUnavailable, "proxy is stopping").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Local AI proxy request failed: {ex.GetType().Name}: {ex.Message}");
            await WriteErrorAsync(context, HttpStatusCode.BadGateway, "upstream request failed").ConfigureAwait(false);
        }
        finally
        {
            if (counted)
                Interlocked.Decrement(ref _inFlight);
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The client is already gone.
            }
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return [];
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBufferedBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Runs under the same gate as request preparation, so a chat that arrives while the model is
    // loading and the file is being restored waits for it, then finds the prefix already resident.
    private async Task RestoreNewestAsync()
    {
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                string? fileName = _store.Newest();
                if (fileName is null || _inFlight != 0 || _residentKey is not null)
                    return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(_options.PrepareTimeout);
                string? apiKey = _options.GetApiKey?.Invoke();
                if (await RestoreAsync(_options.ModelName!, fileName, apiKey is null ? null : $"Bearer {apiKey}", timeout.Token)
                        .ConfigureAwait(false))
                {
                    _store.Touch(fileName);
                    _residentKey = LocalAiPrefixCacheStore.KeyFromFileName(fileName);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The proxy is stopping.
        }
        catch (Exception ex)
        {
            // A missing or unusable file only means the first chat prepares its own prefix.
            _logger.Warn($"Local AI prefix cache startup restore failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task PrepareAsync(HttpListenerRequest request, byte[] body)
    {
        // Anything but a chat with a prefix may overwrite or evict the chat slot (another
        // request, or another model under --models-max 1), so residency is no longer known.
        ParsedChat? chat = request.Url?.AbsolutePath == ChatPath ? ParseChat(body) : null;
        if (chat is null)
        {
            InvalidateResidency();
            return;
        }

        string key = LocalAiPrefixCacheKey.Compute(_options.ProfileFingerprint, chat.Model, chat.Prefix, chat.Tools);
        if (key == _residentKey)
            return;

        string fileName = LocalAiPrefixCacheStore.FileNameFor(key);
        // Only the caller's own credential is ever sent: an unauthenticated client must not be
        // able to make the proxy prefill and write a large file.
        string? authorization = request.Headers["Authorization"];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(_options.PrepareTimeout);
        try
        {
            if (_store.Exists(fileName) &&
                await RestoreAsync(chat.Model, fileName, authorization, timeout.Token).ConfigureAwait(false))
            {
                _store.Touch(fileName);
                _residentKey = key;
                return;
            }

            if (_lastSaveTimestamp is { } last && Stopwatch.GetElapsedTime(last) < _options.MinimumSaveInterval)
            {
                // A prompt that keeps changing would otherwise cost a prefill and a large write every turn.
                _residentKey = key;
                return;
            }

            bool saved = await _slots.WarmAsync(BuildWarmBody(chat), authorization, timeout.Token).ConfigureAwait(false) &&
                await _slots.SaveAsync(chat.Model, 0, fileName, authorization, timeout.Token).ConfigureAwait(false);
            if (saved)
            {
                _lastSaveTimestamp = Stopwatch.GetTimestamp();
                _store.Prune();
            }
            // A refused warm or save (a template with no user turn, a missing credential) would
            // fail the same way every turn, so treat this prefix as handled instead of retrying.
            _residentKey = key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            // Never block the real request on the cache; it just runs without it.
            _logger.Warn($"Local AI prefix cache preparation failed: {ex.GetType().Name}: {ex.Message}");
            _residentKey = null;
        }
    }

    // With one slot any other request overwrites it; with several, llama-server places a request
    // by prefix similarity, so another slot may have served it and ours still holds the prefix.
    private void InvalidateResidency()
    {
        if (_options.Slots == 1)
            _residentKey = null;
    }

    private async Task<bool> RestoreAsync(string model, string fileName, string? authorization, CancellationToken cancellationToken)
    {
        bool restored = false;
        for (int slot = 0; slot < _options.Slots; slot++)
        {
            if (!await _slots.RestoreAsync(model, slot, fileName, authorization, cancellationToken).ConfigureAwait(false))
                break;
            restored = true;
        }
        return restored;
    }

    private JsonObject BuildWarmBody(ParsedChat chat)
    {
        var warm = (JsonObject)chat.Request.DeepClone();
        foreach (string key in s_warmRemovedKeys)
            warm.Remove(key);
        if (warm["tool_choice"] is not JsonValue { } choice || !choice.TryGetValue(out string? mode) || mode is not ("none" or "auto"))
            warm.Remove("tool_choice");
        warm["messages"] = chat.Prefix.DeepClone();
        warm["stream"] = false;
        warm["max_tokens"] = 1;
        warm["id_slot"] = 0;
        warm["cache_prompt"] = true;
        return warm;
    }

    private ParsedChat? ParseChat(byte[] body)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (root?["model"] is not JsonValue modelValue || !modelValue.TryGetValue(out string? model) ||
            string.IsNullOrEmpty(model) || root["messages"] is not JsonArray messages)
        {
            return null;
        }

        var prefix = new JsonArray();
        foreach (JsonNode? message in messages)
        {
            string? role = (message as JsonObject)?["role"]?.GetValue<string>();
            if (role is not ("system" or "developer"))
                break;
            prefix.Add(message!.DeepClone());
        }
        if (prefix.Count == 0 || prefix.ToJsonString().Length < _options.MinimumPrefixCharacters)
            return null;
        return new ParsedChat(root, model, prefix, root["tools"]?.DeepClone());
    }

    private async Task ForwardAsync(HttpListenerContext context, byte[] body)
    {
        HttpListenerRequest incoming = context.Request;
        using var upstreamRequest = new HttpRequestMessage(
            new HttpMethod(incoming.HttpMethod),
            new Uri(_options.Upstream, incoming.Url!.PathAndQuery));
        if (body.Length > 0 || incoming.HttpMethod is "POST" or "PUT" or "PATCH")
            upstreamRequest.Content = new ByteArrayContent(body);
        foreach (string? name in incoming.Headers.AllKeys)
        {
            if (name is null || s_hopByHop.Contains(name))
                continue;
            string[] values = incoming.Headers.GetValues(name) ?? [];
            if (!upstreamRequest.Headers.TryAddWithoutValidation(name, values))
                upstreamRequest.Content?.Headers.TryAddWithoutValidation(name, values);
        }

        // Cancelled when the client disconnects mid-stream so the server stops generating.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        HttpResponseMessage upstream;
        try
        {
            upstream = await _http
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn($"Local AI proxy could not reach the router: {ex.Message}");
            await WriteErrorAsync(context, HttpStatusCode.BadGateway, "router unreachable").ConfigureAwait(false);
            return;
        }

        using (upstream)
        {
            HttpListenerResponse response = context.Response;
            response.StatusCode = (int)upstream.StatusCode;
            foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
            {
                if (s_hopByHop.Contains(header.Key))
                    continue;
                foreach (string value in header.Value)
                    response.AppendHeader(header.Key, value);
            }
            bool bodyless = incoming.HttpMethod == "HEAD" ||
                upstream.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified;
            if (!bodyless)
            {
                if (upstream.Content.Headers.ContentLength is { } length)
                    response.ContentLength64 = length;
                else
                    response.SendChunked = true;
            }

            try
            {
                await using Stream input = await upstream.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await input.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
                {
                    await response.OutputStream.WriteAsync(chunk.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    await response.OutputStream.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteErrorAsync(HttpListenerContext context, HttpStatusCode status, string message)
    {
        try
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString());
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // Headers were already sent or the client left; nothing more to report.
        }
    }

    private sealed record ParsedChat(JsonObject Request, string Model, JsonArray Prefix, JsonNode? Tools);
}
