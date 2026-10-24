using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Theory]
    [InlineData("operator.read")]
    [InlineData("operator.write")]
    [InlineData("operator.admin")]
    public async Task LastHeartbeat_AuthenticatedReadUsesEmptyParamsAndNeverMutates(string scope)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateHeartbeat(scopes: [scope]), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        server.HoldHeartbeatReads();
        var read = client.GetLastHeartbeatAsync(client.SessionMutationConnectionEpoch!.Value);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("last-heartbeat", null, cancellationToken: deadline.Token);
        await client.SendWizardRequestAsync("health");
        Assert.False(read.IsCompleted);
        server.ReleaseHeartbeatReads();
        var result = Assert.IsType<GatewayHeartbeatEvent>(await read.WaitAsync(Deadline));
        Assert.Equal(1791257718360, result.Timestamp);
        Assert.Equal("skipped", result.Status);
        Assert.Equal("target-none", result.Reason);
        Assert.Equal(42.5, result.DurationMs);
        Assert.Equal(1, server.SharedCredentialConnects);
        Assert.Single(server.Requests, request => request.Method == "last-heartbeat");
        Assert.DoesNotContain(server.Requests, request => request.Method is "set-heartbeats" or "system-event" or "wake");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task LastHeartbeat_NullReplyIsNoRecordedEvent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateHeartbeat(
            JsonSerializer.SerializeToElement<object?>(null)), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.Null(await connected.Client.GetLastHeartbeatAsync(connected.Client.SessionMutationConnectionEpoch!.Value));
        Assert.Single(server.Requests, request => request.Method == "last-heartbeat");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("INVALID_REQUEST")]
    [InlineData("UNAVAILABLE")]
    public async Task LastHeartbeat_GatewayErrorIsNotNoRecord(string error)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateHeartbeat(errorCode: error), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.GetLastHeartbeatAsync(connected.Client.SessionMutationConnectionEpoch!.Value));
        Assert.Contains(server.Requests, request => request.Method == "last-heartbeat" && request.Outcome == "error:" + error);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task LastHeartbeat_MalformedReplyDoesNotBecomeEmptyOrSuccess()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateHeartbeat(JsonSerializer.SerializeToElement(new { status = "sent" })), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await Assert.ThrowsAsync<JsonException>(() =>
            connected.Client.GetLastHeartbeatAsync(connected.Client.SessionMutationConnectionEpoch!.Value));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("scope")]
    [InlineData("epoch")]
    [InlineData("cancelled")]
    public async Task LastHeartbeat_PreflightDenialSendsNoFrame(string denial)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateHeartbeat(
            scopes: denial == "scope" ? ["operator.sessions.read"] : ["operator.read"],
            advertise: denial != "unsupported"), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var cancellation = new CancellationTokenSource();
        if (denial == "cancelled") cancellation.Cancel();
        var epoch = connected.Client.SessionMutationConnectionEpoch!.Value + (denial == "epoch" ? 1 : 0);
        var failure = await Record.ExceptionAsync(() => connected.Client.GetLastHeartbeatAsync(epoch, cancellation.Token));
        Assert.NotNull(failure);
        if (denial == "unsupported") Assert.IsType<NotSupportedException>(failure);
        if (denial == "scope") Assert.IsType<UnauthorizedAccessException>(failure);
        if (denial == "epoch") Assert.IsType<InvalidOperationException>(failure);
        if (denial == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        await connected.Client.SendWizardRequestAsync("health");
        Assert.DoesNotContain(server.Requests, request => request.Method == "last-heartbeat");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task LastHeartbeat_CancellationRejectsLateResponseAndDoesNotReplay()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateHeartbeat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        server.HoldHeartbeatReads();
        using var cancellation = new CancellationTokenSource();
        var read = client.GetLastHeartbeatAsync(client.SessionMutationConnectionEpoch!.Value, cancellation.Token);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("last-heartbeat", null, cancellationToken: deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Deadline));
        server.ReleaseHeartbeatReads();
        await client.SendWizardRequestAsync("health");
        Assert.Single(server.Requests, request => request.Method == "last-heartbeat");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task LastHeartbeat_DisconnectRejectsHeldReadAndOldEpochOnSamePhysicalClient()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateHeartbeat(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        server.HoldHeartbeatReads();
        var read = client.GetLastHeartbeatAsync(epoch);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("last-heartbeat", null, cancellationToken: deadline.Token);
        await client.DisconnectAsync();
        Assert.NotNull(await Record.ExceptionAsync(() => read.WaitAsync(Deadline)));
        server.ReleaseHeartbeatReads();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => ready.TrySetResult();
        await client.ConnectAsync();
        await ready.Task.WaitAsync(Deadline);
        Assert.NotEqual(epoch, client.SessionMutationConnectionEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLastHeartbeatAsync(epoch));
        await client.SendWizardRequestAsync("health");
        Assert.Single(server.Requests, request => request.Method == "last-heartbeat");
        Assert.Empty(server.UnexpectedRequests);
    }
}
