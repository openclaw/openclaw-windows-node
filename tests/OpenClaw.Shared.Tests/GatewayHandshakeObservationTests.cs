using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayHandshakeObservationTests
{
    [Fact]
    public async Task RefusedTransport_ReportsNetworkWithoutClassifyingStackFrames()
    {
        using var temp = new TempDirectory();
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        using var client = new OpenClawGatewayClient($"ws://127.0.0.1:{endpoint.Port}", "fixture-token",
            NullLogger.Instance, identityPath: temp.Path);
        var failed = new TaskCompletionSource<GatewayHandshakeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionFailed = new TaskCompletionSource<GatewayErrorKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeObserved += (_, observation) =>
        {
            if (observation.Phase == GatewayHandshakePhase.Failed)
                failed.TrySetResult(observation);
        };
        client.ConnectionFailure += (_, kind) => connectionFailed.TrySetResult(kind);

        await client.ConnectAsync();
        var observation = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connectionFailure = await connectionFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(GatewayErrorKind.Network, observation.FailureKind);
        Assert.Equal(GatewayErrorKind.Network, connectionFailure);
        Assert.True(observation.TransportGeneration > 0);
    }

    [Theory]
    [InlineData(55001)]
    [InlineData(54291)]
    [InlineData(54011)]
    public async Task RefusedTransport_UsesTypedSocketCauseBeforePortText(int port)
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        await client.BeginAttemptAsync(1);

        var exception = new System.Net.WebSockets.WebSocketException(
            "Unable to connect to the remote server",
            new System.Net.Http.HttpRequestException(
                $"No connection could be made because the target machine actively refused it. (127.0.0.1:{port})",
                new SocketException((int)SocketError.ConnectionRefused)));
        client.ObserveTransport(1, GatewayHandshakePhase.Failed, exception);

        Assert.Equal(GatewayErrorKind.Network, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
    }

    [Fact]
    public async Task TimeoutFailure_UsesTypedCauseBeforeCollidingMessage()
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        await client.BeginAttemptAsync(1);

        client.ObserveTransport(1, GatewayHandshakePhase.Failed,
            new System.Net.Http.HttpRequestException(
                "The request timed out at 127.0.0.1:55001",
                new TimeoutException()));

        Assert.Equal(GatewayErrorKind.Network, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GatewayErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, GatewayErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GatewayErrorKind.Server)]
    public async Task TransportFailure_PreservesHttpStatusOverSocketCause(
        HttpStatusCode statusCode, GatewayErrorKind expected)
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        await client.BeginAttemptAsync(1);

        client.ObserveTransport(1, GatewayHandshakePhase.Failed,
            new System.Net.Http.HttpRequestException(
                "Upgrade rejected at 127.0.0.1:55001",
                new SocketException((int)SocketError.ConnectionRefused),
                statusCode));

        Assert.Equal(expected, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
    }

    [Fact]
    public async Task TransportFailure_PreservesTlsOverSocketCause()
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        await client.BeginAttemptAsync(1);

        client.ObserveTransport(1, GatewayHandshakePhase.Failed,
            new System.Security.Authentication.AuthenticationException(
                "TLS handshake failed",
                new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(GatewayErrorKind.Tls, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
    }

    [Fact]
    public async Task TlsHandshakeSocketReset_UsesTypedHttpErrorBeforeNetworkCause()
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        await client.BeginAttemptAsync(1);

        var exception = new System.Net.WebSockets.WebSocketException(
            "Unable to connect to the remote server",
            new System.Net.Http.HttpRequestException(
                System.Net.Http.HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established (127.0.0.1:55001).",
                new SocketException((int)SocketError.ConnectionReset)));
        client.ObserveTransport(1, GatewayHandshakePhase.Failed, exception);

        Assert.Equal(GatewayErrorKind.Tls, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
    }

    [Theory]
    [InlineData(GatewayErrorKind.Network)]
    [InlineData(GatewayErrorKind.Tls)]
    [InlineData(GatewayErrorKind.Auth)]
    [InlineData(GatewayErrorKind.Unknown)]
    public async Task TransportFailure_UsesExceptionCauseWithoutStackOrTypeName(GatewayErrorKind expected)
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        Exception cause = expected switch
        {
            GatewayErrorKind.Network => new SocketException((int)SocketError.ConnectionRefused),
            GatewayErrorKind.Tls => new System.Security.Authentication.AuthenticationException("sensitive-sentinel"),
            GatewayErrorKind.Auth => new InvalidOperationException("unauthorized sensitive-sentinel"),
            _ => new InvalidOperationException("sensitive-sentinel")
        };
        await client.BeginAttemptAsync(1);

        client.ObserveTransport(1, GatewayHandshakePhase.Failed,
            new InvalidOperationException("unrecognized outer failure", cause));

        Assert.Equal(expected, Assert.Single(observations,
            observation => observation.Phase == GatewayHandshakePhase.Failed).FailureKind);
        Assert.DoesNotContain("sensitive-", JsonSerializer.Serialize(observations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededAuthorization_CannotEmitSuccessOrFailure(bool throws)
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new ConcurrentQueue<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Enqueue(observation);
        var release = new TaskCompletionSource<ReconnectAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeAuthorizationAsync = _ => release.Task;
        await client.BeginAttemptAsync(1);
        var authorizing = client.AuthorizeAsync(1);
        Assert.Equal(GatewayHandshakePhase.AuthorizationStarted, observations.Last().Phase);
        await client.BeginAttemptAsync(2);
        var before = observations.ToArray();

        if (throws) release.SetException(new InvalidOperationException("sensitive-exception-sentinel"));
        else release.SetResult(ReconnectAuthorizationResult.AllowedResult);
        await authorizing;
        await client.ReplayAsync("""
            {"type":"event","event":"connect.challenge","payload":{"nonce":"sensitive-nonce-sentinel"}}
            """, 1);
        await client.ReplayAsync("""
            {"type":"res","id":"stale","ok":false,"error":{"code":"UNAVAILABLE","retryable":true,"details":{"reason":"startup-sidecars"}}}
            """, 1);
        client.ObserveTransport(1, GatewayHandshakePhase.Disconnected);
        client.ObserveTransport(1, GatewayHandshakePhase.Failed,
            new InvalidOperationException("sensitive-stale-transport-sentinel"));

        Assert.Equal(before, observations.ToArray());
    }

    [Fact]
    public async Task CancellationAndDisposal_SuppressLateAuthorizationAndTransportObservations()
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        var release = new TaskCompletionSource<ReconnectAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeAuthorizationAsync = _ => release.Task;
        await client.BeginAttemptAsync(1);
        var authorizing = client.AuthorizeAsync(1);
        var before = observations.ToArray();

        client.CancelLifetime();
        release.SetCanceled();
        await authorizing;
        client.Dispose();
        await client.ReplayAsync("""
            {"type":"event","event":"connect.challenge","payload":{"nonce":"sensitive-nonce-sentinel"}}
            """, 1);

        Assert.Equal(before, observations);
    }

    [Theory]
    [InlineData(false, GatewayErrorKind.LocalPortConflict)]
    [InlineData(true, GatewayErrorKind.Unknown)]
    public async Task AuthorizationDenial_ReportsSafeCategoryWithoutDetails(bool throws, GatewayErrorKind expected)
    {
        using var temp = new TempDirectory();
        using var client = new ObservationClient(temp.Path);
        var observations = new List<GatewayHandshakeObservation>();
        client.HandshakeObserved += (_, observation) => observations.Add(observation);
        client.HandshakeAuthorizationAsync = _ => throws
            ? throw new InvalidOperationException("sensitive-provenance-sentinel")
            : Task.FromResult(new ReconnectAuthorizationResult(false, GatewayErrorKind.LocalPortConflict,
                "sensitive-provenance-sentinel"));
        await client.BeginAttemptAsync(1);

        await client.AuthorizeAsync(1);

        var denied = Assert.Single(observations, item => item.Phase == GatewayHandshakePhase.AuthorizationDenied);
        Assert.Equal(expected, denied.FailureKind);
        Assert.DoesNotContain(observations, item => item.Phase == GatewayHandshakePhase.ConnectSending);
        Assert.DoesNotContain("sensitive-", JsonSerializer.Serialize(observations));
    }

    // Drive the existing asynchronous protocol seam without a socket for deterministic
    // stale-generation and cancellation races. Wire ordering is covered by loopback tests.
    private sealed class ObservationClient(string identityPath)
        : OpenClawGatewayClient("ws://127.0.0.1:1", "fixture-token", NullLogger.Instance, identityPath: identityPath)
    {
        public async Task BeginAttemptAsync(long generation)
        {
            typeof(WebSocketClientBase).GetField("_connectionGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, generation);
            OnTransportObservation(generation, GatewayHandshakePhase.TransportConnecting);
            OnTransportObservation(generation, GatewayHandshakePhase.TransportConnected);
            await OnConnectedAsync();
            var gate = (HandshakeChallengeGate)typeof(OpenClawGatewayClient)
                .GetField("_handshakeChallengeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            Assert.True(gate.TryBegin(generation));
        }

        public Task AuthorizeAsync(long generation) =>
            (Task)typeof(OpenClawGatewayClient).GetMethod("SendConnectSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(this, ["sensitive-nonce-sentinel", generation])!;

        public Task ReplayAsync(string json, long generation) => ProcessMessageForConnectionAsync(json, generation);

        public void ObserveTransport(long generation, GatewayHandshakePhase phase, Exception? error = null) =>
            OnTransportObservation(generation, phase, error);

        public void CancelLifetime() =>
            ((CancellationTokenSource)typeof(WebSocketClientBase)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Cancel();
    }
}
