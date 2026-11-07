using System.Buffers.Binary;
using System.Text.Json;

namespace OpenClaw.Shared.FileTransfer;

public enum FilePacketKind : byte { Request = 1, Message = 2, Result = 3, Listen = 4 }
public sealed record FilePacket(FilePacketKind Kind, byte[] Bytes);

/// <summary>Private process protocol. Exactly one request/result; opaque messages are never text-captured.</summary>
public static class FileWorkerProtocol
{
    public const int Version = 1;
    private const uint Magic = 0x3146434F; // OCF1, little endian
    public const int HeaderBytes = 9;

    public static void Write(Stream stream, FilePacketKind kind, ReadOnlySpan<byte> bytes)
    {
        Validate(kind, bytes.Length);
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Flush();
    }

    public static FilePacket Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("Unknown file protocol version.");
        var kind = (FilePacketKind)header[4];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[5..]);
        Validate(kind, length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        stream.ReadExactly(bytes);
        return new(kind, bytes);
    }

    public static async Task<FilePacket> ReadAsync(Stream stream, CancellationToken stop)
    {
        var header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, stop).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("Unknown file protocol version.");
        var kind = (FilePacketKind)header[4];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
        Validate(kind, length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(bytes, stop).ConfigureAwait(false);
        return new(kind, bytes);
    }

    public static async Task WriteAsync(Stream stream, FilePacket packet, CancellationToken stop)
    {
        Validate(packet.Kind, packet.Bytes.Length);
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = (byte)packet.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), packet.Bytes.Length);
        await stream.WriteAsync(header, stop).ConfigureAwait(false);
        await stream.WriteAsync(packet.Bytes, stop).ConfigureAwait(false);
        await stream.FlushAsync(stop).ConfigureAwait(false);
    }

    public static void WriteJson<T>(Stream stream, FilePacketKind kind, T value) =>
        Write(stream, kind, JsonSerializer.SerializeToUtf8Bytes(value, FileJson.Options));

    public static T Json<T>(FilePacket packet) =>
        JsonSerializer.Deserialize<T>(packet.Bytes, FileJson.Options) ?? throw new InvalidDataException("Missing file protocol payload.");

    private static void Validate(FilePacketKind kind, int length)
    {
        var limit = kind switch
        {
            FilePacketKind.Request => FileLimits.RequestBytes,
            FilePacketKind.Result => FileLimits.ResultBytes,
            FilePacketKind.Message => FileLimits.MessageBytes,
            FilePacketKind.Listen => 0,
            _ => throw new InvalidDataException("Unknown file packet.")
        };
        if (length < 0 || length > limit) throw new InvalidDataException("File packet exceeds its bound.");
    }
}
