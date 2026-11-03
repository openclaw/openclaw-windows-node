using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeviceDecision_WaitsForAckAndRefreshesAuthoritativeList(bool approve)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateDevicePairing(new DevicePairingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var list = Signal<DevicePairingListInfo>();
        client.DevicePairListUpdated += (_, value) => list.TrySetResult(value);
        await client.RequestDevicePairListAsync();
        var pending = Assert.Single((await list.Task.WaitAsync(Deadline)).Pending);
        Assert.Equal("fixture-device-request", pending.RequestId);
        Assert.Equal("fixture-other-device", pending.DeviceId);
        Assert.NotNull(pending.Scopes);
        Assert.Equal(["operator.read"], pending.Scopes);

        var refreshed = Signal<DevicePairingListInfo>();
        client.DevicePairListUpdated += (_, value) => refreshed.TrySetResult(value);
        server.HoldDeviceDecisions();
        var decision = Decide(client, approve, "fixture-device-request");
        var method = approve ? "device.pair.approve" : "device.pair.reject";
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync(method, cancellationToken: deadline.Token);
        await client.SendWizardRequestAsync("health");
        Assert.False(decision.IsCompleted);
        server.ReleaseDeviceDecisions();
        Assert.True(await decision.WaitAsync(Deadline));
        Assert.Empty((await refreshed.Task.WaitAsync(Deadline)).Pending);
        var authoritative = await client.SendWizardRequestAsync("device.pair.list", new { });
        Assert.Equal(approve ? 1 : 0, authoritative.GetProperty("paired").GetArrayLength());
        Assert.Single(server.Requests, request => request.Method == method);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(true, "operator.admin", true, "unknown")]
    [InlineData(false, "operator.admin", true, "unknown")]
    [InlineData(true, "operator.pairing", true, "fixture-device-request")]
    [InlineData(false, "operator.pairing", true, "fixture-device-request")]
    [InlineData(true, "operator.read", true, "fixture-device-request")]
    [InlineData(false, "operator.read", true, "fixture-device-request")]
    [InlineData(true, "operator.admin", false, "fixture-device-request")]
    [InlineData(false, "operator.admin", false, "fixture-device-request")]
    public async Task DeviceDecision_GatewayDenialIsNotSuccess(
        bool approve, string scope, bool supported, string requestId)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateDevicePairing(new DevicePairingFixture(scope, supported)), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.HoldDeviceDecisions();
        var decision = Decide(connected.Client, approve, requestId);
        var method = approve ? "device.pair.approve" : "device.pair.reject";
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync(method, cancellationToken: deadline.Token);
        await connected.Client.SendWizardRequestAsync("health");
        Assert.False(decision.IsCompleted);
        server.ReleaseDeviceDecisions();
        Assert.False(await decision.WaitAsync(Deadline));
        var rejected = Assert.Single(server.Requests, request => request.Method == method);
        Assert.Equal(supported ? "error:INVALID_REQUEST" : "error:METHOD_NOT_FOUND", rejected.Outcome);
        Assert.DoesNotContain(server.Requests, request => request.Method == "device.pair.list");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeviceDecision_LostAckIsNotReplayedOnReconnect(bool approve)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateDevicePairing(new DevicePairingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.SessionMutationConnectionEpoch;
        server.HoldDeviceDecisions();
        var method = approve ? "device.pair.approve" : "device.pair.reject";
        var decision = Decide(client, approve, "fixture-device-request");
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync(method, cancellationToken: deadline.Token);
        // This read also proves the mutation landed before losing its acknowledgement.
        var after = await client.SendWizardRequestAsync("device.pair.list", new { });
        Assert.Empty(after.GetProperty("pending").EnumerateArray());
        Assert.Equal(approve ? 1 : 0, after.GetProperty("paired").GetArrayLength());
        await client.DisconnectAsync();
        Assert.False(await decision.WaitAsync(Deadline));
        server.ReleaseDeviceDecisions();
        var reconnected = Signal<bool>();
        client.HandshakeSucceeded += (_, _) => reconnected.TrySetResult(true);
        await client.ConnectAsync();
        await reconnected.Task.WaitAsync(Deadline);
        await client.SendWizardRequestAsync("health");
        Assert.NotEqual(epoch, client.SessionMutationConnectionEpoch);
        Assert.Single(server.Requests, request => request.Method == method);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, " ")]
    public async Task DeviceDecision_InvalidTargetDoesNotDispatch(bool approve, string requestId)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateDevicePairing(new DevicePairingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.False(await Decide(connected.Client, approve, requestId));
        await connected.Client.SendWizardRequestAsync("health");
        Assert.DoesNotContain(server.Requests,
            request => request.Method is "device.pair.approve" or "device.pair.reject");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeviceDecision_TimeoutAndLateAckDoNotConfirmOrReplay(bool approve)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateDevicePairing(new DevicePairingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.HoldDeviceDecisions();
        var method = approve ? "device.pair.approve" : "device.pair.reject";
        var decision = Decide(connected.Client, approve, "fixture-device-request");
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync(method, cancellationToken: deadline.Token);

        Assert.False(await decision.WaitAsync(Deadline));
        server.ReleaseDeviceDecisions();
        await server.WaitForCompletedRequestAsync(method, cancellationToken: deadline.Token);
        await connected.Client.SendWizardRequestAsync("health");
        Assert.DoesNotContain(server.Requests, request => request.Method == "device.pair.list");
        Assert.Single(server.Requests, request => request.Method == method);
        var after = await connected.Client.SendWizardRequestAsync("device.pair.list", new { });
        Assert.Empty(after.GetProperty("pending").EnumerateArray());
        Assert.Equal(approve ? 1 : 0, after.GetProperty("paired").GetArrayLength());
        Assert.Empty(server.UnexpectedRequests);
    }

    private static Task<bool> Decide(OpenClawGatewayClient client, bool approve, string requestId) =>
        approve ? client.DevicePairApproveAsync(requestId) : client.DevicePairRejectAsync(requestId);
}
