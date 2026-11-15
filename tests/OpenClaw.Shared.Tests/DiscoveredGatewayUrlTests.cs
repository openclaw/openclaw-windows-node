using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class DiscoveredGatewayUrlTests
{
    [Fact]
    public void ConnectionUrl_BracketsAnIpv6Host()
    {
        var gateway = new DiscoveredGateway { Host = "::1", Port = 18789 };

        Assert.Equal("ws://[::1]:18789", gateway.ConnectionUrl);
        Assert.Equal("http://[::1]:18789", gateway.HttpUrl);
        Assert.True(GatewayUrlHelper.IsValidGatewayUrl(gateway.ConnectionUrl));
    }

    [Fact]
    public void ConnectionUrl_BracketsAnExpandedIpv6Host()
    {
        var gateway = new DiscoveredGateway { Host = "0:0:0:0:0:0:0:1", Port = 18789 };

        Assert.Equal("ws://[::1]:18789", gateway.ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_BracketsAnAlreadyBracketedIpv6HostOnce()
    {
        var gateway = new DiscoveredGateway { Host = "[fe80::1]", Port = 443, TlsEnabled = true };

        Assert.Equal("wss://[fe80::1]:443", gateway.ConnectionUrl);
        Assert.Equal("https://[fe80::1]:443", gateway.HttpUrl);
    }

    [Fact]
    public void ConnectionUrl_KeepsAnIpv4HostAndADnsName()
    {
        var address = new DiscoveredGateway { Host = "192.168.1.25", Port = 18789 };
        var name = new DiscoveredGateway { LanHost = "gateway.local", Port = 18789 };

        Assert.Equal("ws://192.168.1.25:18789", address.ConnectionUrl);
        Assert.Equal("ws://gateway.local:18789", name.ConnectionUrl);
    }

    [Theory]
    [InlineData("Kitchen Gateway")]
    [InlineData("user@192.168.1.25")]
    [InlineData("192.168.1.25/admin")]
    [InlineData("192.168.1.25?token=1")]
    [InlineData("192.168.1.25#frag")]
    [InlineData("")]
    public void ConnectionUrl_RejectsAHostThatIsNotAnEndpoint(string host)
    {
        var gateway = new DiscoveredGateway { Host = host, Port = 18789 };

        Assert.Equal("", gateway.ConnectionUrl);
        Assert.Equal("", gateway.HttpUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ConnectionUrl_RejectsAPortOutsideTheTcpRange(int port)
    {
        var gateway = new DiscoveredGateway { Host = "127.0.0.1", Port = port };

        Assert.Equal("", gateway.ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_PreservesAnIpv6Zone()
    {
        var gateway = new DiscoveredGateway { Host = "fe80::1%12", Port = 18789 };

        Assert.Equal("ws://[fe80::1%2512]:18789", gateway.ConnectionUrl);
    }
}
