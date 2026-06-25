using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Real WebSocket round-trip proof for the gateway protocol client APIs.
/// Unlike <see cref="GatewayProtocolModelsTests"/> (which exercise
/// the parsers against crafted <see cref="JsonElement"/> payloads), these tests
/// connect the <b>real</b> <see cref="OpenClawGatewayClient"/> over a <b>real</b>
/// loopback WebSocket to a stub gateway, invoke each new typed method, and:
///   1. capture the exact JSON request frame the client serialized onto the wire
///      (proving it matches the upstream openclaw/openclaw schema — param names,
///      method names, and the tri-state sessions.patch null/value encoding), and
///   2. feed back schema-shaped responses and assert the typed DTOs parse.
///
/// This exercises the full method → SerializeRequest → socket send → server
/// receive → response → parse → typed-DTO stack end-to-end. The captured frames
/// are written to test output so they can serve as redacted behavior proof.
/// </summary>
public sealed class GatewayProtocolLiveRoundTripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _identityDir;

    public GatewayProtocolLiveRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        _identityDir = Path.Combine(Path.GetTempPath(), "openclaw-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_identityDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_identityDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NewProtocolMethods_RealWebSocketRoundTrip_SendCorrectWireFramesAndParseResponses()
    {
        using var server = new LoopbackGatewayServer();
        ConfigureResponders(server);

        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            tokenIsBootstrapToken: false, bootstrapPairAsNode: false,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            const string key = "agent:main:main";
            // Generous timeout: the happy path resolves as soon as the response
            // arrives, but a large ceiling prevents load-induced flakiness on
            // shared CI runners (the loopback server can be scheduling-starved
            // when many test classes run in parallel).
            const int rpc = 20000;

            // ── 1. commands.list (typed catalog read) ──
            var catalog = await client.ListCommandsAsync(timeoutMs: rpc);
            Assert.True(catalog.IsSupported);
            var cmd = Assert.Single(catalog.Commands);
            Assert.Equal("model", cmd.Name);
            var arg = Assert.Single(cmd.Args);
            Assert.Equal("gpt-5", Assert.Single(arg.Choices).Value);

            // ── 2. sessions.files.get (read; param must be sessionKey, response nested under "file") ──
            var file = await client.GetSessionFileAsync(key, "src/a.cs", timeoutMs: rpc);
            Assert.True(file.Found);
            Assert.Equal("hello world", file.Content);
            Assert.Contains("\"sessionKey\"", server.FrameFor("sessions.files.get"));

            // ── 3. sessions.compaction.list + branch (param key/checkpointId; branch returns sourceKey + new key) ──
            var checkpoints = await client.ListCompactionCheckpointsAsync(key, timeoutMs: rpc);
            Assert.True(checkpoints.IsSupported);
            Assert.Equal("cp1", Assert.Single(checkpoints.Checkpoints).Id);

            var branch = await client.BranchCompactionCheckpointAsync(key, "cp1", timeoutMs: rpc);
            Assert.True(branch.Ok);
            Assert.Equal("agent:main:main", branch.SourceKey);
            Assert.Equal("agent:main:branch-1", branch.ResultSessionKey);
            Assert.Contains("\"checkpointId\"", server.FrameFor("sessions.compaction.branch"));

            // ── 4. sessions.patch SET then CLEAR (the tri-state proof) ──
            // PatchSessionAsync is fire-and-tracked (returns on send, not on
            // response), so wait for the captured frame to arrive on the server.
            var setOk = await client.PatchSessionAsync(key, new SessionPatch { Model = "gpt-5", FastMode = SessionFastMode.Auto });
            Assert.True(setOk);
            var setFrame = await server.WaitFrameAsync("sessions.patch", occurrence: 0, timeoutMs: rpc);
            Assert.Contains("\"model\":\"gpt-5\"", setFrame);
            Assert.Contains("\"fastMode\":\"auto\"", setFrame);

            var clearOk = await client.PatchSessionAsync(key, new SessionPatch { Model = SessionPatch.Clear });
            Assert.True(clearOk);
            var clearFrame = await server.WaitFrameAsync("sessions.patch", occurrence: 1, timeoutMs: rpc);
            Assert.Contains("\"model\":null", clearFrame);

            PrintProof(server);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task CronRunDetailed_FallsBackToLegacyIdPayload_WhenJobIdPayloadIsRejected()
    {
        using var server = new LoopbackGatewayServer();
        var requestCount = 0;
        var observedParameters = new ConcurrentQueue<JsonElement>();
        server.OnMethod("cron.run", parameters =>
        {
            requestCount++;
            observedParameters.Enqueue(parameters.Clone());
            if (requestCount == 1)
                return LoopbackResponse.Fail("invalid cron.run params: unexpected property jobId");

            return new { ok = true, enqueued = true, runId = "manual:job-legacy:1" };
        });

        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            tokenIsBootstrapToken: false, bootstrapPairAsNode: false,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            var result = await client.RunCronJobDetailedAsync("job-legacy", timeoutMs: 20000);

            Assert.True(result.Accepted);
            Assert.True(result.Enqueued);
            Assert.Equal("manual:job-legacy:1", result.RunId);
            Assert.Equal(2, requestCount);
            await server.WaitFrameAsync("cron.run", occurrence: 1, timeoutMs: 20000);

            var payloads = observedParameters.ToArray();
            Assert.Equal(2, payloads.Length);
            Assert.True(payloads[0].TryGetProperty("jobId", out var jobId));
            Assert.Equal("job-legacy", jobId.GetString());
            Assert.False(payloads[0].TryGetProperty("id", out _));
            Assert.True(payloads[1].TryGetProperty("id", out var id));
            Assert.Equal("job-legacy", id.GetString());
            Assert.True(payloads[1].TryGetProperty("force", out var force));
            Assert.True(force.GetBoolean());
            Assert.False(payloads[1].TryGetProperty("jobId", out _));
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    private static void ConfigureResponders(LoopbackGatewayServer server)
    {
        // NOTE: intentionally NO hello-ok responder. The new methods only require
        // an open socket (IsConnectedToGateway), not the full handshake. Skipping
        // hello-ok avoids the client's post-handshake auto-request storm
        // (health/sessions.list/subscribe/usage/nodes/agents), keeping this test
        // lightweight so it does not add scheduler/socket contention that could
        // destabilize other timing-sensitive socket tests under parallel load.

        server.OnMethod("commands.list", _ => new
        {
            commands = new object[]
            {
                new
                {
                    name = "model",
                    description = "Change the active model",
                    source = "native",
                    scope = "both",
                    acceptsArgs = true,
                    args = new object[]
                    {
                        new
                        {
                            name = "id",
                            description = "Model id",
                            type = "string",
                            choices = new object[] { new { value = "gpt-5", label = "GPT-5" } }
                        }
                    }
                }
            }
        });

        server.OnMethod("sessions.files.get", _ => new
        {
            sessionKey = "agent:main:main",
            root = "/work/repo",
            file = new
            {
                path = "src/a.cs",
                name = "a.cs",
                kind = "modified",
                missing = false,
                size = 11,
                updatedAtMs = 1700000000000L,
                content = "hello world"
            }
        });

        server.OnMethod("sessions.compaction.list", _ => new
        {
            ok = true,
            key = "agent:main:main",
            checkpoints = new object[]
            {
                new { checkpointId = "cp1", sessionKey = "agent:main:main", sessionId = "sid-1", createdAt = 1700000000000L, reason = "manual" }
            }
        });

        server.OnMethod("sessions.compaction.branch", _ => new
        {
            ok = true,
            sourceKey = "agent:main:main",
            key = "agent:main:branch-1",
            sessionId = "sid-branch",
            checkpoint = new { checkpointId = "cp1" }
        });

        // sessions.patch is a tracked (non-wizard) request; an ok response with a
        // key payload completes it.
        server.OnMethod("sessions.patch", _ => new { key = "agent:main:main" });
    }

    private static async Task ConnectAndWaitAsync(OpenClawGatewayClient client, LoopbackGatewayServer server)
    {
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? _, ConnectionStatus s)
        {
            if (s == ConnectionStatus.Connected) connected.TrySetResult(true);
        }
        client.StatusChanged += OnStatus;
        try
        {
            await client.ConnectAsync();
            // The new methods only require an open socket (IsConnectedToGateway),
            // not the full hello-ok handshake. Poll readiness with a generous
            // ceiling so a load-starved runner doesn't cause a false failure;
            // the loop exits as soon as the socket is open. The Connected event
            // (hello-ok) is a fast-path signal but not required.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!client.IsConnectedToGateway && DateTime.UtcNow < deadline)
            {
                var completed = await Task.WhenAny(connected.Task, Task.Delay(100));
                if (completed == connected.Task) break;
            }
            // Give the socket a final moment to flip to Open if the event fired.
            for (var i = 0; i < 100 && !client.IsConnectedToGateway; i++)
                await Task.Delay(50);
            Assert.True(client.IsConnectedToGateway, "client did not reach connected state within timeout");
        }
        finally
        {
            client.StatusChanged -= OnStatus;
        }
    }

    private void PrintProof(LoopbackGatewayServer server)
    {
        _output.WriteLine("===== Gateway protocol live round-trip: captured request frames =====");
        foreach (var frame in server.AllFrames)
        {
            _output.WriteLine(frame);
            Console.WriteLine("[gateway-rx] " + frame);
        }
        _output.WriteLine("====================================================================");
    }

    /// <summary>
    /// Minimal real loopback WebSocket "gateway": accepts the client connection,
    /// records every request frame, and replies with a per-method payload. Uses
    /// the same HttpListener-on-127.0.0.1 pattern as the repo's other loopback
    /// test servers (no admin/urlacl needed for an explicit loopback prefix).
    /// </summary>
    private sealed class LoopbackGatewayServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Dictionary<string, Func<JsonElement, object>> _responders = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Method, string Frame)> _frames = new();

        public int Port { get; }
        public string WebSocketUrl => $"ws://127.0.0.1:{Port}/";

        public LoopbackGatewayServer()
        {
            // FindFreePort + HttpListener.Start has a TOCTOU race: another process
            // can grab the port between probe and bind, especially when many test
            // classes run in parallel. Retry on a fresh port a few times.
            Exception? last = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = FindFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = candidate;
                    _loop = Task.Run(AcceptLoopAsync);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { listener.Close(); } catch { /* ignore */ }
                }
            }
            throw new InvalidOperationException("Could not bind a loopback HttpListener after multiple attempts", last);
        }

        public void OnMethod(string method, Func<JsonElement, object> responder) => _responders[method] = responder;

        public IEnumerable<string> AllFrames
        {
            get
            {
                foreach (var f in _frames) yield return f.Frame;
            }
        }

        /// <summary>Returns the captured request frame for the Nth occurrence of a method.</summary>
        public string FrameFor(string method, int occurrence = 0)
        {
            if (TryGetFrame(method, occurrence, out var frame))
                return frame;
            throw new InvalidOperationException($"No captured frame for method '{method}' occurrence {occurrence}");
        }

        /// <summary>Waits for the Nth occurrence of a method's request frame to arrive.</summary>
        public async Task<string> WaitFrameAsync(string method, int occurrence, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (TryGetFrame(method, occurrence, out var frame))
                    return frame;
                await Task.Delay(25);
            }
            throw new InvalidOperationException($"Timed out waiting for method '{method}' occurrence {occurrence}");
        }

        private bool TryGetFrame(string method, int occurrence, out string frame)
        {
            var seen = 0;
            foreach (var f in _frames)
            {
                if (f.Method != method) continue;
                if (seen == occurrence) { frame = f.Frame; return true; }
                seen++;
            }
            frame = "";
            return false;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }

                _ = Task.Run(() => ServeAsync(ctx));
            }
        }

        private async Task ServeAsync(HttpListenerContext ctx)
        {
            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
            catch { return; }

            var socket = wsCtx.WebSocket;
            var buffer = new byte[16 * 1024];
            var sb = new StringBuilder();

            try
            {
                while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    await HandleFrameAsync(socket, sb.ToString());
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch { /* client closed */ }
        }

        private async Task HandleFrameAsync(WebSocket socket, string frame)
        {
            string? id = null, method = null;
            JsonElement parameters = default;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() != "req") return;
                id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
                if (root.TryGetProperty("params", out var p)) parameters = p.Clone();
            }
            catch { return; }

            if (method is null || id is null) return;

            _frames.Enqueue((method, frame));

            object payload = _responders.TryGetValue(method, out var responder)
                ? responder(parameters)
                : new { };

            var response = payload is LoopbackResponse loopbackResponse
                ? loopbackResponse.Ok
                    ? JsonSerializer.Serialize(new { type = "res", id, ok = true, payload = loopbackResponse.Payload })
                    : JsonSerializer.Serialize(new { type = "res", id, ok = false, error = loopbackResponse.Error })
                : JsonSerializer.Serialize(new { type = "res", id, ok = true, payload });
            var bytes = Encoding.UTF8.GetBytes(response);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }

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

    private sealed record LoopbackResponse(bool Ok, object? Payload = null, string? Error = null)
    {
        public static LoopbackResponse Fail(string error) => new(false, Error: error);
    }
}
