using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class BrowserBootstrapPipeTests
{
    [Fact]
    public async Task CurrentUserPipe_RoundTripsOneBoundedRequest()
    {
        var name = "OpenClaw.BrowserBootstrap.Test." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new BrowserBootstrapPipeServer((payload, _) =>
        {
            var request = BrowserNativeProtocol.ParseRequest(payload);
            return Task.FromResult(BrowserNativeProtocol.Pairing(request.Nonce, "synthetic-test-pairing"));
        }, name);
        server.Start();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await BrowserNativeProtocol.WriteAsync(pipe,
            Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}"), timeout.Token);
        using var result = JsonDocument.Parse(await BrowserNativeProtocol.ReadAsync(pipe, 4096, timeout.Token));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("synthetic-test-pairing", result.RootElement.GetProperty("pairingString").GetString());
    }

    [Fact]
    public void Manifest_RequiresExactExecutableOriginAndSchema()
    {
        var path = Path.GetFullPath("OpenClaw.BrowserNativeHost.exe");
        var manifest = BrowserNativeRegistration.BuildManifest(path);
        Assert.True(BrowserNativeRegistration.ManifestMatches(manifest, path));
        Assert.False(BrowserNativeRegistration.ManifestMatches(manifest, path + "other"));
        Assert.False(BrowserNativeRegistration.ManifestMatches(manifest.Replace(BrowserNativeProtocol.ExtensionId, new string('a', 32)), path));
        Assert.False(BrowserNativeRegistration.ManifestMatches(manifest.Replace("\"stdio\"", "\"http\""), path));
        Assert.False(BrowserNativeRegistration.ManifestMatches(manifest[..^1] + ",\"type\":\"stdio\"}", path));
    }
}
