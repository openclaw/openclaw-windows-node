using System.Text;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class CommandOutputCaptureTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf16be")]
    [InlineData("utf32")]
    [InlineData("utf32be")]
    public async Task FragmentedBomsAndSurrogatesUseTheSameCollector(string name)
    {
        var encoding = name switch
        {
            "utf16" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode,
            "utf32" => new UTF32Encoding(false, true), "utf32be" => new UTF32Encoding(true, true),
            _ => new UTF8Encoding(true),
        };
        var text = "界🙂é\0a";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var asyncCapture = new CommandOutputCapture.BoundedStream(100);
        using var asyncStream = new FragmentedStream(bytes);
        await asyncCapture.CollectAsync(asyncStream);
        var syncCapture = new CommandOutputCapture.BoundedStream(100);
        using var syncStream = new FragmentedStream(bytes);
        syncCapture.Collect(syncStream);
        Assert.Equal(text, asyncCapture.Snapshot());
        Assert.Equal(text, syncCapture.Snapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(64)]
    public async Task FloodIsDrainedAndEverySnapshotFitsUtf8Budget(int budget)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x界🙂é", 10_000)));
        using var stream = new MemoryStream(bytes);
        var capture = new CommandOutputCapture.BoundedStream(budget);
        await capture.CollectAsync(stream);
        var result = capture.Snapshot();
        Assert.True(Encoding.UTF8.GetByteCount(result) <= budget);
        Assert.DoesNotContain("\uFFFD", result);
        Assert.Equal(bytes.Length, stream.Position);
        if (budget >= 12) Assert.EndsWith("\n[truncated]", result);
        else if (budget > 0) Assert.StartsWith("x", result);
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("ab", 4)]
    [InlineData("abc", 64)]
    public async Task CompleteShortTextIsVisibleBeforeEof(string text, int budget)
    {
        using var stream = new PausedStream(Encoding.UTF8.GetBytes(text));
        using var stop = new CancellationTokenSource();
        var capture = new CommandOutputCapture.BoundedStream(budget);
        var reader = capture.CollectAsync(stream, stop.Token);
        try
        {
            await stream.Paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(text, capture.Snapshot());
            Assert.False(reader.IsCompleted);
            var terminal = capture.Snapshot(incomplete: true);
            Assert.StartsWith(text, terminal);
            Assert.True(Encoding.UTF8.GetByteCount(terminal) <= budget);
        }
        finally
        {
            await stop.CancelAsync();
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xef })]
    [InlineData(new byte[] { 0xef, 0xbb })]
    [InlineData(new byte[] { 0xff, 0xfe, 0 })]
    [InlineData(new byte[] { 0, 0, 0xfe })]
    [InlineData(new byte[] { 0xe7, 0x95 })]
    [InlineData(new byte[] { 0xff, 0xfe, 0x3d, 0xd8 })]
    public async Task DeadlineDoesNotInventBytesFromIncompleteBomsOrCharacters(byte[] prefix)
    {
        using var stream = new PausedStream(prefix);
        using var stop = new CancellationTokenSource();
        var capture = new CommandOutputCapture.BoundedStream(64);
        var reader = capture.CollectAsync(stream, stop.Token);
        await stream.Paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(capture.Snapshot());
        await stop.CancelAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("\n[truncated]", capture.Snapshot());
    }

    [Fact]
    public async Task InvalidUtf8AtRealEofUsesDecoderReplacementWithinBudget()
    {
        using var stream = new MemoryStream([0xe7, 0x95]);
        var capture = new CommandOutputCapture.BoundedStream(4);
        await capture.CollectAsync(stream);
        Assert.Equal("\uFFFD", capture.Snapshot());
    }

    [Fact]
    public void KnownRootExitWinsDeadlineWithoutRequiringOutputEof()
    {
        var capture = new CommandOutputCapture(64);
        capture.BindRootExit(Task.CompletedTask, () => (7, false));
        var result = capture.Snapshot(NodeToolExecutionMode.Host, deadlineExpired: true);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(NodeToolErrorCategory.CommandFailed, result.ErrorCategory);
        Assert.Equal("\n[truncated]", result.Stdout);
    }

    [Fact]
    public void EmptyDeadlineUsesCutoffMarkerWithoutInventingOutputOrExceedingBudget()
    {
        var capture = new CommandOutputCapture(12);
        var result = capture.Snapshot(NodeToolExecutionMode.Sandbox, deadlineExpired: true);
        Assert.True(result.TimedOut);
        Assert.Equal("\n[truncated]", result.Stdout);
        Assert.Equal("\n[truncated]", result.Stderr);
        Assert.Equal(12, Encoding.UTF8.GetByteCount(result.Stderr));
    }

    private sealed class PausedStream(byte[] prefix) : MemoryStream(prefix)
    {
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            Paused.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
