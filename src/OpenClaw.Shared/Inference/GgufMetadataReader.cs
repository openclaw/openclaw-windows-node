using System.Buffers.Binary;
using System.Text;

namespace OpenClaw.Shared.Inference;

internal sealed record GgufModelMetadata(
    string Architecture,
    string? Name,
    long BlockCount,
    long? ContextLength,
    long KeyValueHeadCount,
    long KeyValueHeadDimension);

/// <summary>
/// Streams only the GGUF key/value header (never tensor data) to recover the attention
/// geometry the capacity estimate needs. Works on non-seekable HTTP response streams.
/// </summary>
internal static class GgufMetadataReader
{
    private const int MaximumStringBytes = 1024 * 1024;
    private const long MaximumHeaderBytes = 64L * 1024 * 1024;
    private const string Unreadable = "The file is not a readable GGUF model.";
    private const string MissingLayout =
        "The GGUF file does not describe its attention layout, so its memory use cannot be estimated.";

    private enum ValueType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6,
        Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12,
    }

    public static async Task<GgufModelMetadata> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new Reader(stream);
        try
        {
            if (await reader.ReadUInt32Async(ct).ConfigureAwait(false) != 0x46554747) // "GGUF"
                throw new InvalidDataException(Unreadable);
            uint version = await reader.ReadUInt32Async(ct).ConfigureAwait(false);
            if (version is not (2 or 3))
                throw new InvalidDataException(Unreadable);
            _ = await reader.ReadUInt64Async(ct).ConfigureAwait(false);
            ulong kvCount = await reader.ReadUInt64Async(ct).ConfigureAwait(false);
            if (kvCount > 1_000_000)
                throw new InvalidDataException(Unreadable);

            string? architecture = null;
            string? name = null;
            var integers = new Dictionary<string, long>(StringComparer.Ordinal);
            for (ulong i = 0; i < kvCount; i++)
            {
                string key = await reader.ReadStringAsync(ct).ConfigureAwait(false);
                var type = (ValueType)await reader.ReadUInt32Async(ct).ConfigureAwait(false);
                if (type == ValueType.String)
                {
                    string value = await reader.ReadStringAsync(ct).ConfigureAwait(false);
                    if (key == "general.architecture") architecture = value;
                    else if (key == "general.name") name = value;
                    continue;
                }
                long? integer = await ReadValueAsync(reader, type, depth: 0, ct).ConfigureAwait(false);
                if (integer is { } number)
                    integers[key] = number;
            }

            if (string.IsNullOrWhiteSpace(architecture))
                throw new InvalidDataException(MissingLayout);
            long? Get(string suffix) => integers.TryGetValue($"{architecture}.{suffix}", out long v) ? v : null;

            long blockCount = Get("block_count") ?? throw new InvalidDataException(MissingLayout);
            long? headCount = Get("attention.head_count");
            long kvHeads = Get("attention.head_count_kv") ?? headCount ?? throw new InvalidDataException(MissingLayout);
            long? keyLength = Get("attention.key_length");
            long? valueLength = Get("attention.value_length");
            long headDimension = keyLength is not null || valueLength is not null
                ? Math.Max(keyLength ?? 0, valueLength ?? 0)
                : Get("embedding_length") is { } embedding && headCount is > 0
                    ? embedding / headCount.Value
                    : throw new InvalidDataException(MissingLayout);
            if (blockCount <= 0 || kvHeads <= 0 || headDimension <= 0)
                throw new InvalidDataException(MissingLayout);
            return new GgufModelMetadata(architecture, name, blockCount, Get("context_length"), kvHeads, headDimension);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(Unreadable, ex);
        }
    }

    /// <summary>Returns integer scalars and the maximum of integer arrays; skips everything else.</summary>
    private static async Task<long?> ReadValueAsync(Reader reader, ValueType type, int depth, CancellationToken ct)
    {
        switch (type)
        {
            case ValueType.UInt8: return (await reader.ReadBytesAsync(1, ct).ConfigureAwait(false))[0];
            case ValueType.Int8: return (sbyte)(await reader.ReadBytesAsync(1, ct).ConfigureAwait(false))[0];
            case ValueType.UInt16: return BinaryPrimitives.ReadUInt16LittleEndian(await reader.ReadBytesAsync(2, ct).ConfigureAwait(false));
            case ValueType.Int16: return BinaryPrimitives.ReadInt16LittleEndian(await reader.ReadBytesAsync(2, ct).ConfigureAwait(false));
            case ValueType.UInt32: return await reader.ReadUInt32Async(ct).ConfigureAwait(false);
            case ValueType.Int32: return BinaryPrimitives.ReadInt32LittleEndian(await reader.ReadBytesAsync(4, ct).ConfigureAwait(false));
            case ValueType.UInt64:
                ulong u64 = await reader.ReadUInt64Async(ct).ConfigureAwait(false);
                return u64 > long.MaxValue ? long.MaxValue : (long)u64;
            case ValueType.Int64: return BinaryPrimitives.ReadInt64LittleEndian(await reader.ReadBytesAsync(8, ct).ConfigureAwait(false));
            case ValueType.Float32: await reader.ReadBytesAsync(4, ct).ConfigureAwait(false); return null;
            case ValueType.Float64: await reader.ReadBytesAsync(8, ct).ConfigureAwait(false); return null;
            case ValueType.Bool: await reader.ReadBytesAsync(1, ct).ConfigureAwait(false); return null;
            case ValueType.String: await reader.ReadStringAsync(ct).ConfigureAwait(false); return null;
            case ValueType.Array:
                if (depth >= 2)
                    throw new InvalidDataException(Unreadable);
                var elementType = (ValueType)await reader.ReadUInt32Async(ct).ConfigureAwait(false);
                ulong count = await reader.ReadUInt64Async(ct).ConfigureAwait(false);
                long? maximum = null;
                for (ulong i = 0; i < count; i++)
                {
                    long? element = await ReadValueAsync(reader, elementType, depth + 1, ct).ConfigureAwait(false);
                    if (element is { } value && (maximum is null || value > maximum))
                        maximum = value;
                }
                return maximum;
            default:
                throw new InvalidDataException(Unreadable);
        }
    }

    private sealed class Reader(Stream stream)
    {
        private readonly byte[] _scratch = new byte[8];
        private long _consumed;

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
        {
            byte[] buffer = count <= _scratch.Length ? _scratch : new byte[count];
            await FillAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            return buffer;
        }

        public async Task<uint> ReadUInt32Async(CancellationToken ct) =>
            BinaryPrimitives.ReadUInt32LittleEndian(await ReadBytesAsync(4, ct).ConfigureAwait(false));

        public async Task<ulong> ReadUInt64Async(CancellationToken ct) =>
            BinaryPrimitives.ReadUInt64LittleEndian(await ReadBytesAsync(8, ct).ConfigureAwait(false));

        public async Task<string> ReadStringAsync(CancellationToken ct)
        {
            ulong length = await ReadUInt64Async(ct).ConfigureAwait(false);
            if (length > MaximumStringBytes)
                throw new InvalidDataException(Unreadable);
            if (length == 0)
                return string.Empty;
            byte[] buffer = new byte[(int)length];
            await FillAsync(buffer, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(buffer);
        }

        private async Task FillAsync(Memory<byte> destination, CancellationToken ct)
        {
            _consumed += destination.Length;
            if (_consumed > MaximumHeaderBytes)
                throw new InvalidDataException(Unreadable);
            await stream.ReadExactlyAsync(destination, ct).ConfigureAwait(false);
        }
    }
}
