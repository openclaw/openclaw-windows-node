using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayHandshakeObservationTests
{
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
