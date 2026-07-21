using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Mcp;

/// <summary>
/// Localhost-only HTTP transport for the MCP server.
///
/// Security model — three layers:
///   1. Loopback bind (127.0.0.1). Unreachable from another machine, regardless
///      of firewall configuration.
///   2. Defensive IsLoopback check on every request.
///   3. Browser/CSRF gate: a browser tab fetching http://127.0.0.1:8765/ is
///      *also* on the loopback interface, so loopback alone does not protect
///      against a malicious page. We reject any request that:
///        - presents an Origin header (real MCP clients do not send Origin),
///        - has a Host header that is not 127.0.0.1/localhost,
///        - is a POST with Content-Type other than application/json.
///      Together these force a CORS preflight from a browser, which we never
///      satisfy (no Access-Control-Allow-Origin), so the cross-origin call
///      fails before reaching capability code.
///
/// Bearer-token auth in front of request dispatch. Required on every request
/// when constructed with a non-null token (the tray always passes one — see
/// <c>NodeService.McpTokenPath</c> / <c>McpAuthToken.LoadOrCreate</c>; legacy
/// callers that pass null disable the check, kept for in-process tests). The
/// token defends against untrusted local processes that could otherwise reach
/// the predictable 127.0.0.1:port endpoint — a process running as the same
/// user on the same box can read the token file and would defeat this layer,
/// but anything sandboxed away from <c>%APPDATA%\OpenClawTray\</c> cannot.
///
/// Stability defenses (CR-003/CR-005):
///   - Per-request hard deadline (RequestTimeoutMs) bounds body-read and
///     bridge dispatch so a slow or hung client cannot pin a handler slot
///     forever.
///   - Active handler tasks are tracked so Stop/Dispose can drain in-flight
///     work before tearing down the semaphore and capability services.
/// </summary>
public sealed class McpHttpServer : IDisposable, IAsyncDisposable
{
    private const long MaxRequestBodyBytes = 4L * 1024 * 1024; // 4 MiB
    // 16 leaves headroom for parallel tool callers (e.g. an editor + Claude
    // Desktop + a CLI script) without making each connection cheap enough to
    // become a DoS lever — request size cap + per-handler timeout still bound
    // memory. Bumped from 8 after queue-stall reports under multi-IDE use.
    private const int MaxConcurrentHandlers = 16;
    // Sized to cover the longest legitimate capability: screen.record up to
    // 300s plus encoding + serialization. Earlier 90s deadline silently abort
    // ed valid recording requests while the OS capture pipeline kept running
    // unobserved (unified review H10). Cancellation now flows through the
    // capability via INodeCapability.ExecuteAsync(NodeInvokeRequest, CT) so
    // tools that opt in actually stop the underlying work.
    private const int RequestTimeoutMs = 360_000;
    // How long Dispose waits for in-flight handlers to drain before forcing
    // tear-down. Past this point handlers may observe disposed services.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly McpToolBridge _bridge;
    private readonly int _port;
    private readonly IOpenClawLogger _logger;
    private readonly HttpListener _listener;
    private readonly Action<HttpListenerResponse, HttpStatusCode, string, string> _writeText;
    /// <summary>
    /// Required bearer token for HTTP requests. Empty/null disables auth (the
    /// pre-auth contract — kept so existing dev configs keep working). When set,
    /// every request must carry <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    private string? _authToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _handlerLimiter = new(MaxConcurrentHandlers, MaxConcurrentHandlers);
    private readonly object _activeLock = new();
    private readonly HashSet<Task> _activeHandlers = new();
    private readonly object _shutdownLock = new();
    private Task? _acceptLoop;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _disposed;
    private bool _resourcesDisposed;

    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}/";

    public McpHttpServer(McpToolBridge bridge, int port, IOpenClawLogger logger, string? authToken = null)
        : this(bridge, port, logger, authToken, WriteText)
    {
    }

    internal McpHttpServer(
        McpToolBridge bridge,
        int port,
        IOpenClawLogger logger,
        string? authToken,
        Action<HttpListenerResponse, HttpStatusCode, string, string> writeText)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writeText = writeText ?? throw new ArgumentNullException(nameof(writeText));
        _port = port;
        _authToken = string.IsNullOrEmpty(authToken) ? null : authToken;
        _listener = new HttpListener();
        // Loopback binding — not reachable from other machines. Use only the
        // numeric host on Windows so non-elevated startup does not require a
        // separate netsh http urlacl reservation for http://localhost:port/.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        if (_listener.IsListening) return;
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.Info($"[MCP] HTTP server listening on {Endpoint}");
    }

    public void UpdateAuthToken(string? authToken)
    {
        Volatile.Write(ref _authToken, string.IsNullOrEmpty(authToken) ? null : authToken);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                _logger.Error("[MCP] Accept failed", ex);
                continue;
            }

            // Defensive: even though the prefix is loopback-only, double-check.
            if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "loopback only");
                continue;
            }

            // Cap concurrent handlers — a misbehaving local client can otherwise
            // pin every threadpool thread on long-running screen/camera calls.
            // Wait briefly: a slot freed during typical request handoff is well
            // under 50ms, so a small queue here turns transient spikes into
            // success rather than 503s without inviting unbounded queueing.
            if (!await _handlerLimiter.WaitAsync(50, ct).ConfigureAwait(false))
            {
                Reject(ctx, (HttpStatusCode)503, "server busy");
                continue;
            }

            // NOTE: do not pass `ct` to Task.Run. If the token is cancelled
            // between WaitAsync returning and the delegate starting, Task.Run
            // skips the delegate and the finally never runs — leaking a
            // semaphore slot. Let the delegate observe cancellation itself.
            var handlerTask = Task.Run(() => RunHandlerAsync(ctx));
            TrackHandler(handlerTask);
        }
    }

    private async Task RunHandlerAsync(HttpListenerContext ctx)
    {
        // Per-request linked CTS: server shutdown OR per-request deadline.
        // The bridge call observes this so a wedged tool cannot pin the slot.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        requestCts.CancelAfter(RequestTimeoutMs);
        try
        {
            await HandleAsync(ctx, requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            // Defensive: if Dispose has already disposed the limiter, swallow.
            // Without this guard, a handler racing with shutdown can throw
            // ObjectDisposedException into an unobserved task, which surfaces
            // through global unhandled-exception handlers.
            try { _handlerLimiter.Release(); }
            catch (ObjectDisposedException) { /* Server torn down during request; expected. */ }
            catch (SemaphoreFullException ex)
            {
                // Release-without-Acquire indicates a real bug (counting imbalance);
                // promote to Warn so it surfaces in production diagnostics. Include
                // ex.ToString() to capture the stack since Warn has no ex overload.
                _logger.Warn($"[MCP] Handler limiter release was already at max — possible release/acquire imbalance: {ex}");
            }
        }
    }

    private void TrackHandler(Task task)
    {
        lock (_activeLock) { _activeHandlers.Add(task); }
        _ = task.ContinueWith(t =>
        {
            lock (_activeLock) { _activeHandlers.Remove(t); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // Snapshot the auth token once. UpdateAuthToken can rotate _authToken
        // on another thread, and reading the field separately for the null-test
        // and the comparison would let a single request observe two different
        // values (e.g. enter the auth branch with the old token, then compare
        // against the new one — or vice versa).
        var authToken = Volatile.Read(ref _authToken);
        McpToolBridge.McpTransportResponse? transportResponse = null;
        try
        {
            // CSRF/browser gate — reject anything carrying a browser Origin.
            // Real MCP HTTP clients (Claude Desktop, Cursor, Claude Code, curl)
            // do not set Origin. A browser fetch always does.
            var origin = ctx.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "origin not allowed");
                return;
            }
            // Belt-and-suspenders: a browser may strip Origin (e.g. via a
            // privacy extension) but still send Sec-Fetch-Site / Sec-Fetch-Mode
            // / Referer. Treat any of those as evidence of a browser context.
            // Native MCP clients don't emit these headers.
            if (!string.IsNullOrEmpty(ctx.Request.Headers["Sec-Fetch-Site"]) ||
                !string.IsNullOrEmpty(ctx.Request.Headers["Sec-Fetch-Mode"]) ||
                !string.IsNullOrEmpty(ctx.Request.Headers["Referer"]))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "browser context not allowed");
                return;
            }

            // Host header must match our loopback bind. Defends against DNS
            // rebinding pivots that route a public hostname to 127.0.0.1.
            if (!IsHostAllowed(ctx.Request.Headers["Host"]))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "host not allowed");
                return;
            }

            // Bearer-token check. Defends against untrusted local processes
            // (browser helpers, editor extensions) that share the loopback
            // surface with the legitimate MCP client. Token lives in a
            // user-only-readable file under %LOCALAPPDATA%; CLI/agent
            // registration reads from there. Keep this before method dispatch
            // so alternate verbs cannot bypass the configured token gate.
            if (authToken != null && !IsAuthorized(authToken, ctx.Request.Headers["Authorization"]))
            {
                Reject(ctx, HttpStatusCode.Unauthorized, "missing or invalid bearer token");
                return;
            }

            if (ctx.Request.HttpMethod == "GET")
            {
                // Friendly probe response — useful for confirming the server is up
                // from a curl/browser without hitting the JSON-RPC endpoint.
                _writeText(ctx.Response, HttpStatusCode.OK,
                    $"OpenClaw MCP server. POST JSON-RPC to {Endpoint}", "text/plain");
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                Reject(ctx, HttpStatusCode.MethodNotAllowed, "POST only");
                return;
            }

            // Force application/json on POST. Combined with the Origin check,
            // this means a browser cross-origin fetch must use a non-simple
            // Content-Type and trigger a CORS preflight, which we don't honor.
            var contentType = ctx.Request.ContentType ?? "";
            var semi = contentType.IndexOf(';');
            var contentTypeBase = (semi >= 0 ? contentType.Substring(0, semi) : contentType).Trim();
            if (!string.Equals(contentTypeBase, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                Reject(ctx, HttpStatusCode.UnsupportedMediaType, "application/json required");
                return;
            }

            // Reject bodies that exceed our cap *before* reading them — a
            // multi-GB POST would otherwise OOM the tray.
            if (ctx.Request.ContentLength64 > MaxRequestBodyBytes)
            {
                Reject(ctx, HttpStatusCode.RequestEntityTooLarge, "request body too large");
                return;
            }

            string body;
            try
            {
                body = await ReadBodyAsync(ctx.Request, MaxRequestBodyBytes, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                Reject(ctx, HttpStatusCode.RequestEntityTooLarge, "request body too large");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Slow-body or stuck client — free the slot rather than blocking forever.
                Reject(ctx, HttpStatusCode.RequestTimeout, "request timed out");
                return;
            }

            try
            {
                transportResponse = await _bridge
                    .HandleTransportRequestAsync(body, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Reject(ctx, HttpStatusCode.RequestTimeout, "request timed out");
                return;
            }

            if (transportResponse.Body == null)
            {
                // Notification — JSON-RPC says no body. 204 is the most honest signal.
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                ctx.Response.Close();
                transportResponse.CompleteDelivery();
                return;
            }

            _writeText(ctx.Response, HttpStatusCode.OK, transportResponse.Body, "application/json");
            transportResponse.CompleteDelivery();
        }
        catch (Exception ex)
        {
            transportResponse?.CompleteDelivery(ex.GetType());
            _logger.Error("[MCP] Request failed", ex);
            // Response may already be partially written or closed; swallow.
            try { Reject(ctx, HttpStatusCode.InternalServerError, "internal error"); }
            catch (Exception rejEx) { _logger.Debug($"[MCP] Reject after handler error failed (response already disposed?): {rejEx.Message}"); }
        }
    }

    private static bool IsAuthorized(string authToken, string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader)) return false;
        // Accept "Bearer <token>" (RFC 6750) — case-insensitive scheme, exact token.
        const string scheme = "Bearer ";
        if (!authHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = authHeader.Substring(scheme.Length).Trim();
        if (presented.Length != authToken.Length) return false;
        // Constant-time compare; both strings already known length.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(authToken));
    }

    private static bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var trimmed = host.Trim();
        // IPv6 form: [::1]:port — strip the bracketed address.
        if (trimmed.StartsWith('['))
        {
            var closeBracket = trimmed.IndexOf(']');
            if (closeBracket < 0) return false;
            var v6 = trimmed.Substring(1, closeBracket - 1);
            return string.Equals(v6, "::1", StringComparison.Ordinal);
        }
        // IPv4 / hostname: strip trailing :port if present.
        var colon = trimmed.LastIndexOf(':');
        var hostname = (colon > 0 ? trimmed.Substring(0, colon) : trimmed).Trim();
        return string.Equals(hostname, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(hostname, "::1", StringComparison.Ordinal)
            || string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, long maxBytes, CancellationToken ct)
    {
        // Bounded read — never trust ContentLength as a sole limit; the client
        // can send chunked encoding or just lie. Read up to maxBytes+1 and
        // throw if we crossed the cap. The cancellation token enforces the
        // per-request deadline so a slow-body client can't hold a handler slot.
        // Pool the read buffer so we don't allocate 8 KiB per request — under
        // load these are a noticeable LOH-adjacent allocation.
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var ms = new MemoryStream();
            long total = 0;
            while (true)
            {
                var n = await request.InputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
                if (total > maxBytes) throw new InvalidDataException("request body exceeds cap");
                ms.Write(buffer, 0, n);
            }
            return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Reject(HttpListenerContext ctx, HttpStatusCode status, string reason)
    {
        try { _writeText(ctx.Response, status, reason, "text/plain"); }
        catch (Exception ex)
        {
            // Response may already be disposed; a failed write means the client
            // already disconnected. Most Reject call sites are validation paths
            // outside a catch block, so emit a Trace breadcrumb here rather than
            // relying on a (non-existent) outer log.
            System.Diagnostics.Trace.WriteLine($"McpHttpServer.Reject: failed to write {(int)status} '{reason}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void WriteText(HttpListenerResponse response, HttpStatusCode status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        using var output = response.OutputStream;
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Stop accepting new requests, cancel in-flight ones, and wait for
    /// active handlers to drain (or the timeout to elapse) before returning.
    /// Idempotent. Returns when it is safe to dispose downstream services
    /// (capabilities, capture services) without racing live handlers.
    /// </summary>
    public Task StopAsync(TimeSpan drainTimeout)
    {
        lock (_shutdownLock)
        {
            // Return the in-flight stop so later callers still wait for the
            // original drain instead of observing a false "already stopped".
            return _stopTask ??= StopCoreAsync(drainTimeout);
        }
    }

    private async Task StopCoreAsync(TimeSpan drainTimeout)
    {
        try { _cts.Cancel(); }
        catch (Exception ex) { _logger.Debug($"[MCP] StopCore cts.Cancel threw: {ex.Message}"); }
        try { if (_listener.IsListening) _listener.Stop(); }
        catch (Exception ex) { _logger.Debug($"[MCP] StopCore listener.Stop threw: {ex.Message}"); }

        // Snapshot before awaiting — handlers remove themselves on completion,
        // and we don't want enumeration to race the continuation.
        Task[] toAwait;
        lock (_activeLock) { toAwait = new Task[_activeHandlers.Count]; _activeHandlers.CopyTo(toAwait); }

        var allHandlers = Task.WhenAll(toAwait);
        var deadline = Task.Delay(drainTimeout);
        var winner = await Task.WhenAny(allHandlers, deadline).ConfigureAwait(false);
        if (winner == deadline && toAwait.Length > 0)
        {
            int still;
            lock (_activeLock) { still = _activeHandlers.Count; }
            _logger.Warn($"[MCP] Drain timeout ({drainTimeout.TotalSeconds:F1}s); {still} handler(s) still running");
        }

        if (_acceptLoop != null)
        {
            try { await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug($"[MCP] Accept loop final await threw (loop may have errored): {ex.Message}"); }
        }
    }

    public ValueTask DisposeAsync()
    {
        var task = EnsureDisposeTask();
        return new ValueTask(task);
    }

    public void Dispose()
    {
        ObserveBackgroundFault(EnsureDisposeTask(), "[MCP] Dispose error");
    }

    private Task EnsureDisposeTask()
    {
        lock (_shutdownLock)
        {
            if (_disposeTask != null)
            {
                return _disposeTask;
            }

            Interlocked.Exchange(ref _disposed, 1);
            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[MCP] Drain error: {ex.Message}");
        }
        finally
        {
            DisposeResources();
            GC.SuppressFinalize(this);
        }
    }

    private void DisposeResources()
    {
        lock (_shutdownLock)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
        }

        try { _listener.Close(); }
        catch (Exception ex) { _logger.Debug($"[MCP] listener.Close during dispose threw: {ex.Message}"); }
        _cts.Dispose();
        _handlerLimiter.Dispose();
    }

    private void ObserveBackgroundFault(Task task, string message)
    {
        if (task.IsFaulted)
        {
            _logger.Warn($"{message}: {task.Exception.GetBaseException().Message}");
            return;
        }

        if (task.IsCanceled)
        {
            _logger.Warn($"{message}: canceled");
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => _logger.Warn($"{message}: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
