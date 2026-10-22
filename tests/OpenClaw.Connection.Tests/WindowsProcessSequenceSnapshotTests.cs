using System.Buffers.Binary;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection.Tests;

public sealed class WindowsProcessSequenceSnapshotTests
{
    [Fact]
    public void DecodesTwoProcessGenerationsWithoutOpeningTheirHandles()
    {
        byte[] table = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(table, 48);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), 1234);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), 17);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(56), 5678);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72), 0x8000_0000_0000_0001);

        IReadOnlyDictionary<int, ulong> sequences = WindowsProcessSequenceSnapshot.Parse(table);

        Assert.Equal((ulong)17, sequences[1234]);
        Assert.Equal(0x8000_0000_0000_0001, sequences[5678]);
    }

    [Theory]
    [InlineData(8, 5678, 18)]
    [InlineData(48, 1234, 18)]
    [InlineData(48, 5678, 0)]
    public void InvalidOrAmbiguousGenerationFailsClosed(
        int nextOffset, long secondProcessId, ulong secondSequence)
    {
        byte[] table = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(table, nextOffset);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), 1234);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), 17);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(56), secondProcessId);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72), secondSequence);

        Assert.Throws<InvalidDataException>(() => WindowsProcessSequenceSnapshot.Parse(table));
    }
}
