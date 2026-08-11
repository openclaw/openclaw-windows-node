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
    public void Create_BootstrapCredential_IgnoresStoredOperatorDeviceToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var identity = new DeviceIdentity(tempDir);
            identity.Initialize();
            identity.StoreDeviceTokenForRole("operator", "stored-device-token");

            using var lifecycle = new GatewayClientFactory().Create(
                "ws://127.0.0.1:18789",
                new GatewayCredential("bootstrap-token", IsBootstrapToken: true, Source: "test"),
                tempDir,
                NullLogger.Instance);

            var auth = GetAuthPayload(lifecycle.DataClient);
            Assert.Equal("bootstrap-token", auth["bootstrapToken"]);
            Assert.DoesNotContain("deviceToken", auth.Keys);
            Assert.DoesNotContain("token", auth.Keys);
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

    [Fact]
    public void ValidationClient_DoesNotPersistHandshakeDeviceTokens()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var identity = new DeviceIdentity(tempDir);
            identity.Initialize();
            identity.StoreDeviceTokenForRole("operator", "stored-device-token");
            using var client = new OpenClawGatewayClient(
                "ws://127.0.0.1:18789",
                "replacement-token",
                NullLogger.Instance,
                identityPath: tempDir,
                ignoreStoredDeviceToken: true,
                persistHandshakeDeviceTokens: false);

            Assert.False(TryStoreHandshakeDeviceToken(client, "operator", "candidate-device-token"));
            Assert.Equal(
                "stored-device-token",
                DeviceIdentity.TryReadStoredDeviceTokenForRole(tempDir, "operator"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task HandshakeAuthorization_BlocksCredentialBearingConnectMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new OpenClawGatewayClient(
                "ws://127.0.0.1:18789",
                "replacement-token",
                NullLogger.Instance,
                identityPath: tempDir,
                ignoreStoredDeviceToken: true,
                persistHandshakeDeviceTokens: false);
            var authorizationCalls = 0;
            string? failure = null;
            ConnectionStatus? lastStatus = null;
            client.HandshakeAuthorizationAsync = _ =>
            {
                authorizationCalls++;
                return Task.FromResult(new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    "validation listener ownership lost"));
            };
            client.AuthenticationFailed += (_, message) => failure = message;
            client.StatusChanged += (_, status) => lastStatus = status;

            await InvokeSendConnectSafeAsync(client);

            Assert.Equal(1, authorizationCalls);
            Assert.Equal("validation listener ownership lost", failure);
            Assert.Equal(ConnectionStatus.Error, lastStatus);
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

    private static IReadOnlyDictionary<string, string> GetAuthPayload(OpenClawGatewayClient client)
    {
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "BuildAuthPayload",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(method.Invoke(client, null));
    }

    private static bool TryStoreHandshakeDeviceToken(
        OpenClawGatewayClient client,
        string role,
        string token)
    {
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "TryStoreHandshakeDeviceToken",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(client, [role, token, null]));
    }

    private static async Task InvokeSendConnectSafeAsync(OpenClawGatewayClient client)
    {
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "SendConnectSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(client, [null, 0L]));
        await task;
    }
}
