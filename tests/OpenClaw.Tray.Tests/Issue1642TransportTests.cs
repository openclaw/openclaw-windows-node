using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

// Transport component only. The pinned Updatum client cannot accept this handler
// through an exposed application boundary. See TRANSPORT-GAP.md in the kit.
internal sealed class Issue1642PinnedReleaseHandler(string latestJson, string releasesJson)
    : HttpMessageHandler
{
    internal const string Root = "https://api.github.com/repos/openclaw/openclaw-windows-node/releases";
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri?.AbsoluteUri;
        if (request.Method != HttpMethod.Get || request.Headers.Authorization is not null ||
            request.Headers.Contains("Cookie"))
            throw new InvalidOperationException("Fixture permits credential-free metadata GET only.");

        // Exact allowlist. No socket, proxy, default handler, redirect or live fallback.
        var body = uri switch
        {
            Root + "/latest" => latestJson,
            Root => releasesJson,
            _ => throw new InvalidOperationException("Unpinned request blocked: " + uri)
        };
        Requests.Add(uri!);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        });
    }
}

public sealed class Issue1642TransportTests
{
    private static Issue1642PinnedReleaseHandler CreateHandler()
    {
        using var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "tests", "OpenClaw.Tray.Tests",
            "Fixtures", "issue1642-feed.json")));
        return new Issue1642PinnedReleaseHandler(
            feed.RootElement.GetProperty("latest").GetRawText(),
            feed.RootElement.GetProperty("releases").GetRawText());
    }

    [Fact]
    public async Task MetadataRequestsReturnPinnedUnsortedInputWithoutSelection()
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        using var latest = JsonDocument.Parse(await client.GetStringAsync(Issue1642PinnedReleaseHandler.Root + "/latest"));
        Assert.Equal("v2026.9.4", latest.RootElement.GetProperty("tag_name").GetString());
        using var releases = JsonDocument.Parse(await client.GetStringAsync(Issue1642PinnedReleaseHandler.Root));
        Assert.Equal(new[] { "v2026.9.5-alpha.10", "v2026.9.5-alpha.117", "v2026.9.5-alpha.97", "v2026.9.4" },
            releases.RootElement.EnumerateArray().Select(item => item.GetProperty("tag_name").GetString()).ToArray());
        Assert.Equal(new[] { Issue1642PinnedReleaseHandler.Root + "/latest", Issue1642PinnedReleaseHandler.Root }, handler.Requests);
    }

    [Theory]
    [InlineData("https://example.invalid/no-download")]
    [InlineData("https://api.github.com/repos/openclaw/openclaw-windows-node/releases/123/assets")]
    [InlineData("https://github.com/openclaw/openclaw-windows-node/releases/download/v1/app.exe")]
    [InlineData("https://api.github.com/repos/openclaw/openclaw-windows-node/releases?page=2")]
    public async Task UnpinnedRequestsCannotFallThroughToNetwork(string uri)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(uri));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CredentialsAndWritesAreRejected()
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler);
        using var credentialed = new HttpRequestMessage(HttpMethod.Get, Issue1642PinnedReleaseHandler.Root);
        credentialed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic-fixture-only");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(credentialed));
        using var write = new HttpRequestMessage(HttpMethod.Post, Issue1642PinnedReleaseHandler.Root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(write));
        Assert.Empty(handler.Requests);
    }
}
