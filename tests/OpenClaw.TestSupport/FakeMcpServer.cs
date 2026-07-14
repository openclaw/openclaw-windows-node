using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenClaw.TestSupport;

/// <summary>
/// Tiny loopback HTTP server that captures the request body and returns a
/// canned response. Lets CLI/MCP tests exercise the real HttpClient code path
/// (timeouts, connection failures, JSON-RPC envelopes) without any reliance on
/// the running tray. Shared single source: see <c>docs/ARCHITECTURE.md</c>
/// (ledger id <c>test-fake-mcp</c>).
/// </summary>
public sealed class FakeMcpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    public string? LastRequestBody { get; private set; }
    public string? LastRequestMethod { get; private set; }
    public string? LastRequestContentType { get; private set; }
    public string? LastRequestAuthorization { get; private set; }

    /// <summary>Set by the test before issuing the call.</summary>
    public Func<string, (HttpStatusCode Status, string Body, string ContentType)>? Responder { get; set; }

    /// <summary>If true, the server holds the request to force a client timeout.</summary>
    public bool HoldForever { get; set; }

    public FakeMcpServer()
    {
        (Port, _listener) = StartListenerWithRetry();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Binding is a two-step "find a free port, then start an HttpListener on it"
    /// operation with an inherent TOCTOU window: another process/test can grab the
    /// port between steps. Retry on bind failure so this shared fixture does not
    /// flake under parallel test runs.
    /// </summary>
    private static (int Port, HttpListener Listener) StartListenerWithRetry()
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            var port = FindFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (port, listener);
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                try { listener.Close(); } catch { }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            LastRequestMethod = ctx.Request.HttpMethod;
            LastRequestContentType = ctx.Request.ContentType;
            LastRequestAuthorization = ctx.Request.Headers["Authorization"];

            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
            {
                LastRequestBody = await reader.ReadToEndAsync();
            }

            if (HoldForever)
            {
                // slopwatch-ignore: SW004 Test deliberately blocks until cancellation to exercise cancellation behavior deterministically.
                try { await Task.Delay(Timeout.Infinite, _cts.Token); }
                // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                catch { /* server shutting down */ }
                return;
            }

            var responder = Responder ?? DefaultResponder;
            var (status, body, contentType) = responder(LastRequestBody ?? "");
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static (HttpStatusCode, string, string) DefaultResponder(string _)
        => (HttpStatusCode.OK,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"isError\":false}}",
            "application/json");

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { _cts.Cancel(); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { _listener.Stop(); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { _listener.Close(); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
