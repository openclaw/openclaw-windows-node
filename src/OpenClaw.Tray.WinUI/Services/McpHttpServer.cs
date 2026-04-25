using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Mcp;

namespace OpenClawTray.Services;

/// <summary>
/// Localhost-only HTTP transport for the MCP server. Binds to 127.0.0.1 — the
/// loopback interface — so the endpoint is unreachable from any other machine
/// regardless of firewall configuration. No authentication; intended for
/// local-only MCP clients (e.g. Claude Desktop, Cursor).
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private readonly McpToolBridge _bridge;
    private readonly int _port;
    private readonly IOpenClawLogger _logger;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public int Port => _port;
    public string Endpoint => $"http://127.0.0.1:{_port}/mcp";

    public McpHttpServer(McpToolBridge bridge, int port, IOpenClawLogger logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _port = port;
        _logger = logger;
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
                _logger.Error($"[MCP] Accept failed: {ex.Message}");
                continue;
            }

            // Defensive: even though the prefix is loopback-only, double-check.
            if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
            {
                Reject(ctx, HttpStatusCode.Forbidden, "loopback only");
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET")
            {
                // Friendly probe response — useful for confirming the server is up
                // from a browser without hitting the JSON-RPC endpoint.
                WriteText(ctx.Response, HttpStatusCode.OK,
                    $"OpenClaw MCP server. POST JSON-RPC to {Endpoint}", "text/plain");
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                Reject(ctx, HttpStatusCode.MethodNotAllowed, "POST only");
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var responseBody = await _bridge.HandleRequestAsync(body).ConfigureAwait(false);

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
            _logger.Error($"[MCP] Request failed: {ex.Message}");
            try { Reject(ctx, HttpStatusCode.InternalServerError, "internal error"); }
            catch { /* response may already be closed */ }
        }
    }

    private static void Reject(HttpListenerContext ctx, HttpStatusCode status, string reason)
    {
        WriteText(ctx.Response, status, reason, "text/plain");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
