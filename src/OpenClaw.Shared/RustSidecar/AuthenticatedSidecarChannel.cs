using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Shared.RustSidecar;

internal enum SidecarPeerRole
{
    Supervisor,
    Runtime
}

/// <summary>
/// Windows implementation of the OpenClaw authenticated sidecar frame contract.
/// A channel belongs to one process generation and is terminal after any inbound failure.
/// </summary>
internal sealed class AuthenticatedSidecarChannel : IDisposable
{
    internal const ushort ProtocolMajor = 1;
    internal const ushort ProtocolMinor = 0;
    private const int AuthenticationTagBytes = 32;
    private const int FixedHeaderBytes = 31;
    private static ReadOnlySpan<byte> Magic => "OCSC"u8;

    private readonly SidecarPeerRole _role;
    private readonly byte[] _sessionId;
    private readonly ulong _generation;
    private readonly byte[] _key;
    private uint _maxFrameBytes;
    private ulong _sendSequence;
    private ulong _receiveSequence;
    private bool _retired;

    internal AuthenticatedSidecarChannel(
        SidecarPeerRole role,
        string sessionId,
        ulong generation,
        ReadOnlySpan<byte> sessionKey,
        uint maxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (sessionId.Length == 0)
            throw new ArgumentException("Sidecar session id must not be empty.", nameof(sessionId));
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (sessionKey.Length != AuthenticationTagBytes)
            throw new ArgumentException("Sidecar session keys must contain 32 bytes.", nameof(sessionKey));

        _sessionId = Encoding.UTF8.GetBytes(sessionId);
        if (_sessionId.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (maxFrameBytes < MinimumFrameBytes(_sessionId.Length))
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));

        _role = role;
        _generation = generation;
        _key = sessionKey.ToArray();
        _maxFrameBytes = maxFrameBytes;
    }

    internal bool IsRetired => _retired;
    internal SidecarPeerRole LocalRole => _role;
    internal uint MaxFrameBytes => _maxFrameBytes;
    internal int MaxPayloadBytes => checked((int)_maxFrameBytes - FixedHeaderBytes - _sessionId.Length - AuthenticationTagBytes);

    internal void LowerFrameLimit(uint maxFrameBytes)
    {
        ThrowIfRetired();
        if (maxFrameBytes < MinimumFrameBytes(_sessionId.Length) || maxFrameBytes > _maxFrameBytes)
        {
            Retire();
            throw new SidecarProtocolException("Invalid negotiated sidecar frame limit.");
        }
        _maxFrameBytes = maxFrameBytes;
    }

    internal byte[] Seal(ReadOnlySpan<byte> jsonPayload)
    {
        ThrowIfRetired();
        if (_sendSequence == ulong.MaxValue)
            throw new SidecarProtocolException("Sidecar send sequence is exhausted.");

        var frameLength = checked(FixedHeaderBytes + _sessionId.Length + jsonPayload.Length + AuthenticationTagBytes);
        if (frameLength > _maxFrameBytes)
            throw new SidecarProtocolException("Sidecar frame exceeds the negotiated limit.");

        var frame = new byte[frameLength];
        var cursor = 0;
        Magic.CopyTo(frame.AsSpan(cursor));
        cursor += Magic.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(cursor), ProtocolMajor);
        cursor += sizeof(ushort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(cursor), ProtocolMinor);
        cursor += sizeof(ushort);
        frame[cursor++] = _role == SidecarPeerRole.Supervisor ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(cursor), _generation);
        cursor += sizeof(ulong);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(cursor), _sendSequence + 1);
        cursor += sizeof(ulong);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(cursor), checked((ushort)_sessionId.Length));
        cursor += sizeof(ushort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(cursor), checked((uint)jsonPayload.Length));
        cursor += sizeof(uint);
        _sessionId.CopyTo(frame.AsSpan(cursor));
        cursor += _sessionId.Length;
        jsonPayload.CopyTo(frame.AsSpan(cursor));
        cursor += jsonPayload.Length;

        HMACSHA256.HashData(_key, frame.AsSpan(0, cursor), frame.AsSpan(cursor, AuthenticationTagBytes));
        _sendSequence++;
        return frame;
    }

    internal byte[] Open(ReadOnlySpan<byte> frame)
    {
        ThrowIfRetired();
        try
        {
            if (frame.Length > _maxFrameBytes || frame.Length < FixedHeaderBytes + AuthenticationTagBytes)
                throw new SidecarProtocolException("Invalid sidecar frame size.");

            var authenticatedLength = frame.Length - AuthenticationTagBytes;
            Span<byte> expectedTag = stackalloc byte[AuthenticationTagBytes];
            HMACSHA256.HashData(_key, frame[..authenticatedLength], expectedTag);
            if (!CryptographicOperations.FixedTimeEquals(expectedTag, frame[authenticatedLength..]))
                throw new SidecarProtocolException("Sidecar frame authentication failed.");

            var cursor = 0;
            if (!frame[..Magic.Length].SequenceEqual(Magic))
                throw new SidecarProtocolException("Invalid sidecar frame magic.");
            cursor += Magic.Length;
            var major = ReadUInt16(frame, ref cursor);
            var minor = ReadUInt16(frame, ref cursor);
            if (major != ProtocolMajor || minor > ProtocolMinor)
                throw new SidecarProtocolException("Unsupported sidecar frame version.");

            var expectedDirection = _role == SidecarPeerRole.Supervisor ? (byte)2 : (byte)1;
            if (Take(frame, ref cursor, 1)[0] != expectedDirection)
                throw new SidecarProtocolException("Sidecar frame direction does not match the channel role.");
            if (ReadUInt64(frame, ref cursor) != _generation)
                throw new SidecarProtocolException("Sidecar frame belongs to another generation.");
            if (_receiveSequence == ulong.MaxValue || ReadUInt64(frame, ref cursor) != _receiveSequence + 1)
                throw new SidecarProtocolException("Unexpected sidecar receive sequence.");

            var sessionLength = ReadUInt16(frame, ref cursor);
            var payloadLength = ReadUInt32(frame, ref cursor);
            if (!Take(frame, ref cursor, sessionLength).SequenceEqual(_sessionId))
                throw new SidecarProtocolException("Sidecar frame belongs to another session.");
            var payload = Take(frame[..authenticatedLength], ref cursor, checked((int)payloadLength));
            if (cursor != authenticatedLength)
                throw new SidecarProtocolException("Sidecar frame contains trailing bytes.");

            _receiveSequence++;
            return payload.ToArray();
        }
        catch
        {
            Retire();
            throw;
        }
    }

    internal void Retire()
    {
        if (_retired)
            return;
        CryptographicOperations.ZeroMemory(_key);
        _retired = true;
    }

    public void Dispose() => Retire();

    private static int MinimumFrameBytes(int sessionIdBytes) =>
        checked(FixedHeaderBytes + sessionIdBytes + AuthenticationTagBytes + 1);

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int cursor) =>
        BinaryPrimitives.ReadUInt16BigEndian(Take(bytes, ref cursor, sizeof(ushort)));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int cursor) =>
        BinaryPrimitives.ReadUInt32BigEndian(Take(bytes, ref cursor, sizeof(uint)));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, ref int cursor) =>
        BinaryPrimitives.ReadUInt64BigEndian(Take(bytes, ref cursor, sizeof(ulong)));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int cursor, int length)
    {
        if (length < 0 || cursor > bytes.Length - length)
            throw new SidecarProtocolException("Sidecar frame is truncated.");
        var value = bytes.Slice(cursor, length);
        cursor += length;
        return value;
    }

    private void ThrowIfRetired()
    {
        if (_retired)
            throw new SidecarProtocolException("Sidecar channel is retired.");
    }
}

internal sealed class SidecarProtocolException(string message) : Exception(message);
