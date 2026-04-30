using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Mcp;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Verifies the McpHttpServer security gate and protocol envelope. Each test
/// boots the server on an ephemeral port so they can run in parallel and we
/// don't collide with the production 8765.
/// </summary>
public class McpHttpServerTests
{
    private sealed class FakeCapability : INodeCapability
    {
        public string Category => "alpha";
        public IReadOnlyList<string> Commands => new[] { "alpha.echo" };
        public bool CanHandle(string command) => command == "alpha.echo";
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
            => Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = new { echoed = request.Command } });
    }

    private static int FreePort()
    {
        // Bind to port 0, ask the kernel for the assigned port, release.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (McpHttpServer server, HttpClient client, Uri url) Boot()
    {
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { new FakeCapability() });
        var server = new McpHttpServer(bridge, port, NullLogger.Instance);
        server.Start();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        return (server, http, new Uri($"http://127.0.0.1:{port}/"));
    }

    private static (McpHttpServer server, HttpClient client, Uri url) BootWith(INodeCapability cap)
    {
        var port = FreePort();
        var bridge = new McpToolBridge(() => new[] { cap });
        var server = new McpHttpServer(bridge, port, NullLogger.Instance);
        server.Start();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        return (server, http, new Uri($"http://127.0.0.1:{port}/"));
    }

    private sealed class GatedCapability : INodeCapability
    {
        private readonly TaskCompletionSource<bool> _entered = new();
        private readonly TaskCompletionSource<bool> _release;
        public GatedCapability(TaskCompletionSource<bool> release) { _release = release; }
        public Task Entered => _entered.Task;
        public string Category => "gate";
        public IReadOnlyList<string> Commands => new[] { "gate.wait" };
        public bool CanHandle(string command) => command == "gate.wait";
        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        {
            _entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            return new NodeInvokeResponse { Ok = true, Payload = new { done = true } };
        }
    }

    [Fact]
    public async Task Get_ReturnsFriendlyProbe()
    {
        var (server, http, _) = Boot();
        try
        {
            var resp = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("OpenClaw MCP server", body);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_ValidJsonRpc_ReturnsResult()
    {
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithBrowserOrigin_RejectedWithForbidden()
    {
        // The CSRF gate: any Origin header means a browser is the caller.
        // Real MCP clients do not send Origin.
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("Origin", "https://evil.com");
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithRebindHost_RejectedWithForbidden()
    {
        // DNS rebinding: attacker hostname masking 127.0.0.1.
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Host = "evil.com";
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithLocalhostHost_Accepted()
    {
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Host = "localhost";
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithTextPlain_RejectedWithUnsupportedMediaType()
    {
        // CORS-simple POST defense: text/plain bypasses preflight, so we
        // require application/json explicitly.
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8, "text/plain");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithJsonAndCharset_Accepted()
    {
        // application/json with charset suffix should still be accepted.
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Put_RejectedWithMethodNotAllowed()
    {
        var (server, http, _) = Boot();
        try
        {
            var resp = await http.PutAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_OversizedBody_RejectedWithRequestTooLarge()
    {
        var (server, http, _) = Boot();
        try
        {
            // 5 MiB exceeds the 4 MiB cap.
            var big = new string('x', 5 * 1024 * 1024);
            var content = new StringContent(big, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Notification_Returns204NoContent()
    {
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (server, http, _) = Boot();
        try
        {
            server.Dispose();
            server.Dispose(); // must not throw
        }
        finally { http.Dispose(); }
    }

    [Fact]
    public async Task Dispose_DuringInFlightHandler_DoesNotSurfaceObjectDisposedException()
    {
        // CR-005: when the server is disposed while a handler is mid-flight,
        // the handler must not throw ObjectDisposedException as it tries to
        // release the semaphore on its way out. Before the fix, Dispose
        // disposed `_handlerLimiter` while handlers were still running, so
        // the `Release()` in the finally throws — and because the handler
        // task was fire-and-forget, that throw became an unobserved exception.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cap = new GatedCapability(release);
        var (server, http, _) = BootWith(cap);

        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            foreach (var ex in e.Exception.InnerExceptions) unobserved.Add(ex);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            // Kick the request off without awaiting; HttpClient may surface a
            // socket-level error when Dispose tears the listener down, so we
            // tolerate exceptions on the response itself — the assertion here
            // is about the server's internal task hygiene, not the HTTP wire.
            var inflight = Task.Run(async () =>
            {
                try
                {
                    await http.PostAsync("/", new StringContent(
                        @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""gate.wait""}}",
                        Encoding.UTF8, "application/json"));
                }
                catch { /* socket may close on shutdown; not what we're testing */ }
            });

            var entered = await Task.WhenAny(cap.Entered, Task.Delay(2000));
            Assert.Equal(cap.Entered, entered);

            // Dispose while the handler is alive. Drain awaits in-flight
            // handler tasks; the linked CT inside the handler causes the
            // bridge WaitAsync to abort, so the handler unwinds cleanly.
            server.Dispose();

            // Unblock the capability so its task doesn't hang the test runner.
            release.TrySetResult(true);
            await inflight;

            // Force any continuations + finalizers to run so unobserved
            // exception events fire deterministically.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.DoesNotContain(unobserved, e => e is ObjectDisposedException);
            Assert.DoesNotContain(unobserved, e => e is SemaphoreFullException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
            release.TrySetResult(true);
            http.Dispose();
        }
    }

    [Fact]
    public async Task SlowBody_TimesOut_FreesHandlerSlot()
    {
        // CR-003: a client that opens a POST and dribbles bytes must not pin
        // a handler slot. The per-request deadline aborts the body read; the
        // server then accepts new requests normally.
        // We can't easily exceed the 90s production timeout in a fast test,
        // so this test verifies the read path observes the request-scoped CT
        // by closing the underlying TCP connection mid-read and confirming
        // the server doesn't hang.
        var (server, http, url) = Boot();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            using var stream = tcp.GetStream();
            var headers =
                "POST / HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{url.Port}\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 1000000\r\n" +
                "\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            // Send a few bytes then close — server should release its slot.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("{\"a\":"));
            await stream.FlushAsync();
            await Task.Delay(50);
            tcp.Close();

            // After the bad request goes away, normal requests must still work.
            var resp = await http.PostAsync("/", new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public void Ctor_NullBridge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new McpHttpServer(null!, 1234, NullLogger.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        Assert.Throws<ArgumentNullException>(() => new McpHttpServer(bridge, 1234, null!));
    }

    [Fact]
    public async Task Auth_RequiresBearerToken_When_TokenConfigured()
    {
        const string token = "supersecret";
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { new FakeCapability() });
        using var server = new McpHttpServer(bridge, port, NullLogger.Instance, token);
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8,
            "application/json");

        // No Authorization header → 401.
        var resp = await http.PostAsync("", content);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);

        // Wrong token → 401.
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong");
        var content2 = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var resp2 = await http.PostAsync("", content2);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp2.StatusCode);

        // Correct token → 200.
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var content3 = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var resp3 = await http.PostAsync("", content3);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp3.StatusCode);
    }

    [Fact]
    public async Task Auth_RequiresBearerToken_BeforeMethodDispatch_When_TokenConfigured()
    {
        const string token = "supersecret";
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { new FakeCapability() });
        using var server = new McpHttpServer(bridge, port, NullLogger.Instance, token);
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var unauthenticatedGet = await http.GetAsync("");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthenticatedGet.StatusCode);

        var unauthenticatedPut = await http.PutAsync(
            "",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthenticatedPut.StatusCode);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var authorizedGet = await http.GetAsync("");
        Assert.Equal(System.Net.HttpStatusCode.OK, authorizedGet.StatusCode);

        var authorizedPut = await http.PutAsync(
            "",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, authorizedPut.StatusCode);
    }

    [Fact]
    public async Task Auth_NoToken_AllowsAllRequests_LegacyDevMode()
    {
        // Constructing without an authToken keeps the prior unauthenticated
        // contract (relying on loopback + Origin/Host gates only). Existing
        // setups that haven't migrated to bearer auth keep working.
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { new FakeCapability() });
        using var server = new McpHttpServer(bridge, port, NullLogger.Instance);
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var resp = await http.PostAsync("", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}
