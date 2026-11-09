using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Telemetry;
using Xunit;

namespace OpenClaw.Shared.Tests.FileTransfer;

public class GatewayFileTransportTests
{
    [Theory]
    [InlineData("fixture.result", false)]
    [InlineData("file.stat", true)]
    public async Task ResultSerializationPreservesGenericContractAndReturnsTypedFileErrors(string command, bool file)
    {
        await using var gateway = await SocketFixture.Start();
        using var deep = JsonDocument.Parse(new string('[', 40) + "0" + new string(']', 40));
        gateway.Node.RegisterCapability(new ResultCapability(command,
            new { Nested = deep.RootElement.Clone(), Pair = new KeyValuePair<string, string>("key", "<value>") }));
        await gateway.Invoke("result", command: command, args: "{}");
        var result = (await gateway.Read()).GetProperty("params");
        Assert.Equal(!file, result.GetProperty("ok").GetBoolean());
        if (file)
            Assert.Equal("INVALID_RESULT", result.GetProperty("error").GetProperty("code").GetString());
        else
        {
            Assert.Equal("key", result.GetProperty("payload").GetProperty("Pair").GetProperty("Key").GetString());
            Assert.Equal("<value>", result.GetProperty("payload").GetProperty("Pair").GetProperty("Value").GetString());
            Assert.Equal(JsonValueKind.Array, result.GetProperty("payload").GetProperty("Nested").ValueKind);
        }
        await gateway.HealthBarrier();
    }

    [Fact]
    public async Task OversizedUnaryFileResultIsDeliveredAsAnExplicitError()
    {
        await using var gateway = await SocketFixture.Start();
        gateway.Node.RegisterCapability(new ResultCapability("file.stat", new string('x', FileTransportJson.ResultEnvelopeBytes)));
        await gateway.Invoke("oversized", command: "file.stat", args: "{}");
        var result = (await gateway.Read()).GetProperty("params");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("FILE_TOO_LARGE", result.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidFileValuesReturnErrorsOnBothInvokeEnvelopes(bool legacy, bool undefined)
    {
        await using var gateway = await SocketFixture.Start();
        gateway.Node.RegisterCapability(new ResultCapability("dir.list", undefined ? default(JsonElement) : double.NaN));
        if (legacy)
            await gateway.Send(new { type = "req", id = "invalid", method = "node.invoke", @params = new { command = "dir.list", args = new { } } });
        else await gateway.Invoke("invalid", command: "dir.list", args: "{}");
        var envelope = await gateway.Read();
        var result = legacy ? envelope : envelope.GetProperty("params");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("INVALID_RESULT", result.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericGatewayResultsAreNotSubjectToTheFileEnvelopeCap()
    {
        await using var gateway = await SocketFixture.Start();
        var value = new string('x', FileTransportJson.ResultEnvelopeBytes);
        gateway.Node.RegisterCapability(new ResultCapability("fixture.result", value));
        await gateway.Invoke("large-generic", command: "fixture.result", args: "{}");
        var result = (await gateway.Read()).GetProperty("params");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(value, result.GetProperty("payload").GetString());
    }

    private sealed class ResultCapability(string command, object payload) : INodeCapability
    {
        public string Category => "fixture";
        public IReadOnlyList<string> Commands => [command];
        public bool CanHandle(string value) => value == command;
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = payload });
    }

    [Fact]
    public async Task RealSocketUsesCorrelatedProgressAndTypedInputWithoutBlockingHealth()
    {
        await using var gateway = await SocketFixture.Start();
        var capability = new FetchCapability();
        gateway.Node.RegisterCapability(capability);
        var completion = new TaskCompletionSource<NodeToolTelemetryCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Node.ToolTelemetryCompleted += (_, value) => completion.TrySetResult(value);
        await gateway.Invoke("transfer");
        var ready = await gateway.Read();
        Assert.Equal("node.invoke.progress", ready.GetProperty("method").GetString());
        Assert.Equal("transfer", ready.GetProperty("params").GetProperty("invokeId").GetString());
        Assert.Equal(0, ready.GetProperty("params").GetProperty("seq").GetInt64());
        Assert.Equal("{\"v\":1,\"kind\":\"ready\"}", ready.GetProperty("params").GetProperty("chunk").GetString());

        await gateway.Input("transfer", "not-this-node", 0, "c3RhcnQ=");
        await gateway.HealthBarrier();
        Assert.False(capability.Started.Task.IsCompleted);
        await gateway.Input("transfer", gateway.Node.FullDeviceId, 0, "c3RhcnQ=");
        await gateway.HealthBarrier();
        Assert.False(capability.Started.Task.IsCompleted); // Listener waits for readiness RPC acknowledgment.
        await gateway.Node.DeliverStaleAck(ready.GetProperty("id").GetString()!);
        await gateway.HealthBarrier();
        Assert.False(capability.Started.Task.IsCompleted);
        await gateway.Ack(ready);
        var data = await gateway.Read();
        Assert.Equal(1, data.GetProperty("params").GetProperty("seq").GetInt64());
        using var frame = JsonDocument.Parse(data.GetProperty("params").GetProperty("chunk").GetString()!);
        Assert.Equal("YWJj", frame.RootElement.GetProperty("data").GetString());
        Assert.Equal(0, frame.RootElement.GetProperty("message").GetInt64());
        await gateway.HealthBarrier();
        Assert.False(capability.DataAcknowledged.Task.IsCompleted);
        await gateway.Ack(data);
        await gateway.Input("transfer", gateway.Node.FullDeviceId, 1, "YWNr", message: 1);
        var result = await gateway.Read();
        Assert.Equal("node.invoke.result", result.GetProperty("method").GetString());
        var payload = result.GetProperty("params");
        Assert.Equal("transfer", payload.GetProperty("id").GetString());
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.False(payload.GetProperty("payload").GetProperty("ok").GetBoolean());
        Assert.Equal(NodeToolOutcome.Failure, (await completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [Fact]
    public async Task MatchingCancelStopsInvocationWhileWrongNodeDoesNot()
    {
        await using var gateway = await SocketFixture.Start();
        var capability = new FetchCapability();
        gateway.Node.RegisterCapability(capability);
        await gateway.Invoke("cancel");
        var ready = await gateway.Read();
        await gateway.Ack(ready);
        await gateway.Send(new { type = "event", @event = "node.invoke.cancel", payload = new { invokeId = "cancel", nodeId = "wrong" } });
        await gateway.HealthBarrier();
        Assert.False(capability.Cancelled.Task.IsCompleted);
        await gateway.Send(new { type = "event", @event = "node.invoke.cancel", payload = new { invokeId = "cancel", nodeId = gateway.Node.FullDeviceId } });
        var result = await gateway.Read();
        Assert.False(result.GetProperty("params").GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", result.GetProperty("params").GetProperty("error").GetProperty("message").GetString());
        await capability.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BinaryFetchPipelinesSixteenFramesAndWaitsForEveryCorrelatedAck()
    {
        await using var gateway = await SocketFixture.Start();
        var bytes = Enumerable.Range(0, 49 * 8192).Select(i => (byte)i).ToArray();
        var capability = new FetchCapability(bytes);
        gateway.Node.RegisterCapability(capability);
        await gateway.Invoke("window");
        await gateway.Ack(await gateway.Read());
        await gateway.Input("window", gateway.Node.FullDeviceId, 0, "c3RhcnQ=");
        using var reconstructed = new MemoryStream();
        long sequence = 1;
        var index = 0;
        while (index < 49)
        {
            var frames = new List<JsonElement>();
            for (var i = 0; i < Math.Min(16, 49 - index); i++)
            {
                var progress = await gateway.Read().WaitAsync(TimeSpan.FromSeconds(3));
                var payload = progress.GetProperty("params");
                Assert.Equal(sequence++, payload.GetProperty("seq").GetInt64());
                using var frame = JsonDocument.Parse(payload.GetProperty("chunk").GetString()!);
                Assert.Equal(index + i, frame.RootElement.GetProperty("index").GetInt32());
                Assert.Equal(0, frame.RootElement.GetProperty("message").GetInt64());
                Assert.Equal(index + i == 48, frame.RootElement.GetProperty("last").GetBoolean());
                reconstructed.Write(Convert.FromBase64String(frame.RootElement.GetProperty("data").GetString()!));
                frames.Add(progress);
            }
            await gateway.HealthBarrier();
            Assert.False(capability.DataAcknowledged.Task.IsCompleted);
            foreach (var frame in frames.AsEnumerable().Reverse()) await gateway.Ack(frame);
            index += frames.Count;
        }
        await capability.DataAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(bytes, reconstructed.ToArray());
        await gateway.Input("window", gateway.Node.FullDeviceId, 1, "YWNr", message: 1);
        Assert.Equal("node.invoke.result", (await gateway.Read()).GetProperty("method").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task RejectedFrameInWindowStopsTransferAndLeavesGatewayResponsive(int rejected)
    {
        await using var gateway = await SocketFixture.Start();
        var capability = new FetchCapability(new byte[17 * 8192]);
        gateway.Node.RegisterCapability(capability);
        await gateway.Invoke("reject-window");
        await gateway.Ack(await gateway.Read());
        await gateway.Input("reject-window", gateway.Node.FullDeviceId, 0, "c3RhcnQ=");
        var frames = new List<JsonElement>();
        for (var i = 0; i < 16; i++) frames.Add(await gateway.Read());
        for (var i = 15; i >= 0; i--)
        {
            if (i == rejected)
                await gateway.Send(new { type = "res", id = frames[i].GetProperty("id").GetString(), ok = false });
            else await gateway.Ack(frames[i]);
        }
        var result = await gateway.Read();
        Assert.Equal("node.invoke.result", result.GetProperty("method").GetString());
        Assert.False(result.GetProperty("params").GetProperty("ok").GetBoolean());
        Assert.False(capability.DataAcknowledged.Task.IsCompleted);
        Assert.True(gateway.Node.IsConnected);
        await gateway.HealthBarrier();
    }

    [Fact]
    public async Task RejectedProgressAckFailsOperationWithoutHandshakeFailure()
    {
        await using var gateway = await SocketFixture.Start();
        var capability = new FetchCapability();
        gateway.Node.RegisterCapability(capability);
        await gateway.Invoke("reject");
        var ready = await gateway.Read();
        await gateway.Send(new { type = "res", id = ready.GetProperty("id").GetString(), ok = false, error = new { code = "INVALID_REQUEST", message = "fixture rejected progress" } });
        var result = await gateway.Read();
        Assert.Equal("node.invoke.result", result.GetProperty("method").GetString());
        Assert.False(result.GetProperty("params").GetProperty("ok").GetBoolean());
        Assert.True(gateway.Node.IsConnected);
        await gateway.HealthBarrier();
    }

    [Fact]
    public async Task HeartbeatsShareProgressSequenceAndDisposalCancelsPendingListener()
    {
        await using var gateway = await SocketFixture.Start();
        var capability = new FetchCapability();
        gateway.Node.RegisterCapability(capability);
        await gateway.Invoke("heartbeat");
        await gateway.Ack(await gateway.Read());
        var heartbeat = await gateway.Read();
        Assert.Equal("node.invoke.progress", heartbeat.GetProperty("method").GetString());
        Assert.Equal("", heartbeat.GetProperty("params").GetProperty("chunk").GetString());
        Assert.Equal(1, heartbeat.GetProperty("params").GetProperty("seq").GetInt64());
        // Leave its RPC acknowledgment pending while disposing the transport.
        gateway.Node.Dispose();
        await capability.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnvelopeDeadlineReturnsErrorAndKeepsHealthResponsive()
    {
        await using var gateway = await SocketFixture.Start();
        gateway.Node.RegisterCapability(new FetchCapability());
        await gateway.Invoke("deadline", 1000);
        await gateway.Ack(await gateway.Read());
        var result = await gateway.Read();
        Assert.Equal("node.invoke.result", result.GetProperty("method").GetString());
        Assert.False(result.GetProperty("params").GetProperty("ok").GetBoolean());
        Assert.Contains("timed out", result.GetProperty("params").GetProperty("error").GetProperty("message").GetString());
        await gateway.HealthBarrier();
    }

    private sealed class FetchCapability(byte[]? bytes = null) : INodeCapability
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DataAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Category => "file";
        public IReadOnlyList<string> Commands => ["file.fetch"];
        public bool CanHandle(string command) => command == "file.fetch";
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) => ExecuteAsync(request, default);
        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken ct)
        {
            try
            {
                var messages = Channel.CreateBounded<string>(1);
                await using var listener = await request.FileChannel!.ListenAsync((message, _) =>
                {
                    messages.Writer.TryWrite(Encoding.UTF8.GetString(message.Span));
                    return ValueTask.CompletedTask;
                }, ct);
                Assert.Equal("start", await messages.Reader.ReadAsync(ct));
                Started.TrySetResult();
                await request.FileChannel.SendAsync(bytes ?? "abc"u8.ToArray(), ct);
                DataAcknowledged.TrySetResult();
                Assert.Equal("ack", await messages.Reader.ReadAsync(ct));
                return new()
                {
                    Ok = true, Payload = new { ok = false, code = "NOT_FOUND", message = "Synthetic failure after transport exchange" },
                    FileOutcome = new(false, "NOT_FOUND", "Synthetic failure after transport exchange")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A pending read can unwind and dispose a scoped token callback before
                // cancellation invokes it. Observe the operation's cancellation instead.
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class SocketFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, $".file-socket-{Guid.NewGuid():N}");
        private readonly Channel<JsonElement> _received = Channel.CreateUnbounded<JsonElement>();
        private WebSocket _socket = null!;
        private Task _readLoop = Task.CompletedTask;
        public GenerationNode Node { get; private set; } = null!;
        public static async Task<SocketFixture> Start()
        {
            var fixture = new SocketFixture();
            Directory.CreateDirectory(fixture._directory);
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            fixture._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            fixture._listener.Start();
            fixture.Node = new GenerationNode($"ws://127.0.0.1:{port}/", fixture._directory);
            var connected = fixture.Node.ConnectAsync();
            var context = await fixture._listener.GetContextAsync().WaitAsync(fixture._stop.Token);
            fixture._socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            fixture._readLoop = fixture.Pump();
            await connected;
            var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Node.HandshakeSucceeded += (_, _) => handshake.TrySetResult();
            await fixture.Send(new { type = "event", @event = "connect.challenge", payload = new { nonce = "fixture-nonce", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
            var connect = await fixture.Read();
            Assert.Equal("connect", connect.GetProperty("method").GetString());
            await fixture.Send(new { type = "res", id = connect.GetProperty("id").GetString(), ok = true, payload = new { type = "hello-ok", protocol = 4 } });
            await handshake.Task.WaitAsync(fixture._stop.Token);
            return fixture;
        }
        public Task Invoke(string id, int timeoutMs = 20000, string command = "file.fetch", string args = "{\"transport\":\"binary\"}") => Send(new
        {
            type = "event", @event = "node.invoke.request",
            payload = new { id, nodeId = Node.FullDeviceId, command, paramsJSON = args, timeoutMs }
        });
        public Task Input(string id, string nodeId, long seq, string data, long message = 0) => Send(new
        {
            type = "event", @event = "node.invoke.input",
            payload = new { id, nodeId, seq, payloadJSON = JsonSerializer.Serialize(new { v = 1, kind = "data", message, index = 0, last = true, data }) }
        });
        public async Task HealthBarrier()
        {
            var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, JsonElement value) => receipt.TrySetResult();
            Node.HealthReceived += Handler;
            try { await Send(new { type = "event", @event = "health", payload = new { } }); await receipt.Task.WaitAsync(_stop.Token); }
            finally { Node.HealthReceived -= Handler; }
        }
        public Task Ack(JsonElement message) => Send(new { type = "res", id = message.GetProperty("id").GetString(), ok = true, payload = new { accepted = true } });
        public Task<JsonElement> Read() => _received.Reader.ReadAsync(_stop.Token).AsTask();
        public async Task Send(object message) =>
            await _socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, _stop.Token);
        private async Task Pump()
        {
            var buffer = new byte[65536];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var bytes = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(buffer, _stop.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                        bytes.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    using var doc = JsonDocument.Parse(bytes.ToArray());
                    await _received.Writer.WriteAsync(doc.RootElement.Clone(), _stop.Token);
                }
            }
            catch (Exception ex) { _received.Writer.TryComplete(ex); }
        }
        public async ValueTask DisposeAsync()
        {
            Node?.Dispose();
            _stop.Cancel();
            _socket?.Abort();
            await _readLoop;
            _socket?.Dispose();
            _listener.Close();
            Directory.Delete(_directory, recursive: true);
            _stop.Dispose();
        }
    }

    private sealed class GenerationNode(string url, string directory) : WindowsNodeClient(url, "fixture-token", directory)
    {
        public Task DeliverStaleAck(string id) => ProcessMessageForConnectionAsync(
            JsonSerializer.Serialize(new { type = "res", id, ok = true }), CurrentConnectionGeneration - 1);
    }
}
