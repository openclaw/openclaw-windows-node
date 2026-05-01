using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// Tiny loopback HTTP server that captures the request body and returns a
/// canned response. Lets the RunAsync tests exercise the real HttpClient code
/// path (timeouts, connection failures, JSON-RPC envelopes) without any
/// reliance on the running tray.
/// </summary>
internal sealed class FakeMcpServer : IDisposable
{
    private readonly HttpListener _listener = new();
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
        Port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
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
                try { await Task.Delay(Timeout.Infinite, _cts.Token); }
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
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
