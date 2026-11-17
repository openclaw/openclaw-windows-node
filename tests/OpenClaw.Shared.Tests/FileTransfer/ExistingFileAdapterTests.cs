using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed class ExistingFileAdapterTests
{
    [Fact]
    public async Task BrowserReturnedFilesUseTheSandboxReaderAndPreserveByteLimit()
    {
        var observed = new List<(string Path, long Limit)>();
        var capability = new BrowserProxyCapability(NullLogger.Instance, "ws://localhost:18789", "fixture-token",
            handler: new BrowserResult(),
            readFile: (path, limit, _) =>
            {
                observed.Add((path, limit));
                return Task.FromResult<byte[]?>("abc"u8.ToArray());
            });
        var result = await capability.ExecuteAsync(Request());
        Assert.True(result.Ok);
        Assert.Equal((@"C:\Fixture\screen.png", 10L * 1024 * 1024), Assert.Single(observed));
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        Assert.Equal("YWJj", payload.GetProperty("files")[0].GetProperty("base64").GetString());
    }

    [Fact]
    public async Task BrowserFileCollectionDoesNotFallBackWhenNoSandboxReaderExists()
    {
        var capability = new BrowserProxyCapability(NullLogger.Instance, "ws://localhost:18789", "fixture-token",
            handler: new BrowserResult());
        var result = await capability.ExecuteAsync(Request());
        Assert.False(result.Ok);
        Assert.Equal("Browser proxy file read failed", result.Error);
    }

    private static NodeInvokeRequest Request() => new()
    {
        Command = "browser.proxy",
        Args = JsonSerializer.SerializeToElement(new { path = "/screenshot", method = "GET" })
    };

    private sealed class BrowserResult : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"path":"C:\\Fixture\\screen.png"}""", Encoding.UTF8, "application/json")
            });
    }
}
