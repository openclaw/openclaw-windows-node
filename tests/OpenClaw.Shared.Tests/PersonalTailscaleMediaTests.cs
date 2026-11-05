using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class PersonalTailscaleMediaTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, AssistantMediaResolutionStatus.Ready, 2)]
    [InlineData(HttpStatusCode.Forbidden, AssistantMediaResolutionStatus.Unavailable, 1)]
    public async Task LegacyMediaUsesCurrentServeIdentityAndScopeCeilingWithoutBearerFallback(
        HttpStatusCode status, AssistantMediaResolutionStatus expected, int requests)
    {
        using var identity = new TempDirectory("personal-media-");
        await using var peer = new PersonalGatewayProtocolFixture();
        using var handler = new MediaHandler((_, count) => Task.FromResult(count == 1 ? Metadata(status) : Image()));
        using var client = peer.CreateClient(identity.Path, handler);
        await ReadyAsync(peer, client);
        var media = client.ResolveAssistantMediaAsync("main", Legacy());
        await SelfAsync(peer);
        Assert.Equal(expected, (await media).Status);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("https://protocol.example.ts.net", request.Uri.GetLeftPart(UriPartial.Authority));
            Assert.Null(request.Authorization);
            Assert.Equal("https://protocol.example.ts.net", request.Origin);
            Assert.Equal("operator.read,operator.write", request.Scopes);
            Assert.False(request.HasAmbientCredentials);
        });
        if (requests == 2)
            Assert.Contains("mediaTicket=fixture-ticket", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedCanonicalPersonBeforeHttpRefusesMediaAndRetiresReadiness()
    {
        using var identity = new TempDirectory("personal-media-person-");
        await using var peer = new PersonalGatewayProtocolFixture();
        using var handler = new MediaHandler((_, _) => Task.FromResult(Metadata(HttpStatusCode.OK)));
        using var client = peer.CreateClient(identity.Path, handler);
        await ReadyAsync(peer, client);
        var media = client.ResolveAssistantMediaAsync("main", Legacy());
        await SelfAsync(peer, "another-person");
        Assert.Equal(AssistantMediaResolutionStatus.Unavailable, (await media).Status);
        Assert.Empty(handler.Requests);
        Assert.False(client.IsConnectedToGateway);
    }

    [Fact]
    public async Task ProfileInvalidationDuringMetadataCannotFetchBytesWithRetiredAuthority()
    {
        using var identity = new TempDirectory("personal-media-retire-");
        await using var peer = new PersonalGatewayProtocolFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MediaHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Metadata(HttpStatusCode.OK);
        });
        using var client = peer.CreateClient(identity.Path, handler);
        await ReadyAsync(peer, client);
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SelfProfileChanged += (_, _) => invalidated.TrySetResult();
        var media = client.ResolveAssistantMediaAsync("main", Legacy());
        await SelfAsync(peer);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peer.ProfileChangedAsync();
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal(AssistantMediaResolutionStatus.Unavailable, (await media).Status);
        Assert.Single(handler.Requests);
    }

    private static ChatMediaContentInfo Legacy() => new()
    {
        Kind = ChatMediaContentKind.Image, Source = ChatMediaContentSource.LegacyDirective,
        GatewaySource = "/fixture/banner.png"
    };

    private static async Task ReadyAsync(PersonalGatewayProtocolFixture peer, OpenClawGatewayClient client)
    {
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => hello.TrySetResult();
        await client.ConnectAsync();
        await peer.ChallengeAsync();
        await peer.HelloAsync(await peer.ReceiveAsync());
        await hello.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var epoch = client.PersonalHandshakeConnectionEpoch!.Value;
        var self = client.ReadPersonalIdentityAsync(epoch);
        await SelfAsync(peer);
        await self;
        client.VerifyPersonalIdentity("person-42", epoch);
    }

    private static async Task SelfAsync(PersonalGatewayProtocolFixture peer, string profile = "person-42")
    {
        var request = await peer.ReceiveAsync();
        Assert.Equal("users.self", request.GetProperty("method").GetString());
        await peer.SelfAsync(request, profile);
    }

    private static HttpResponseMessage Metadata(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("""{"available":true,"mimeType":"image/png","sizeBytes":3,"mediaTicket":"fixture-ticket"}""", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed record Request(Uri Uri, string? Authorization, string? Origin, string? Scopes, bool HasAmbientCredentials);
    private sealed class MediaHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
            Requests.Add(new(request.RequestUri!, request.Headers.Authorization?.ToString(), Header("Origin"), Header("x-openclaw-scopes"),
                request.Headers.Any(header => header.Key.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || header.Key is "Cookie" or "Sec-Fetch-Site")));
            return respond(request, Requests.Count);
        }
    }
}
