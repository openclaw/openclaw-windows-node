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
public sealed partial class GatewayFixtureProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Browse_AdvertisesReadOnlyEmptyQuestionsForStartup()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);

        var result = await connected.Client.ListQuestionsAsync();
        Assert.True(result.IsSupported);
        Assert.Empty(result.Questions);
        Assert.Contains("question.list", connected.Client.AdvertisedServerMethods);
        Assert.DoesNotContain("question.resolve", connected.Client.AdvertisedServerMethods);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyScenarios_ReturnEmptyChannelAndScopedSkillStatus(bool nativeSetup)
    {
        var scenario = nativeSetup
            ? GatewayScenario.CreateNativeSetup((method, _) =>
                throw new InvalidOperationException($"Unexpected setup operation: {method}"))
            : GatewayScenario.CreateBrowse();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var channels = await connected.Client.GetChannelsStatusAsync();
        Assert.NotNull(channels);
        Assert.Empty(channels.Channels);
        Assert.Empty(channels.ChannelOrder);
        foreach (var agent in new[] { "main", "research" })
        {
            var skills = await connected.Client.SendWizardRequestAsync("skills.status", new { agentId = agent });
            Assert.Empty(skills.GetProperty("skills").EnumerateArray());
        }
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyScenarios_ListEmptyQuestionsThroughRealClient(bool nativeSetup)
    {
        var scenario = nativeSetup
            ? GatewayScenario.CreateNativeSetup((method, _) =>
                throw new InvalidOperationException($"Unexpected setup operation: {method}"))
            : GatewayScenario.CreateBrowse();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var result = await connected.Client.ListQuestionsAsync().WaitAsync(Deadline);
        Assert.True(result.IsSupported);
        Assert.Empty(result.Questions);
        Assert.DoesNotContain("question.resolve", scenario.ReadMethods);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync("question.list", new { unexpected = true }));
        Assert.Contains("Unsupported fixture request parameter", error.Message);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionCreation_IsOptInResponseBoundAndPreservesParent(bool allow)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowSessionMutations: allow), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        server.HoldSessionMutations();
        var create = client.CreateSessionAsync(new SessionCreateRequest
        {
            ParentSessionKey = GatewayScenario.OtherSessionKey,
            EmitCommandHooks = true,
            SucceedsParent = false
        });
        await server.WaitForRequestAsync("sessions.create", GatewayScenario.OtherSessionKey);
        await client.SendWizardRequestAsync("health");
        Assert.False(create.IsCompleted);
        server.ReleaseSessionMutations();
        var result = await create.WaitAsync(Deadline);
        Assert.Equal(allow, result.Ok);
        if (!allow)
        {
            Assert.Contains("read-only", result.Error);
            Assert.Equal(5, (await client.SendWizardRequestAsync("sessions.list")).GetProperty("count").GetInt32());
            return;
        }

        Assert.Equal("agent:research:fixture-created-1", result.Key);
        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        var listed = (await client.SendWizardRequestAsync("sessions.list")).GetProperty("sessions");
        Assert.Equal(6, listed.GetArrayLength());
        Assert.Contains(listed.EnumerateArray(), row => row.GetProperty("key").GetString() == GatewayScenario.OtherSessionKey);
        Assert.Contains(listed.EnumerateArray(), row => row.GetProperty("key").GetString() == result.Key);
        var history = await client.SendWizardRequestAsync("chat.history", new { sessionKey = result.Key });
        Assert.Empty(history.GetProperty("messages").EnumerateArray());
        Assert.Equal(result.SessionId, history.GetProperty("sessionId").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
            "sessions.create", new { parentSessionKey = GatewayScenario.OtherSessionKey, fork = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
            "sessions.create", new { parentSessionKey = "unknown" }));
        Assert.Equal(6, (await client.SendWizardRequestAsync("sessions.list")).GetProperty("count").GetInt32());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_FractionalVoiceHistoryPreservesAllContentAndIdentities()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateFractionalVoiceHistory(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: 6);
        var raw = await connected.Client.SendWizardRequestAsync("chat.history",
            new { sessionKey = GatewayScenario.VoiceSessionKey });
        var rows = raw.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(30, rows.Length);
        Assert.Equal(18, rows.Count(row => !row.GetProperty("timestamp").TryGetInt64(out _)));
        var history = await connected.Client.RequestChatHistoryAsync(GatewayScenario.VoiceSessionKey);

        Assert.Equal(30, history.Messages.Count);
        for (var index = 0; index < rows.Length; index++)
        {
            var expectedText = index == 29 ? GatewayScenario.VoiceFinalSentinel : $"Fixture voice history message {index + 1:D2}.";
            Assert.Equal(expectedText, history.Messages[index].Text);
            Assert.Equal(rows[index].GetProperty("__openclaw").GetProperty("id").GetString(), history.Messages[index].OpenClawId);
            Assert.Equal(index + 1, history.Messages[index].OpenClawSeq);
            Assert.Equal((long)rows[index].GetProperty("timestamp").GetDouble(), history.Messages[index].Ts);
        }
        Assert.Equal(30, history.Messages.Select(message => message.OpenClawId).Distinct().Count());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task NativeSetup_ResponderGuardStillFaultsServerDisposal()
    {
        var failure = new InvalidOperationException("Unexpected fixture setup method.");
        var entered = Signal<bool>();
        var token = CreateToken();
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup((_, _) =>
        {
            entered.TrySetResult(true);
            throw failure;
        }), token);
        try
        {
            await using var connected = await ConnectedClient.OpenAsync(server, token);
            var requestError = await Record.ExceptionAsync(() =>
                connected.Client.SendWizardRequestAsync("openclaw.setup.detect", timeoutMs: 1000));
            Assert.NotNull(requestError);
            await entered.Task.WaitAsync(Deadline);
        }
        finally
        {
            var disposalError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                server.DisposeAsync().AsTask().WaitAsync(Deadline));
            Assert.Same(failure, disposalError);
        }
    }

    [Fact]
    public async Task CronEditing_StoresExactSchedulesAndKeepsOtherMutationsDisabled()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateCronEditing();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var listed = await client.SendWizardRequestAsync("cron.list", new { includeDisabled = true });
        Assert.Equal(4, listed.GetProperty("jobs").GetArrayLength());

        foreach (var schedule in new object[]
        {
            new { kind = "cron", expr = "0 4 * * *", tz = "Pacific/Auckland" },
            new { kind = "cron", expr = "0 4 * * *", tz = "UTC" },
            new { kind = "cron", expr = "0 4 * * *" },
            new { kind = "every", everyMs = 60_000 }
        })
        {
            var response = await client.SendWizardRequestAsync("cron.update", new
            {
                id = "unlisted", patch = new { name = "Fixture cron unlisted", schedule }
            });
            var refreshed = await client.SendWizardRequestAsync("cron.list", new { includeDisabled = true });
            var stored = refreshed.GetProperty("jobs").EnumerateArray().Single(job => job.GetProperty("id").GetString() == "unlisted");
            Assert.Equal(JsonSerializer.Serialize(schedule), response.GetProperty("schedule").GetRawText());
            Assert.Equal(response.GetProperty("schedule").GetRawText(), stored.GetProperty("schedule").GetRawText());
        }
        var added = await client.SendWizardRequestAsync("cron.add", new
        {
            name = "New fixture job", schedule = new { kind = "cron", expr = "0 5 * * *" }
        });
        Assert.Equal("created-4", added.GetProperty("id").GetString());
        Assert.False(added.GetProperty("schedule").TryGetProperty("tz", out _));
        Assert.Equal(5, scenario.GetCronJobs().Length);
        var toggled = await client.SendWizardRequestAsync("cron.update", new
        {
            id = "listed", patch = new { enabled = false }
        });
        Assert.False(toggled.GetProperty("enabled").GetBoolean());
        Assert.Equal("America/Chicago", toggled.GetProperty("schedule").GetProperty("tz").GetString());
        var beforeInvalid = JsonSerializer.Serialize(scenario.GetCronJobs());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("cron.update",
            new { id = "listed", patch = new { schedule = new { kind = "every", everyMs = "invalid" } } }));
        Assert.Equal(beforeInvalid, JsonSerializer.Serialize(scenario.GetCronJobs()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("cron.update",
            new { id = "unknown", patch = new { name = "Unknown" } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("cron.run", new { id = "listed" }));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RestartClose_DrainsConcurrentClosesBeforeConnectionResourcesAreDisposed()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        for (var iteration = 0; iteration < 25; iteration++)
        {
            await using var connected = await ConnectedClient.OpenAsync(server, token);
            using var deadline = new CancellationTokenSource(Deadline);
            var closes = Enumerable.Range(0, 8)
                .Select(_ => server.CloseConnectionsAsync(deadline.Token)).ToArray();
            var disconnect = connected.Client.DisconnectAsync();
            await Task.WhenAll(closes.Append(disconnect)).WaitAsync(Deadline);
        }
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(0, server.ActiveConnectionCount);
        Assert.Equal(0, server.SubscriptionCount);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ContextUsageScenario_RealClientReceivesStrictPromptsReplayHistoryAndIndependentListingSnapshots()
    {
        var usage = new ContextUsageFixture();
        var scenario = GatewayScenario.CreateContextUsage(usage);
        Assert.Equal(ContextUsageFixture.Name, scenario.Name);
        Assert.NotEqual(GatewayScenario.CreateBrowse().Sha256, scenario.Sha256);
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var frames = new System.Collections.Concurrent.ConcurrentQueue<ChatMessageInfo>();
        var delivered = Signal<bool>();
        client.ChatMessageReceived += (_, message) =>
        {
            frames.Enqueue(message);
            if (frames.Count == 5) delivered.TrySetResult(true);
        };
        using var deadline = new CancellationTokenSource(Deadline);
        server.HoldHistory(ContextUsageFixture.SessionKey);
        var history = client.RequestChatHistoryAsync(ContextUsageFixture.SessionKey);
        await server.WaitForRequestAsync("chat.history", ContextUsageFixture.SessionKey, cancellationToken: deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendChatMessageForRunAsync("not a fixture prompt", ContextUsageFixture.SessionKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendChatMessageForRunAsync(ContextUsageFixture.FirstPrompt, GatewayScenario.MainSessionKey));
        await client.SendChatMessageForRunAsync(ContextUsageFixture.FirstPrompt, ContextUsageFixture.SessionKey);
        await client.SendChatMessageForRunAsync(ContextUsageFixture.SecondPrompt, ContextUsageFixture.SessionKey);
        await delivered.Task.WaitAsync(deadline.Token);
        var assistants = frames.Where(frame => frame.Role == "assistant").ToArray();
        Assert.Equal(3, assistants.Length);
        Assert.Equal(1_000, assistants[0].ResponseTokens);
        Assert.All(assistants.Skip(1), frame => Assert.Equal(1_500, frame.ResponseTokens));
        Assert.Equal(ContextUsageFixture.FirstReply, assistants[0].Text);
        Assert.Equal(ContextUsageFixture.FinalReply, assistants[1].Text);
        Assert.Equal(ContextUsageFixture.MeasurementTimestamp, assistants[1].Ts);
        Assert.Equal(assistants[1].Text, assistants[2].Text);
        Assert.Equal(0, assistants[2].Ts);
        Assert.Null(assistants[2].OpenClawId);
        Assert.Null(assistants[2].OpenClawSeq);
        Assert.False(history.IsCompleted);
        server.ReleaseHistory(ContextUsageFixture.SessionKey);
        var historyMessages = (await history.WaitAsync(deadline.Token)).Messages;
        Assert.Equal(4, historyMessages.Count());
        Assert.Equal(ContextUsageFixture.FirstReply, historyMessages[1].Text);
        var older = historyMessages.Last();
        Assert.Equal(ContextUsageFixture.FinalReply, older.Text);
        Assert.Equal(ContextUsageFixture.MeasurementTimestamp - 1, older.Ts);
        Assert.Equal(1_000, older.ResponseTokens);
        Assert.Equal(20, older.ContextPercent);

        usage.SelectListing(ContextUsageListing.MissingTotal);
        var missing = await client.SendWizardRequestAsync("sessions.list");
        var missingRow = UsageRow(missing);
        Assert.Equal(12, missingRow.GetProperty("inputTokens").GetInt32());
        Assert.Equal(480, missingRow.GetProperty("outputTokens").GetInt32());
        Assert.False(missingRow.TryGetProperty("totalTokens", out _));
        Assert.False(missingRow.GetProperty("totalTokensFresh").GetBoolean());
        usage.SelectListing(ContextUsageListing.Lower800);
        var lower = await client.SendWizardRequestAsync("sessions.list");
        Assert.True(lower.GetProperty("ts").GetInt64() < missing.GetProperty("ts").GetInt64());
        Assert.Equal(800, UsageRow(lower).GetProperty("totalTokens").GetInt32());
        usage.SelectListing(ContextUsageListing.Delayed1200);
        var captured = server.HoldNextSessionList();
        var delayed = client.SendWizardRequestAsync("sessions.list");
        await captured.WaitAsync(deadline.Token);
        usage.SelectListing(ContextUsageListing.Lower600);
        var newest = await client.SendWizardRequestAsync("sessions.list");
        Assert.Equal(600, UsageRow(newest).GetProperty("totalTokens").GetInt32());
        Assert.False(delayed.IsCompleted);
        server.ReleaseSessionList();
        var stale = await delayed.WaitAsync(deadline.Token);
        Assert.Equal(1_200, UsageRow(stale).GetProperty("totalTokens").GetInt32());
        Assert.True(stale.GetProperty("ts").GetInt64() < newest.GetProperty("ts").GetInt64());
        var refreshed = Signal<bool>();
        client.SessionsUpdated += (_, sessions) =>
        {
            if (sessions.Any(session => session.Key == ContextUsageFixture.SessionKey && session.TotalTokens == 600))
                refreshed.TrySetResult(true);
        };
        await server.NotifySessionsChangedAsync(deadline.Token);
        await refreshed.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendChatMessageForRunAsync(ContextUsageFixture.SecondPrompt, ContextUsageFixture.SessionKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("sessions.reset", new { key = ContextUsageFixture.SessionKey }));
        Assert.Empty(server.UnexpectedRequests);

        static JsonElement UsageRow(JsonElement listing) => listing.GetProperty("sessions").EnumerateArray()
            .Single(row => row.GetProperty("key").GetString() == ContextUsageFixture.SessionKey);
    }

    [Fact]
    public async Task RestartClose_PreservesCallerCancellation()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.CloseConnectionsAsync(cancellation.Token));
        Assert.Equal(GatewayScenario.MainSessionKey,
            (await connected.Client.RequestChatHistoryAsync()).SessionKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionMutations_AuthenticatedAcceptanceIsOptInAndResponseBound(bool allow)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowSessionMutations: allow), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        server.HoldSessionMutations();
        var patch = client.PatchSessionConfirmedAsync(GatewayScenario.OtherSessionKey,
            new SessionPatch { Archived = true }, epoch);
        await server.WaitForRequestAsync("sessions.patch", GatewayScenario.OtherSessionKey);
        // Drain an independent authenticated request while the mutation response is held.
        await client.SendWizardRequestAsync("health");
        Assert.False(patch.IsCompleted);
        server.ReleaseSessionMutations();
        if (!allow)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => patch);
            return;
        }
        await patch.WaitAsync(Deadline);
        var active = await client.SendWizardRequestAsync("sessions.list");
        Assert.DoesNotContain(active.GetProperty("sessions").EnumerateArray(),
            row => row.GetProperty("key").GetString() == GatewayScenario.OtherSessionKey);
        var archived = await client.ListArchivedSessionsAsync();
        Assert.Equal(GatewayScenario.OtherSessionKey, Assert.Single(archived.Sessions).Key);
        await client.DeleteSessionConfirmedAsync(GatewayScenario.OtherSessionKey, epoch);
        Assert.Empty((await client.ListArchivedSessionsAsync()).Sessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteSessionConfirmedAsync(GatewayScenario.MainSessionKey, epoch));
        Assert.Empty(server.UnexpectedRequests);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealClient_AdvertisesExecApprovalsOnlyWhenExplicitlyEnabled(bool supportsExecApprovals)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, supportsExecApprovals);

        Assert.Equal(
            supportsExecApprovals,
            Assert.Single(server.Requests, request => request.Method == "connect").AdvertisesExecApprovals);
        Assert.Empty(server.UnexpectedRequests);
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
        Assert.False(Assert.Single(server.Requests, request => request.Method == "connect").AdvertisesExecApprovals);
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
    public async Task UsagePeriodLegacyScenario_ReturnsSeedThenMismatchedAndSupportedTotals()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateUsagePeriod(new UsagePeriodFixture()), token);
        using var identity = CreateIdentity();
        using var client = new OpenClawGatewayClient(
            server.Endpoint.AbsoluteUri, token, NullLogger.Instance, identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var costs = System.Threading.Channels.Channel.CreateUnbounded<GatewayCostUsageInfo>();
        client.UsageCostUpdated += (_, cost) => costs.Writer.TryWrite(cost);
        try
        {
            await client.ConnectAsync();
            using var deadline = new CancellationTokenSource(Deadline);
            var cached = await costs.Reader.ReadAsync(deadline.Token);
            Assert.Equal(7, cached.Days);
            Assert.Equal(7, cached.Totals.TotalCost);

            await client.RequestUsageCostAsync(7);
            var mismatched = await costs.Reader.ReadAsync(deadline.Token);
            Assert.Equal(30, mismatched.Days);
            Assert.Equal(30, mismatched.Totals.TotalCost);

            await client.RequestUsageCostAsync(30);
            var selected = await costs.Reader.ReadAsync(deadline.Token);
            Assert.Equal(30, selected.Days);
            Assert.Equal(30, selected.Totals.TotalCost);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task UsagePeriodGate_HoldsCostsWithoutBlockingOtherReads()
    {
        var token = CreateToken();
        var fixture = new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.B);
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateUsagePeriod(fixture), token);
        server.HoldUsageCosts(7);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var response = connected.Client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        try
        {
            await server.WaitForRequestAsync("usage.cost");
            await connected.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
            var thirtyDays = await connected.Client.SendWizardRequestAsync("usage.cost", new { days = 30 }).WaitAsync(Deadline);
            Assert.Equal(30, thirtyDays.GetProperty("days").GetInt32());
            Assert.Equal(300, thirtyDays.GetProperty("totals").GetProperty("totalCost").GetDouble());
            Assert.False(response.IsCompleted);
            fixture.EnableMatchingSevenDays();
        }
        finally
        {
            server.ReleaseUsageCosts(7);
        }
        var cost = await response.WaitAsync(Deadline);
        Assert.Equal(7, cost.GetProperty("days").GetInt32());
        Assert.Equal(70, cost.GetProperty("totals").GetProperty("totalCost").GetDouble());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task UsagePeriodSwitch_SeparateGatewaysReturnMismatchThenOlderMatchingSnapshot()
    {
        var token = CreateToken();
        var second = new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.B);
        await using var a = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateUsagePeriod(new UsagePeriodFixture(UsagePeriodFixture.GatewayMode.A)), token);
        await using var b = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateUsagePeriod(second), token);
        await using var firstClient = await ConnectedClient.OpenAsync(a, token);
        await using var secondClient = await ConnectedClient.OpenAsync(b, token);

        var seed = await firstClient.Client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        Assert.Equal(7, seed.GetProperty("days").GetInt32());
        Assert.Equal(7, seed.GetProperty("totals").GetProperty("totalCost").GetDouble());

        var mismatch = await secondClient.Client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        Assert.Equal(30, mismatch.GetProperty("days").GetInt32());
        Assert.Equal(300, mismatch.GetProperty("totals").GetProperty("totalCost").GetDouble());
        var selected = await secondClient.Client.SendWizardRequestAsync("usage.cost", new { days = 30 });
        Assert.Equal(30, selected.GetProperty("days").GetInt32());
        Assert.Equal(300, selected.GetProperty("totals").GetProperty("totalCost").GetDouble());

        second.EnableMatchingSevenDays();
        var matching = await secondClient.Client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        Assert.Equal(7, matching.GetProperty("days").GetInt32());
        Assert.Equal(70, matching.GetProperty("totals").GetProperty("totalCost").GetDouble());
        Assert.True(matching.GetProperty("updatedAt").GetInt64() < seed.GetProperty("updatedAt").GetInt64());
        var unchanged = await firstClient.Client.SendWizardRequestAsync("usage.cost", new { days = 7 });
        Assert.Equal(7, unchanged.GetProperty("totals").GetProperty("totalCost").GetDouble());
    }

    [Fact]
    public async Task RealClient_SupportingReadsUseTypedModelsAndConsistentConfiguration()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateBrowse();
        Assert.Contains("projects.list", scenario.ReadMethods);
        Assert.Contains("environments.list", scenario.ReadMethods);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
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
        Assert.Contains("session-scoped-model-catalog", client.AdvertisedServerCapabilities);
        var sessionModels = await client.RequestSessionModelsListAsync(GatewayScenario.OtherSessionKey);
        Assert.Equal("worker", Assert.Single(sessionModels!.Models).Id);
        Assert.Contains(
            server.Requests,
            request => request.Method == "models.list" && request.SessionKey == GatewayScenario.OtherSessionKey);
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
        var placement = await client.CreateChatPlacementGateway()
            .LoadAsync("main", CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal("Fixture Main", placement.AgentName);
        Assert.Empty(placement.Workspaces);
        var environment = Assert.Single(placement.Environments);
        Assert.Equal("gateway:fixture", environment.Id);
        Assert.Equal("Fixture Gateway", environment.Label);
        Assert.True(environment.IsLocal);
        Assert.True(environment.IsSelectable);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealClient_ScopedConfiguredCatalogRetainsCurrentModelOutsideManualPolicy(bool publishedModelCatalog)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(publishedModelCatalog: publishedModelCatalog), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        var defaultCatalog = await client.SendWizardRequestAsync(
            "models.list", new { sessionKey = GatewayScenario.EdgeSessionKey });
        Assert.Empty(defaultCatalog.GetProperty("models").EnumerateArray());

        var models = await client.RequestSessionModelsListAsync(GatewayScenario.EdgeSessionKey);
        var current = Assert.Single(Assert.IsType<ModelsListInfo>(models).Models);
        Assert.Equal("research", current.Id);
        Assert.Equal("Fixture Retained Research", current.Name);
        Assert.False(current.ManualSelectionAllowed);
        Assert.True(current.IsAvailable);
        Assert.Equal("high", Assert.Single(current.ThinkingContext!.Profile!.Levels!.Value).Id);
        var request = Assert.Single(server.Requests, request =>
            request.Method == "models.list" && request.SessionKey == GatewayScenario.EdgeSessionKey
            && request.ModelCatalogView == "configured");
        Assert.Equal(publishedModelCatalog ? true : (bool?)null, request.IncludeModelDetails);
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
    [InlineData("usage.cost", """{"days":"seven"}""", "positive integer")]
    [InlineData("projects.list", """{"unexpected":true}""", "Unsupported fixture request parameter")]
    [InlineData("environments.list", """{"unexpected":true}""", "Unsupported fixture request parameter")]
    [InlineData("channels.status", """{"probe":"yes"}""", "must be a boolean")]
    [InlineData("channels.status", """{"timeoutMs":-1}""", "positive integer")]
    [InlineData("channels.status", """{"unexpected":true}""", "Unsupported fixture request parameter")]
    [InlineData("skills.status", """{"agentId":"unknown"}""", "Unknown fixture agentId")]
    [InlineData("skills.status", """{"unexpected":true}""", "Unsupported fixture request parameter")]
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
    [InlineData("chat.abort")]
    [InlineData("sessions.patch")]
    [InlineData("sessions.delete")]
    [InlineData("config.patch")]
    [InlineData("config.apply")]
    [InlineData("node.invoke")]
    [InlineData("projects.add")]
    [InlineData("skills.install")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatRun_EventsAndHeldAbortUseIssuedRunAndSession(bool fail)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowChatSend: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var sent = await client.SendChatMessageForRunAsync("Synthetic prompt.", GatewayScenario.MainSessionKey);
        Assert.Equal("fixture-chat-1", sent.RunId);
        var agent = Signal<AgentEventInfo>();
        var final = Signal<ChatMessageInfo>();
        client.AgentEventReceived += (_, value) => agent.TrySetResult(value);
        client.ChatMessageReceived += (_, value) => final.TrySetResult(value);
        await server.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, sent.RunId!, "assistant",
            new { delta = "Synthetic stream." });
        var received = await agent.Task.WaitAsync(Deadline);
        Assert.Equal("fixture-chat-1", received.RunId);
        Assert.Equal(GatewayScenario.MainSessionKey, received.SessionKey);
        Assert.Equal("assistant", received.Stream);
        Assert.Equal("Synthetic stream.", received.Data.GetProperty("delta").GetString());
        await server.PublishChatFinalAsync(GatewayScenario.MainSessionKey, sent.RunId!, "Synthetic final.");
        var completed = await final.Task.WaitAsync(Deadline);
        Assert.Equal("fixture-chat-1", completed.RunId);
        Assert.Equal(GatewayScenario.MainSessionKey, completed.SessionKey);
        Assert.Equal("Synthetic final.", completed.Text);
        Assert.Equal("final", completed.State);
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatFinalAsync(
            GatewayScenario.OtherSessionKey, sent.RunId!, "Wrong session."));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatFinalAsync(
            GatewayScenario.MainSessionKey, "unissued-run", "Unknown run."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendChatAbortAsync(sent.RunId!, GatewayScenario.OtherSessionKey));

        server.HoldChatAborts();
        var abort = client.SendChatAbortAsync(sent.RunId!, GatewayScenario.MainSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("chat.abort", GatewayScenario.MainSessionKey, cancellationToken: deadline.Token);
        await client.SendWizardRequestAsync("health");
        Assert.False(abort.IsCompleted);
        server.ReleaseChatAborts(fail);
        if (fail)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => abort.WaitAsync(Deadline));
            Assert.Contains("Fixture abort rejected.", error.Message);
        }
        else
            await abort.WaitAsync(Deadline);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ChatRun_ContentAndToolFramesPreserveIdentityAndRejectUnissuedRuns()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowChatSend: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var sent = await client.SendChatMessageForRunAsync("Synthetic prompt.", GatewayScenario.MainSessionKey);
        var tool = Signal<AgentEventInfo>();
        client.AgentEventReceived += (_, frame) => tool.TrySetResult(frame);
        await server.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, sent.RunId!, "tool",
            new { phase = "result", toolCallId = "shared-tool", result = "Synthetic result." });
        var outcome = await tool.Task.WaitAsync(Deadline);
        Assert.Equal(sent.RunId, outcome.RunId);
        Assert.Equal(GatewayScenario.MainSessionKey, outcome.SessionKey);
        Assert.Equal("tool", outcome.Stream);
        Assert.Equal("shared-tool", outcome.Data.GetProperty("toolCallId").GetString());
        foreach (var isFinal in new[] { false, true })
        {
            var message = Signal<ChatMessageInfo>();
            EventHandler<ChatMessageInfo> handler = (_, frame) => message.TrySetResult(frame);
            client.ChatMessageReceived += handler;
            await server.PublishChatContentAsync(GatewayScenario.MainSessionKey, sent.RunId!,
                [new { type = "text", text = "Synthetic content." },
                 new { type = "audio", artifactId = "fixture-audio", fileName = "fixture.wav" }], isFinal);
            var received = await message.Task.WaitAsync(Deadline);
            client.ChatMessageReceived -= handler;
            Assert.Equal(sent.RunId, received.RunId);
            Assert.Equal(GatewayScenario.MainSessionKey, received.SessionKey);
            Assert.Equal(isFinal, received.IsFinal);
            Assert.Equal("Synthetic content.", received.Text);
            Assert.Null(received.OpenClawId);
            Assert.Null(received.OpenClawSeq);
            var media = Assert.Single(received.ContentParts, part => part.Media is not null).Media!;
            Assert.Equal(ChatMediaContentKind.Audio, media.Kind);
            Assert.Equal("fixture-audio", media.ArtifactId);
            Assert.Equal("fixture.wav", media.FileName);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatContentAsync(
            GatewayScenario.OtherSessionKey, sent.RunId!, [new { type = "text", text = "Wrong session." }], true));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatContentAsync(
            GatewayScenario.MainSessionKey, "unissued-run", [new { type = "audio" }], true));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatContentAsync(
            GatewayScenario.MainSessionKey, sent.RunId!, [], true));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatRunEventAsync(
            GatewayScenario.OtherSessionKey, sent.RunId!, "tool", new { phase = "result" }));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatRunEventAsync(
            GatewayScenario.MainSessionKey, "unissued-run", "tool", new { phase = "result" }));
        await Assert.ThrowsAsync<ArgumentException>(() => server.PublishChatRunEventAsync(
            GatewayScenario.MainSessionKey, sent.RunId!, "arbitrary", new { phase = "result" }));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task ChatRun_ShutdownCancelsHeldAbort()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateBrowse(allowChatSend: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var sent = await connected.Client.SendChatMessageForRunAsync("Synthetic prompt.", GatewayScenario.MainSessionKey);
        server.HoldChatAborts();
        var abort = connected.Client.SendChatAbortAsync(sent.RunId!, GatewayScenario.MainSessionKey);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("chat.abort", GatewayScenario.MainSessionKey, cancellationToken: deadline.Token);
        await server.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abort.WaitAsync(Deadline));
        Assert.Equal(0, server.ActiveConnectionCount);
        Assert.Contains(server.Requests, request => request.Method == "chat.abort" && request.Outcome == "cancelled");
    }

    [Fact]
    public async Task RealClient_ApprovalResolveGetsSyntheticAckAndRecordsSafeCorrelation()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await server.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
        {
            phase = "requested",
            approvalId = "fixture-approval-1",
            command = "echo fixture",
        });

        await connected.Client.ResolveExecApprovalAsync("fixture-approval-1", "allow-once");

        var request = await server.WaitForCompletedRequestAsync("exec.approval.resolve");
        Assert.Equal("fixture-approval-1", request.ApprovalId);
        Assert.Equal("allow-once", request.Decision);
        Assert.Equal("ok", request.Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_ApprovalResolveInvalidDecisionDoesNotConsumeIssuedId()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await server.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
        {
            phase = "requested",
            approvalId = "fixture-approval-invalid",
            command = "echo fixture",
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync(
                "exec.approval.resolve",
                new { id = "fixture-approval-invalid", decision = "approve" }));
        await connected.Client.ResolveExecApprovalAsync("fixture-approval-invalid", "allow-once");

        Assert.Contains("Unsupported approval decision", error.Message);
        var requests = server.Requests
            .Where(request => request.Method == "exec.approval.resolve")
            .ToArray();
        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal("fixture-approval-invalid", request.ApprovalId);
                Assert.Equal("<unknown>", request.Decision);
                Assert.Equal("error:INVALID_PARAMS", request.Outcome);
            },
            request =>
            {
                Assert.Equal("fixture-approval-invalid", request.ApprovalId);
                Assert.Equal("allow-once", request.Decision);
                Assert.Equal("ok", request.Outcome);
            });
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_ApprovalResolveRejectsUnissuedFixtureId()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.ResolveExecApprovalAsync("fixture-never-issued", "deny"));

        Assert.Contains("not issued by the fixture", error.Message);
        var request = Assert.Single(server.Requests, request => request.Method == "exec.approval.resolve");
        Assert.Equal("fixture-never-issued", request.ApprovalId);
        Assert.Equal("deny", request.Decision);
        Assert.Equal("error:INVALID_PARAMS", request.Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_ApprovalResolveRejectsIdFromUndeliveredEvent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);

        var publishError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            {
                phase = "requested",
                approvalId = "fixture-undelivered",
            }));
        Assert.Contains("No authenticated fixture connection", publishError.Message);

        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var resolveError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.ResolveExecApprovalAsync("fixture-undelivered", "deny"));

        Assert.Contains("not issued by the fixture", resolveError.Message);
        var request = Assert.Single(server.Requests, request => request.Method == "exec.approval.resolve");
        Assert.Equal("error:INVALID_PARAMS", request.Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task RealClient_ApprovalResolveAcknowledgesIssuedIdOnlyOnce()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await server.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
        {
            phase = "requested",
            approvalId = "fixture-one-shot",
            command = "echo fixture",
        });

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await connected.Client.ResolveExecApprovalAsync("fixture-one-shot", "allow-once");
                return "ok";
            }
            catch (InvalidOperationException ex)
            {
                Assert.Contains("not issued by the fixture", ex.Message);
                return "rejected";
            }
        }));

        Assert.Equal(1, attempts.Count(result => result == "ok"));
        Assert.Equal(1, attempts.Count(result => result == "rejected"));
        Assert.Contains(server.Requests, request =>
            request.Method == "exec.approval.resolve" && request.Outcome == "ok");
        Assert.Contains(server.Requests, request =>
            request.Method == "exec.approval.resolve" && request.Outcome == "error:INVALID_PARAMS");
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

    [Fact]
    public async Task CloseConnectionsAsync_ToleratesAConnectionHandlerThatDisposedItsSendLock()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var connected = await ConnectedClient.OpenAsync(server, token);
            var restart = server.CloseConnectionsAsync();
            await connected.DisposeAsync();
            await restart.WaitAsync(Deadline);
        }
    }

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

    [Fact]
    public async Task RealNodeClient_FixtureAcceptsTheNodeRole()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var data = new TempDirectory(".fixture-node-");
        using var node = new WindowsNodeClient(server.Endpoint.AbsoluteUri, token, data.Path);
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.HandshakeSucceeded += (_, _) => handshake.TrySetResult();
        await node.ConnectAsync();
        await handshake.Task.WaitAsync(Deadline);
        Assert.Contains(server.Requests, request => request.Method == "connect" && request.Outcome == "ok");
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TempDirectory CreateIdentity() => new(Path.Combine(Directory.GetCurrentDirectory(), ".fixture-identity-"));

    private sealed class ConnectedClient(OpenClawGatewayClient client, TempDirectory identity) : IAsyncDisposable
    {
        public OpenClawGatewayClient Client { get; } = client;

        public static async Task<ConnectedClient> OpenAsync(
            FixtureGatewayServer server, string token, bool supportsExecApprovals = false, int expectedSessionCount = 5)
        {
            var startupOccurrence = server.Requests.Count(r => r.Method == "agents.list") + 1;
            var identity = CreateIdentity();
            var client = new OpenClawGatewayClient(
                server.Endpoint.AbsoluteUri, token, NullLogger.Instance, identityPath: identity.Path,
                ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false)
            {
                SupportsExecApprovals = supportsExecApprovals,
            };
            var owner = new ConnectedClient(client, identity);
            var handshake = Signal<bool>();
            var sessions = Signal<bool>();
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            client.SessionsUpdated += (_, data) =>
            {
                if (data.Length == expectedSessionCount)
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
