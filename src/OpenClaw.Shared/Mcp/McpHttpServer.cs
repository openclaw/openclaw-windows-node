using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
/// No bearer-token auth yet — local user-context processes are intentionally
/// in-scope of trust (they can already call Win32 directly). Browser pages and
/// other-machine attackers are not.
///
/// Stability defenses (CR-003/CR-005):
///   - Per-request hard deadline (RequestTimeoutMs) bounds body-read and
///     bridge dispatch so a slow or hung client cannot pin a handler slot
///     forever.
///   - Active handler tasks are tracked so Stop/Dispose can drain in-flight
///     work before tearing down the semaphore and capability services.
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private const long MaxRequestBodyBytes = 4L * 1024 * 1024; // 4 MiB
    private const int MaxConcurrentHandlers = 8;
    // Generous enough to cover 60s camera.clip plus encoding/serialization
    // overhead; tight enough to free handler slots if a tool wedges.
    private const int RequestTimeoutMs = 90_000;
    // How long Dispose waits for in-flight handlers to drain before forcing
    // tear-down. Past this point handlers may observe disposed services.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly McpToolBridge _bridge;
    private readonly int _port;
    private readonly IOpenClawLogger _logger;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _handlerLimiter = new(MaxConcurrentHandlers, MaxConcurrentHandlers);
    private readonly object _activeLock = new();
    private readonly HashSet<Task> _activeHandlers = new();
    private Task? _acceptLoop;
    private int _disposed;

    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}/";

    public McpHttpServer(McpToolBridge bridge, int port, IOpenClawLogger logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _port = port;
        _listener = new HttpListener();
        // Loopback binding — not reachable from other machines.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        if (_listener.IsListening) return;
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.Info($"[MCP] HTTP server listening on {Endpoint}");
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
            if (!await _handlerLimiter.WaitAsync(0, ct).ConfigureAwait(false))
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
            catch (ObjectDisposedException) { /* server torn down */ }
            catch (SemaphoreFullException) { /* defensive */ }
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

            // Host header must match our loopback bind. Defends against DNS
            // rebinding pivots that route a public hostname to 127.0.0.1.
            if (!IsHostAllowed(ctx.Request.Headers["Host"]))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "host not allowed");
                return;
            }

            if (ctx.Request.HttpMethod == "GET")
            {
                // Friendly probe response — useful for confirming the server is up
                // from a curl/browser without hitting the JSON-RPC endpoint.
                WriteText(ctx.Response, HttpStatusCode.OK,
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

            string? responseBody;
            try
            {
                responseBody = await _bridge.HandleRequestAsync(body, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Reject(ctx, HttpStatusCode.RequestTimeout, "request timed out");
                return;
            }

            if (responseBody == null)
            {
                // Notification — JSON-RPC says no body. 204 is the most honest signal.
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                ctx.Response.Close();
                return;
            }

            WriteText(ctx.Response, HttpStatusCode.OK, responseBody, "application/json");
        }
        catch (Exception ex)
        {
            _logger.Error("[MCP] Request failed", ex);
            // Response may already be partially written or closed; swallow.
            try { Reject(ctx, HttpStatusCode.InternalServerError, "internal error"); }
            catch { /* response already disposed */ }
        }
    }

    private static bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        // Strip port if present.
        var colon = host.LastIndexOf(':');
        var hostname = (colon > 0 && host.IndexOf(']') < colon ? host.Substring(0, colon) : host).Trim();
        return string.Equals(hostname, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, long maxBytes, CancellationToken ct)
    {
        // Bounded read — never trust ContentLength as a sole limit; the client
        // can send chunked encoding or just lie. Read up to maxBytes+1 and
        // throw if we crossed the cap. The cancellation token enforces the
        // per-request deadline so a slow-body client can't hold a handler slot.
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        var buffer = new byte[8192];
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

    private static void Reject(HttpListenerContext ctx, HttpStatusCode status, string reason)
    {
        try { WriteText(ctx.Response, status, reason, "text/plain"); }
        catch { /* response already disposed */ }
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
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try { _cts.Cancel(); } catch { /* already cancelled or disposed */ }
        try { if (_listener.IsListening) _listener.Stop(); } catch { /* already stopped */ }

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
            catch { /* loop may have errored */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Drain in-flight handlers first so we don't pull the limiter out from
        // under them. Block here — Dispose is the sync seam.
        try { StopAsync(DrainTimeout).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.Warn($"[MCP] Drain error: {ex.Message}"); }
        try { _listener.Close(); } catch { /* already closed */ }
        _cts.Dispose();
        _handlerLimiter.Dispose();
    }
}
