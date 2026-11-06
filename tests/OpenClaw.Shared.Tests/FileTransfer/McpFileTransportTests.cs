using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mcp;
using OpenClaw.Shared.Telemetry;
using Xunit;

namespace OpenClaw.Shared.Tests.FileTransfer;

public class McpFileTransportTests
{
    [Fact]
    public async Task GenericResponseAboveSixteenMiBPreservesTheExistingHttpContract()
    {
        var value = new string('x', 17 * 1024 * 1024);
        await using var fixture = new Server(new ResultCapability("fixture.result", value));
        var result = await fixture.Call("tools/call", new { name = "fixture.result", arguments = new { } });
        Assert.Equal(value, JsonSerializer.Deserialize<string>(result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!));
    }

    [Fact]
    public async Task DeepFileRequestCannotUseGenericFallbackToEscapeFileBounds()
    {
        object nested = 0;
        for (var i = 0; i < 35; i++) nested = new object[] { nested };
        await using var fixture = new Server(new ResultCapability("file.stat", "must not run"));
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "file.stat", arguments = new { nested } }
        }), Encoding.UTF8, "application/json");
        using var response = await fixture.Http.PostAsync("/", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class ResultCapability(string command, object payload) : INodeCapability
    {
        public string Category => "fixture";
        public IReadOnlyList<string> Commands => [command];
        public bool CanHandle(string value) => value == command;
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = payload });
    }

    [Theory]
    [InlineData("file.stat")]
    [InlineData("file.fetch")]
    [InlineData("dir.list")]
    [InlineData("dir.fetch")]
    [InlineData("file.write")]
    [InlineData("file.create")]
    [InlineData("fs.listDir")]
    [InlineData("terminal.upload")]
    public async Task TypedSemanticFailureBecomesMcpErrorAndFailedTelemetry(string command)
    {
        var bridge = new McpToolBridge(() => [new FailureCapability()]);
        NodeToolTelemetryCompletion? completion = null;
        bridge.ToolTelemetryCompleted += (_, value) => completion = value;
        var response = await bridge.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = command, arguments = new { } }
        }));
        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("NOT_FOUND", response);
        Assert.Equal(NodeToolOutcome.Failure, completion!.Outcome);
    }

    [Fact]
    public async Task ControlRemainsAvailableWhenAllNormalAndStreamingSlotsAreOccupied()
    {
        var capability = new HoldingCapability(20);
        await using var fixture = new Server(capability);
        var normal = Enumerable.Range(0, 16)
            .Select(i => fixture.Call("tools/call", new { name = "hold", arguments = new { } }, $"n{i}")).ToArray();
        var leases = new List<string>();
        var streaming = new List<Task<JsonElement>>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var opened = await fixture.Call("openclaw/files/open",
                    new { v = 1, command = "file.fetch", requestId = $"f{i}", timeoutMs = 30000 });
                var lease = opened.GetProperty("result").GetProperty("lease").GetString()!;
                leases.Add(lease);
                streaming.Add(fixture.Call("tools/call", new
                {
                    name = "file.fetch", arguments = new { },
                    _meta = new Dictionary<string, object> { ["openclaw/file-channel"] = new { v = 1, lease } }
                }, $"f{i}"));
            }
            await capability.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var poll = await fixture.Call("openclaw/files/poll", new { v = 1, lease = leases[0], ack = 0 });
            var frames = poll.GetProperty("result").GetProperty("frames");
            Assert.Equal("{\"v\":1,\"kind\":\"ready\"}", frames[0].GetProperty("payloadJSON").GetString());
            var retry = await fixture.Call("openclaw/files/poll", new { v = 1, lease = leases[0], ack = 0 });
            Assert.Equal(frames.GetRawText(), retry.GetProperty("result").GetProperty("frames").GetRawText());
            var acknowledged = await fixture.Call("openclaw/files/poll", new { v = 1, lease = leases[0], ack = 1 });
            Assert.Empty(acknowledged.GetProperty("result").GetProperty("frames").EnumerateArray());
            await fixture.Call("openclaw/files/close", new { v = 1, lease = leases[0] });
            Assert.True((await streaming[0]).GetProperty("result").GetProperty("isError").GetBoolean());
            await fixture.NotifyCancel("n0");
            Assert.True((await normal[0]).GetProperty("result").GetProperty("isError").GetBoolean());
        }
        finally
        {
            fixture.HttpServer.UpdateAuthToken("replacement");
            capability.Release.TrySetResult();
            await Task.WhenAll(normal.Concat(streaming));
        }
    }

    [Fact]
    public async Task LeaseAuthBindingReplayAndRotationFailClosed()
    {
        var capability = new HoldingCapability(1);
        await using var fixture = new Server(capability);
        var opened = await fixture.Call("openclaw/files/open",
            new { v = 1, command = "file.fetch", requestId = "bound-id", timeoutMs = 30000 });
        var lease = opened.GetProperty("result").GetProperty("lease").GetString()!;
        var wrongId = await fixture.Call("tools/call", new
        {
            name = "file.fetch", arguments = new { },
            _meta = new Dictionary<string, object> { ["openclaw/file-channel"] = new { v = 1, lease } }
        }, "wrong-id");
        Assert.True(wrongId.GetProperty("result").GetProperty("isError").GetBoolean());
        using var unauthenticated = new HttpClient { BaseAddress = fixture.Http.BaseAddress };
        using var denied = await unauthenticated.PostAsync("/", new StringContent(
            JsonSerializer.Serialize(new { method = "openclaw/files/poll", id = 1, @params = new { v = 1, lease, ack = 0 } }),
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var call = fixture.Call("tools/call", new
        {
            name = "file.fetch", arguments = new { },
            _meta = new Dictionary<string, object> { ["openclaw/file-channel"] = new { v = 1, lease } }
        }, "bound-id");
        await capability.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        const string start = "{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"c3RhcnQ=\"}";
        var first = await fixture.Call("openclaw/files/input", new { v = 1, lease, seq = 0, payloadJSON = start });
        var retry = await fixture.Call("openclaw/files/input", new { v = 1, lease, seq = 0, payloadJSON = start });
        Assert.True(first.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.True(retry.GetProperty("result").GetProperty("accepted").GetBoolean());
        var changed = await fixture.Call("openclaw/files/input", new { v = 1, lease, seq = 0, payloadJSON = "" });
        Assert.True(changed.GetProperty("result").GetProperty("isError").GetBoolean());
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.HttpServer.UpdateAuthToken("new-token");
        fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "new-token");
        var stale = await fixture.Call("openclaw/files/poll", new { v = 1, lease, ack = 0 });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task RequestDuringBearerRotationCannotAcquireTheReplacementEpoch()
    {
        var bodyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rotating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRotation = new ManualResetEventSlim();
        var bridge = new McpToolBridge(() => [new ResultCapability("file.fetch", "must not run")]);
        var open = new { v = 1, command = "file.fetch", requestId = "old", timeoutMs = 30000 };
        var opened = JsonSerializer.SerializeToElement(bridge.FileChannels.Control("openclaw/files/open",
            JsonSerializer.SerializeToElement(open), bridge.FileChannels.Epoch));
        var lease = bridge.FileChannels.Attach(
            JsonSerializer.SerializeToElement(new { v = 1, lease = opened.GetProperty("lease").GetString() }),
            "file.fetch", McpToolBridge.GetRequestKey(JsonSerializer.SerializeToElement("old")), bridge.FileChannels.Epoch);
        using var registration = lease.Token.Register(() =>
        {
            rotating.SetResult();
            Assert.True(releaseRotation.Wait(TimeSpan.FromSeconds(10)));
        });
        await using var fixture = new Server(new ResultCapability("file.fetch", "must not run"), bridge: bridge,
            bodyReader: async (request, _, ct) =>
            {
                bodyRead.TrySetResult();
                using var reader = new StreamReader(request.InputStream);
                return await reader.ReadToEndAsync(ct);
            });
        var rotation = Task.Run(() => fixture.HttpServer.UpdateAuthToken("replacement"));
        Task<JsonElement>? stale = null;
        try
        {
            await rotating.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stale = fixture.Call("openclaw/files/open", open);
            await bodyRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseRotation.Set();
            await rotation;
        }
        Assert.True((await stale!).GetProperty("result").GetProperty("isError").GetBoolean());
        fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "replacement");
        var current = await fixture.Call("openclaw/files/open", open);
        Assert.True(current.GetProperty("result").TryGetProperty("lease", out _));
    }

    [Fact]
    public async Task FullSizeUnaryRoundTripsPlusHeavyBase64ButNormalBodyLimitStaysSmall()
    {
        var bytes = Enumerable.Repeat((byte)0xfb, 16 * 1024 * 1024).ToArray();
        var base64 = Convert.ToBase64String(bytes);
        var workerJson = JsonSerializer.SerializeToUtf8Bytes(new { contentBase64 = base64 }, FileJson.Options);
        Assert.True(workerJson.Length < FileLimits.RequestBytes);
        var request = new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "file.write", arguments = new { path = @"C:\Fixture\binary", contentBase64 = base64 } }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(request, FileTransportJson.Options);
        Assert.True(body.Length < FileTransportJson.EnvelopeBytes);
        Assert.DoesNotContain("\\u002B", Encoding.UTF8.GetString(body));
        await using var fixture = new Server(new EchoFileCapability());
        using (var normal = new ByteArrayContent(body))
        {
            normal.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var rejection = await fixture.Http.PostAsync("/", normal);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejection.StatusCode);
        }
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var fileRequest = new HttpRequestMessage(HttpMethod.Post, "/") { Content = content };
        fileRequest.Headers.Add(FileTransportJson.EnvelopeHeader, "file.write");
        using var response = await fixture.Http.SendAsync(fileRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resultBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(resultBytes.Length < FileTransportJson.ResultEnvelopeBytes);
        using var doc = JsonDocument.Parse(resultBytes);
        using var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(base64, payload.RootElement.GetProperty("base64").GetString());
    }

    [Fact]
    public async Task DeepGenericJsonCannotBypassOrdinaryAdmissionOrPinControlReaders()
    {
        var capability = new HoldingCapability(1);
        await using var fixture = new Server(capability, handlers: 1);
        object nested = 0;
        for (var i = 0; i < 35; i++) nested = new object[] { nested };
        var call = fixture.Call("tools/call", new { name = "hold", arguments = new { nested } }, "deep");
        try
        {
            await capability.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var busy = await fixture.Http.GetAsync("/");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            await fixture.NotifyCancel("deep");
            Assert.True((await call).GetProperty("result").GetProperty("isError").GetBoolean());
        }
        finally { capability.Release.TrySetResult(); await call; }
    }

    private sealed class FailureCapability : INodeCapability
    {
        public string Category => "file";
        public IReadOnlyList<string> Commands => FileCommandParser.Commands;
        public bool CanHandle(string command) => Commands.Contains(command);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) => Task.FromResult(new NodeInvokeResponse
        {
            Ok = request.Command is not ("fs.listDir" or "terminal.upload"),
            FileOutcome = new(false, "NOT_FOUND", "File not found"),
            Payload = new { ok = false, code = "NOT_FOUND", message = "File not found" }
        });
    }

    private sealed class EchoFileCapability : INodeCapability
    {
        public string Category => "file";
        public IReadOnlyList<string> Commands => ["file.write"];
        public bool CanHandle(string command) => command == "file.write";
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        {
            var parsed = FileCommandParser.Parse(request.Command, request.Args);
            return Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = new { base64 = parsed.ContentBase64 } });
        }
    }

    private sealed class HoldingCapability(int count) : INodeCapability
    {
        private int _remaining = count;
        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Category => "file";
        public IReadOnlyList<string> Commands => ["hold", "file.fetch"];
        public bool CanHandle(string command) => Commands.Contains(command);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) => ExecuteAsync(request, default);
        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken ct)
        {
            await using var listener = request.FileChannel == null ? null :
                await request.FileChannel.ListenAsync((_, _) => ValueTask.CompletedTask, ct);
            if (Interlocked.Decrement(ref _remaining) == 0) AllEntered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new() { Ok = true };
        }
    }

    private sealed class Server : IAsyncDisposable
    {
        public McpHttpServer HttpServer { get; }
        public HttpClient Http { get; }
        public Server(INodeCapability capability, int handlers = 16, McpToolBridge? bridge = null,
            Func<HttpListenerRequest, long, CancellationToken, Task<string>>? bodyReader = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            HttpServer = new McpHttpServer(bridge ?? new McpToolBridge(() => [capability]), port, NullLogger.Instance, "fixture-token",
                new McpHttpServerOptions(handlers, TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(5),
                    requestBodyReader: bodyReader));
            HttpServer.Start();
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(HttpServer.Endpoint), Timeout = TimeSpan.FromSeconds(30) };
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-token");
        }
        public async Task<JsonElement> Call(string method, object parameters, string? id = null)
        {
            using var content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = id ?? Guid.NewGuid().ToString("N"), method, @params = parameters
            }), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = content };
            var fields = JsonSerializer.SerializeToElement(parameters);
            if (method == "tools/call" && fields.TryGetProperty("name", out var name) &&
                FileCommandParser.Commands.Contains(name.GetString()!))
                request.Headers.Add(FileTransportJson.EnvelopeHeader, name.GetString());
            using var response = await Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }
        public async Task NotifyCancel(string id)
        {
            using var content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id }
            }), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        public async ValueTask DisposeAsync() { await HttpServer.DisposeAsync(); Http.Dispose(); }
    }
}
