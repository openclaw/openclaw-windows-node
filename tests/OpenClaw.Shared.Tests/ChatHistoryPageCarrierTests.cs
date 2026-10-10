using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Portable fixture proof for the bounded chat.history page carrier: real socket + tracked request.
/// The admitted WebSocket handler treats offset 0 as the TAIL, so pages are asserted tail-relative
/// (offset 0 returns the newest rows; larger offsets walk toward older rows), chronological (oldest
/// first) within each page. Exact row identities, exhaustion, and the transmitted limit/offset/
/// maxBytes parameters (and omissions) are asserted from the tracked request data. No byte-cap and no
/// completeness of the legacy call are claimed here.
/// </summary>
public class ChatHistoryPageCarrierTests
{
    private static JsonNode LoadActualProducerFixture(string name)
    {
        // Fixtures are the MATERIALIZED bytes of the ACTUAL source producer (chat-history-response-page-
        // DI9eo1_W.mjs prepareChatHistoryResponsePage) driven on synthetic data, gzipped to keep the repo small.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", name);
            if (File.Exists(candidate))
            {
                using var file = File.OpenRead(candidate);
                using var gz = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gz);
                return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture '{name}' was not found.");
    }

    // GROUNDED COUNTEREXAMPLE (actual producer): the ACTUAL current handler assembly STRIPS omission from the
    // wire payload (pinned chat-history-handler-ClK-_hls.mjs, line 723 destructures omission out of the response
    // fields), so a VALID completeCLI partial that legitimately exceeds the requested row/byte budget carries no
    // provenance and the strict consumer REJECTS it - the valid received content is LOST. This behaviour must FAIL
    // the desired contract.
    [Fact]
    public async Task Mini_ActualHandlerStripsLossMetadata_ValidPartialBeyondRequestIsRejected()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var raw = LoadActualProducerFixture("actual-producer-completecli-partial-legacy.json.gz");
            raw["sessionKey"] = key;
            server.HistoryResponseOverride = _ => raw;
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 4, MaxBytes: 1024)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // ACTUAL object/encoded serializer output (619B/5rows/rawcursor16 fixture path) through the REAL socket
    // carrier: the encoded branch materializes IDENTICAL content to the object branch (same 5 rows, cursor 16).
    [Fact]
    public async Task Mini_ActualEncodedSerializedPayload_AdmitsWithSameContentIdentity()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var raw = LoadActualProducerFixture("actual-producer-object-encoded-5rows.json.gz");
            raw["sessionKey"] = key;
            server.HistoryResponseOverride = _ => raw;
            var page = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 5));
            Assert.Equal(5, page.Messages.Count);
            Assert.Equal(16, page.NextOffset);
            Assert.True(page.HasMore);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // ISOLATED CORRECTED producer (ACTUAL projection + additive wire metadata): the ACTUAL omission is forwarded
    // and the explicit completeCliImport provenance admits the valid partial; ALL received rows are preserved and
    // the reported loss stays visible (never a false complete).
    [Fact]
    public async Task Mini_IsolatedProducerForwardsOmission_ValidPartialBeyondRequestIsAdmitted()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var raw = LoadActualProducerFixture("actual-producer-completecli-partial-corrected.json.gz");
            raw["sessionKey"] = key;
            server.HistoryResponseOverride = _ => raw;
            var page = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 4, MaxBytes: 1024));
            Assert.Equal(3045, page.Messages.Count);                 // all received rows preserved
            Assert.True(page.CompleteCliImport);                     // explicit source-backed provenance
            Assert.True(page.ContentLossReported);                   // actual reported loss surfaced
            Assert.Equal(1155, page.OmittedCount);
            Assert.False(page.ContentComplete);                      // never a false complete
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }
    // Pinned synthetic fixture: exact bytes of Mini-Actual-Gateway-Projected-Sibling-Boundary/Payload.json
    // (604 bytes, sha256 d334acefbd23b4dbdd9d1bad4f31314ef0bc777317753be2e73932d7f210ef34). Embedded so the
    // control is self-contained and platform independent (no shared-mailbox path dependency).
    private const string ActualGatewayProjectedSiblingPayload = """
        {"sessionKey": "synthetic-source-session", "sessionId": "00000000-0000-0000-0000-000000000001", "messages": [{"role": "assistant", "content": "synthetic-a", "__openclaw": {"seq": 85, "id": "a"}}, {"role": "assistant", "content": "synthetic-b", "__openclaw": {"seq": 85, "id": "b"}}, {"role": "assistant", "content": "synthetic-c", "__openclaw": {"seq": 85, "id": "c"}}, {"role": "user", "content": "synthetic-d", "__openclaw": {"seq": 86, "id": "d"}}, {"role": "user", "content": "synthetic-e", "__openclaw": {"seq": 87, "id": "e"}}], "offset": 5, "nextOffset": 16, "hasMore": true, "totalMessages": 100}
        """;

    // FULL CLI / large-group contract: a wire-proven completeSnapshot page may exceed BOTH the requested byte
    // budget and the old invented 4096 projected-row cap (it is bounded by the hard inbound wire cap only).
    [Fact]
    public async Task PageAdmissionAcceptsWireCompleteSnapshotBeyondRequestedBudgetAndRowBounds()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var rows = Enumerable.Range(0, 4100).Select(i => (object)new
            {
                role = "assistant", content = "complete-row-" + i,
                __openclaw = new { seq = 7 }
            }).ToArray();
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = rows, offset = 5, hasMore = false, totalMessages = 4100, completeSnapshot = true
            };
            var page = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 4, MaxBytes: 1024, Offset: 5));
            Assert.Equal(4100, page.Messages.Count);      // beyond the old 4096 cap, inside the wire bound
            Assert.Equal(ChatHistoryPageCompleteness.SnapshotExplicitComplete, page.Completeness);
            Assert.True(page.ContentComplete);
            Assert.False(page.ContentLossReported);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // ORDINARY paginated page: the completeSnapshot flag is OMITTED BY DESIGN, so it must NOT read as partial/loss.
    [Fact]
    public async Task PageAdmissionOrdinaryPaginatedPageIsNotPartial()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[] { new { role = "assistant", content = "ordinary-0", __openclaw = new { seq = 9 } } },
                offset = 0, hasMore = false, totalMessages = 99
            };
            var page = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 0));
            Assert.Single(page.Messages);
            Assert.False(page.ContentComplete);
            Assert.False(page.ContentLossReported);   // a missing flag is NOT evidence of omission
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // ACTUAL reported omission (source emits omission only when content really was omitted) => ReportedLoss.
    [Fact]
    public async Task PageAdmissionReportedOmissionIsLossNotFalseComplete()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[] { new { role = "assistant", content = "kept-0", __openclaw = new { seq = 11 } } },
                offset = 0, hasMore = false, totalMessages = 99,
                omission = new { omittedCount = 3, normalizedBytes = 4096 }
            };
            var page = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 0));
            Assert.Single(page.Messages);                       // valid received content preserved
            Assert.False(page.ContentComplete);
            Assert.True(page.ContentLossReported);
            Assert.Equal(3, page.OmittedCount);
            Assert.Equal(4096L, page.NormalizedBytes);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // CONTRADICTORY wire flags are rejected (complete snapshot cannot also claim more pages).
    [Fact]
    public async Task PageAdmissionRejectsContradictoryCompleteAndHasMore()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[] { new { role = "assistant", content = "x", __openclaw = new { seq = 1 } } },
                offset = 0, hasMore = true, nextOffset = 1, totalMessages = 9, completeSnapshot = true
            };
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 0)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // ---- Adopted verbatim from Mini-Actual-Gateway-Numeric-0ba-Counterexamples (expectations unweakened). ----
    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e0", true)]
    [InlineData("9007199254740992", false)]
    public async Task MiniReview_SequenceNumericContractMatchesActualGateway(string sequence, bool sourcePreservesSiblings)
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var row = "{\"role\":\"assistant\",\"content\":\"synthetic\",\"__openclaw\":{\"seq\":" + sequence + "}}";
            var raw = System.Text.Json.Nodes.JsonNode.Parse("{\"sessionKey\":\"synthetic\",\"sessionId\":\"00000000-0000-0000-0000-000000000001\",\"messages\":[" + row + "," + row + "," + row + "],\"offset\":0,\"hasMore\":false,\"totalMessages\":3}")!.AsObject();
            raw["sessionKey"] = key;
            server.HistoryResponseOverride = _ => raw;
            if (sourcePreservesSiblings)
            {
                var page = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit:2,MaxBytes:1024,Offset:0));
                Assert.Equal(3,page.Messages.Count);
            }
            else
                await Assert.ThrowsAsync<ChatHistoryPageException>(() => client.RequestChatHistoryPageAsync(key,new ChatHistoryPageOptions(Limit:2,MaxBytes:1024,Offset:0)));
        }
        finally { try { await client.DisconnectAsync(); } catch { } await server.DisposeAsync(); }
    }

    // ---- Adopted verbatim from Mini-Actual-Projected-Admission-5aca-Counterexample (must be unaltered). ----
    [Fact]
    public async Task MiniReview_ActualGatewayProjectedSiblingsMustRemainUsableWithinByteBudget()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var raw = System.Text.Json.Nodes.JsonNode.Parse(ActualGatewayProjectedSiblingPayload)!.AsObject();
            raw["sessionKey"] = key;
            server.HistoryResponseOverride = _ => raw;
            var page = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 4, MaxBytes: 1024, Offset: 5));
            Assert.Equal(5, page.Messages.Count);
            Assert.Equal(16, page.NextOffset);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // A projected overflow justified ONLY by same-seq siblings; a DIFFERENT-seq extra row is still rejected.
    [Fact]
    public async Task PageAdmissionRejectsOverflowWithoutSameSequenceSiblings()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[]
                {
                    new { role = "assistant", content = "synthetic-a", __openclaw = new { seq = 85, id = "a" } },
                    new { role = "assistant", content = "synthetic-b", __openclaw = new { seq = 86, id = "b" } },
                    new { role = "user", content = "synthetic-c", __openclaw = new { seq = 87, id = "c" } },
                },
                offset = 0, hasMore = false, totalMessages = 3
            };
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 2, MaxBytes: 1024, Offset: 0)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // A page within the requested limit is admitted regardless of seq metadata (guard is not over-strict).
    [Fact]
    public async Task PageAdmissionAcceptsWithinLimitWithSeqMetadata()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[]
                {
                    new { role = "assistant", content = "synthetic-a", __openclaw = new { seq = 85, id = "a" } },
                    new { role = "assistant", content = "synthetic-b", __openclaw = new { seq = 85, id = "b" } },
                },
                offset = 0, hasMore = false, totalMessages = 2
            };
            var page = await client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 2, MaxBytes: 1024, Offset: 0));
            Assert.Equal(2, page.Messages.Count);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // The message-array byte budget counts UTF-8 bytes for MULTIBYTE content (600 x U+00E9 = 1200 bytes > 1024).
    [Fact]
    public async Task PageAdmissionCountsMultibyteUtf8Bytes()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var content = new string('\u00e9', 600);   // string length 600 < 1024; UTF-8 = 1200 bytes
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[] { new { role = "user", content, timestamp = 1000 } },
                offset = 0, hasMore = false, totalMessages = 1
            };
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 0)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MiniReview_PageAdmissionRejectsIgnoredRequestedContentBudget(bool excessRows)
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            object[] messages = excessRows
                ? Enumerable.Range(0, 3).Select(i => (object)new { role = "user", content = "synthetic-row-" + i, timestamp = 1000 + i }).ToArray()
                : [(object)new { role = "user", content = new string('x', 4096), timestamp = 1000 }];
            server.HistoryResponseOverride = _ => new {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages, offset = 0, hasMore = false, totalMessages = messages.Length
            };
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 2, MaxBytes: 1024, Offset: 0)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // The requested CONTENT budget is exact: a page that returns exactly the requested rows and JSON array
    // bytes (including array/metadata escaping) is ADMITTED (no over-strict rejection).
    [Fact]
    public async Task MiniReview_PageAdmissionAcceptsExactRequestedContentBudget()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[]
                {
                    new { role = "user", content = "synthetic-row-0", timestamp = 1000 },
                    new { role = "user", content = "synthetic-row-1", timestamp = 1001 },
                },
                offset = 0, hasMore = false, totalMessages = 2
            };
            var page = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 2, MaxBytes: 1024, Offset: 0));
            Assert.Equal(2, page.Messages.Count);
            Assert.Equal(0, page.ResponseOffset);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // The budget counts UTF-8 JSON BYTES (escaping + multibyte), NOT the .NET string length: 600 newline chars
    // have a string length well under 1024 but serialize to > 1024 bytes once escaped.
    [Fact]
    public async Task MiniReview_PageAdmissionCountsEscapedMultibyteBytesNotStringLength()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var content = new string('\n', 600);   // string length 600 (< 1024); escaped JSON >= 1200 bytes
            server.HistoryResponseOverride = _ => new {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[] { new { role = "user", content, timestamp = 1000 } },
                offset = 0, hasMore = false, totalMessages = 1
            };
            await Assert.ThrowsAsync<ChatHistoryPageException>(() =>
                client.RequestChatHistoryPageAsync(key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 0)));
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    // A VALID replay page (older offset > 0) is admitted and its RAW nextOffset is preserved verbatim - never
    // recomputed from row arithmetic.
    [Fact]
    public async Task MiniReview_PageAdmissionAcceptsValidReplayNextOffset()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            server.HistoryResponseOverride = _ => new {
                sessionKey = key, sessionId = "00000000-0000-0000-0000-000000000001",
                messages = new object[]
                {
                    new { role = "user", content = "replay-row-0", timestamp = 900 },
                    new { role = "user", content = "replay-row-1", timestamp = 901 },
                },
                offset = 5, hasMore = true, nextOffset = 15, totalMessages = 100
            };
            var page = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 1024, Offset: 5));
            Assert.Equal(2, page.Messages.Count);
            Assert.Equal(5, page.ResponseOffset);
            Assert.True(page.HasMore);
            Assert.Equal(15, page.NextOffset);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static string CreateToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static string CreateIdentityPath() =>
        Path.Combine(Directory.CreateTempSubdirectory("oc-page-identity-").FullName, "device.json");

    private static async Task<(FixtureGatewayServer Server, OpenClawGatewayClient Client)> OpenAsync()
    {
        var token = CreateToken();
        var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        var client = new OpenClawGatewayClient(
            server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: CreateIdentityPath(), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var five = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
        client.SessionsUpdated += (_, data) => { if (data.Length == 5) five.TrySetResult(true); };
        await client.ConnectAsync();
        await Task.WhenAll(handshake.Task, five.Task).WaitAsync(Deadline);
        return (server, client);
    }

    /// <summary>Expected text for the synthetic long-conversation message with this 1-based number.</summary>
    private static string ExpectedText(int number) => number switch
    {
        1 => GatewayScenario.LongEarlySentinel,
        120 => GatewayScenario.LongMiddleSentinel,
        240 => GatewayScenario.LongFinalSentinel,
        _ => $"Fixture long message {number:D3}"
    };

    /// <summary>Recover the fixture message number from a returned message's flattened text.</summary>
    private static int NumberOf(string? text)
    {
        text ??= string.Empty;
        if (text.Contains(GatewayScenario.LongFinalSentinel, StringComparison.Ordinal)) return 240;
        if (text.Contains(GatewayScenario.LongMiddleSentinel, StringComparison.Ordinal)) return 120;
        if (text.Contains(GatewayScenario.LongEarlySentinel, StringComparison.Ordinal)) return 1;
        const string marker = "Fixture long message ";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Unexpected fixture message text: {text}");
        return int.Parse(text.Substring(idx + marker.Length, 3));
    }

    private static int[] Numbers(GatewayChatHistoryPage page) =>
        page.Messages.Select(m => NumberOf(m.Text)).ToArray();

    [Fact]
    public async Task PagedHistory_WalksTailRelativeWithExactIdentitiesAndTransmittedParams()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var total = GatewayScenario.LongMessageCount;

            // Tail-relative page 1: offset 0 is the tail, so the newest rows return, oldest first.
            var page1 = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, Offset: 0));
            Assert.Equal(Enumerable.Range(231, 10), Numbers(page1));
            Assert.Equal(0, page1.ResponseOffset);
            Assert.True(page1.HasMore);
            Assert.Equal(10, page1.NextOffset);
            Assert.Equal(total, page1.Total);
            Assert.Contains(ExpectedText(240), page1.Messages[^1].Text);

            // Page 2 continues toward older history: strictly older rows, no overlap.
            var page2 = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, Offset: page1.NextOffset!.Value));
            Assert.Equal(Enumerable.Range(221, 10), Numbers(page2));
            Assert.Equal(10, page2.ResponseOffset);
            Assert.Equal(20, page2.NextOffset);
            Assert.True(Numbers(page2).Max() < Numbers(page1).Min());

            // Exhaustion at the oldest end: full bounded window with no further page.
            var oldest = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, Offset: total - 10));
            Assert.Equal(Enumerable.Range(1, 10), Numbers(oldest));
            Assert.Equal(total - 10, oldest.ResponseOffset);
            Assert.False(oldest.HasMore);
            Assert.Null(oldest.NextOffset);

            // A cap-carrying request: maxBytes is transmitted (boundary rows still tail-relative).
            var capped = await client.RequestChatHistoryPageAsync(
                key, new ChatHistoryPageOptions(Limit: 10, MaxBytes: 4096, Offset: 0));
            Assert.Equal(Enumerable.Range(231, 10), Numbers(capped));

            // Tracked request data proves the transmitted bounds and the omissions.
            var history = server.Requests.Where(r => r.Method == "chat.history").ToArray();
            Assert.Equal(4, history.Length);
            Assert.All(history, r => Assert.Equal(key, r.SessionKey));
            Assert.Equal("10", history[0].Limit);
            Assert.Equal("0", history[0].Offset);
            Assert.Null(history[0].MaxBytes);
            Assert.Equal("10", history[1].Offset);
            Assert.Null(history[1].MaxBytes);
            Assert.Equal((total - 10).ToString(), history[2].Offset);
            Assert.Equal("0", history[3].Offset);
            Assert.Equal("4096", history[3].MaxBytes);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task PagedHistory_ChainWalksEveryRowExactlyOnceDownToTheOldest()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var total = GatewayScenario.LongMessageCount;
            const int limit = 40;

            var seen = new List<int>();
            var pageOffsets = new List<int>();
            int? offset = 0;
            while (offset is int current)
            {
                var page = await client.RequestChatHistoryPageAsync(
                    key, new ChatHistoryPageOptions(Limit: limit, Offset: current));
                Assert.Equal(current, page.ResponseOffset);
                Assert.Equal(total, page.Total);

                var numbers = Numbers(page);
                Assert.NotEmpty(numbers);
                Assert.Equal(numbers.OrderBy(n => n), numbers);           // chronological within the page
                Assert.Equal(Enumerable.Range(numbers.Min(), numbers.Length), numbers); // contiguous
                pageOffsets.Add(current);
                seen.AddRange(numbers);

                if (page.HasMore)
                {
                    Assert.NotNull(page.NextOffset);
                    Assert.True(page.NextOffset > current);
                    offset = page.NextOffset;
                }
                else
                {
                    Assert.Null(page.NextOffset);
                    offset = null;
                }
            }

            Assert.Equal(Enumerable.Range(0, total / limit).Select(i => i * limit), pageOffsets);
            Assert.Equal(total, seen.Count);
            Assert.Equal(total, seen.Distinct().Count());
            Assert.Equal(Enumerable.Range(1, total), seen.OrderBy(n => n)); // every row exactly once; pages walk newest to oldest
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyHistoryCall_StillReturnsTailAndOmitsEveryPagingParam()
    {
        var (server, client) = await OpenAsync();
        try
        {
            // Legacy path is untouched: no limit/offset/maxBytes is sent, and the tail is returned.
            var legacy = await client.RequestChatHistoryAsync(GatewayScenario.LongSessionKey);
            Assert.NotEmpty(legacy.Messages);
            Assert.Contains(GatewayScenario.LongFinalSentinel,
                legacy.Messages[^1].Text ?? string.Empty);

            var history = server.Requests.Where(r => r.Method == "chat.history").ToArray();
            var request = Assert.Single(history);
            Assert.Equal(GatewayScenario.LongSessionKey, request.SessionKey);
            Assert.Null(request.Limit);
            Assert.Null(request.Offset);
            Assert.Null(request.MaxBytes);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StrictAdmission_AdmitsOnlySourceShapedPages()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            var opts = new ChatHistoryPageOptions(Limit: 10, Offset: 0);

            // Valid mid-history page: hasMore with a nextOffset strictly past the request and below total.
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "fixture-id", messages = Array.Empty<object>(),
                offset = 0, nextOffset = 10, hasMore = true, totalMessages = GatewayScenario.LongMessageCount
            };
            var mid = await client.RequestChatHistoryPageAsync(key, opts);
            Assert.True(mid.HasMore);
            Assert.Equal(10, mid.NextOffset);
            Assert.Equal(GatewayScenario.LongMessageCount, mid.Total);
            Assert.Equal(0, mid.ResponseOffset);

            // Valid exhausted page: completion legitimately omits nextOffset (null is honored).
            server.HistoryResponseOverride = _ => new
            {
                sessionKey = key, sessionId = "fixture-id", messages = Array.Empty<object>(),
                offset = 0, nextOffset = (int?)null, hasMore = false, totalMessages = GatewayScenario.LongMessageCount
            };
            var done = await client.RequestChatHistoryPageAsync(key, opts);
            Assert.False(done.HasMore);
            Assert.Null(done.NextOffset);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StrictAdmission_AdmitsValidEmptyExhaustedHistory()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var page = await client.RequestChatHistoryPageAsync(
                GatewayScenario.EmptySessionKey, new ChatHistoryPageOptions(Limit: 10, Offset: 0));
            Assert.Empty(page.Messages);
            Assert.False(page.HasMore);
            Assert.Null(page.NextOffset);
            Assert.Equal(0, page.Total);
            Assert.Equal(0, page.ResponseOffset);
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StrictAdmission_RejectsMalformedAndUnsupportedMetadata()
    {
        var (server, client) = await OpenAsync();
        try
        {
            var key = GatewayScenario.LongSessionKey;
            const int total = GatewayScenario.LongMessageCount;
            var opts = new ChatHistoryPageOptions(Limit: 10, Offset: 0);
            var rows = Array.Empty<object>();

            // Every shape below was silently admitted by the prior permissive parser (non-object ->
            // complete=false; missing/ill-typed hasMore -> false; hasMore with no nextOffset -> accepted;
            // missing/negative totalMessages -> null). Each must now fail visibly.
            (string Name, Func<System.Text.Json.JsonElement, object?> Payload)[] cases =
            [
                ("non-object payload", _ => "not-an-object"),
                ("null payload", _ => null),
                ("missing messages", _ => new { sessionKey = key, offset = 0, nextOffset = 10, hasMore = true, totalMessages = total }),
                ("messages wrong type", _ => new { sessionKey = key, messages = "nope", offset = 0, nextOffset = 10, hasMore = true, totalMessages = total }),
                ("missing hasMore", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 10, totalMessages = total }),
                ("hasMore wrong type", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 10, hasMore = "true", totalMessages = total }),
                ("hasMore true missing nextOffset", _ => new { sessionKey = key, messages = rows, offset = 0, hasMore = true, totalMessages = total }),
                ("hasMore true negative nextOffset", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = -1, hasMore = true, totalMessages = total }),
                ("hasMore true non-advancing nextOffset", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 0, hasMore = true, totalMessages = total }),
                ("hasMore true nextOffset beyond total", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = total, hasMore = true, totalMessages = total }),
                ("completed but numeric nextOffset", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 5, hasMore = false, totalMessages = total }),
                ("missing totalMessages", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 10, hasMore = true }),
                ("negative totalMessages", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 10, hasMore = true, totalMessages = -1 }),
                ("totalMessages wrong type", _ => new { sessionKey = key, messages = rows, offset = 0, nextOffset = 10, hasMore = true, totalMessages = "240" }),
                ("offset mismatch", _ => new { sessionKey = key, messages = rows, offset = 5, nextOffset = 15, hasMore = true, totalMessages = total }),
                ("missing sessionKey echo", _ => new { messages = rows, offset = 0, nextOffset = 10, hasMore = true, totalMessages = total }),
                ("mismatched sessionKey echo", _ => new { sessionKey = GatewayScenario.OtherSessionKey, messages = rows, offset = 0, nextOffset = 10, hasMore = true, totalMessages = total }),
            ];

            foreach (var (name, payload) in cases)
            {
                server.HistoryResponseOverride = payload;
                var ex = await Assert.ThrowsAsync<ChatHistoryPageException>(
                    () => client.RequestChatHistoryPageAsync(key, opts));
                Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"no diagnostic message for: {name}");
            }
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            await server.DisposeAsync();
        }
    }
}
