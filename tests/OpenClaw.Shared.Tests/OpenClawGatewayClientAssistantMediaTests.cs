using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class OpenClawGatewayClientAssistantMediaTests
{
    [Fact]
    public async Task ResolveStructuredMedia_InlineBase64_ReturnsBoundedTypedBytes()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "test-token",
            identityPath: identity.Path);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var resolution = client.ResolveAssistantMediaAsync(
            "main",
            new ChatMediaContentInfo
            {
                Kind = ChatMediaContentKind.Image,
                Source = ChatMediaContentSource.Structured,
                ArtifactId = "artifact-1",
            });
        var request = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var requestDocument = JsonDocument.Parse(request);
        Assert.Equal("artifacts.download", requestDocument.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "main",
            requestDocument.RootElement.GetProperty("params").GetProperty("sessionKey").GetString());

        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res",
            id = requestDocument.RootElement.GetProperty("id").GetString(),
            ok = true,
            payload = new
            {
                artifact = new
                {
                    id = "artifact-1",
                    type = "image",
                    title = "banner.png",
                    mimeType = "image/png",
                    sizeBytes = 4,
                    download = new { mode = "bytes" },
                },
                encoding = "base64",
                data = Convert.ToBase64String([1, 2, 3, 4]),
            },
        }));

        var result = await resolution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AssistantMediaResolutionStatus.Ready, result.Status);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Data);
    }

    [Theory]
    [InlineData(
        "/api/chat/media/outgoing/agent%3Amain%3Amain/dbedbff5-b83b-4da5-abaf-c479f4f40bb0/full?mediaTicket=ticket-1",
        true)]
    [InlineData("https://other.example/api/chat/media/outgoing/a/b/full?mediaTicket=ticket-1", false)]
    [InlineData("/api/chat/media/outgoing/a/b/full", false)]
    public async Task ResolveAssistantFileDownloadUri_ReturnsOnlyGatewayTicketedUrls(
        string gatewayUrl,
        bool expected)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "test-token",
            identityPath: identity.Path);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var resolution = client.ResolveAssistantFileDownloadUriAsync(
            "agent:main:main",
            new ChatMediaContentInfo
            {
                Kind = ChatMediaContentKind.File,
                Source = ChatMediaContentSource.Structured,
                ArtifactId = "artifact_managed_media_dbedbff5-b83b-4da5-abaf-c479f4f40bb0",
            });
        var request = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var requestDocument = JsonDocument.Parse(request);
        Assert.Equal("artifacts.download", requestDocument.RootElement.GetProperty("method").GetString());
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res",
            id = requestDocument.RootElement.GetProperty("id").GetString(),
            ok = true,
            payload = new
            {
                // Live Gateway response shape for a managed zip attachment.
                artifact = new
                {
                    id = "artifact_managed_media_dbedbff5-b83b-4da5-abaf-c479f4f40bb0",
                    type = "file",
                    title = "agent-images.zip",
                    mimeType = "application/zip",
                    sizeBytes = 64690236,
                    download = new { mode = "url" },
                },
                url = gatewayUrl,
            },
        }));

        var uri = await resolution.WaitAsync(TimeSpan.FromSeconds(2));

        if (!expected)
        {
            Assert.Null(uri);
            return;
        }
        Assert.NotNull(uri);
        Assert.Equal(new Uri(server.WebSocketUrl).Host, uri.Host);
        Assert.Equal(new Uri(server.WebSocketUrl).Port, uri.Port);
        Assert.Equal(Uri.UriSchemeHttp, uri.Scheme);
        Assert.Contains("mediaTicket=ticket-1", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAssistantFileDownloadUri_NonFileMedia_ReturnsNullWithoutGatewayCall()
    {
        // Connected and handshake-ready, so only the File-kind guard can stop the request.
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "test-token",
            identityPath: identity.Path);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var uri = await client.ResolveAssistantFileDownloadUriAsync(
            "agent:main:main",
            new ChatMediaContentInfo
            {
                Kind = ChatMediaContentKind.Image,
                Source = ChatMediaContentSource.Structured,
                ArtifactId = "artifact_managed_image_dbedbff5-b83b-4da5-abaf-c479f4f40bb0",
            }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(uri);
        await Assert.ThrowsAsync<TimeoutException>(
            () => server.ReceiveTextAsync().WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task ResolveLegacyMedia_UsesBearerMetadataAndSourceBoundTicket()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            JsonResponse(
                """{"available":true,"mimeType":"image/png","sizeBytes":4,"mediaTicket":"ticket-1"}"""),
            BytesResponse([1, 2, 3, 4], "image/png"));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var media = new ChatMediaContentInfo
        {
            Kind = ChatMediaContentKind.Image,
            Source = ChatMediaContentSource.LegacyDirective,
            GatewaySource = "/home/openclaw/private/banner.png",
        };
        var result = await client.ResolveAssistantMediaAsync(
            "main",
            media);

        Assert.Equal(AssistantMediaResolutionStatus.Ready, result.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Data);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("shared-http-token", request.AuthorizationParameter));
        Assert.Contains("meta=1", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("mediaTicket=ticket-1", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains(
            "source=%2Fhome%2Fopenclaw%2Fprivate%2Fbanner.png",
            handler.Requests[1].Uri.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUserAvatar_UsesGatewayRouteHeaderCredentialAndTypedStatuses()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            BytesResponse([1, 2, 3], "image/png"),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            BytesResponse([1], "image/svg+xml"),
            new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var ready = await client.ResolveUserAvatarAsync("gateway-owner", "7");
        Assert.Equal(UserAvatarStatus.Ready, ready.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, ready.Image!.Bytes);
        Assert.Equal("/api/users/gateway-owner/avatar", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?v=7", handler.Requests[0].Uri.Query);
        Assert.Equal("shared-http-token", handler.Requests[0].AuthorizationParameter);
        Assert.DoesNotContain("shared-http-token", handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);

        Assert.Equal(UserAvatarStatus.NotFound, (await client.ResolveUserAvatarAsync("a", null)).Status);
        Assert.Equal(UserAvatarStatus.Unauthorized, (await client.ResolveUserAvatarAsync("a", null)).Status);
        Assert.Equal(UserAvatarStatus.Failed, (await client.ResolveUserAvatarAsync("a", null)).Status);
        Assert.Equal(UserAvatarStatus.Failed, (await client.ResolveUserAvatarAsync("a", null)).Status);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveUserAvatar_RejectsUnroutableProfileIdsBeforeAnyRequest(string profileId)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(BytesResponse([1], "image/png"));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var result = await client.ResolveUserAvatarAsync(profileId, null);

        Assert.Equal(UserAvatarStatus.Failed, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResolveUserAvatar_BoundsIdentifierLengthsAtFiveHundredTwelve()
    {
        var atLimit = new string('a', 512);
        var overLimit = new string('a', 513);
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            BytesResponse([1], "image/png"),
            BytesResponse([2], "image/png"));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        Assert.Equal(UserAvatarStatus.Failed, (await client.ResolveUserAvatarAsync(overLimit, null)).Status);
        Assert.Equal(UserAvatarStatus.Failed, (await client.ResolveUserAvatarAsync("gateway-owner", overLimit)).Status);
        Assert.Empty(handler.Requests);

        Assert.Equal(UserAvatarStatus.Ready, (await client.ResolveUserAvatarAsync(atLimit, null)).Status);
        Assert.Equal(UserAvatarStatus.Ready, (await client.ResolveUserAvatarAsync("gateway-owner", atLimit)).Status);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal($"/api/users/{atLimit}/avatar", request.Uri.AbsolutePath),
            request => Assert.Equal($"?v={atLimit}", request.Uri.Query));
    }

    [Fact]
    public async Task ResolveUserAvatar_WithoutConnectionIsCancelledNotAFailure()
    {
        using var identity = new TempDirectory("assistant-media-");
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:1", "token", identityPath: identity.Path,
            assistantMediaHandler: new SequentialMediaHandler());
        Assert.Equal(UserAvatarStatus.Cancelled, (await client.ResolveUserAvatarAsync("a", null)).Status);
    }

    [Fact]
    public async Task ResolveUserAvatar_StalledBodyTimesOutAsFailureButCallerCancellationStaysCancelled()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(StalledBodyResponse(), StalledBodyResponse());
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);
        client.UserAvatarTimeout = TimeSpan.FromMilliseconds(200);

        var timedOut = await client.ResolveUserAvatarAsync("gateway-owner", null)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UserAvatarStatus.Failed, timedOut.Status);

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        client.UserAvatarTimeout = TimeSpan.FromSeconds(30);
        var cancelled = await client.ResolveUserAvatarAsync("gateway-owner", null, caller.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UserAvatarStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task ResolveUserAvatar_StalledHeadersAlsoTimeOut()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: new HangingHeadersHandler());
        await client.ConnectAsync();
        MarkHandshakeReady(client);
        client.UserAvatarTimeout = TimeSpan.FromMilliseconds(200);
        var result = await client.ResolveUserAvatarAsync("gateway-owner", null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UserAvatarStatus.Failed, result.Status);
    }

    [Fact]
    public void UserAvatarTimeout_DefaultsAndHonorsEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable(OpenClawGatewayClient.UserAvatarTimeoutEnvironmentVariable);
        try
        {
            using var identity = new TempDirectory("assistant-media-");
            Environment.SetEnvironmentVariable(OpenClawGatewayClient.UserAvatarTimeoutEnvironmentVariable, null);
            using var defaulted = new OpenClawGatewayClient("ws://127.0.0.1:1", "t", identityPath: identity.Path);
            Assert.Equal(TimeSpan.FromMilliseconds(OpenClawGatewayClient.DefaultUserAvatarTimeoutMs), defaulted.UserAvatarTimeout);
            Environment.SetEnvironmentVariable(OpenClawGatewayClient.UserAvatarTimeoutEnvironmentVariable, "1234");
            using var overridden = new OpenClawGatewayClient("ws://127.0.0.1:1", "t", identityPath: identity.Path);
            Assert.Equal(TimeSpan.FromMilliseconds(1234), overridden.UserAvatarTimeout);
            Environment.SetEnvironmentVariable(OpenClawGatewayClient.UserAvatarTimeoutEnvironmentVariable, "-5");
            using var invalid = new OpenClawGatewayClient("ws://127.0.0.1:1", "t", identityPath: identity.Path);
            Assert.Equal(TimeSpan.FromMilliseconds(OpenClawGatewayClient.DefaultUserAvatarTimeoutMs), invalid.UserAvatarTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenClawGatewayClient.UserAvatarTimeoutEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task ResolveLegacyMedia_DoesNotSendThisNetworkOrCgnatSource()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            JsonResponse("""{"available":false}"""));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-http-token",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        foreach (var source in new[]
        {
            "https://0.0.0.0/a.png",
            "https://100.64.0.1/a.png",
            "https://[::ffff:0.1.2.3]/a.png",
        })
        {
            var blocked = await client.ResolveAssistantMediaAsync(
                "main",
                new ChatMediaContentInfo
                {
                    Kind = ChatMediaContentKind.Image,
                    Source = ChatMediaContentSource.LegacyDirective,
                    GatewaySource = source,
                });
            Assert.Equal(AssistantMediaResolutionStatus.Unavailable, blocked.Status);
        }

        Assert.Empty(handler.Requests);

        var allowed = await client.ResolveAssistantMediaAsync(
            "main",
            new ChatMediaContentInfo
            {
                Kind = ChatMediaContentKind.Image,
                Source = ChatMediaContentSource.LegacyDirective,
                GatewaySource = "https://example.com/a.png",
            });
        Assert.Equal(AssistantMediaResolutionStatus.Unavailable, allowed.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("example.com", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("100.64.0.1", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("shared-http-token", request.AuthorizationParameter);
    }

    [Fact]
    public async Task ResolveLegacyMedia_WithoutExplicitHttpCredential_DoesNotUseWebSocketToken()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            JsonResponse(
                """{"available":true,"mimeType":"image/png","sizeBytes":4,"mediaTicket":"ticket-1"}"""),
            BytesResponse([1, 2, 3, 4], "image/png"));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        var result = await client.ResolveAssistantMediaAsync(
            "main",
            new ChatMediaContentInfo
            {
                Kind = ChatMediaContentKind.Image,
                Source = ChatMediaContentSource.LegacyDirective,
                GatewaySource = "/home/openclaw/private/banner.png",
            });

        Assert.Equal(AssistantMediaResolutionStatus.Ready, result.Status);
        Assert.All(handler.Requests, request => Assert.Null(request.AuthorizationParameter));
    }

    [Fact]
    public async Task ResolveLegacyMedia_UsesUpdatedHttpCredential()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("assistant-media-");
        await server.StartAsync();
        var handler = new SequentialMediaHandler(
            JsonResponse(
                """{"available":true,"mimeType":"image/png","sizeBytes":4,"mediaTicket":"ticket-1"}"""),
            BytesResponse([1, 2, 3, 4], "image/png"),
            JsonResponse(
                """{"available":true,"mimeType":"image/png","sizeBytes":4,"mediaTicket":"ticket-2"}"""),
            BytesResponse([1, 2, 3, 4], "image/png"));
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "paired-device-token",
            identityPath: identity.Path,
            assistantMediaAuthToken: "shared-token-1",
            assistantMediaHandler: handler);
        await client.ConnectAsync();
        MarkHandshakeReady(client);
        var media = new ChatMediaContentInfo
        {
            Kind = ChatMediaContentKind.Image,
            Source = ChatMediaContentSource.LegacyDirective,
            GatewaySource = "/home/openclaw/private/banner.png",
        };

        await client.ResolveAssistantMediaAsync("main", media);
        client.SetAssistantMediaAuthToken("shared-token-2");
        await client.ResolveAssistantMediaAsync("main", media);

        Assert.All(
            handler.Requests.Take(2),
            request => Assert.Equal("shared-token-1", request.AuthorizationParameter));
        Assert.All(
            handler.Requests.Skip(2),
            request => Assert.Equal("shared-token-2", request.AuthorizationParameter));
    }

    [Fact]
    public void TryDecodeBoundedBase64_RejectsDecodedSizeOverflow()
    {
        const int maximumBytes = 4;
        var encoded = string.Concat(new string(' ', 1024), "AQIDBAUG");

        var decoded = OpenClawGatewayClient.TryDecodeBoundedBase64(
            encoded,
            maximumBytes,
            out var bytes);

        Assert.False(decoded);
        Assert.Empty(bytes);
    }

    [Fact]
    public void TryDecodeBoundedBase64_RejectsOversizedCompactInputBeforeNormalization()
    {
        var decoded = OpenClawGatewayClient.TryDecodeBoundedBase64(
            " AQIDBAUGB ",
            maximumBytes: 4,
            out var bytes);

        Assert.False(decoded);
        Assert.Empty(bytes);
    }

    [Fact]
    public void TryDecodeBoundedBase64_DecodesWhitespaceAndUnpaddedBase64Url()
    {
        var decoded = OpenClawGatewayClient.TryDecodeBoundedBase64(
            " AQI-\n_w ",
            maximumBytes: 5,
            out var bytes);

        Assert.True(decoded);
        Assert.Equal(new byte[] { 1, 2, 62, 255 }, bytes);
    }

    [Theory]
    [InlineData("/api/chat/media/outgoing/item?mediaTicket=ticket", true)]
    [InlineData("/api/chat/media/outgoing/item", false)]
    [InlineData("/api/chat/media/outgoing/../secret?mediaTicket=ticket", false)]
    [InlineData("//other.example/api/chat/media/outgoing/item?mediaTicket=ticket", false)]
    [InlineData("https://other.example/api/chat/media/outgoing/item?mediaTicket=ticket", false)]
    public void TryResolveManagedMediaUri_EnforcesGatewayRelativeTicketPath(
        string path,
        bool expected)
    {
        var baseUri = new Uri("https://gateway.example/base");

        var resolved = OpenClawGatewayClient.TryResolveManagedMediaUri(baseUri, path, out var uri);

        Assert.Equal(expected, resolved);
        if (expected)
            Assert.Equal("gateway.example", uri.Host);
    }

    /// <summary>
    /// Satisfies the #1418 readiness gate (IsConnectedToGateway requires the
    /// hello-ok snapshot) without a wire handshake: media resolution leases
    /// gate on that property. Mechanical tests set the snapshot flag directly
    /// so the post-handshake auto-request burst cannot race their HTTP handler
    /// sequencing; the real handshake path is covered by
    /// GatewayProtocolLiveRoundTripTests.
    /// </summary>
    private static void CompleteHandshakeForTest(OpenClawGatewayClient client)
    {
        MarkHandshakeReady(client);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage StalledBodyResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());
    }

    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangingHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage BytesResponse(byte[] data, string mimeType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        return response;
    }

    private static void MarkHandshakeReady(OpenClawGatewayClient client)
    {
        var generationProperty = typeof(WebSocketClientBase).GetProperty(
            "CurrentConnectionGeneration",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var generation = (long)generationProperty!.GetValue(client)!;
        SetPrivateField(client, "_mainSessionKeyIsCanonical", true);
        SetPrivateField(client, "_mainSessionKey", "agent:main:main");
        SetPrivateField(client, "_handshakeConnectionGeneration", generation);
        SetPrivateField(client, "_hasHandshakeSnapshot", true);
    }

    private static void SetPrivateField(OpenClawGatewayClient client, string fieldName, object? value)
    {
        var field = typeof(OpenClawGatewayClient).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(client, value);
    }

    private sealed class SequentialMediaHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private int _nextResponse;

        public List<CapturedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Authorization?.Parameter));
            var index = Interlocked.Increment(ref _nextResponse) - 1;
            return Task.FromResult(responses[index]);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? Authorization,
        string? AuthorizationParameter);
}
