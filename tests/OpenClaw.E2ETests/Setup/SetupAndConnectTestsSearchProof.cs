// Real search interoperability proof, reusing the disposable setup/connect Gateway.
// Run through the existing hosted setup/connect lane, not against a user's Gateway.
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Exercises production search reads and browser routes on the setup fixture's released Gateway.</summary>
[Collection("E2E Setup")]
public sealed class SetupAndConnectTestsSearchProof(E2ESetupFixture fixture)
{
    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Seeds synthetic content without an AI call, verifies search and denial, and captures the real Dashboard.</summary>
    [E2EFact]
    public async Task ReleasedGateway_SearchDenialAndDashboardRoutes()
    {
        Assert.Null(fixture.SetupError);
        var credentials = await fixture.WaitForDurablePairedCredentialsAsync();
        var gateway = fixture.ReadActiveGatewayRecord();
        Assert.False(string.IsNullOrWhiteSpace(gateway.SharedGatewayToken));
        using var client = new OpenClawGatewayClient(gateway.GatewayUrl, gateway.SharedGatewayToken!,
            identityPath: credentials.IdentityDir, persistHandshakeDeviceTokens: false, requireExistingIdentity: true);
        await ConnectAsync(client);
        var version = await fixture.RunInWslAsync("openclaw --version", ProofTimeout);
        Assert.Equal(0, version.ExitCode);
        Console.WriteLine($"[Search proof] released Gateway: {version.Stdout.Trim()}");

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"agent:main:search-proof-{suffix}";
        var title = $"Search proof {suffix}";
        var sentinel = $"heliotrope lighthouse {suffix}";
        var created = await client.CreateSessionAsync(new SessionCreateRequest
        {
            Key = key, AgentId = "main", EmitCommandHooks = false
        });
        Assert.True(created.Ok, "The released Gateway did not create the synthetic search session.");
        Assert.Equal(key, created.Key);
        try
        {
            await client.SendWizardRequestAsync("sessions.patch", new { key, label = title });
            await client.SendWizardRequestAsync("chat.inject", new { sessionKey = key, message = sentinel });
            using var timeout = new CancellationTokenSource(ProofTimeout);
            var metadata = await client.SearchSessionsAsync(title, messages: false, timeout.Token);
            Assert.Contains(metadata.Sessions, row => row.Key == key);

            GatewaySearchResult transcript;
            do
            {
                transcript = await client.SearchSessionsAsync(sentinel, messages: true, timeout.Token);
                if (transcript.Hits.Any(hit => hit.SessionKey == key)) break;
                await Task.Delay(PollInterval, timeout.Token);
            } while (true);
            var hit = Assert.Single(transcript.Hits, row => row.SessionKey == key);
            Assert.Contains(sentinel, hit.Snippet, StringComparison.Ordinal);
            var schema = await client.ReadSearchCatalogAsync("config.schema", null, timeout.Token);
            Assert.Equal(JsonValueKind.Object, schema.GetProperty("schema").ValueKind);

            var deviceToken = DeviceIdentity.TryReadStoredDeviceToken(credentials.IdentityDir);
            Assert.False(string.IsNullOrWhiteSpace(deviceToken));
            using var restricted = new OpenClawGatewayClient(gateway.GatewayUrl, gateway.SharedGatewayToken!,
                identityPath: credentials.IdentityDir, persistHandshakeDeviceTokens: false,
                ephemeralOperatorCredential: new(deviceToken!, ["operator.approvals"], "operator"),
                requireExistingIdentity: true);
            await ConnectAsync(restricted);
            Assert.DoesNotContain(restricted.GrantedOperatorScopes,
                scope => scope is "operator.admin" or "operator.read" or "operator.write" or "operator.sessions.read");
            var denial = await Assert.ThrowsAsync<GatewayRequestException>(() =>
                restricted.SearchSessionsAsync(sentinel, messages: true, timeout.Token));
            Assert.Equal("FORBIDDEN", denial.Code);
            Console.WriteLine("[Search proof] metadata and transcript matched the exact synthetic session; restricted read returned FORBIDDEN.");

            var pendingBefore = await PendingPairingIdsAsync(client);
            var approved = new HashSet<string>(StringComparer.Ordinal);
            await using var browser = await GatewaySearchBrowserProof.StartAsync(fixture.DataDir);
            var origin = new UriBuilder(gateway.GatewayUrl) { Scheme = "http" }.Uri;
            foreach (var route in new[] { "/", "/skills" })
            {
                await browser.NavigateAsync(new Uri(origin, route + "#token=" + Uri.EscapeDataString(gateway.SharedGatewayToken!)));
                using var browserTimeout = new CancellationTokenSource(ProofTimeout);
                JsonElement? lastState = null;
                try
                {
                    while (true)
                    {
                        var state = await browser.EvaluateAsync("""
                            (() => {
                              const app = document.querySelector("openclaw-app");
                              const gateway = app?.runtime?.context?.gateway?.snapshot;
                              return { connected: gateway?.phase === "connected",
                                appLoaded: !!app, runtimeLoaded: !!app?.runtime,
                                skills: !!document.querySelector("openclaw-skills-page"),
                                path: location.pathname, title: document.title };
                            })()
                            """, browserTimeout.Token);
                        lastState = state;
                        if (state.GetProperty("connected").GetBoolean() &&
                            (route == "/" || state.GetProperty("skills").GetBoolean()))
                        {
                            if (route != "/") Assert.Equal(route, state.GetProperty("path").GetString());
                            Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("title").GetString()));
                            break;
                        }
                        var pending = (await PendingPairingIdsAsync(client))
                            .Except(pendingBefore).Except(approved).ToArray();
                        Assert.True(pending.Length <= 1, "Ambiguous new pairing requests during the owned browser proof.");
                        if (pending.Length == 1)
                        {
                            await client.SendWizardRequestAsync("device.pair.approve", new { requestId = pending[0] });
                            approved.Add(pending[0]);
                        }
                        await Task.Delay(PollInterval, browserTimeout.Token);
                    }
                }
                catch (OperationCanceledException ex) when (browserTimeout.IsCancellationRequested)
                {
                    Console.WriteLine($"[Search proof] browser route {route} timed out; state={lastState?.GetRawText() ?? "unavailable"}.");
                    try
                    {
                        await browser.CaptureAsync(Path.Combine(fixture.ArtifactDir, "search-dashboard-timeout.png"));
                    }
                    catch (Exception captureError)
                    {
                        Console.WriteLine($"[Search proof] failure capture unavailable: {captureError.GetType().Name}.");
                    }
                    throw new TimeoutException("The released Dashboard did not render an authenticated route; see bounded state and screenshot artifacts.", ex);
                }
                await browser.CaptureAsync(Path.Combine(fixture.ArtifactDir,
                    route == "/" ? "search-dashboard-root.png" : "search-dashboard-skills.png"));
                Console.WriteLine($"[Search proof] authenticated browser rendered {route}.");
            }
        }
        finally
        {
            // Only the unique synthetic session owned by this test is deleted.
            await client.SendWizardRequestAsync("sessions.delete", new { key, deleteTranscript = true });
        }
    }

    private static async Task ConnectAsync(OpenClawGatewayClient client)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += OnStatus;
        try
        {
            await client.ConnectAsync().WaitAsync(ProofTimeout);
            await ready.Task.WaitAsync(ProofTimeout);
        }
        finally { client.StatusChanged -= OnStatus; }
        void OnStatus(object? sender, ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected) ready.TrySetResult();
        }
    }

    private static async Task<HashSet<string>> PendingPairingIdsAsync(OpenClawGatewayClient client)
    {
        var response = await client.SendWizardRequestAsync("device.pair.list");
        return response.GetProperty("pending").EnumerateArray()
            .Select(row => row.GetProperty("requestId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
