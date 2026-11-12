using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared.FileTransfer;
using Xunit;

namespace OpenClaw.Shared.Tests.FileTransfer;

public class FileFrameChannelTests
{
    // Literal vectors independently authored against upstream 82df040c.
    [Theory]
    [InlineData("{")]
    [InlineData("{\"v\":2,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"YWJj\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"YWJj\",\"extra\":true}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true}")]
    [InlineData("{\"v\":1,\"kind\":\"ready\",\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"cancel\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"bWVzc2FnZQ\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"YR==\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"%%%%\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":-1,\"index\":0,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":1,\"index\":0,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":1,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":9007199254740992,\"index\":0,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0.5,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":false,\"data\":\"YWJj\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":\"true\",\"data\":\"YWJj\"}")]
    [InlineData("{\"v\":1,\"v\":1,\"kind\":\"ready\"}")]
    public async Task InvalidFramesCloseBeforeDelivery(string frame)
    {
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        Assert.Throws<FileCommandException>(() => channel.Receive(frame));
        Assert.True(channel.Token.IsCancellationRequested);
        Assert.ThrowsAny<OperationCanceledException>(() => channel.Receive(""));
    }

    [Fact]
    public async Task ReadinessPrecedesBufferedCallbacksAndInputNeedsNoReciprocalReady()
    {
        var readyWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = Channel.CreateUnbounded<string>();
        await using var channel = new FileFrameChannel(async (frame, ct) =>
        {
            Assert.Equal("{\"v\":1,\"kind\":\"ready\"}", frame);
            readyWritten.SetResult();
            await readyAck.Task.WaitAsync(ct);
        });
        channel.Receive("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"c3RhcnQ=\"}");
        channel.Receive("");
        channel.Receive("{\"v\":1,\"kind\":\"data\",\"message\":1,\"index\":0,\"last\":true,\"data\":\"YWNr\"}");
        var subscribe = channel.ListenAsync((message, _) =>
        {
            received.Writer.TryWrite(Encoding.UTF8.GetString(message.Span));
            return ValueTask.CompletedTask;
        }, default).AsTask();
        await readyWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(received.Reader.TryRead(out _));
        readyAck.SetResult();
        await using var listener = await subscribe;
        Assert.Equal("start", await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ack", await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequiredReadinessAndDuplicateReadinessAreRejected()
    {
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask, requireReady: true);
        Assert.Throws<FileCommandException>(() => channel.Receive(Data(0, 0, true, [])));
        await using var duplicate = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        duplicate.Receive("{\"v\":1,\"kind\":\"ready\"}");
        Assert.Throws<FileCommandException>(() => duplicate.Receive("{\"v\":1,\"kind\":\"ready\"}"));
    }

    [Fact]
    public async Task IntegralJsonNumbersUseUpstreamJavascriptNumberSemantics()
    {
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        channel.Receive("{\"v\":1.0,\"kind\":\"data\",\"message\":0e0,\"index\":0.0,\"last\":true,\"data\":\"YWJj\"}");
        Assert.False(channel.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task IncompleteFragmentsCountAgainstPreListenerBudget()
    {
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        var bytes = new byte[8192];
        for (var i = 0; i < 128; i++) channel.Receive(Data(0, i, i == 127, bytes));
        Assert.Throws<FileCommandException>(() => channel.Receive(Data(1, 0, false, bytes)));
        await using var count = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        for (var i = 0; i < 8; i++) count.Receive(Data(i, 0, true, []));
        Assert.Throws<FileCommandException>(() => count.Receive(Data(8, 0, true, [])));
    }

    [Fact]
    public async Task EmptyNonfirstFinalFragmentAndOversizedWireFrameFail()
    {
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        channel.Receive(Data(0, 0, false, new byte[8192]));
        Assert.Throws<FileCommandException>(() => channel.Receive(Data(0, 1, true, [])));
        await using var oversized = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        Assert.Throws<FileCommandException>(() => oversized.Receive(new string(' ', 16385)));
    }

    [Fact]
    public async Task ConcurrentSendsAreSerializedAndSnapshotBytesBeforeAwait()
    {
        var sent = new List<string>();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new FileFrameChannel(async (frame, ct) =>
        {
            sent.Add(frame);
            if (sent.Count == 2) { blocked.SetResult(); await release.Task.WaitAsync(ct); }
        });
        await using var listener = await channel.ListenAsync((_, _) => ValueTask.CompletedTask, default);
        var bytes = Enumerable.Repeat((byte)7, 8193).ToArray();
        var first = channel.SendAsync(bytes, default).AsTask();
        await blocked.Task;
        bytes[^1] = 99;
        var second = channel.SendAsync(new byte[] { 8 }, default).AsTask();
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(4, sent.Count);
        using var final = JsonDocument.Parse(sent[2]);
        Assert.Equal("Bw==", final.RootElement.GetProperty("data").GetString());
        Assert.Equal(0, final.RootElement.GetProperty("message").GetInt64());
        using var next = JsonDocument.Parse(sent[3]);
        Assert.Equal(1, next.RootElement.GetProperty("message").GetInt64());
    }

    [Fact]
    public async Task StopDrainsActiveCallbackWithoutCancellingSuccessfulOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new FileFrameChannel((_, _) => ValueTask.CompletedTask);
        var subscription = await channel.ListenAsync(async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped.SetResult(); }
        }, default);
        channel.Receive(Data(0, 0, true, [1]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await subscription.DisposeAsync();
        Assert.True(stopped.Task.IsCompletedSuccessfully);
        Assert.False(channel.Token.IsCancellationRequested);
    }

    private static string Data(long message, int index, bool last, byte[] bytes) =>
        JsonSerializer.Serialize(new { v = 1, kind = "data", message, index, last, data = Convert.ToBase64String(bytes) });
}
