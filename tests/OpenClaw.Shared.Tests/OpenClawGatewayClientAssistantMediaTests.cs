using System.Net;
using System.Net.Http.Headers;
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

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BytesResponse(byte[] data, string mimeType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        return response;
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
