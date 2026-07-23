using System.Text.Json;
using OpenClaw.E2ETests;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

[CollectionDefinition("E2E Recovery", DisableParallelization = true)]
public class E2ERecoveryCollection : ICollectionFixture<E2ESetupFixture> { }

[Collection("E2E Recovery")]
public class RevocationAndRecoveryTests
{
    private readonly E2ESetupFixture _fixture;

    public RevocationAndRecoveryTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public async Task RealGateway_DeviceRemoval_RecoversThroughSharedTokenReconnect()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        Assert.False(string.IsNullOrWhiteSpace(gateway.SharedGatewayToken));
        var deviceId = _fixture.ReadActiveGatewayDeviceId();
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = gateway.SharedGatewayToken! };
        var removed = false;
        var recovered = false;

        try
        {
            var removeDevice = await _fixture.RunInWslAsync(
                $"openclaw devices remove {ShellSingleQuote(deviceId)} --json",
                TimeSpan.FromSeconds(30),
                env);
            AssertCommandSucceeded(removeDevice, "remove paired device entry");
            removed = true;
            Console.WriteLine($"[E2E] remove paired device:\n{removeDevice.Stdout}");

            using var connectDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.connection.connectSharedToken",
                new
                {
                    gatewayUrl = gateway.GatewayUrl,
                    token = gateway.SharedGatewayToken
                });
            var connect = connectDoc.RootElement;
            Console.WriteLine($"[E2E] reconnect after device removal response: {connect.GetRawText()}");
            Assert.Equal("ConnectionFailed", connect.GetProperty("outcome").GetString());

            var pendingDevices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
            AssertCommandSucceeded(pendingDevices, "list pending device approval after removal");
            var requestId = ReadFirstPendingRequestId(pendingDevices.Stdout);
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var approve = await _fixture.RunInWslAsync(
                $"openclaw devices approve {ShellSingleQuote(requestId)} --json",
                TimeSpan.FromSeconds(30),
                env);
            AssertCommandSucceeded(approve, "approve replacement device pairing request");
            Console.WriteLine($"[E2E] approve replacement device:\n{approve.Stdout}");

            using var reconnectDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.connection.connectSharedToken",
                new
                {
                    gatewayUrl = gateway.GatewayUrl,
                    token = gateway.SharedGatewayToken
                });
            var reconnect = reconnectDoc.RootElement;
            Console.WriteLine($"[E2E] reconnect after approval response: {reconnect.GetRawText()}");
            Assert.Equal("Success", reconnect.GetProperty("outcome").GetString());

            var credentials = await _fixture.WaitForDurablePairedCredentialsAsync();
            Assert.True(credentials.HasOperatorToken, $"Expected replacement operator token in {credentials.IdentityDir}");
            Assert.True(credentials.HasNodeToken, $"Expected replacement node token in {credentials.IdentityDir}");

            await ApprovePendingNodeTrustAndReconnectAsync(deviceId);
            await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(90));
            using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
            AssertReadyStatus(statusDoc.RootElement);
            AssertOperatorCanApproveNodeTrust(statusDoc.RootElement);
            await AssertGatewayCliStateHealthy();
            recovered = true;
        }
        finally
        {
            if (removed && !recovered)
                await TryRecoverDeviceAfterRemovalAsync(gateway, env);
        }
    }

    private async Task ApprovePendingNodeTrustAndReconnectAsync(string nodeId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string lastOutput = "<none>";
        string? requestId = null;

        while (DateTime.UtcNow < deadline && requestId is null)
        {
            using var approvals = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.connection.pendingApprovals");
            lastOutput = approvals.RootElement.GetRawText();
            requestId = ReadPendingNodeTrustRequestId(approvals.RootElement, nodeId);
            if (requestId is null)
                await Task.Delay(500);
        }

        Assert.False(
            string.IsNullOrWhiteSpace(requestId),
            $"Expected a pending node-trust request for {nodeId} after device recovery. Last output: {lastOutput}");

        using (var approve = await _fixture.Client!.CallToolExpectSuccessAsync(
                   "app.connection.approveNodePairing",
                   new { requestId }))
        {
            var decision = approve.RootElement.GetProperty("decision");
            Assert.Equal("node", decision.GetProperty("kind").GetString());
            Assert.Equal("approve", decision.GetProperty("action").GetString());
            Assert.Equal(requestId, decision.GetProperty("requestId").GetString());
            Assert.True(decision.GetProperty("succeeded").GetBoolean());
        }

        using var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.reconnectNode");
        Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
    }

    private static string? ReadPendingNodeTrustRequestId(JsonElement root, string nodeId)
    {
        if (!root.TryGetProperty("nodePending", out var pending) ||
            pending.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var request in pending.EnumerateArray())
        {
            if (!request.TryGetProperty("nodeId", out var requestNodeId) ||
                !string.Equals(requestNodeId.GetString(), nodeId, StringComparison.OrdinalIgnoreCase) ||
                !request.TryGetProperty("requestId", out var requestId))
            {
                continue;
            }

            return requestId.GetString();
        }

        return null;
    }

    private static void AssertCommandSucceeded(CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static void AssertReadyStatus(JsonElement root)
    {
        var rawJson = root.GetRawText();
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");
        Assert.True(root.GetProperty("nodeConnected").GetBoolean(), $"nodeConnected should be true; full status: {rawJson}");
        Assert.True(root.GetProperty("nodePaired").GetBoolean(), $"nodePaired should be true; full status: {rawJson}");
    }

    private static void AssertOperatorCanApproveNodeTrust(JsonElement root)
    {
        var rawJson = root.GetRawText();
        Assert.True(root.TryGetProperty("operatorScopes", out var scopes), $"operatorScopes missing from app.status: {rawJson}");
        var values = ReadStringArray(scopes);
        Assert.Contains(values, scope => string.Equals(scope, "operator.admin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(values, scope => string.Equals(scope, "operator.pairing", StringComparison.OrdinalIgnoreCase));
    }

    private async Task AssertGatewayCliStateHealthy()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        Assert.False(string.IsNullOrWhiteSpace(gateway.SharedGatewayToken));
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = gateway.SharedGatewayToken! };

        var devices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(devices, "list gateway devices after reconnect");
        AssertNoPendingRequests(devices.Stdout);

        var nodes = await _fixture.RunInWslAsync("openclaw nodes list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(nodes, "list gateway nodes after reconnect");
        Assert.Contains("windows", nodes.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryRecoverDeviceAfterRemovalAsync(
        (string GatewayUrl, string? SharedGatewayToken, string ActiveId) gateway,
        Dictionary<string, string> env)
    {
        try
        {
            var pending = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
            if (pending.ExitCode == 0)
            {
                var requestId = TryReadFirstPendingRequestId(pending.Stdout);
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    _ = await _fixture.RunInWslAsync(
                        $"openclaw devices approve {ShellSingleQuote(requestId)} --json",
                        TimeSpan.FromSeconds(30),
                        env);
                }
            }

            _ = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.connection.connectSharedToken",
                new
                {
                    gatewayUrl = gateway.GatewayUrl,
                    token = gateway.SharedGatewayToken
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[E2E] Best-effort recovery after device removal failed: {ex.Message}");
        }
    }

    private static string ReadFirstPendingRequestId(string output)
    {
        using var doc = JsonDocument.Parse(ExtractJsonObject(output));
        Assert.True(doc.RootElement.TryGetProperty("pending", out var pending), $"Missing pending array: {output}");
        Assert.Equal(JsonValueKind.Array, pending.ValueKind);
        Assert.True(pending.GetArrayLength() > 0, $"Expected at least one pending request: {output}");
        var request = pending[0];
        Assert.True(request.TryGetProperty("requestId", out var requestId), $"Missing requestId: {request.GetRawText()}");
        return requestId.GetString() ?? string.Empty;
    }

    private static string? TryReadFirstPendingRequestId(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(output));
            if (!doc.RootElement.TryGetProperty("pending", out var pending) ||
                pending.ValueKind != JsonValueKind.Array ||
                pending.GetArrayLength() == 0)
            {
                return null;
            }

            var request = pending[0];
            return request.TryGetProperty("requestId", out var requestId)
                ? requestId.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AssertNoPendingRequests(string output)
    {
        using var doc = JsonDocument.Parse(ExtractJsonObject(output));
        if (doc.RootElement.TryGetProperty("pending", out var pending))
        {
            Assert.Equal(JsonValueKind.Array, pending.ValueKind);
            Assert.Equal(0, pending.GetArrayLength());
        }
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        return element.EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"Expected JSON object in output: {output}");
        return output[start..(end + 1)];
    }

    private static string ShellSingleQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
