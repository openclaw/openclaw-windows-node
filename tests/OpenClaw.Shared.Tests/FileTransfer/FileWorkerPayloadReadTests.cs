using System.Text;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed class FileWorkerPayloadReadTests
{
    private const string AbcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void DefaultReaderHashesActualBytesWithoutOwningTheStream()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var digest = FileWorkerPayloadHash.Compute(stream, new FileWorkerPayloadReader(), CancellationToken.None);
        Assert.Equal(AbcSha, Convert.ToHexStringLower(digest));
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.Equal((int)'a', stream.ReadByte());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationInsideDataReadOrEofMustBeObservedAfterHash(bool atEof)
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var reader = new CancelingPayloadReader(cancel, atEof);
        var error = Assert.Throws<OperationCanceledException>(() =>
            FileWorkerPayloadHash.Compute(stream, reader, cancel.Token));
        Assert.True(reader.ReadCalls > 0);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void AlreadyCanceledVerificationDoesNotReadAnyPayloadBytes()
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var reader = new CancelingPayloadReader(cancel, atEof: false);
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => FileWorkerPayloadHash.Compute(stream, reader, cancel.Token));
        Assert.Equal(0, reader.ReadCalls);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ExistingReadFailureIsNotConvertedIntoCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var failure = new IOException("Controlled external read failure.");
        Assert.Same(failure, Assert.Throws<IOException>(() => FileWorkerPayloadHash.Compute(
            stream, new FailingPayloadReader(failure), CancellationToken.None)));
        Assert.True(stream.CanRead);
    }

    private sealed class CancelingPayloadReader(CancellationTokenSource cancel, bool atEof) : IFileWorkerPayloadReader
    {
        internal int ReadCalls { get; private set; }
        public int Read(Stream pinnedStream, Span<byte> buffer)
        {
            var count = pinnedStream.Read(buffer);
            ReadCalls++;
            if (atEof ? count == 0 : count > 0) cancel.Cancel();
            return count;
        }
    }

    private sealed class FailingPayloadReader(IOException failure) : IFileWorkerPayloadReader
    {
        public int Read(Stream pinnedStream, Span<byte> buffer) => throw failure;
    }
}
