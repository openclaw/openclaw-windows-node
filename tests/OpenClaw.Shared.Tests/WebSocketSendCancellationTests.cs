using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Xunit;

namespace OpenClaw.Shared.Tests;

[Collection("WebSocketClientBase")]
public sealed class WebSocketSendCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationCannotAbortAnInProgressSharedFrame(bool cancelWhileQueued)
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var tcp = new TcpClient();
        GatedWriteStream? stream = null;
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                await tcp.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                stream = new(tcp.GetStream());
                return stream;
            }
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var socket = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(Deadline);
        await socket.ConnectAsync(new(server.WebSocketUrl), invoker, deadline.Token);
        using var client = new SendClient(server.WebSocketUrl);
        // Supply a real connected socket with a controllable network write, not a fake SendAsync.
        typeof(WebSocketClientBase).GetField("_webSocket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, socket);
        var sendLock = (SemaphoreSlim)typeof(WebSocketClientBase)
            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        using var caller = new CancellationTokenSource();
        Assert.NotNull(stream);
        if (cancelWhileQueued) await sendLock.WaitAsync(deadline.Token);
        else stream.BlockNextWrite();
        try
        {
            var sending = client.SendAsync("search frame", caller.Token);
            if (!cancelWhileQueued) await stream.WriteStarted.Task.WaitAsync(Deadline);
            caller.Cancel();
            stream.ReleaseWrite();
            Assert.Equal(!cancelWhileQueued, await sending.WaitAsync(Deadline));
        }
        finally
        {
            stream.ReleaseWrite();
            if (cancelWhileQueued) sendLock.Release();
        }
        if (!cancelWhileQueued)
            Assert.Equal("search frame", await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.True(await client.SendAsync("unrelated health frame", deadline.Token));
        Assert.Equal("unrelated health frame", await server.ReceiveTextAsync().WaitAsync(Deadline));
    }

    [Fact]
    public async Task ClientDisposalStillCancelsABlockedTransportWrite()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var tcp = new TcpClient();
        GatedWriteStream? stream = null;
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                await tcp.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                stream = new(tcp.GetStream());
                return stream;
            }
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var socket = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(Deadline);
        await socket.ConnectAsync(new(server.WebSocketUrl), invoker, deadline.Token);
        using var client = new SendClient(server.WebSocketUrl);
        typeof(WebSocketClientBase).GetField("_webSocket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, socket);
        Assert.NotNull(stream);
        stream.BlockNextWrite();
        try
        {
            var sending = client.SendAsync("blocked frame", CancellationToken.None);
            await stream.WriteStarted.Task.WaitAsync(Deadline);
            client.Dispose();
            Assert.False(await sending.WaitAsync(Deadline));
        }
        finally { stream.ReleaseWrite(); }
    }

    private sealed class SendClient(string url) : WebSocketClientBase(url, "test-value")
    {
        protected override int ReceiveBufferSize => 8192;
        protected override string ClientRole => "send-cancellation";
        protected override bool ShouldAutoReconnect() => false;
        protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;
        internal Task<bool> SendAsync(string message, CancellationToken ct) =>
            SendRawAsync(message, ConnectionGeneration, ct);
    }

    private sealed class GatedWriteStream(Stream inner) : Stream
    {
        private bool _block;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void BlockNextWrite() => _block = true;
        internal void ReleaseWrite() => _release.TrySetResult();
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_block)
            {
                _block = false;
                WriteStarted.TrySetResult();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                await _release.Task.WaitAsync(wait.Token);
            }
            await inner.WriteAsync(buffer, ct);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Cancel();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
