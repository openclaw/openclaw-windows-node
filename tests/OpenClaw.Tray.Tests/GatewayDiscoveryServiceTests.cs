using OpenClawTray.Services;
using Zeroconf;

namespace OpenClaw.Tray.Tests;

public class GatewayDiscoveryServiceTests
{
    [Fact]
    public void ParseHost_UsesIpAddressForRouting_WhenDisplayNameIsFriendly()
    {
        var host = new TestZeroconfHost
        {
            DisplayName = "Kitchen Gateway",
            IPAddress = "192.168.1.25",
            Services = new Dictionary<string, IService>
            {
                ["_openclaw-gw._tcp.local."] = new TestService
                {
                    Port = 18789,
                    Properties =
                    [
                        new Dictionary<string, string>
                        {
                            ["displayName"] = "Kitchen Gateway"
                        }
                    ]
                }
            }
        };

        var gateway = GatewayDiscoveryService.ParseHost(host);

        Assert.NotNull(gateway);
        Assert.Equal("192.168.1.25", gateway!.Host);
        Assert.Equal("ws://192.168.1.25:18789", gateway.ConnectionUrl);
        Assert.Equal("Kitchen Gateway", gateway.DisplayName);
    }

    [Fact]
    public void ParseHost_FallsBackToDisplayName_WhenIpAddressIsMissing()
    {
        var host = new TestZeroconfHost
        {
            DisplayName = "gateway.local",
            IPAddress = "",
            Services = new Dictionary<string, IService>
            {
                ["_openclaw-gw._tcp.local."] = new TestService { Port = 18789 }
            }
        };

        var gateway = GatewayDiscoveryService.ParseHost(host);

        Assert.NotNull(gateway);
        Assert.Equal("gateway.local", gateway!.Host);
    }

    private sealed class TestZeroconfHost : IZeroconfHost
    {
        public string DisplayName { get; set; } = "";
        public string Id { get; set; } = "";
        public string IPAddress { get; set; } = "";
        public IReadOnlyList<string> IPAddresses { get; set; } = [];
        public IReadOnlyDictionary<string, IService> Services { get; set; } =
            new Dictionary<string, IService>();
    }

    private sealed class TestService : IService
    {
        public string Name { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public int Port { get; set; }
        public int Ttl { get; set; }
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Properties { get; set; } = [];
    }
}
