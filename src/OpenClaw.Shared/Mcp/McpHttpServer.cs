using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Telemetry;

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
///   - Per-request hard deadline bounds body-read and
///     bridge dispatch so a slow or hung client cannot pin a handler slot
///     forever.
///   - Active handler tasks are tracked so Stop/Dispose can drain in-flight
///     work before tearing down the semaphore and capability services.
/// </summary>
public sealed class McpHttpServer : IDisposable, IAsyncDisposable
{
    private const long MaxRequestBodyBytes = 4L * 1024 * 1024; // 4 MiB
    private readonly McpToolBridge _bridge;
    private readonly int _port;
    private readonly IOpenClawLogger _logger;
    private readonly IMcpHttpListener _listener;
    private readonly McpHttpServerOptions _options;
    private readonly Action<HttpListenerResponse, HttpStatusCode, string, string> _writeText;
    /// <summary>
    /// Required bearer token for HTTP requests. Empty/null disables auth (the
    /// pre-auth contract — kept so existing dev configs keep working). When set,
    /// every request must carry <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    private string? _authToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _handlerLimiter;
    private readonly object _activeLock = new();
    private readonly HashSet<Task> _activeHandlers = new();
    private readonly object _startLock = new();
    private readonly object _shutdownLock = new();
    private Task? _acceptLoop;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _started;
    private int _disposed;
    private bool _resourcesDisposed;

    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}/";

    public McpHttpServer(McpToolBridge bridge, int port, IOpenClawLogger logger, string? authToken = null)
        : this(
            bridge,
            port,
            logger,
            authToken,
            WriteText,
            McpHttpServerOptions.Default,
            new SystemMcpHttpListener(port))
    {
    }

    internal McpHttpServer(
        McpToolBridge bridge,
        int port,
        IOpenClawLogger logger,
        string? authToken,
        Action<HttpListenerResponse, HttpStatusCode, string, string> writeText)
        : this(
            bridge,
            port,
            logger,
            authToken,
            writeText,
            McpHttpServerOptions.Default,
            new SystemMcpHttpListener(port))
    {
    }

    internal McpHttpServer(
        McpToolBridge bridge,
        int port,
        IOpenClawLogger logger,
        string? authToken,
        McpHttpServerOptions options)
        : this(
            bridge,
            port,
            logger,
            authToken,
            WriteText,
            options,
            new SystemMcpHttpListener(port))
    {
    }

    internal McpHttpServer(
        McpToolBridge bridge,
        int port,
        IOpenClawLogger logger,
        string? authToken,
        McpHttpServerOptions options,
        IMcpHttpListener listener)
        : this(
            bridge,
            port,
            logger,
            authToken,
            WriteText,
            options,
            listener)
    {
    }

    internal McpHttpServer(
        McpToolBridge bridge,
        int port,
        IOpenClawLogger logger,
        string? authToken,
        Action<HttpListenerResponse, HttpStatusCode, string, string> writeText,
        McpHttpServerOptions options,
        IMcpHttpListener? listener = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writeText = writeText ?? throw new ArgumentNullException(nameof(writeText));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _port = port;
        _authToken = string.IsNullOrEmpty(authToken) ? null : authToken;
        _listener = listener ?? new SystemMcpHttpListener(port);
        _handlerLimiter = new SemaphoreSlim(
            _options.MaxConcurrentHandlers,
            _options.MaxConcurrentHandlers);
        // Loopback binding — not reachable from other machines. Use only the
        // numeric host on Windows so non-elevated startup does not require a
        // separate netsh http urlacl reservation for http://localhost:port/.
    }

    public void Start()
    {
        lock (_startLock)
        {
            if (_listener.IsListening)
                return;

            using var telemetry = McpServerTelemetry.StartLifecycle(McpServerOperation.Start);
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerStart);
                telemetry.Complete(
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.ListenerStart,
                    ex.GetType());
                throw;
            }

            try
            {
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _logger.Info($"[MCP] HTTP server listening on {Endpoint}");
                Volatile.Write(ref _started, 1);
                telemetry.Complete(McpServerOutcome.Success);
            }
            catch (Exception ex)
            {
                telemetry.Complete(
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InternalFailure,
                    ex.GetType());
                try { _listener.Stop(); }
                catch (Exception stopEx)
                {
                    McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerStop);
                    _logger.Debug($"[MCP] Listener cleanup after start failure threw: {stopEx.Message}");
                }
                throw;
            }
        }
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
                McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerAccept);
                _logger.Error("[MCP] Accept failed", ex);
                continue;
            }

            McpServerRequestOperation? requestTelemetry = McpServerTelemetry.StartRequest(
                GetRequestKind(ctx.Request.HttpMethod));
            var handlerSlotAcquired = false;
            try
            {
                // Defensive: even though the prefix is loopback-only, double-check.
                if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
                {
                    CompleteRejection(
                        requestTelemetry,
                        ctx,
                        HttpStatusCode.Forbidden,
                        "loopback only",
                        McpServerOutcome.Failure,
                        McpServerErrorCategory.InvalidRequest);
                    continue;
                }

                // Cap concurrent handlers — a misbehaving local client can otherwise
                // pin every threadpool thread on long-running screen/camera calls.
                // Wait briefly so transient handoff spikes can succeed without
                // introducing unbounded queueing.
                _options.HandlerAdmissionStarted?.Invoke();
                if (!await _handlerLimiter
                        .WaitAsync(_options.HandlerAdmissionTimeout, ct)
                        .ConfigureAwait(false))
                {
                    CompleteRejection(
                        requestTelemetry,
                        ctx,
                        HttpStatusCode.ServiceUnavailable,
                        "server busy",
                        McpServerOutcome.Failure,
                        McpServerErrorCategory.Busy);
                    continue;
                }
                handlerSlotAcquired = true;

                // NOTE: do not pass `ct` to Task.Run. If the token is cancelled
                // between WaitAsync returning and the delegate starting, Task.Run
                // skips the delegate and the finally never runs — leaking a
                // semaphore slot. Let the delegate observe cancellation itself.
                var handlerTelemetry = requestTelemetry;
                var handlerTask = Task.Run(() => RunHandlerAsync(ctx, handlerTelemetry));
                TrackHandler(handlerTask);
                requestTelemetry = null;
                handlerSlotAcquired = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                requestTelemetry?.Complete(
                    McpServerOutcome.Canceled,
                    McpServerErrorCategory.Shutdown);
                break;
            }
            catch (ObjectDisposedException) when (
                Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested)
            {
                requestTelemetry?.Complete(
                    McpServerOutcome.Canceled,
                    McpServerErrorCategory.Shutdown);
                break;
            }
            catch (Exception ex)
            {
                requestTelemetry?.Complete(
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InternalFailure,
                    ex.GetType());
                _logger.Error("[MCP] Failed to admit request", ex);
                Reject(ctx, HttpStatusCode.InternalServerError, "internal error");
            }
            finally
            {
                if (handlerSlotAcquired)
                {
                    try { _handlerLimiter.Release(); }
                    catch (ObjectDisposedException) { }
                }
                requestTelemetry?.Dispose();
            }
        }
    }

    private async Task RunHandlerAsync(
        HttpListenerContext ctx,
        McpServerRequestOperation requestTelemetry)
    {
        var cancellationState = new McpRequestCancellationState();
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? requestCts = null;
        CancellationTokenRegistration shutdownRegistration = default;
        CancellationTokenRegistration timeoutRegistration = default;
        CancellationToken shutdownCancellation = default;
        CancellationToken timeoutCancellation = default;
        try
        {
            _options.HandlerExecutionStarting?.Invoke();
            timeoutCts = new CancellationTokenSource();
            shutdownCancellation = _cts.Token;
            timeoutCancellation = timeoutCts.Token;
            requestCts = new CancellationTokenSource();
            var requestCancellation = requestCts;
            shutdownRegistration = shutdownCancellation.Register(
                static state =>
                {
                    var propagation = (McpRequestCancellationPropagation)state!;
                    PropagateCancellation(
                        propagation.State,
                        propagation.RequestCancellation,
                        propagation.Cause);
                },
                new McpRequestCancellationPropagation(
                    cancellationState,
                    requestCancellation,
                    McpRequestCancellationCause.Shutdown));
            timeoutRegistration = timeoutCancellation.Register(
                static state =>
                {
                    var propagation = (McpRequestCancellationPropagation)state!;
                    PropagateCancellation(
                        propagation.State,
                        propagation.RequestCancellation,
                        propagation.Cause);
                },
                new McpRequestCancellationPropagation(
                    cancellationState,
                    requestCancellation,
                    McpRequestCancellationCause.Timeout));
            timeoutCts.CancelAfter(_options.RequestTimeout);

            var result = await HandleAsync(
                    ctx,
                    requestCts.Token,
                    cancellationState,
                    timeoutCancellation,
                    shutdownCancellation,
                    requestTelemetry.Context)
                .ConfigureAwait(false);
            requestTelemetry.Complete(result.Outcome, result.ErrorCategory, result.ErrorType);
        }
        catch (OperationCanceledException) when (requestCts?.IsCancellationRequested == true)
        {
            var result = GetCancellationResult(
                cancellationState,
                timeoutCancellation,
                shutdownCancellation);
            requestTelemetry.Complete(result.Outcome, result.ErrorCategory);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            requestTelemetry.Complete(
                McpServerOutcome.Canceled,
                McpServerErrorCategory.Shutdown);
        }
        catch (Exception ex)
        {
            requestTelemetry.Complete(
                McpServerOutcome.Failure,
                McpServerErrorCategory.InternalFailure,
                ex.GetType());
            _logger.Error("[MCP] Request handler failed outside transport handling", ex);
        }
        finally
        {
            timeoutRegistration.Dispose();
            shutdownRegistration.Dispose();
            requestCts?.Dispose();
            timeoutCts?.Dispose();
            requestTelemetry.Dispose();
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

    private async Task<McpRequestResult> HandleAsync(
        HttpListenerContext ctx,
        CancellationToken ct,
        McpRequestCancellationState cancellationState,
        CancellationToken timeoutCancellation,
        CancellationToken shutdownCancellation,
        ActivityContext requestTelemetryContext)
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
                return RejectRequest(
                    ctx,
                    HttpStatusCode.Forbidden,
                    "origin not allowed",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }
            // Belt-and-suspenders: a browser may strip Origin (e.g. via a
            // privacy extension) but still send Sec-Fetch-Site / Sec-Fetch-Mode
            // / Referer. Treat any of those as evidence of a browser context.
            // Native MCP clients don't emit these headers.
            if (!string.IsNullOrEmpty(ctx.Request.Headers["Sec-Fetch-Site"]) ||
                !string.IsNullOrEmpty(ctx.Request.Headers["Sec-Fetch-Mode"]) ||
                !string.IsNullOrEmpty(ctx.Request.Headers["Referer"]))
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.Forbidden,
                    "browser context not allowed",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }

            // Host header must match our loopback bind. Defends against DNS
            // rebinding pivots that route a public hostname to 127.0.0.1.
            if (!IsHostAllowed(ctx.Request.Headers["Host"]))
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.Forbidden,
                    "host not allowed",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }

            // Bearer-token check. Defends against untrusted local processes
            // (browser helpers, editor extensions) that share the loopback
            // surface with the legitimate MCP client. Token lives in a
            // user-only-readable file under %LOCALAPPDATA%; CLI/agent
            // registration reads from there. Keep this before method dispatch
            // so alternate verbs cannot bypass the configured token gate.
            if (authToken != null && !IsAuthorized(authToken, ctx.Request.Headers["Authorization"]))
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.Unauthorized,
                    "missing or invalid bearer token",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.AuthenticationFailed);
            }

            if (ctx.Request.HttpMethod == "GET")
            {
                // Friendly probe response — useful for confirming the server is up
                // from a curl/browser without hitting the JSON-RPC endpoint.
                _writeText(ctx.Response, HttpStatusCode.OK,
                    $"OpenClaw MCP server. POST JSON-RPC to {Endpoint}", "text/plain");
                return McpRequestResult.Success;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.MethodNotAllowed,
                    "POST only",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }

            // Force application/json on POST. Combined with the Origin check,
            // this means a browser cross-origin fetch must use a non-simple
            // Content-Type and trigger a CORS preflight, which we don't honor.
            var contentType = ctx.Request.ContentType ?? "";
            var semi = contentType.IndexOf(';');
            var contentTypeBase = (semi >= 0 ? contentType.Substring(0, semi) : contentType).Trim();
            if (!string.Equals(contentTypeBase, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.UnsupportedMediaType,
                    "application/json required",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }

            // Reject bodies that exceed our cap *before* reading them — a
            // multi-GB POST would otherwise OOM the tray.
            if (ctx.Request.ContentLength64 > MaxRequestBodyBytes)
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.RequestEntityTooLarge,
                    "request body too large",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }

            string body;
            try
            {
                body = await (_options.RequestBodyReader ?? ReadBodyAsync)(
                        ctx.Request,
                        MaxRequestBodyBytes,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return RejectRequest(
                    ctx,
                    HttpStatusCode.RequestEntityTooLarge,
                    "request body too large",
                    McpServerOutcome.Failure,
                    McpServerErrorCategory.InvalidRequest);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Slow-body or stuck client — free the slot rather than blocking forever.
                return RejectCancellation(
                    ctx,
                    cancellationState,
                    timeoutCancellation,
                    shutdownCancellation);
            }

            try
            {
                transportResponse = await _bridge
                    .HandleTransportRequestAsync(body, ct, requestTelemetryContext)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return RejectCancellation(
                    ctx,
                    cancellationState,
                    timeoutCancellation,
                    shutdownCancellation);
            }

            if (transportResponse.Body == null)
            {
                // Notification — JSON-RPC says no body. 204 is the most honest signal.
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                ctx.Response.Close();
                transportResponse.CompleteDelivery();
                return McpRequestResult.Success;
            }

            _writeText(ctx.Response, HttpStatusCode.OK, transportResponse.Body, "application/json");
            transportResponse.CompleteDelivery();
            return McpRequestResult.Success;
        }
        catch (Exception ex)
        {
            transportResponse?.CompleteDelivery(ex.GetType());
            _logger.Error("[MCP] Request failed", ex);
            Reject(ctx, HttpStatusCode.InternalServerError, "internal error");

            if (ct.IsCancellationRequested)
            {
                return GetCancellationResult(
                    cancellationState,
                    timeoutCancellation,
                    shutdownCancellation,
                    ex.GetType());
            }

            return new McpRequestResult(
                McpServerOutcome.Failure,
                IsTransportException(ex)
                    ? McpServerErrorCategory.TransportFailure
                    : McpServerErrorCategory.InternalFailure,
                ex.GetType());
        }
    }

    private McpRequestResult RejectCancellation(
        HttpListenerContext ctx,
        McpRequestCancellationState cancellationState,
        CancellationToken timeoutCancellation,
        CancellationToken shutdownCancellation)
    {
        var result = GetCancellationResult(
            cancellationState,
            timeoutCancellation,
            shutdownCancellation);
        var writeError = Reject(ctx, HttpStatusCode.RequestTimeout, "request timed out");
        return writeError == null
            ? result
            : new McpRequestResult(
                McpServerOutcome.Failure,
                McpServerErrorCategory.TransportFailure,
                writeError);
    }

    private McpRequestResult RejectRequest(
        HttpListenerContext ctx,
        HttpStatusCode status,
        string reason,
        McpServerOutcome outcome,
        McpServerErrorCategory errorCategory)
    {
        var writeError = Reject(ctx, status, reason);
        return writeError == null
            ? new McpRequestResult(outcome, errorCategory)
            : new McpRequestResult(
                McpServerOutcome.Failure,
                McpServerErrorCategory.TransportFailure,
                writeError);
    }

    private void CompleteRejection(
        McpServerRequestOperation telemetry,
        HttpListenerContext ctx,
        HttpStatusCode status,
        string reason,
        McpServerOutcome outcome,
        McpServerErrorCategory errorCategory)
    {
        var result = RejectRequest(ctx, status, reason, outcome, errorCategory);
        telemetry.Complete(result.Outcome, result.ErrorCategory, result.ErrorType);
    }

    private static McpServerRequestKind GetRequestKind(string method) =>
        method switch
        {
            "GET" => McpServerRequestKind.Probe,
            "POST" => McpServerRequestKind.JsonRpc,
            _ => McpServerRequestKind.Other
        };

    private static McpRequestResult GetCancellationResult(
        McpRequestCancellationState cancellationState,
        CancellationToken timeoutCancellation,
        CancellationToken shutdownCancellation,
        Type? errorType = null) =>
        ResolveCancellationCause(
            cancellationState,
            timeoutCancellation,
            shutdownCancellation) switch
        {
            McpRequestCancellationCause.Shutdown => new McpRequestResult(
                McpServerOutcome.Canceled,
                McpServerErrorCategory.Shutdown,
                errorType),
            McpRequestCancellationCause.Timeout => new McpRequestResult(
                McpServerOutcome.Failure,
                McpServerErrorCategory.Timeout,
                errorType),
            _ => new McpRequestResult(
                McpServerOutcome.Failure,
                McpServerErrorCategory.InternalFailure,
                errorType)
        };

    internal static McpRequestCancellationCause ResolveCancellationCause(
        McpRequestCancellationState cancellationState,
        CancellationToken timeoutCancellation,
        CancellationToken shutdownCancellation)
    {
        var cause = cancellationState.Cause;
        if (cause != McpRequestCancellationCause.None)
            return cause;

        var timeoutRequested = timeoutCancellation.IsCancellationRequested;
        var shutdownRequested = shutdownCancellation.IsCancellationRequested;
        if (timeoutRequested == shutdownRequested)
            return McpRequestCancellationCause.None;

        cancellationState.TrySet(
            timeoutRequested
                ? McpRequestCancellationCause.Timeout
                : McpRequestCancellationCause.Shutdown);

        return cancellationState.Cause;
    }

    internal static void PropagateCancellation(
        McpRequestCancellationState cancellationState,
        CancellationTokenSource requestCancellation,
        McpRequestCancellationCause cause)
    {
        cancellationState.TrySet(cause);
        requestCancellation.Cancel();
    }

    private static bool IsTransportException(Exception exception) =>
        exception is IOException or HttpListenerException or ObjectDisposedException;

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
                var n = await request.InputStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                    .ConfigureAwait(false);
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

    private Type? Reject(HttpListenerContext ctx, HttpStatusCode status, string reason)
    {
        try
        {
            _writeText(ctx.Response, status, reason, "text/plain");
            return null;
        }
        catch (Exception ex)
        {
            // Response may already be disposed; a failed write means the client
            // already disconnected. Most Reject call sites are validation paths
            // outside a catch block, so emit a Trace breadcrumb here rather than
            // relying on a (non-existent) outer log.
            System.Diagnostics.Trace.WriteLine($"McpHttpServer.Reject: failed to write {(int)status} '{reason}': {ex.GetType().Name}: {ex.Message}");
            return ex.GetType();
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
        lock (_startLock)
        {
            if (Interlocked.Exchange(ref _started, 0) == 0)
                return;
        }

        using var telemetry = McpServerTelemetry.StartLifecycle(McpServerOperation.Stop);
        var outcome = McpServerOutcome.Success;
        var errorCategory = McpServerErrorCategory.None;
        Type? errorType = null;

        void SetFailure(McpServerErrorCategory category, Type? type = null)
        {
            if (outcome != McpServerOutcome.Success)
                return;

            outcome = McpServerOutcome.Failure;
            errorCategory = category;
            errorType = type;
        }

        try
        {
            try { _cts.Cancel(); }
            catch (Exception ex)
            {
                SetFailure(McpServerErrorCategory.InternalFailure, ex.GetType());
                _logger.Debug($"[MCP] StopCore cts.Cancel threw: {ex.Message}");
            }

            try
            {
                if (_listener.IsListening)
                    _listener.Stop();
            }
            catch (Exception ex)
            {
                SetFailure(McpServerErrorCategory.ListenerStop, ex.GetType());
                McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerStop);
                _logger.Debug($"[MCP] StopCore listener.Stop threw: {ex.Message}");
            }

            // Snapshot before awaiting — handlers remove themselves on completion,
            // and we don't want enumeration to race the continuation.
            Task[] toAwait;
            lock (_activeLock)
            {
                toAwait = new Task[_activeHandlers.Count];
                _activeHandlers.CopyTo(toAwait);
            }

            var allHandlers = Task.WhenAll(toAwait);
            var deadline = Task.Delay(drainTimeout);
            var winner = await Task.WhenAny(allHandlers, deadline).ConfigureAwait(false);
            if (winner == deadline && toAwait.Length > 0)
            {
                SetFailure(McpServerErrorCategory.DrainTimeout);
                int still;
                lock (_activeLock) { still = _activeHandlers.Count; }
                _logger.Warn($"[MCP] Drain timeout ({drainTimeout.TotalSeconds:F1}s); {still} handler(s) still running");
            }

            if (_acceptLoop != null)
            {
                try
                {
                    await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(1)))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[MCP] Accept loop final await threw (loop may have errored): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SetFailure(McpServerErrorCategory.InternalFailure, ex.GetType());
            throw;
        }
        finally
        {
            telemetry.Complete(outcome, errorCategory, errorType);
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
            await StopAsync(_options.DrainTimeout).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerClose);
            _logger.Debug($"[MCP] listener.Close during dispose threw: {ex.Message}");
        }
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

    private readonly record struct McpRequestResult(
        McpServerOutcome Outcome,
        McpServerErrorCategory ErrorCategory,
        Type? ErrorType = null)
    {
        public static McpRequestResult Success { get; } = new(
            McpServerOutcome.Success,
            McpServerErrorCategory.None);
    }

    internal enum McpRequestCancellationCause
    {
        None,
        Timeout,
        Shutdown
    }

    internal sealed class McpRequestCancellationState
    {
        private int _cause;

        public McpRequestCancellationCause Cause =>
            (McpRequestCancellationCause)Volatile.Read(ref _cause);

        public void TrySet(McpRequestCancellationCause cause) =>
            Interlocked.CompareExchange(ref _cause, (int)cause, (int)McpRequestCancellationCause.None);
    }

    private sealed record McpRequestCancellationPropagation(
        McpRequestCancellationState State,
        CancellationTokenSource RequestCancellation,
        McpRequestCancellationCause Cause);
}
