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
                              const visible = element => {
                                const bounds = element.getBoundingClientRect();
                                if (!element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })
                                  || element.closest("[inert],[hidden],[aria-hidden=true]")) return false;
                                const left = Math.max(0, bounds.left), right = Math.min(innerWidth, bounds.right);
                                const top = Math.max(0, bounds.top), bottom = Math.min(innerHeight, bounds.bottom);
                                if (right <= left || bottom <= top) return false;
                                const hit = document.elementFromPoint((left + right) / 2, (top + bottom) / 2);
                                return hit === element || element.contains(hit);
                              };
                              const headings = [...document.querySelectorAll("h1,h2,[role=heading]")]
                                .filter(visible)
                                .map(element => element.textContent.trim());
                              return { appLoaded: !!document.querySelector("openclaw-app"),
                                hasContent: headings.some(text => text.length > 0 && text !== "OpenClaw")
                                  || [...document.querySelectorAll("input,textarea,[role=textbox]")].some(visible),
                                rootSettled: location.pathname !== "/",
                                skills: headings.includes("Skills"),
                                path: location.pathname, title: document.title };
                            })()
                            """, browserTimeout.Token);
                        lastState = state;
                        if (browser.Authenticated && state.GetProperty("appLoaded").GetBoolean() &&
                            state.GetProperty("hasContent").GetBoolean() &&
                            (route != "/" || state.GetProperty("rootSettled").GetBoolean()) &&
                            (route == "/" || state.GetProperty("skills").GetBoolean()))
                        {
                            if (route != "/") Assert.Equal(route, state.GetProperty("path").GetString());
                            Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("title").GetString()));
                            break;
                        }
                        if (!browser.Authenticated && browser.PairingRequestId is { } requestId &&
                            !approved.Contains(requestId) && (await PendingPairingIdsAsync(client)).Contains(requestId))
                        {
                            try { await client.SendWizardRequestAsync("device.pair.approve", new { requestId }); }
                            catch (InvalidOperationException ex) when (ex.Message == "unknown requestId")
                            {
                                // The Gateway may finish loopback pairing between listing and approval.
                                Assert.DoesNotContain(requestId, await PendingPairingIdsAsync(client));
                                Console.WriteLine("[Search proof] browser pairing request already resolved; awaiting authenticated hello.");
                            }
                            approved.Add(requestId);
                        }
                        await Task.Delay(PollInterval, browserTimeout.Token);
                    }
                }
                catch (OperationCanceledException ex) when (browserTimeout.IsCancellationRequested)
                {
                    Console.WriteLine($"[Search proof] browser route {route} timed out; authenticated={browser.Authenticated}; " +
                        $"state={lastState?.GetRawText() ?? "unavailable"}.");
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
