using System.Security.Cryptography;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>
/// Synchronous read boundary over a stream whose identity is already pinned by
/// the resolver. Readers do not own the stream, grant access or supply a digest.
/// </summary>
internal interface IFileWorkerPayloadReader
{
    int Read(Stream pinnedStream, Span<byte> buffer);
}

internal sealed class FileWorkerPayloadReader : IFileWorkerPayloadReader
{
    public int Read(Stream pinnedStream, Span<byte> buffer) => pinnedStream.Read(buffer);
}

/// <summary>
/// Hashes retained payload bytes without releasing their native lease. For example,
/// the resolver verifies both each payload file and the installed manifest here.
/// Cancellation brackets the bounded synchronous hash, not individual kernel IO.
/// </summary>
internal static class FileWorkerPayloadHash
{
    internal static byte[] Compute(Stream pinnedStream, IFileWorkerPayloadReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var view = new ReadView(pinnedStream, reader);
        var digest = SHA256.HashData(view);
        ct.ThrowIfCancellationRequested();
        return digest;
    }

    // Disposal of this non-owning view must never close the resolver's pinned stream.
    private sealed class ReadView(Stream pinnedStream, IFileWorkerPayloadReader reader) : Stream
    {
        public override bool CanRead => pinnedStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(pinnedStream, buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => reader.Read(pinnedStream, buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
