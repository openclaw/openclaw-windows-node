using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class BrowserNativeProtocolTests
{
    private const string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";
    private static byte[] Request(string extra = "") => Encoding.UTF8.GetBytes(
        "{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"" + Nonce + "\"" + extra + "}");

    [Fact]
    public void Bootstrap_UsesCanonicalV1Contract()
    {
        var request = BrowserNativeProtocol.ParseRequest(Request());
        Assert.Equal("bootstrap", request.Op);
        Assert.Equal(Nonce, request.Nonce);
        Assert.Null(request.RelayPort);
    }

    [Theory]
    [InlineData(",\"v\":1")]
    [InlineData(",\"\\u0076\":1")]
    [InlineData(",\"extra\":true")]
    [InlineData(",\"relayPort\":18792")]
    public void RejectsDuplicateEscapedOrUnknownKeys(string extra) =>
        Assert.Equal("invalid_request", Assert.Throws<InvalidDataException>(() => BrowserNativeProtocol.ParseRequest(Request(extra))).Message);

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA+")]
    public void RejectsNoncanonicalNonce(string nonce) =>
        Assert.Throws<InvalidDataException>(() => BrowserNativeProtocol.ParseRequest(
            Encoding.UTF8.GetBytes($"{{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"{nonce}\"}}")));

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"v\":\"1\",\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":1}")]
    [InlineData("{\"v\":1,\"op\":\"unknown\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}")]
    public void RejectsInvalidSchema(string json) =>
        Assert.Throws<InvalidDataException>(() => BrowserNativeProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void RejectsInvalidUtf8() => Assert.Equal("invalid_utf8",
        Assert.Throws<InvalidDataException>(() => BrowserNativeProtocol.ParseRequest([0xff])).Message);

    [Fact]
    public void ExactOriginAndChromeParentWindowOnly()
    {
        Assert.True(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin]));
        Assert.True(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin, "--parent-window=0"]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin.TrimEnd('/')]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin + "evil"]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin.ToUpperInvariant()]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin, "--register"]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller([BrowserNativeProtocol.Origin, "--parent-window=-1"]));
        Assert.False(BrowserNativeProtocol.IsAllowedCaller(["chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(uint.MaxValue)]
    public async Task RejectsFrameLengthBeforeAllocation(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeProtocol.ReadAsync(new MemoryStream(header), 4096, default));
    }

    [Fact]
    public async Task Frame_RoundTripsBinaryWithoutTextConversion()
    {
        var payload = Encoding.UTF8.GetBytes("{\"line\":\"é\\r\\n\"}");
        using var stream = new MemoryStream();
        await BrowserNativeProtocol.WriteAsync(stream, payload, default);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        stream.Position = 0;
        Assert.Equal(payload, await BrowserNativeProtocol.ReadAsync(stream, 4096, default));
    }

    [Fact]
    public async Task RejectsTruncatedFrame() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeProtocol.ReadAsync(new MemoryStream([3, 0, 0, 0, 1]), 4096, default));

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void EnsureRelay_ParsesOnlyBoundedPorts(int port) => Assert.Equal(port,
        BrowserNativeProtocol.ParseRequest(Encoding.UTF8.GetBytes(
            $"{{\"v\":1,\"op\":\"ensure_relay\",\"nonce\":\"{Nonce}\",\"relayPort\":{port}}}")).RelayPort);

    [Fact]
    public void Response_ContainsNonceButNoAdditionalCredentialFields()
    {
        using var result = JsonDocument.Parse(BrowserNativeProtocol.Pairing(Nonce, "test-pairing"));
        Assert.Equal(4, result.RootElement.EnumerateObject().Count());
        Assert.Equal(Nonce, result.RootElement.GetProperty("nonce").GetString());
    }
}
