using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Real correlated stale-response case through the socket and PendingRequestRegistry. The fixture
/// stamps the main session label per response, so the newer and older responses carry materially
/// distinct observable values for the SAME scope/key. Admission of the stale response would rewrite
/// that label; a deterministic client-side rejection counter (not a sleep) proves it was processed.
/// </summary>
public class SessionCatalogSocketCorrelationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static string CreateToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static string CreateIdentityPath()
    {
        var dir = Directory.CreateTempSubdirectory("oc-socket-identity-").FullName;
        return Path.Combine(dir, "device.json");
    }

    [Fact]
    public async Task StaleOlderResponse_ThroughSocketRegistry_NeverRewritesNewerCatalogValue()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);

        var stamps = 0;
        server.SessionsListLabelSource = () => "STAMP-" + Interlocked.Increment(ref stamps);

        var client = new OpenClawGatewayClient(
            server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: CreateIdentityPath(), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        try
        {
            var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var five = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            client.SessionsUpdated += (_, data) => { if (data.Length == 5) five.TrySetResult(true); };
            await client.ConnectAsync();
            await Task.WhenAll(handshake.Task, five.Task).WaitAsync(Deadline);
            Assert.Equal(5, client.GetSessionList().Length);

            var before = server.Requests.Count(r => r.Method == "sessions.list");
            var staleBefore = client.TestStaleResponseRejections;

            // Older request: response is held AFTER the request id was registered and sent.
            var olderGate = server.HoldSessionsList();
            _ = client.RequestSessionsAsync();
            using (var cts = new CancellationTokenSource(Deadline))
                await server.WaitForRequestAsync("sessions.list", null, before + 1, cts.Token);

            // Newer request supersedes it (higher generation); its response is held too.
            var newerGate = server.HoldSessionsList();
            _ = client.RequestSessionsAsync();
            using (var cts = new CancellationTokenSource(Deadline))
                await server.WaitForRequestAsync("sessions.list", null, before + 2, cts.Token);

            // Deliver the NEWER response and wait for its terminal transition, then read its label.
            server.ReleaseSessionsList(newerGate);
            for (var i = 0; i < 600 && client.SessionCatalogAcquisitionInProgress; i++)
                await Task.Delay(5);
            Assert.False(client.SessionCatalogAcquisitionInProgress);
            var newerLabel = client.GetSessionList().Single(s => s.Key == "agent:main:main").Label;
            Assert.NotNull(newerLabel);

            // Deliver the OLDER (stale) response; barrier on the client-side rejection counter.
            server.ReleaseSessionsList(olderGate);
            for (var i = 0; i < 600 && client.TestStaleResponseRejections == staleBefore; i++)
                await Task.Delay(5);
            Assert.True(client.TestStaleResponseRejections > staleBefore,
                "the stale response was not observed as rejected (barrier timed out)");

            // The stale response must not have rewritten the newer observable value.
            Assert.Equal(newerLabel, client.GetSessionList().Single(s => s.Key == "agent:main:main").Label);
            Assert.Equal(5, client.GetSessionList().Length);
            Assert.False(client.SessionCatalogRetryRequired);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
        }
    }
}
