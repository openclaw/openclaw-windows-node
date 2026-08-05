using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests.Architecture;

public sealed class GatewayProtocolCoreClosureTests
{
    [Fact]
    public void GatewayClients_DoNotReintroduce_InlineConnectEnvelopeConstruction()
    {
        var operatorClient = GetSource("OpenClawGatewayClient.cs");
        var nodeClient = GetSource("WindowsNodeClient.cs");
        var builder = GetSource("ConnectEnvelopeBuilder.cs");
        var clients = new[] { operatorClient, nodeClient };

        var directIdentityProtocolCall = new Regex(
            @"\.\s*(?:SignConnectPayloadV2|SignConnectPayloadV3|BuildConnectPayloadV2|BuildConnectPayloadV3)\s*\(");
        var inlineProtocolRange = new Regex(@"\b(?:minProtocol|maxProtocol)\s*=");
        var directAuthAssignment = new Regex(
            @"\bauth\s*\[\s*""(?:token|bootstrapToken|deviceToken)""\s*\]\s*=");

        foreach (var client in clients)
        {
            Assert.DoesNotMatch(directIdentityProtocolCall, client.Text);
            Assert.DoesNotMatch(inlineProtocolRange, client.Text);
            Assert.DoesNotMatch(directAuthAssignment, client.Text);
        }

        Assert.Contains("ConnectEnvelopeBuilder.PrepareOperator(", operatorClient.Text);
        Assert.Contains("ConnectEnvelopeBuilder.PrepareNode(", nodeClient.Text);

        Assert.Matches(@"\bminProtocol\s*=\s*3\b", builder.Text);
        Assert.Matches(@"\bmaxProtocol\s*=\s*4\b", builder.Text);
        Assert.Matches(@"\.\s*SignConnectPayloadV2\s*\(", builder.Text);
        Assert.Matches(@"\.\s*SignConnectPayloadV3\s*\(", builder.Text);
        Assert.Matches(@"\.\s*BuildConnectPayloadV2\s*\(", builder.Text);
        Assert.Matches(@"\.\s*BuildConnectPayloadV3\s*\(", builder.Text);
        Assert.Contains("auth = _credential.ToAuthPayload()", builder.Text);
    }

    [Fact]
    public void OpenClawGatewayClient_DoesNotReintroduce_InlinePendingRequestTracking()
    {
        var client = GetSource("OpenClawGatewayClient.cs");
        string[] prohibitedIdentifiers =
        [
            "_pendingRequestMethods",
            "_pendingChatSendRequests",
            "_pendingWizardResponses",
            "_pendingApprovalResolves",
            "_pendingRequestLock",
            "_pendingChatSendLock",
            "TrackPendingRequest",
            "TakePendingRequestMethod",
            "ClearPendingRequests",
        ];

        foreach (var identifier in prohibitedIdentifiers)
        {
            Assert.DoesNotMatch($@"\b{Regex.Escape(identifier)}\b", client.Text);
        }

        Assert.DoesNotMatch(
            @"\b(?:Track|Remove|Take)PendingChatSend[A-Za-z0-9_]*\b",
            client.Text);

        Assert.Matches(
            @"private\s+readonly\s+PendingRequestRegistry\s+_pendingRequests\s*=\s*new\s*\(\s*\)\s*;",
            client.Text);
        Assert.Contains("_pendingRequests.OpenConnection()", client.Text);
        Assert.Contains("_pendingRequests.Drain()", client.Text);
        Assert.Contains("_pendingRequests.RegisterTracked(", client.Text);
        Assert.Contains("_pendingRequests.RegisterChatSend(", client.Text);
        Assert.Contains("_pendingRequests.RegisterWizard(", client.Text);
        Assert.Contains("_pendingRequests.RegisterApproval(", client.Text);
        Assert.Contains("_pendingRequests.TryTake(", client.Text);
        Assert.Contains("_pendingRequests.TryRemove(", client.Text);
    }

    private static SourceFileSnapshot GetSource(string fileName)
    {
        var matches = ProductionSourceFiles.All
            .Where(file => file.Path.EndsWith(fileName, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(matches);
    }
}
