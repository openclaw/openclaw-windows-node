using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class WindowsClientMetadataTests
{
    [Fact]
    public async Task OperatorConnect_SerializesCanonicalMetadataAndSignsTheSameV3Metadata()
    {
        var dataPath = CreateTempDirectory();
        try
        {
            using var client = new CapturingGatewayClient(dataPath, "shared-token");

            using var message = JsonDocument.Parse(await client.BuildConnectMessageAsync("nonce-v3"));
            var parameters = message.RootElement.GetProperty("params");
            var clientMetadata = parameters.GetProperty("client");

            AssertCanonicalMetadata(clientMetadata);
            Assert.Equal(
                BuildExpectedSignature(client, parameters, useV2: false),
                parameters.GetProperty("device").GetProperty("signature").GetString());
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task OperatorAndNodeConnect_SharedIdentityEmitIdenticalCanonicalMetadata()
    {
        var dataPath = CreateTempDirectory();
        try
        {
            using var operatorClient = new CapturingGatewayClient(dataPath, "shared-token");
            using var nodeClient = new WindowsNodeClient("ws://localhost:18789", "shared-token", dataPath);

            using var operatorMessage = JsonDocument.Parse(
                await operatorClient.BuildConnectMessageAsync("operator-nonce"));
            using var nodeMessage = JsonDocument.Parse(BuildNodeConnectMessage(nodeClient, "node-nonce"));
            var operatorParameters = operatorMessage.RootElement.GetProperty("params");
            var nodeParameters = nodeMessage.RootElement.GetProperty("params");

            Assert.Equal(
                operatorParameters.GetProperty("device").GetProperty("id").GetString(),
                nodeParameters.GetProperty("device").GetProperty("id").GetString());
            Assert.Equal(
                operatorParameters.GetProperty("client").GetProperty("platform").GetString(),
                nodeParameters.GetProperty("client").GetProperty("platform").GetString());
            Assert.Equal(
                operatorParameters.GetProperty("client").GetProperty("deviceFamily").GetString(),
                nodeParameters.GetProperty("client").GetProperty("deviceFamily").GetString());
            AssertCanonicalMetadata(operatorParameters.GetProperty("client"));
            AssertCanonicalMetadata(nodeParameters.GetProperty("client"));
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task OperatorReconnect_AfterRestartPreservesIdentityTokenSourceAndMetadata()
    {
        var dataPath = CreateTempDirectory();
        try
        {
            var identity = new DeviceIdentity(dataPath);
            identity.Initialize();
            identity.StoreDeviceTokenWithScopes("operator-device-token", ["operator.read"]);

            using var firstClient = new CapturingGatewayClient(dataPath, "shared-token");
            using var firstMessage = JsonDocument.Parse(
                await firstClient.BuildConnectMessageAsync("before-restart"));

            using var restartedClient = new CapturingGatewayClient(dataPath, "different-shared-token");
            using var restartedMessage = JsonDocument.Parse(
                await restartedClient.BuildConnectMessageAsync("after-restart"));

            var before = firstMessage.RootElement.GetProperty("params");
            var after = restartedMessage.RootElement.GetProperty("params");

            Assert.Equal(
                before.GetProperty("device").GetProperty("id").GetString(),
                after.GetProperty("device").GetProperty("id").GetString());
            Assert.Equal(
                "operator-device-token",
                before.GetProperty("auth").GetProperty("deviceToken").GetString());
            Assert.Equal(
                "operator-device-token",
                after.GetProperty("auth").GetProperty("deviceToken").GetString());
            Assert.False(after.GetProperty("auth").TryGetProperty("token", out _));
            Assert.False(after.GetProperty("auth").TryGetProperty("bootstrapToken", out _));
            AssertCanonicalMetadata(before.GetProperty("client"));
            AssertCanonicalMetadata(after.GetProperty("client"));
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task OperatorConnect_V2CompatibilityKeepsProtocolAndCanonicalSerializedMetadata()
    {
        var dataPath = CreateTempDirectory();
        try
        {
            using var client = new CapturingGatewayClient(
                dataPath,
                "bootstrap-token",
                tokenIsBootstrapToken: true);

            using var message = JsonDocument.Parse(await client.BuildConnectMessageAsync("nonce-v2"));
            var parameters = message.RootElement.GetProperty("params");

            Assert.True(client.UseV2Signature);
            Assert.Equal(3, parameters.GetProperty("minProtocol").GetInt32());
            Assert.Equal(4, parameters.GetProperty("maxProtocol").GetInt32());
            AssertCanonicalMetadata(parameters.GetProperty("client"));
            Assert.Equal(
                BuildExpectedSignature(client, parameters, useV2: true),
                parameters.GetProperty("device").GetProperty("signature").GetString());
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static string BuildExpectedSignature(
        CapturingGatewayClient client,
        JsonElement parameters,
        bool useV2)
    {
        var clientMetadata = parameters.GetProperty("client");
        var device = parameters.GetProperty("device");
        var auth = parameters.GetProperty("auth");
        var scopes = parameters.GetProperty("scopes")
            .EnumerateArray()
            .Select(scope => scope.GetString()!)
            .ToArray();
        var token = auth.EnumerateObject().Single().Value.GetString()!;
        var nonce = device.GetProperty("nonce").GetString()!;
        var signedAt = device.GetProperty("signedAt").GetInt64();
        var clientId = clientMetadata.GetProperty("id").GetString()!;
        var clientMode = clientMetadata.GetProperty("mode").GetString()!;
        var role = parameters.GetProperty("role").GetString()!;
        var identity = client.DeviceIdentity;

        return useV2
            ? identity.SignConnectPayloadV2(
                nonce,
                signedAt,
                clientId,
                clientMode,
                role,
                scopes,
                token)
            : identity.SignConnectPayloadV3(
                nonce,
                signedAt,
                clientId,
                clientMode,
                role,
                scopes,
                token,
                clientMetadata.GetProperty("platform").GetString()!,
                clientMetadata.GetProperty("deviceFamily").GetString()!);
    }

    private static string BuildNodeConnectMessage(WindowsNodeClient client, string nonce)
    {
        var method = typeof(WindowsNodeClient).GetMethod(
            "BuildNodeConnectMessage",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string)method!.Invoke(client, [nonce, 0L])!;
    }

    private static void AssertCanonicalMetadata(JsonElement clientMetadata)
    {
        Assert.Equal("windows", WindowsClientMetadata.Platform);
        Assert.Equal("Windows", WindowsClientMetadata.DeviceFamily);
        Assert.Equal("windows", clientMetadata.GetProperty("platform").GetString());
        Assert.Equal("Windows", clientMetadata.GetProperty("deviceFamily").GetString());
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-client-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingGatewayClient(
        string dataPath,
        string token,
        bool tokenIsBootstrapToken = false)
        : OpenClawGatewayClient(
            "ws://localhost:18789",
            token,
            logger: null,
            tokenIsBootstrapToken,
            identityPath: dataPath)
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public DeviceIdentity DeviceIdentity
        {
            get
            {
                var identityField = typeof(OpenClawGatewayClient).GetField(
                    "_deviceIdentity",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(identityField);
                return (DeviceIdentity)identityField!.GetValue(this)!;
            }
        }

        public async Task<string> BuildConnectMessageAsync(string nonce)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "SendConnectMessageAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await (Task)method!.Invoke(this, [nonce])!;
            return Assert.Single(_messages);
        }

        protected override Task SendRawAsync(string message)
        {
            _messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
