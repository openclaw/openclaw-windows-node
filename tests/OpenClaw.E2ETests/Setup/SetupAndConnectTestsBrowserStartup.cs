// Covers owned Edge page discovery without provisioning WSL or a Gateway.
// Run this class with OPENCLAW_RUN_E2E=1 to include the isolated native Edge smoke.
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Protects initial-page readiness, endpoint isolation and direct page CDP.</summary>
public sealed class SetupAndConnectTestsBrowserStartup
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DiscoveryWaitsForTheOwnedInitialPage()
    {
        using var handler = new TargetListHandler(
            """[{"type":"service_worker","url":"about:blank"},{"type":"page","url":"https://example.invalid/"}]""",
            "[]",
            Targets("ws://127.0.0.1:12345/devtools/page/owned"));
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(DiscoveryTimeout);

        var endpoint = await GatewaySearchBrowserProof.WaitForPageEndpointAsync(
            http, new Uri("http://127.0.0.1:12345"), timeout.Token);

        Assert.Equal("ws://127.0.0.1:12345/devtools/page/owned", endpoint.AbsoluteUri);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("http://127.0.0.1:12345/json/list", uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("ws://example.invalid:12345/devtools/page/owned")]
    [InlineData("ws://127.0.0.1:54321/devtools/page/owned")]
    [InlineData("wss://127.0.0.1:12345/devtools/page/owned")]
    [InlineData("ws://127.0.0.1:12345/devtools/browser/other")]
    [InlineData("ws://user:secret@127.0.0.1:12345/devtools/page/owned")]
    [InlineData("ws://127.0.0.1:12345/devtools/page/owned?token=secret")]
    [InlineData("ws://127.0.0.1:12345/devtools/page/owned#secret")]
    public async Task DiscoveryRejectsEndpointsOutsideTheOwnedPage(string endpoint)
    {
        using var handler = new TargetListHandler(Targets(endpoint));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewaySearchBrowserProof.WaitForPageEndpointAsync(
                http, new Uri("http://127.0.0.1:12345"), CancellationToken.None));

        Assert.DoesNotContain(endpoint, error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoveryHonorsCancellationWhenNoPageIsPublished()
    {
        using var handler = new TargetListHandler("[]");
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource();
        var discovery = GatewaySearchBrowserProof.WaitForPageEndpointAsync(
            http, new Uri("http://127.0.0.1:12345"), timeout.Token);
        Assert.NotEmpty(handler.Requests);
        timeout.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.WaitAsync(DiscoveryTimeout));
    }

    [E2EFact]
    public async Task OwnedEdgePageSupportsEvaluationAndScreenshotWithoutAGateway()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openclaw-edge-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var browser = await GatewaySearchBrowserProof.StartAsync(directory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.Equal("about:blank", (await browser.EvaluateAsync("document.URL", timeout.Token)).GetString());
            Assert.Equal(42, (await browser.EvaluateAsync("6 * 7", timeout.Token)).GetInt32());
            Assert.False(browser.Authenticated);
            Assert.Null(browser.PairingRequestId);
            var screenshot = Path.Combine(directory, "blank.png");
            await browser.CaptureAsync(screenshot);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                (await File.ReadAllBytesAsync(screenshot))[..8]);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    [Fact]
    public void ProfileCleanupDoesNotFailWhenAnOwnedFileIsStillLocked()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openclaw-edge-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "locked");
        try
        {
            using (var locked = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                CleanupDirectory(directory);
                Assert.True(File.Exists(file));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Assert.False(Directory.Exists(directory));
    }

    private static void CleanupDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A terminating Edge child can briefly retain profile file handles.
            Console.WriteLine($"[Search proof] temporary browser cleanup incomplete: {ex.GetType().Name}.");
        }
    }

    private static string Targets(string endpoint) => JsonSerializer.Serialize(new[]
    {
        new { type = "page", url = "about:blank", webSocketDebuggerUrl = endpoint }
    });

    private sealed class TargetListHandler(params string[] responses) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Math.Min(Requests.Count - 1, responses.Length - 1)],
                    Encoding.UTF8, "application/json")
            });
        }
    }
}
