using System.Reflection;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayClientFactoryTests
{
    [Fact]
    public void Create_BootstrapCredential_PairsAsOperator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var lifecycle = new GatewayClientFactory().Create(
                "ws://127.0.0.1:18789",
                new GatewayCredential("bootstrap-token", IsBootstrapToken: true, Source: "test"),
                tempDir,
                NullLogger.Instance);

            Assert.Equal("operator", GetConnectRole(lifecycle.DataClient));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Create_SharedCredential_PairsAsOperator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var lifecycle = new GatewayClientFactory().Create(
                "ws://127.0.0.1:18789",
                new GatewayCredential("shared-token", IsBootstrapToken: false, Source: "test"),
                tempDir,
                NullLogger.Instance);

            Assert.Equal("operator", GetConnectRole(lifecycle.DataClient));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string GetConnectRole(OpenClawGatewayClient client)
    {
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "GetConnectRole",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(client, null));
    }
}
