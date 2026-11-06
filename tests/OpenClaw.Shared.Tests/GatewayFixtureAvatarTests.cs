using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

// The Companion window loads the owner photo with a plain HTTP GET beside its WebSocket. These tests pin the one
// route the fixture serves and prove every other HTTP request is still rejected and recorded as an invalid upgrade.
public sealed partial class GatewayFixtureProtocolTests
{
    private const int AvatarPixels = 8;

    [Fact]
    public async Task Avatar_ServesTheOwnerPhotoToABearerWithoutRecordingTheCredentialOrProfile()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);

        var response = await GetAvatarAsync(
            server, GatewayScenario.OwnerProfileId, $"Bearer {token}", "?v=7", deadline.Token);

        Assert.Equal(200, response.Status);
        Assert.Equal("image/png", response.Headers["Content-Type"]);
        Assert.Equal(response.Body.Length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        AssertSolidPng(response.Body, FixtureGatewayServer.OwnerAvatarRgb);
        var served = Assert.Single(server.Requests);
        Assert.Equal(FixtureGatewayServer.AvatarRequestMethod, served.Method);
        Assert.Null(served.SessionKey);
        Assert.Equal("ok", served.Outcome);
        Assert.Empty(server.UnexpectedRequests);
        Assert.Equal(0, server.ActiveConnectionCount);
        var diagnostic = JsonSerializer.Serialize(server.Requests);
        Assert.DoesNotContain(token, diagnostic);
        Assert.DoesNotContain(GatewayScenario.OwnerProfileId, diagnostic);
    }

    [Fact]
    public async Task Avatar_RealClientFetchesThePhotoWhileItsWebSocketStaysUsable()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        connected.Client.SetAssistantMediaAuthToken(token);

        var result = await connected.Client.ResolveUserAvatarAsync(GatewayScenario.OwnerProfileId, "7")
            .WaitAsync(Deadline);

        Assert.Equal(UserAvatarStatus.Ready, result.Status);
        Assert.Equal("image/png", result.Image!.MimeType);
        AssertSolidPng(result.Image.Bytes, FixtureGatewayServer.OwnerAvatarRgb);
        // The client returns once it has read the body, which can precede the server journalling the finished response.
        var served = await server.WaitForCompletedRequestAsync(FixtureGatewayServer.AvatarRequestMethod)
            .WaitAsync(Deadline);
        Assert.Equal("ok", served.Outcome);
        Assert.Single(server.Requests, r => r.Method == FixtureGatewayServer.AvatarRequestMethod);
        Assert.Empty(server.UnexpectedRequests);
        Assert.Equal(GatewayScenario.MainSessionKey, (await connected.Client.RequestChatHistoryAsync()).SessionKey);
    }

    [Fact]
    public async Task Avatar_MatchesAPercentEncodedProfileIdAfterDecodingIt()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);

        var response = await SendRawHttpAsync(
            server,
            $"GET /api/users/fixture%2Downer/avatar HTTP/1.1\r\nAuthorization: Bearer {token}\r\n\r\n",
            deadline.Token);

        Assert.Equal(200, response.Status);
        AssertSolidPng(response.Body, FixtureGatewayServer.OwnerAvatarRgb);
    }

    [Theory]
    [InlineData("missing", GatewayScenario.OwnerProfileId)]
    [InlineData("wrong-token", GatewayScenario.OwnerProfileId)]
    [InlineData("wrong-scheme", GatewayScenario.OwnerProfileId)]
    [InlineData("missing", "someone-else")]
    public async Task Avatar_RejectsAMissingOrWrongCredentialBeforeLookingUpTheProfile(string credential, string profileId)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);
        var authorization = credential switch
        {
            "missing" => null,
            "wrong-token" => $"Bearer {CreateToken()}",
            "wrong-scheme" => $"Basic {token}",
            _ => throw new ArgumentOutOfRangeException(nameof(credential), credential, null),
        };

        var response = await GetAvatarAsync(server, profileId, authorization, "", deadline.Token);

        Assert.Equal(401, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal("0", response.Headers["Content-Length"]);
        var rejected = Assert.Single(server.UnexpectedRequests);
        Assert.Equal(FixtureGatewayServer.AvatarRequestMethod, rejected.Method);
        Assert.Equal("error:UNAUTHORIZED", rejected.Outcome);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(server.Requests));
    }

    [Fact]
    public async Task Avatar_ReportsAnUnknownProfileAsNotFoundToAnAuthenticatedCaller()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);

        var response = await GetAvatarAsync(server, "someone-else", $"Bearer {token}", "", deadline.Token);

        Assert.Equal(404, response.Status);
        Assert.Empty(response.Body);
        var rejected = Assert.Single(server.UnexpectedRequests);
        Assert.Equal(FixtureGatewayServer.AvatarRequestMethod, rejected.Method);
        Assert.Equal("error:NOT_FOUND", rejected.Outcome);
    }

    [Fact]
    public async Task Avatar_AcceptsTheIssuedDeviceTokenOnlyOnceTheServerAcceptsOne()
    {
        var token = CreateToken();
        var deviceToken = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);
        var bearer = $"Bearer {deviceToken}";

        var before = await GetAvatarAsync(server, GatewayScenario.OwnerProfileId, bearer, "", deadline.Token);
        server.AcceptedDeviceToken = deviceToken;
        var after = await GetAvatarAsync(server, GatewayScenario.OwnerProfileId, bearer, "", deadline.Token);

        Assert.Equal(401, before.Status);
        Assert.Equal(200, after.Status);
        Assert.Equal(["error:UNAUTHORIZED", "ok"], server.Requests.Select(r => r.Outcome));
        Assert.Equal("error:UNAUTHORIZED", Assert.Single(server.UnexpectedRequests).Outcome);
        Assert.DoesNotContain(deviceToken, JsonSerializer.Serialize(server.Requests));
    }

    [Theory]
    [InlineData("POST /api/users/fixture-owner/avatar HTTP/1.1")]
    [InlineData("GET /api/users/fixture-owner/avatar HTTP/1.0")]
    [InlineData("GET /api/users/fixture-owner/avatar/extra HTTP/1.1")]
    [InlineData("GET /api/users//avatar HTTP/1.1")]
    [InlineData("GET /api/users/a/b/avatar HTTP/1.1")]
    [InlineData("GET /api/users/avatar HTTP/1.1")]
    [InlineData("GET /other HTTP/1.1")]
    public Task Avatar_LeavesEveryOtherHttpRequestToBeRejectedAsAnInvalidUpgrade(string requestLine) =>
        AssertRejectedUpgradeAsync(requestLine + "\r\n\r\n", "error:INVALID_UPGRADE");

    private sealed record RawHttpResponse(int Status, Dictionary<string, string> Headers, byte[] Body);

    private static Task<RawHttpResponse> GetAvatarAsync(
        FixtureGatewayServer server, string profileId, string? authorization, string query, CancellationToken ct) =>
        SendRawHttpAsync(
            server,
            $"GET /api/users/{Uri.EscapeDataString(profileId)}/avatar{query} HTTP/1.1\r\nHost: 127.0.0.1\r\n"
            + (authorization is null ? "" : $"Authorization: {authorization}\r\n") + "\r\n",
            ct);

    private static async Task<RawHttpResponse> SendRawHttpAsync(
        FixtureGatewayServer server, string request, CancellationToken ct)
    {
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port, ct);
        var stream = peer.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var received = new MemoryStream();
        // The fixture closes the connection after its one response, so reading to the end yields the whole message.
        await stream.CopyToAsync(received, ct);

        var bytes = received.ToArray();
        var headEnd = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(headEnd >= 0, "The fixture closed the connection without a complete HTTP response head.");
        var lines = Encoding.ASCII.GetString(bytes, 0, headEnd).Split("\r\n");
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new RawHttpResponse(
            int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), headers, bytes[(headEnd + 4)..]);
    }

    // Decodes with the BCL only, so the expectation does not depend on the imaging stack the app renders with.
    private static void AssertSolidPng(byte[] png, int rgb)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan().StartsWith(signature), "The served photo is not a PNG.");
        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();
        for (var offset = signature.Length; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                // 8-bit truecolour, deflate, adaptive filtering, no interlace.
                Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, data[8..13].ToArray());
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }
            offset += 12 + length;
        }

        Assert.Equal((AvatarPixels, AvatarPixels), (width, height));
        compressed.Position = 0;
        using var scanlines = new MemoryStream();
        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
            inflate.CopyTo(scanlines);
        var raw = scanlines.ToArray();
        var stride = 1 + width * 3;
        Assert.Equal(height * stride, raw.Length);
        byte[] expected = [(byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * stride]);
            for (var x = 0; x < width; x++)
                Assert.True(
                    raw.AsSpan(y * stride + 1 + x * 3, 3).SequenceEqual(expected),
                    $"Pixel {x},{y} is not the owner photo colour.");
        }
    }
}
