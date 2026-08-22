using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public class GatewayProtocolContractTests
{
    private sealed class CapturingGatewayClient(string identityPath)
        : OpenClawGatewayClient(
            "ws://localhost:18789",
            "test-token",
            identityPath: identityPath)
    {
        public ConcurrentQueue<string> SentMessages { get; } = new();

        protected override Task SendRawAsync(string message)
        {
            SentMessages.Enqueue(message);
            return Task.CompletedTask;
        }

        protected override Task<bool> SendRawAsync(
            string message,
            long expectedConnectionGeneration,
            CancellationToken cancellationToken)
        {
            SentMessages.Enqueue(message);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void SupportedRange_IsThreeThroughFour()
    {
        Assert.Equal(4, GatewayProtocolContract.SupportedVersion);
        Assert.Equal(4, GatewayProtocolContract.CurrentVersion);
        Assert.Equal(3, GatewayProtocolContract.MinimumSupportedVersion);
        Assert.Equal(4, GatewayProtocolContract.MaximumSupportedVersion);
    }

    [Fact]
    public void Clients_use_shared_contract_for_protocol_range()
    {
        var repositoryRoot = ProductionSourceFiles.FindRepoRoot();

        var source = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "OpenClaw.Shared",
                "ConnectEnvelopeBuilder.cs"));
        Assert.Contains("GatewayProtocolContract.MinimumSupportedVersion", source, StringComparison.Ordinal);
        Assert.Contains("GatewayProtocolContract.MaximumSupportedVersion", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"minProtocol\s*=\s*\d", source);
        Assert.DoesNotMatch(@"maxProtocol\s*=\s*\d", source);
    }

    [Fact]
    public async Task ClientConnectMessages_UseContractRange()
    {
        using var operatorIdentity = new TempDirectory("gateway-protocol-operator-");
        using var operatorClient = new CapturingGatewayClient(operatorIdentity.Path);
        var sendConnect = typeof(OpenClawGatewayClient).GetMethod(
            "SendConnectMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(sendConnect);
        await (Task)sendConnect!.Invoke(
            operatorClient,
            [null, 0L, CancellationToken.None])!;
        Assert.True(operatorClient.SentMessages.TryDequeue(out var operatorMessage));

        using var nodeIdentity = new TempDirectory("gateway-protocol-node-");
        using var nodeClient = new WindowsNodeClient(
            "ws://localhost:18789",
            "test-token",
            nodeIdentity.Path);
        var buildNodeConnect = typeof(WindowsNodeClient).GetMethod(
            "BuildNodeConnectMessage",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(buildNodeConnect);
        var nodeMessage = (string)buildNodeConnect!.Invoke(nodeClient, ["nonce", null, null])!;

        AssertProtocolRange(operatorMessage!);
        AssertProtocolRange(nodeMessage);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HelloOkProtocolAtOrAboveMinimum_AllowsAdditiveFields(int protocol)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "type": "hello-ok",
              "protocol": {{protocol}},
              "snapshot": {
                "futureField": {
                  "nested": true
                }
              },
              "anotherFutureField": [1, 2, 3]
            }
            """);

        Assert.True(
            GatewayProtocolContract.TryValidateHelloOk(
                document.RootElement,
                out var error),
            error);
    }

    [Theory]
    [InlineData("""{"type":"hello-ok","protocol":2}""")]
    [InlineData("""{"type":"hello-ok"}""")]
    [InlineData("""{"type":"hello-ok","protocol":null}""")]
    [InlineData("""{"type":"hello-ok","protocol":"4"}""")]
    [InlineData("""{"type":"hello-ok","protocol":4.5}""")]
    [InlineData("""{"type":"not-hello","protocol":4}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void InvalidHelloOk_IsRejected(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        Assert.False(
            GatewayProtocolContract.TryValidateHelloOk(
                document.RootElement,
                out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(2, GatewayProtocolCompatibilityState.GatewayTooOld)]
    [InlineData(4, GatewayProtocolCompatibilityState.Mismatch)]
    [InlineData(5, GatewayProtocolCompatibilityState.GatewayTooNew)]
    public void StructuredMismatch_ParsesSanitizedProtocolDetails(
        int expectedProtocol,
        GatewayProtocolCompatibilityState expectedState)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "error": {
                "code": "INVALID_REQUEST",
                "message": "protocol mismatch",
                "details": {
                  "code": "PROTOCOL_MISMATCH",
                  "clientMinProtocol": 3,
                  "clientMaxProtocol": 4,
                  "expectedProtocol": {{expectedProtocol}},
                  "minimumProbeProtocol": 2,
                  "ignoredRawDetail": "not propagated"
                }
              }
            }
            """);

        var compatibility = GatewayProtocolContract.ParseMismatch(document.RootElement);

        Assert.Equal(expectedState, compatibility.State);
        Assert.Equal(expectedProtocol, compatibility.GatewayExpectedProtocol);
        Assert.Equal(2, compatibility.GatewayMinimumProtocol);
        Assert.Equal(3, compatibility.ClientMinimumProtocol);
        Assert.Equal(4, compatibility.ClientMaximumProtocol);
        Assert.False(compatibility.Retryable);
    }

    [Fact]
    public void NestedStructuredMismatch_ParsesAvailableExpectation()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "error": {
                "message": "connect rejected",
                "data": {
                  "details": {
                    "code": "PROTOCOL_MISMATCH",
                    "expectedProtocol": 5,
                    "minimumProbeProtocol": 3
                  }
                }
              }
            }
            """);

        var compatibility = GatewayProtocolContract.ParseMismatch(document.RootElement);

        Assert.Equal(GatewayProtocolCompatibilityState.GatewayTooNew, compatibility.State);
        Assert.Equal(5, compatibility.GatewayExpectedProtocol);
        Assert.Equal(3, compatibility.GatewayMinimumProtocol);
    }

    [Fact]
    public void GenericMismatch_HasFiniteUnknownDetails()
    {
        using var document = JsonDocument.Parse(
            """{"error":{"message":"protocol mismatch"}}""");

        var compatibility = GatewayProtocolContract.ParseMismatch(document.RootElement);

        Assert.Equal(GatewayProtocolCompatibilityState.Mismatch, compatibility.State);
        Assert.Null(compatibility.GatewayProtocol);
        Assert.Equal("mismatch", compatibility.NormalizedState);
        Assert.False(compatibility.Retryable);
    }

    private static void AssertProtocolRange(string message)
    {
        using var document = JsonDocument.Parse(message);
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal(
            GatewayProtocolContract.MinimumSupportedVersion,
            parameters.GetProperty("minProtocol").GetInt32());
        Assert.Equal(
            GatewayProtocolContract.MaximumSupportedVersion,
            parameters.GetProperty("maxProtocol").GetInt32());
    }
}
