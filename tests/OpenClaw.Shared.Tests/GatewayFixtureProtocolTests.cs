using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

[Collection("WebSocketClientBase")]
public sealed class GatewayFixtureProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public void BrowseScenario_HasStableSingleSourceMetadataAndRejectsUnknownScenario()
    {
        var first = GatewayScenario.CreateBrowse();
        var second = GatewayScenario.LoadBuiltin(GatewayScenario.BrowseName);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(64, first.Sha256.Length);
        Assert.Equal(GatewayProtocolContract.CurrentVersion, first.ProtocolVersion);
        Assert.Equal(5, first.SessionKeys.Count);
        Assert.Equal(5, first.SessionKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("chat.history", first.ReadMethods);
        Assert.DoesNotContain("chat.send", first.ReadMethods);
        Assert.Throws<ArgumentException>(() => GatewayScenario.LoadBuiltin("not-a-scenario"));
    }

    [Fact]
    public async Task RealClient_ChallengeHandshakeResolvesCanonicalMainAndPopulatesAllSessions()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        Assert.Equal(IPAddress.Loopback.ToString(), server.Endpoint.Host);
        Assert.NotEqual(0, server.Endpoint.Port);
        Assert.True(client.HasHandshakeSnapshot);
        Assert.Equal(GatewayScenario.MainSessionKey, client.MainSessionKey);
        Assert.Equal(["operator.read"], client.GrantedOperatorScopes);
        await server.HandshakeCompleted.WaitAsync(Deadline);
        var sessions = client.GetSessionList();
        Assert.Equal(GatewayScenario.CreateBrowse().SessionKeys.Order(), sessions.Select(s => s.Key).Order());
        Assert.Equal(GatewayScenario.MainSessionKey, Assert.Single(sessions, s => s.IsMain).Key);
        Assert.All(sessions, session =>
        {
            Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
            Assert.StartsWith("Fixture:", session.Label);
            Assert.Equal("idle", session.Status);
        });
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(1, server.ActiveConnectionCount);
        Assert.Equal(1, server.SubscriptionCount);
        await client.SendWizardRequestAsync("sessions.subscribe");
        Assert.Equal(1, server.SubscriptionCount);
        Assert.Empty(server.UnexpectedRequests);

        var main = await client.RequestChatHistoryAsync();
        Assert.Equal(GatewayScenario.MainSessionKey, main.SessionKey);
        Assert.Contains(main.Messages, m => m.Text == GatewayScenario.MainSentinel);
    }

    [Fact]
    public async Task RealClient_LongHistoryHas240MixedRowsStableIdentitiesAndActualFinalSentinel()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var history = await connected.Client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);
        var repeat = await connected.Client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);

        Assert.Equal(GatewayScenario.LongMessageCount, history.Messages.Count);
        Assert.Equal(history.SessionId, repeat.SessionId);
        Assert.Equal(history.Messages.Select(m => m.OpenClawId), repeat.Messages.Select(m => m.OpenClawId));
        Assert.Equal(240, history.Messages.Select(m => m.OpenClawId).Distinct().Count());
        Assert.Equal(GatewayScenario.LongEarlySentinel, history.Messages[0].Text);
        Assert.Equal(GatewayScenario.LongMiddleSentinel, history.Messages[119].Text);
        Assert.StartsWith("Fixture long message 233", history.Messages[232].Text);
        Assert.DoesNotContain(GatewayScenario.LongFinalSentinel, history.Messages[232].Text);
        Assert.Contains(GatewayScenario.LongFinalSentinel, history.Messages[^1].Text);
        Assert.EndsWith(GatewayScenario.LongFinalLine, history.Messages[^1].Text);
        Assert.Equal(GatewayScenario.LongHistoryFinalMarker, history.Messages[^1].Text.Split('\n')[^1]);
        Assert.Single(history.Messages, m => m.Text.Contains(GatewayScenario.LongHistoryFinalMarker, StringComparison.Ordinal));
        Assert.Contains(history.Messages, m => m.Text.Contains("```csharp", StringComparison.Ordinal));
        Assert.Contains(history.Messages, m => m.Text.Contains("| Item | State |", StringComparison.Ordinal));
        Assert.Contains(history.Messages, m => m.Text.Contains("- First synthetic observation", StringComparison.Ordinal));
        Assert.Contains(history.Messages, m => m.ToolContent.Count > 0);
        Assert.All(history.Messages, m =>
        {
            Assert.Equal(GatewayScenario.LongSessionKey, m.SessionKey);
            Assert.InRange(m.Ts, 1_780_000_000_000L, 1_790_000_000_000L);
            Assert.InRange(m.OpenClawSeq!.Value, 1, 240);
        });
        Assert.True(history.Messages.Zip(history.Messages.Skip(1)).All(pair => pair.First.Ts < pair.Second.Ts));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_EmptyAndOtherAgentHistoriesDoNotLeakOverlappingMessageIds()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var main = await client.RequestChatHistoryAsync(GatewayScenario.MainSessionKey);
        var other = await client.RequestChatHistoryAsync(GatewayScenario.OtherSessionKey);
        var empty = await client.RequestChatHistoryAsync(GatewayScenario.EmptySessionKey);
        var edge = await client.RequestChatHistoryAsync(GatewayScenario.EdgeSessionKey);

        Assert.Empty(empty.Messages);
        Assert.False(string.IsNullOrWhiteSpace(empty.SessionId));
        Assert.NotEqual(main.SessionId, other.SessionId);
        Assert.Equal(main.Messages[0].OpenClawId, other.Messages[0].OpenClawId);
        Assert.Equal(main.Messages[1].OpenClawId, edge.Messages[1].OpenClawId);
        Assert.Contains(other.Messages, m => m.Text == GatewayScenario.OtherSentinel);
        Assert.Contains(edge.Messages, m => m.Text == GatewayScenario.EdgeSentinel);
        Assert.DoesNotContain(other.Messages, m => m.Text.Contains(GatewayScenario.MainSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealClient_MeaningfulAgentKeyLimitAndPreviewParametersAreRespected()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var filtered = await client.SendWizardRequestAsync("sessions.list", new { agentId = "research", limit = 1 });
        var row = Assert.Single(filtered.GetProperty("sessions").EnumerateArray());
        Assert.Equal(GatewayScenario.OtherSessionKey, row.GetProperty("key").GetString());
        var recent = await client.SendWizardRequestAsync("sessions.list", new { activeMinutes = 1 });
        Assert.Equal(2, recent.GetProperty("count").GetInt32());
        var limited = await client.SendWizardRequestAsync("chat.history", new { sessionKey = GatewayScenario.LongSessionKey, limit = 3 });
        Assert.Equal(3, limited.GetProperty("messages").GetArrayLength());
        Assert.Contains(GatewayScenario.LongFinalSentinel, limited.GetProperty("messages")[2].GetProperty("content").GetString());

        var preview = await client.SendWizardRequestAsync("sessions.preview", new
        {
            keys = new[] { GatewayScenario.OtherSessionKey, GatewayScenario.EmptySessionKey }, limit = 1, maxChars = 24
        });
        var previews = preview.GetProperty("previews");
        Assert.Equal(2, previews.GetArrayLength());
        Assert.Equal(GatewayScenario.OtherSessionKey, previews[0].GetProperty("key").GetString());
        var item = Assert.Single(previews[0].GetProperty("items").EnumerateArray());
        Assert.Equal(GatewayScenario.OtherSentinel[..24], item.GetProperty("text").GetString());
        Assert.Empty(previews[1].GetProperty("items").EnumerateArray());
        var cost = await client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        Assert.Equal(7, cost.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task RealClient_SupportingReadsUseTypedModelsAndConsistentConfiguration()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var modelsReceived = Signal<ModelsListInfo>();
        var previewsReceived = Signal<SessionsPreviewPayloadInfo>();
        var configReceived = Signal<JsonElement>();
        var schemaReceived = Signal<JsonElement>();
        var nodePairsReceived = Signal<PairingListInfo>();
        var devicePairsReceived = Signal<DevicePairingListInfo>();
        client.ModelsListUpdated += (_, models) => modelsReceived.TrySetResult(models);
        client.SessionPreviewUpdated += (_, previews) => previewsReceived.TrySetResult(previews);
        client.ConfigUpdated += (_, config) => configReceived.TrySetResult(config);
        client.ConfigSchemaUpdated += (_, schema) => schemaReceived.TrySetResult(schema);
        client.NodePairListUpdated += (_, pairs) => nodePairsReceived.TrySetResult(pairs);
        client.DevicePairListUpdated += (_, pairs) => devicePairsReceived.TrySetResult(pairs);

        await client.RequestModelsListAsync();
        var models = await modelsReceived.Task.WaitAsync(Deadline);
        Assert.Equal(2, models.Models.Count);
        Assert.All(models.Models, model => Assert.True(model.IsAvailable && model.IsConfigured));
        Assert.Contains(models.Models, model => model.Id == "research" && model.Provider == "fixture");
        await client.RequestSessionPreviewAsync([GatewayScenario.MainSessionKey], limit: 1);
        var previews = await previewsReceived.Task.WaitAsync(Deadline);
        Assert.Equal(GatewayScenario.MainSentinel, Assert.Single(Assert.Single(previews.Previews).Items).Text);
        await client.RequestConfigAsync();
        await client.RequestConfigSchemaAsync();
        var config = await configReceived.Task.WaitAsync(Deadline);
        var schema = await schemaReceived.Task.WaitAsync(Deadline);
        Assert.True(config.GetProperty("valid").GetBoolean());
        Assert.Equal(config.GetProperty("parsed").GetRawText(), config.GetProperty("config").GetRawText());
        using var rawConfig = JsonDocument.Parse(config.GetProperty("raw").GetString()!);
        Assert.Equal(config.GetProperty("config").GetRawText(), rawConfig.RootElement.GetRawText());
        var properties = schema.GetProperty("schema").GetProperty("properties");
        Assert.All(config.GetProperty("parsed").EnumerateObject(), property => Assert.True(properties.TryGetProperty(property.Name, out _)));
        Assert.Equal("fixture/browse", config.GetProperty("parsed").GetProperty("agents").GetProperty("defaults").GetProperty("model").GetProperty("primary").GetString());

        var commands = await client.ListCommandsAsync();
        Assert.True(commands.IsSupported);
        Assert.Equal("help", Assert.Single(commands.Commands).Name);
        var health = await client.SendWizardRequestAsync("health", new { deep = true });
        Assert.True(health.GetProperty("ok").GetBoolean());
        var nodes = await client.SendWizardRequestAsync("node.list");
        Assert.Empty(nodes.GetProperty("nodes").EnumerateArray());
        await client.RequestNodePairListAsync();
        await client.RequestDevicePairListAsync();
        Assert.Empty((await nodePairsReceived.Task.WaitAsync(Deadline)).Pending);
        Assert.Empty((await devicePairsReceived.Task.WaitAsync(Deadline)).Pending);
        var agents = await client.SendWizardRequestAsync("agents.list");
        Assert.Equal("main", agents.GetProperty("defaultId").GetString());
        Assert.Equal(2, agents.GetProperty("agents").GetArrayLength());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("chat.history", """{"sessionKey":"not-a-fixture-session"}""", "Unknown fixture session key")]
    [InlineData("chat.history", """{"sessionKey":"agent:main:main","limit":0}""", "positive integer")]
    [InlineData("chat.history", """{"sessionKey":42}""", "nonempty string")]
    [InlineData("chat.history", """{"sessionKey":"agent:main:main","typo":true}""", "Unsupported fixture request parameter")]
    [InlineData("chat.history", "[]", "params must be an object")]
    [InlineData("sessions.list", """{"agentId":"unknown"}""", "Unknown fixture agentId")]
    [InlineData("sessions.list", """{"includeGlobal":"yes"}""", "must be a boolean")]
    [InlineData("sessions.preview", """{"keys":[]}""", "nonempty array")]
    [InlineData("sessions.preview", """{"keys":[7]}""", "session key strings")]
    [InlineData("models.list", """{"view":"invented"}""", "configured or all")]
    [InlineData("usage.cost", """{"days":-1}""", "positive integer")]
    public async Task RealClient_InvalidOrUnknownParametersFailExplicitly(string method, string json, string message)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var parameters = JsonDocument.Parse(json);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connected.Client.SendWizardRequestAsync(method, parameters.RootElement));
        Assert.Contains(message, error.Message);
        Assert.Contains(server.Requests, request => request.Method == method && request.Outcome == "error:INVALID_PARAMS");
        Assert.Empty(server.UnexpectedRequests);
        Assert.Equal(GatewayScenario.MainSessionKey, (await connected.Client.RequestChatHistoryAsync()).SessionKey);
    }

    [Theory]
    [InlineData("chat.send")]
    [InlineData("sessions.patch")]
    [InlineData("sessions.delete")]
    [InlineData("config.patch")]
    [InlineData("config.apply")]
    [InlineData("node.invoke")]
    [InlineData("exec.approval.resolve")]
    public async Task RealClient_WritesAreRejectedWithoutChangingScenario(string method)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var before = await connected.Client.SendWizardRequestAsync("config.get");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => method == "chat.send"
                ? connected.Client.SendChatMessageForRunAsync("Synthetic write attempt.", GatewayScenario.MainSessionKey)
                : (Task)connected.Client.SendWizardRequestAsync(method, new { sessionKey = GatewayScenario.MainSessionKey, message = "Synthetic write attempt." }));
        Assert.Contains("read-only", error.Message);
        var after = await connected.Client.SendWizardRequestAsync("config.get");
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Contains(server.Requests, r => r.Method == method && r.Outcome == "error:FIXTURE_READ_ONLY");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_UnknownMethodIsRecordedAndNeverReturnsFallbackSuccessOrCredentials()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connected.Client.SendWizardRequestAsync("surprise.read", new { token, body = "PRIVATE-REQUEST-BODY" }));
        Assert.Contains("Unknown fixture Gateway method", error.Message);
        Assert.Equal("surprise.read", Assert.Single(server.UnexpectedRequests).Method);
        Assert.Equal("error:METHOD_NOT_FOUND", Assert.Single(server.UnexpectedRequests).Outcome);
        var diagnostic = JsonSerializer.Serialize(server.Requests);
        Assert.DoesNotContain(token, diagnostic);
        Assert.DoesNotContain("PRIVATE-REQUEST-BODY", diagnostic);
        Assert.DoesNotContain("signature", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealClients_IndependentServersRejectTheOtherRunTokenAndKeepSubscriptionsSeparate()
    {
        var tokenA = CreateToken();
        var tokenB = CreateToken();
        await using var serverA = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), tokenA);
        await using var serverB = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), tokenB);
        Assert.NotEqual(serverA.Endpoint, serverB.Endpoint);
        await using var first = await ConnectedClient.OpenAsync(serverA, tokenA);
        await using var second = await ConnectedClient.OpenAsync(serverB, tokenB);
        using var identity = CreateIdentity();
        using var wrong = new OpenClawGatewayClient(serverB.Endpoint.AbsoluteUri, tokenA, identityPath: identity.Path);
        var rejected = Signal<GatewayErrorKind>();
        wrong.ConnectionFailure += (_, error) => rejected.TrySetResult(error);
        await wrong.ConnectAsync();
        Assert.Equal(GatewayErrorKind.Auth, await rejected.Task.WaitAsync(Deadline));
        Assert.False(wrong.HasHandshakeSnapshot);
        Assert.True(wrong.IsAuthFailed);
        Assert.Equal(1, serverA.SubscriptionCount);
        Assert.Equal(1, serverB.SubscriptionCount);
        Assert.Equal(1, serverA.ConnectionCount);
        Assert.Equal(2, serverB.ConnectionCount);
        Assert.Contains(serverB.Requests, r => r.Method == "connect" && r.Outcome == "error:AUTH_TOKEN_MISMATCH");
        await wrong.DisconnectAsync();
        Assert.Equal(GatewayScenario.MainSessionKey, (await second.Client.RequestChatHistoryAsync()).SessionKey);
    }

    [Fact]
    public async Task RealClient_ConcurrentFreshRequestIdsCorrelateOutOfOrderRepliesAndRepeatedReads()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.HoldHistory(GatewayScenario.LongSessionKey);
        var historyA = connected.Client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("chat.history", GatewayScenario.LongSessionKey, 1, deadline.Token);
        var reads = Enumerable.Range(0, 12).Select(i => connected.Client.RequestChatHistoryAsync(
            i % 2 == 0 ? GatewayScenario.OtherSessionKey : GatewayScenario.MainSessionKey)).ToArray();
        var completed = await Task.WhenAll(reads).WaitAsync(Deadline);
        Assert.False(historyA.IsCompleted);
        for (var i = 0; i < completed.Length; i++)
            Assert.Equal(i % 2 == 0 ? GatewayScenario.OtherSessionKey : GatewayScenario.MainSessionKey, completed[i].SessionKey);
        server.ReleaseHistory(GatewayScenario.LongSessionKey);
        var longHistory = await historyA.WaitAsync(Deadline);
        Assert.Equal(240, longHistory.Messages.Count);
        Assert.EndsWith(GatewayScenario.LongFinalLine, longHistory.Messages[^1].Text);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Shutdown_CancelsHeldReadsAndRequestWaitersAndClosesOwnedSockets()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.HoldHistory(GatewayScenario.LongSessionKey);
        var history = connected.Client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("chat.history", GatewayScenario.LongSessionKey, 1, deadline.Token);
        var missing = server.WaitForRequestAsync("never.requested");
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing);
        Assert.Equal(0, server.ActiveConnectionCount);
        Assert.Equal(0, server.SubscriptionCount);
        Assert.Contains(server.Requests, r => r.Method == "chat.history" && r.Outcome == "cancelled");
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port));
    }

    [Fact]
    public async Task Shutdown_DoesNotWaitForAnIncompleteHttpUpgrade()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(), CreateToken(), cancellation.Token);
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        await server.ConnectionAccepted.WaitAsync(Deadline);
        Assert.Equal(1, server.ActiveConnectionCount);
        await cancellation.CancelAsync();
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(0, server.ActiveConnectionCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.HandshakeCompleted);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nSec-WebSocket-Key: PRIVATE-UPGRADE-DATA\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]
    public Task Upgrade_MalformedHeadersAreRecordedWithoutPoisoningServerOrShutdown(string headers) =>
        AssertRejectedUpgradeAsync(headers, "error:INVALID_UPGRADE");

    [Fact]
    public Task Upgrade_OversizedHeadersAreRecordedWithoutPoisoningServerOrShutdown() =>
        AssertRejectedUpgradeAsync(new string('x', 16 * 1024), "error:INVALID_UPGRADE");

    [Theory]
    [InlineData("")]
    [InlineData("GET / HTTP/1.1\r\n")]
    public Task Upgrade_DisconnectedPeerIsRecordedWithoutPoisoningServerOrShutdown(string headers) =>
        AssertRejectedUpgradeAsync(headers, "error:UPGRADE_DISCONNECTED");

    private static async Task AssertRejectedUpgradeAsync(string headers, string outcome)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var deadline = new CancellationTokenSource(Deadline);
        using (var peer = new TcpClient())
        {
            await peer.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port, deadline.Token);
            var stream = peer.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
            peer.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        }

        await server.WaitForRequestAsync("<upgrade>", cancellationToken: deadline.Token);
        await using (var connected = await ConnectedClient.OpenAsync(server, token))
        {
            Assert.Equal(GatewayScenario.MainSessionKey,
                (await connected.Client.RequestChatHistoryAsync()).SessionKey);
        }
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);

        var rejected = Assert.Single(server.UnexpectedRequests);
        Assert.Equal("<upgrade>", rejected.Method);
        Assert.Null(rejected.SessionKey);
        Assert.Equal(outcome, rejected.Outcome);
        Assert.Equal(0, server.ActiveConnectionCount);
        var diagnostic = JsonSerializer.Serialize(server.Requests);
        Assert.DoesNotContain(token, diagnostic);
        Assert.DoesNotContain("PRIVATE-UPGRADE-DATA", diagnostic);
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TempDirectory CreateIdentity() => new(Path.Combine(Directory.GetCurrentDirectory(), ".fixture-identity-"));

    private sealed class ConnectedClient(OpenClawGatewayClient client, TempDirectory identity) : IAsyncDisposable
    {
        public OpenClawGatewayClient Client { get; } = client;

        public static async Task<ConnectedClient> OpenAsync(FixtureGatewayServer server, string token)
        {
            var startupOccurrence = server.Requests.Count(r => r.Method == "agents.list") + 1;
            var identity = CreateIdentity();
            var client = new OpenClawGatewayClient(
                server.Endpoint.AbsoluteUri, token, NullLogger.Instance, identityPath: identity.Path,
                ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
            var owner = new ConnectedClient(client, identity);
            var handshake = Signal<bool>();
            var sessions = Signal<bool>();
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            client.SessionsUpdated += (_, data) =>
            {
                if (data.Length == 5)
                    sessions.TrySetResult(true);
            };
            try
            {
                await client.ConnectAsync();
                await Task.WhenAll(handshake.Task, sessions.Task).WaitAsync(Deadline);
                using var deadline = new CancellationTokenSource(Deadline);
                await server.WaitForRequestAsync("agents.list", null, startupOccurrence, deadline.Token);
                return owner;
            }
            catch
            {
                await owner.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisconnectAsync(); }
            finally
            {
                Client.Dispose();
                identity.Dispose();
            }
        }
    }
}
