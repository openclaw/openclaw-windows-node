using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Controlled two-peer routing proof. Each test arranges two independently owned loopback
/// Gateway servers whose synthetic identity, owner, and main-session catalog differ per peer,
/// then uses real <see cref="OpenClawGatewayClient"/> connections to assert that reads route
/// only to the addressed peer, that a wrong peer credential is rejected without leaking the
/// other peer's catalog, that disposing both peers closes their ports and cancels held reads,
/// and that a stale in-flight read on one peer cannot contaminate the other. These are bounded
/// protocol fixtures exercised without the app. They are not a live-qualified gateway and do
/// not prove UI switching, which is covered by the opt-in Connection journey.
/// </summary>
[Collection("WebSocketClientBase")]
public sealed class GatewayTwoPeerRoutingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TwoPeers_DistinctCatalogAndOwnerRouteIndependently()
    {
        var peerA = new GatewayPeerProfile("alpha");
        var peerB = new GatewayPeerProfile("bravo");
        Assert.NotEqual(GatewayScenario.CreatePeerBrowse(peerA).Sha256, GatewayScenario.CreatePeerBrowse(peerB).Sha256);

        var tokenA = CreateToken();
        var tokenB = CreateToken();
        await using var serverA = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerA), tokenA);
        await using var serverB = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerB), tokenB);
        Assert.NotEqual(serverA.Endpoint, serverB.Endpoint);

        await using var a = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverA, tokenA);
        await using var b = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverB, tokenB);

        var ownerA = await a.Client.SendWizardRequestAsync("users.self");
        var ownerB = await b.Client.SendWizardRequestAsync("users.self");
        Assert.Equal(peerA.OwnerId, ownerA.GetProperty("profile").GetProperty("id").GetString());
        Assert.Equal(peerB.OwnerId, ownerB.GetProperty("profile").GetProperty("id").GetString());

        var historyA = await a.Client.RequestChatHistoryAsync();
        var historyB = await b.Client.RequestChatHistoryAsync();
        Assert.Equal(GatewayScenario.MainSessionKey, historyA.SessionKey);
        Assert.Equal(GatewayScenario.MainSessionKey, historyB.SessionKey);
        Assert.Contains(historyA.Messages, m => m.Text == peerA.MainSentinel);
        Assert.Contains(historyB.Messages, m => m.Text == peerB.MainSentinel);
        // The addressed peer must never surface the other peer's distinct content.
        Assert.DoesNotContain(historyA.Messages, m => m.Text == peerB.MainSentinel);
        Assert.DoesNotContain(historyB.Messages, m => m.Text == peerA.MainSentinel);

        Assert.Empty(serverA.UnexpectedRequests);
        Assert.Empty(serverB.UnexpectedRequests);
    }

    [Fact]
    public async Task WrongPeerCredential_IsRejectedAndDoesNotLeakTheOtherPeerCatalog()
    {
        var peerA = new GatewayPeerProfile("alpha");
        var peerB = new GatewayPeerProfile("bravo");
        var tokenA = CreateToken();
        var tokenB = CreateToken();
        await using var serverA = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerA), tokenA);
        await using var serverB = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerB), tokenB);
        await using var b = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverB, tokenB);

        // Present peer A's credential to peer B. It must be rejected, with no handshake and no catalog.
        using var identity = CreateIdentity();
        using var wrong = new OpenClawGatewayClient(serverB.Endpoint.AbsoluteUri, tokenA, identityPath: identity.Path);
        var rejected = Signal<GatewayErrorKind>();
        wrong.ConnectionFailure += (_, error) => rejected.TrySetResult(error);
        await wrong.ConnectAsync();
        Assert.Equal(GatewayErrorKind.Auth, await rejected.Task.WaitAsync(Deadline));
        Assert.False(wrong.HasHandshakeSnapshot);
        Assert.True(wrong.IsAuthFailed);
        Assert.Contains(serverB.Requests, r => r.Method == "connect" && r.Outcome == "error:AUTH_TOKEN_MISMATCH");
        // Peer A never saw the misdirected attempt.
        Assert.DoesNotContain(serverA.Requests, r => r.Method == "connect" && r.Outcome.StartsWith("error:"));

        await wrong.DisconnectAsync();
        // The legitimate peer B connection still reads only peer B's distinct catalog.
        var historyB = await b.Client.RequestChatHistoryAsync();
        Assert.Contains(historyB.Messages, m => m.Text == peerB.MainSentinel);
        Assert.DoesNotContain(historyB.Messages, m => m.Text == peerA.MainSentinel);
        var ownerB = await b.Client.SendWizardRequestAsync("users.self");
        Assert.Equal(peerB.OwnerId, ownerB.GetProperty("profile").GetProperty("id").GetString());
    }

    [Fact]
    public async Task OwnedCleanup_DisposingBothPeersClosesPortsAndCancelsHeldReads()
    {
        var peerA = new GatewayPeerProfile("alpha");
        var peerB = new GatewayPeerProfile("bravo");
        var tokenA = CreateToken();
        var tokenB = CreateToken();
        await using var serverA = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerA), tokenA);
        await using var serverB = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerB), tokenB);
        var portA = serverA.Endpoint.Port;
        var portB = serverB.Endpoint.Port;
        await using var a = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverA, tokenA);
        await using var b = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverB, tokenB);

        serverA.HoldHistory(GatewayScenario.LongSessionKey);
        var heldOnA = a.Client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await serverA.WaitForRequestAsync("chat.history", GatewayScenario.LongSessionKey, 1, deadline.Token);

        await serverA.DisposeAsync().AsTask().WaitAsync(Deadline);
        await serverB.DisposeAsync().AsTask().WaitAsync(Deadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heldOnA.WaitAsync(Deadline));
        Assert.Equal(0, serverA.ActiveConnectionCount);
        Assert.Equal(0, serverB.ActiveConnectionCount);
        Assert.Contains(serverA.Requests, r => r.Method == "chat.history" && r.Outcome == "cancelled");

        using var probeA = new TcpClient();
        using var probeB = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probeA.ConnectAsync(IPAddress.Loopback, portA));
        await Assert.ThrowsAnyAsync<SocketException>(() => probeB.ConnectAsync(IPAddress.Loopback, portB));
    }

    [Fact]
    public async Task StaleReply_HeldReadOnOnePeerCancelsOnDisconnectAndDoesNotContaminateTheOtherPeer()
    {
        var peerA = new GatewayPeerProfile("alpha");
        var peerB = new GatewayPeerProfile("bravo");
        var tokenA = CreateToken();
        var tokenB = CreateToken();
        await using var serverA = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerA), tokenA);
        await using var serverB = await FixtureGatewayServer.StartAsync(GatewayScenario.CreatePeerBrowse(peerB), tokenB);
        await using var a = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverA, tokenA);
        await using var b = await GatewayFixtureProtocolTests.ConnectedClient.OpenAsync(serverB, tokenB);

        // A read against peer A is parked in flight on peer A's held main-session history.
        serverA.HoldHistory(GatewayScenario.MainSessionKey);
        var staleOnA = a.Client.RequestChatHistoryAsync(GatewayScenario.MainSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await serverA.WaitForRequestAsync("chat.history", GatewayScenario.MainSessionKey, 1, deadline.Token);
        Assert.False(staleOnA.IsCompleted);

        // Disconnecting peer A cancels its in-flight read before any reply is produced.
        await a.Client.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleOnA.WaitAsync(Deadline));

        // A late, stale release on peer A must not resurrect the cancelled reply.
        serverA.ReleaseHistory(GatewayScenario.MainSessionKey);

        // Peer B remains clean and returns only peer B's distinct content.
        var historyB = await b.Client.RequestChatHistoryAsync(GatewayScenario.MainSessionKey);
        Assert.Equal(GatewayScenario.MainSessionKey, historyB.SessionKey);
        Assert.Contains(historyB.Messages, m => m.Text == peerB.MainSentinel);
        Assert.DoesNotContain(historyB.Messages, m => m.Text == peerA.MainSentinel);
        Assert.Empty(serverB.UnexpectedRequests);
    }

    [Fact]
    public void PeerProfile_RejectsUnsafeSlugsSoTitlesAndSentinelsCannotBeInjected()
    {
        Assert.Throws<ArgumentException>(() => new GatewayPeerProfile("bad slug"));
        Assert.Throws<ArgumentException>(() => new GatewayPeerProfile("bad/slug"));
        Assert.Throws<ArgumentException>(() => new GatewayPeerProfile(new string('a', 25)));
        Assert.Throws<ArgumentException>(() => new GatewayPeerProfile(""));

        var peer = new GatewayPeerProfile("alpha-1");
        Assert.Contains("alpha-1", peer.MainSentinel);
        Assert.Equal("fixture-owner-alpha-1", peer.OwnerId);
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TempDirectory CreateIdentity() => new(Path.Combine(Directory.GetCurrentDirectory(), ".fixture-identity-"));
}
