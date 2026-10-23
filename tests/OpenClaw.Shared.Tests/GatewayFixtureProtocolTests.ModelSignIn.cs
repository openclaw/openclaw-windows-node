using System.Text.Json;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task ModelSignIn_RealClientAcceptsOmittedOptionalWizardStatusWithoutSuccessClaim()
    {
        var fixture = new ModelSignInFixture { OmitWizardStatus = true, StartWithProgress = true };
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        await owner.StartAsync(ModelSignInFixture.Choice);
        Assert.Equal("fixture-question", owner.Step!.Id);
        await owner.AnswerAsync(JsonSerializer.SerializeToElement(ModelSignInFixture.Answer));
        Assert.Equal(ModelSignInNotice.Ended, owner.Notice);
        Assert.Equal("ok", Assert.Single(owner.AuthStatus!.Providers).Status);
        Assert.Equal(2, fixture.Requests.Count(request => request.Method == "models.authStatus"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ModelSignIn_RealClientCloseDuringHeldAdmissionClosesSameWizardAfterLateReply()
    {
        var fixture = new ModelSignInFixture();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        server.HoldModelSignInReply("models.authLogin");
        var start = owner.StartAsync(ModelSignInFixture.Choice);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("models.authLogin", cancellationToken: deadline.Token);
        var id = owner.SessionId;
        await owner.CloseAsync();
        Assert.Equal(id, owner.SessionId);
        Assert.Equal(ModelSignInNotice.CancellationUnconfirmed, owner.Notice);
        server.ReleaseModelSignInReply("models.authLogin");
        await start.WaitAsync(Deadline);
        Assert.Null(owner.SessionId);
        Assert.Null(owner.Step);
        Assert.Null(owner.AuthStatus);
        Assert.Equal(2, fixture.Requests.Count(request => request.Method == "wizard.cancel"));
        Assert.All(fixture.Requests.Where(request => request.Method == "wizard.cancel"), request =>
            Assert.Equal(id, request.Parameters.GetProperty("sessionId").GetString()));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ModelSignIn_RealClientLostAdmissionReplyRetainsIdWithoutReplay()
    {
        var fixture = new ModelSignInFixture();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        server.HoldModelSignInReply("models.authLogin");
        var start = owner.StartAsync(ModelSignInFixture.Choice);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("models.authLogin", cancellationToken: deadline.Token);
        await server.CloseConnectionsAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Deadline));
        Assert.NotNull(owner.SessionId);
        Assert.Null(owner.Step);
        Assert.Single(fixture.Requests, request => request.Method == "models.authLogin");
        await owner.CancelAsync();
        Assert.Equal(ModelSignInNotice.ContextChanged, owner.Notice);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == "wizard.cancel");
    }

    [Fact]
    public async Task ModelSignIn_RealClientLateStatusIsFencedByConversationIdentity()
    {
        var fixture = new ModelSignInFixture();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var current = true;
        var owner = connected.Client.CreateModelSignInSession("main", () => current);
        server.HoldModelSignInReply("models.authStatus");
        var read = owner.RefreshAsync();
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("models.authStatus", cancellationToken: deadline.Token);
        current = false;
        server.ReleaseModelSignInReply("models.authStatus");
        await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(Deadline));
        Assert.Null(owner.AuthStatus);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ModelSignIn_RealClientMissingMethodDoesNotSendUnsupportedStatus()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(owner.RefreshAsync);
        Assert.DoesNotContain(server.Requests, request => request.Method == "models.authStatus");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(true, "done")]
    [InlineData(false, "error")]
    public async Task ModelSignIn_RealClientSendsPinnedFramesAndReadsSavedState(bool progress, string terminal)
    {
        var fixture = new ModelSignInFixture { StartWithProgress = progress, TerminalStatus = terminal };
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("research", () => true);
        await owner.RefreshAsync();
        Assert.Equal("missing", Assert.Single(owner.AuthStatus!.Providers).Status);
        await owner.StartAsync(ModelSignInFixture.Choice);
        var login = Assert.Single(fixture.Requests, request => request.Method == "models.authLogin").Parameters;
        Assert.Equal(["sessionId", "agentId", "authChoice"], login.EnumerateObject().Select(p => p.Name));
        Assert.Equal("research", login.GetProperty("agentId").GetString());
        Assert.Equal(ModelSignInFixture.Choice, login.GetProperty("authChoice").GetString());
        Assert.Equal("fixture-question", owner.Step!.Id);
        await owner.AnswerAsync(JsonSerializer.SerializeToElement(ModelSignInFixture.Answer));
        Assert.Null(owner.SessionId);
        Assert.Equal("ok", Assert.Single(owner.AuthStatus!.Providers).Status);
        Assert.Equal(terminal == "done" ? ModelSignInNotice.Finished : ModelSignInNotice.Ended, owner.Notice);
        var answers = fixture.Requests.Where(request => request.Method == "wizard.next").ToArray();
        Assert.Equal(progress ? 2 : 1, answers.Length);
        if (progress) Assert.False(answers[0].Parameters.TryGetProperty("answer", out _));
        Assert.Equal("fixture-question", answers[^1].Parameters.GetProperty("answer").GetProperty("stepId").GetString());
        Assert.Equal(2, fixture.Requests.Count(request => request.Method == "models.authStatus"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelSignIn_RealClientCancelIsNotRollbackAndRunningAckStaysUncertain(bool held)
    {
        var fixture = new ModelSignInFixture { CancellationHeld = held };
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        await owner.StartAsync(ModelSignInFixture.Choice);
        var id = owner.SessionId;
        await owner.CancelAsync();
        var cancel = Assert.Single(fixture.Requests, request => request.Method == "wizard.cancel").Parameters;
        Assert.Equal(id, cancel.GetProperty("sessionId").GetString());
        Assert.True(cancel.GetProperty("closeInput").GetBoolean());
        Assert.Equal(held, owner.SessionId is not null);
        Assert.Equal(held ? ModelSignInNotice.CancellationUnconfirmed : ModelSignInNotice.Ended, owner.Notice);
        Assert.Equal(held ? 1 : 2, fixture.Requests.Count(request => request.Method == "models.authStatus"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ModelSignIn_RealClientProtocolDenialRetainsSafeErrorMetadata()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateModelSignIn(new ModelSignInFixture { RejectLogin = true }), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        var failure = await Assert.ThrowsAsync<GatewayModelSignInException>(() => owner.StartAsync(ModelSignInFixture.Choice));
        Assert.Equal("INVALID_REQUEST", failure.Code);
        Assert.Equal("SYNTHETIC_ADMISSION_DENIED", failure.DetailCode);
        Assert.True(failure.Retryable);
        Assert.Equal(250, failure.RetryAfterMs);
        Assert.DoesNotContain("Synthetic provider", failure.Message);
        Assert.Null(owner.SessionId);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelSignIn_RealClientMissingGrantsNeverDispatchUnauthorizedCalls(bool readOnly)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateModelSignIn(scopes: readOnly ? ["operator.read"] : []), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var owner = connected.Client.CreateModelSignInSession("main", () => true);
        if (readOnly)
        {
            await owner.RefreshAsync();
            Assert.False(owner.CanLogin);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(ModelSignInFixture.Choice));
        }
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(owner.RefreshAsync);
        Assert.DoesNotContain(server.Requests, request => request.Method == "models.authLogin");
        Assert.Equal(readOnly ? 1 : 0, server.Requests.Count(request => request.Method == "models.authStatus"));
    }

    [Fact]
    public async Task ModelSignIn_RealClientReconnectCannotReuseOldLeaseOrCancelOnNewSocket()
    {
        var fixture = new ModelSignInFixture();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var owner = client.CreateModelSignInSession("main", () => true);
        await owner.RefreshAsync();
        await owner.StartAsync(ModelSignInFixture.Choice);
        var originalId = owner.SessionId;
        var epoch = client.SessionMutationConnectionEpoch;
        await client.DisconnectAsync();
        var ready = Signal<bool>();
        client.HandshakeSucceeded += (_, _) => ready.TrySetResult(true);
        await client.ConnectAsync();
        await ready.Task.WaitAsync(Deadline);
        Assert.NotEqual(epoch, client.SessionMutationConnectionEpoch);
        await owner.CancelAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.AnswerAsync(JsonSerializer.SerializeToElement(ModelSignInFixture.Answer)));
        Assert.DoesNotContain(fixture.Requests, request => request.Method is "wizard.next" or "wizard.cancel");
        Assert.Equal(ModelSignInNotice.ContextChanged, owner.Notice);
        Assert.Equal(originalId, owner.SessionId);
        Assert.False(owner.CanLogin);
    }
}
