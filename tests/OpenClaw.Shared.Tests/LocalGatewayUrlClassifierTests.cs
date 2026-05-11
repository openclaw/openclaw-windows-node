using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class LocalGatewayUrlClassifierTests
{
    [Theory]
    [InlineData("ws://localhost:18789", true)]
    [InlineData("ws://LOCALHOST:18789", true)]
    [InlineData("ws://127.0.0.1:18789", true)]
    [InlineData("ws://127.0.0.1:9999", true)]
    [InlineData("ws://[::1]:18789", true)]
    [InlineData("wss://localhost:18789", true)]
    [InlineData("http://localhost:18789", true)]
    [InlineData("ws://loopback.example:18789", false)]
    [InlineData("ws://10.0.0.5:18789", false)]
    [InlineData("ws://example.com:18789", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void IsLocalGatewayUrl_ClassifiesLiteralLoopbackHostsOnly(string url, bool expected)
    {
        Assert.Equal(expected, LocalGatewayUrlClassifier.IsLocalGatewayUrl(url));
    }
}
