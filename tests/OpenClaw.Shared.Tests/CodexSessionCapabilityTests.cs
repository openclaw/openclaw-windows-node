using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Codex;

namespace OpenClaw.Shared.Tests;

public sealed class CodexSessionCapabilityTests
{
    private const string ThreadId = "123e4567-e89b-12d3-a456-426614174000";

    [Fact]
    public void Commands_ExposeExactlyTheTwoReadOnlyCatalogOperations()
    {
        var capability = CreateCapability(new RecordingCatalogClient());

        Assert.Equal(
            [
                "codex.appServer.threads.list.v1",
                "codex.appServer.thread.turns.list.v1",
            ],
            capability.Commands);
        Assert.Equal("codex-app-server-threads", capability.Category);
    }

    [Fact]
    public async Task ThreadsList_ProjectsTheLiteralCoreCatalogFixtureWithoutPrivateFields()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = Json("""
                {
                  "data": [
                    {
                      "id": "123e4567-e89b-12d3-a456-426614174000",
                      "sessionId": "cli-session-1",
                      "name": "Remote task",
                      "preview": "must stay private",
                      "cwd": "C:\\workspace\\project",
                      "status": { "type": "active", "activeFlags": ["waitingOnApproval"] },
                      "source": "vscode",
                      "modelProvider": "openai",
                      "cliVersion": "1.2.3",
                      "createdAt": 123,
                      "updatedAt": 456,
                      "recencyAt": 455,
                      "gitInfo": { "branch": "feature/catalog", "originUrl": "private" },
                      "turns": [{ "private": true }],
                      "path": "C:\\private\\rollout.jsonl"
                    }
                  ],
                  "nextCursor": "page-2",
                  "backwardsCursor": "page-0"
                }
                """),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.threads.list.v1",
            """{"cursor":"page-1","limit":25,"cwd":"C:\\workspace\\project"}""");

        Assert.True(response.Ok, response.Error);
        AssertJsonEqual(
            """
                {
                  "sessions": [
                    {
                      "threadId": "123e4567-e89b-12d3-a456-426614174000",
                      "status": "active",
                      "archived": false,
                      "sessionId": "cli-session-1",
                      "name": "Remote task",
                      "cwd": "C:\\workspace\\project",
                      "activeFlags": ["waitingOnApproval"],
                      "createdAt": 123,
                      "updatedAt": 456,
                      "recencyAt": 455,
                      "source": "vscode",
                      "modelProvider": "openai",
                      "cliVersion": "1.2.3",
                      "gitBranch": "feature/catalog"
                    }
                  ],
                  "nextCursor": "page-2",
                  "backwardsCursor": "page-0"
                }
                """,
            PayloadJson(response));
        Assert.Equal([CodexAppServerProtocol.ThreadListMethod], client.Methods);
        AssertJsonEqual(
            """
                {
                  "cursor": "page-1",
                  "limit": 25,
                  "modelProviders": [],
                  "sortKey": "updated_at",
                  "sortDirection": "desc",
                  "archived": false,
                  "useStateDbOnly": true,
                  "cwd": "C:\\workspace\\project"
                }
                """,
            client.Parameters.Single());
        Assert.DoesNotContain("private", PayloadJson(response).GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thread/resume", client.Methods);
    }

    [Fact]
    public async Task ThreadTurnsList_RequiresFreshEligibilityAndProjectsTheLiteralCoreFixture()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = EligibleThreadsPage(),
            TurnsResponse = Json("""
                {
                  "data": [
                    {
                      "id": "turn-1",
                      "status": "completed",
                      "itemsView": "full",
                      "items": [
                        { "id": "item-1", "type": "agentMessage", "text": "bounded answer" }
                      ]
                    }
                  ],
                  "nextCursor": "turns-page-2"
                }
                """),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}","cursor":"turns-page-1","limit":25}""");

        Assert.True(response.Ok, response.Error);
        AssertJsonEqual(client.TurnsResponse.GetRawText(), PayloadJson(response));
        Assert.Equal(
            [CodexAppServerProtocol.ThreadListMethod, CodexAppServerProtocol.ThreadTurnsListMethod],
            client.Methods);
        Assert.Equal(10, client.Parameters[0].GetProperty("limit").GetInt32());
        Assert.False(client.Parameters[0].TryGetProperty("useStateDbOnly", out _));
        AssertJsonEqual(
            $$"""{"threadId":"{{ThreadId}}","cursor":"turns-page-1","limit":25,"sortDirection":"desc","itemsView":"full"}""",
            client.Parameters[1]);
        Assert.DoesNotContain("thread/resume", client.Methods);
    }

    [Fact]
    public async Task ThreadTurnsList_ProjectsOnlyTheAllowlistedTranscriptContract()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = EligibleThreadsPage(),
            TurnsResponse = Json("""
                {
                  "data": [
                    {
                      "id": "turn-1",
                      "status": "completed",
                      "itemsView": "full",
                      "privateTurnField": "do-not-forward",
                      "items": [
                        {
                          "id": "item-1",
                          "type": "agentMessage",
                          "text": "bounded answer",
                          "title": "Answer",
                          "content": "visible content",
                          "clientId": "client-1",
                          "summary": "visible summary",
                          "commandActions": [{ "type": "run" }],
                          "arguments": { "safe": true },
                          "privateItemField": "do-not-forward"
                        }
                      ]
                    }
                  ],
                  "nextCursor": "turns-page-2",
                  "privatePageField": "do-not-forward"
                }
                """),
        };

        var response = await ExecuteAsync(
            CreateCapability(client),
            CodexSessionCapability.ThreadTurnsListCommand,
            $$"""{"threadId":"{{ThreadId}}"}""");

        Assert.True(response.Ok, response.Error);
        var payload = PayloadJson(response);
        var item = payload.GetProperty("data")[0].GetProperty("items")[0];
        Assert.Equal("turn-1", payload.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal("item-1", item.GetProperty("id").GetString());
        Assert.Equal("agentMessage", item.GetProperty("type").GetString());
        Assert.Equal("full", payload.GetProperty("data")[0].GetProperty("itemsView").GetString());
        Assert.Equal("visible content", item.GetProperty("content").GetString());
        Assert.Equal("client-1", item.GetProperty("clientId").GetString());
        Assert.Equal("visible summary", item.GetProperty("summary").GetString());
        Assert.Equal("run", item.GetProperty("commandActions")[0].GetProperty("type").GetString());
        Assert.True(item.GetProperty("arguments").GetProperty("safe").GetBoolean());
        Assert.Equal("turns-page-2", payload.GetProperty("nextCursor").GetString());
        Assert.DoesNotContain("privateTurnField", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("privateItemField", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("privatePageField", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex.appServer.threads.list.v1", "{\"unknown\":true}", "unknown Codex session catalog parameter")]
    [InlineData("codex.appServer.threads.list.v1", "{\"limit\":0}", "limit must be an integer from 1 to 100")]
    [InlineData("codex.appServer.threads.list.v1", "{\"limit\":101}", "limit must be an integer from 1 to 100")]
    [InlineData("codex.appServer.thread.turns.list.v1", "{\"threadId\":\"not-a-uuid\"}", "threadId must be a UUID")]
    [InlineData("codex.appServer.thread.turns.list.v1", "{\"threadId\":\"123e4567-e89b-12d3-a456-426614174000\",\"extra\":1}", "unknown Codex session catalog parameter")]
    [InlineData("codex.appServer.thread.turns.list.v1", "{\"threadId\":\"123e4567-e89b-12d3-a456-426614174000\",\"limit\":51}", "limit must be an integer from 1 to 50")]
    public async Task InvalidParameters_AreRejectedBeforeAppServerIo(
        string command,
        string argsJson,
        string expectedError)
    {
        var client = new RecordingCatalogClient();
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(capability, command, argsJson);

        Assert.False(response.Ok);
        Assert.Contains(expectedError, response.Error, StringComparison.Ordinal);
        Assert.Empty(client.Methods);
    }

    [Theory]
    [InlineData("codex.appServer.threads.list.v1", "null", "Codex session catalog parameters must be an object")]
    [InlineData("codex.appServer.threads.list.v1", "[]", "Codex session catalog parameters must be an object")]
    [InlineData("codex.appServer.thread.turns.list.v1", "null", "Codex session read parameters must be an object")]
    [InlineData("codex.appServer.thread.turns.list.v1", "[]", "Codex session read parameters must be an object")]
    public async Task NullAndNonObjectParameters_AreRejectedBeforeAppServerIo(
        string command,
        string argsJson,
        string expectedError)
    {
        var client = new RecordingCatalogClient();
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(capability, command, argsJson);

        Assert.False(response.Ok);
        Assert.Equal(expectedError, response.Error);
        Assert.Empty(client.Methods);
    }

    [Fact]
    public async Task UndefinedParameters_RemainEquivalentToOmittedArguments()
    {
        var client = new RecordingCatalogClient();
        var capability = CreateCapability(client);

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "request-1",
            Command = "codex.appServer.threads.list.v1",
        });

        Assert.True(response.Ok, response.Error);
        Assert.Single(client.Methods);
        Assert.Equal(50, client.Parameters.Single().GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ThreadsList_TitleSearchWalksBoundedPagesWithoutSearchingPrivatePreviewText()
    {
        var client = new RecordingCatalogClient();
        client.EnqueueThreads(Json("""
            {
              "data": [
                {
                  "id": "123e4567-e89b-12d3-a456-426614174001",
                  "name": "Unrelated",
                  "preview": "match only in private preview",
                  "status": { "type": "idle" },
                  "source": "cli"
                }
              ],
              "nextCursor": "opaque-page-2",
              "backwardsCursor": "opaque-page-0"
            }
            """));
        client.EnqueueThreads(Json("""
            {
              "data": [
                {
                  "id": "123e4567-e89b-12d3-a456-426614174002",
                  "name": "MATCH title",
                  "status": { "type": "idle" },
                  "source": "vscode"
                }
              ]
            }
            """));
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.threads.list.v1",
            """{"limit":1,"searchTerm":"match"}""");

        Assert.True(response.Ok, response.Error);
        AssertJsonEqual(
            """
            {
              "sessions": [
                {
                  "threadId": "123e4567-e89b-12d3-a456-426614174002",
                  "status": "idle",
                  "archived": false,
                  "name": "MATCH title",
                  "source": "vscode"
                }
              ],
              "backwardsCursor": "opaque-page-0"
            }
            """,
            PayloadJson(response));
        Assert.Equal(2, client.ThreadsCallCount);
        Assert.Equal("opaque-page-2", client.Parameters[1].GetProperty("cursor").GetString());
        Assert.All(client.Parameters, parameters => Assert.False(parameters.TryGetProperty("searchTerm", out _)));
        Assert.DoesNotContain("private preview", PayloadJson(response).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreadsList_RepeatedSearchCursorFailsClosedWithSanitizedError()
    {
        var client = new RecordingCatalogClient();
        client.EnqueueThreads(Json("""{"data":[],"nextCursor":"cycle"}"""));
        client.EnqueueThreads(Json("""{"data":[],"nextCursor":"cycle"}"""));
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.threads.list.v1",
            """{"limit":10,"searchTerm":"match"}""");

        Assert.False(response.Ok);
        Assert.Equal("Codex app-server catalog is unavailable", response.Error);
        Assert.Equal(2, client.ThreadsCallCount);
    }

    [Fact]
    public async Task ThreadsList_NeverReturnsMoreSessionsThanTheRequestedLimit()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = Json("""
                {
                  "data": [
                    { "id": "123e4567-e89b-12d3-a456-426614174001", "name": "Match one", "status": { "type": "idle" }, "source": "cli" },
                    { "id": "123e4567-e89b-12d3-a456-426614174002", "name": "Match two", "status": { "type": "idle" }, "source": "cli" }
                  ],
                  "nextCursor": "opaque-page-2"
                }
                """),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.threads.list.v1",
            """{"limit":1,"searchTerm":"match"}""");

        Assert.True(response.Ok, response.Error);
        Assert.Single(PayloadJson(response).GetProperty("sessions").EnumerateArray());
        Assert.Equal(
            "123e4567-e89b-12d3-a456-426614174001",
            PayloadJson(response).GetProperty("sessions")[0].GetProperty("threadId").GetString());
        Assert.Equal("opaque-page-2", PayloadJson(response).GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task ThreadsList_DirectPageNeverReturnsMoreSessionsThanTheRequestedLimit()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = Json("""
                {
                  "data": [
                    { "id": "123e4567-e89b-12d3-a456-426614174001", "status": { "type": "idle" }, "source": "cli" },
                    { "id": "123e4567-e89b-12d3-a456-426614174002", "status": { "type": "idle" }, "source": "cli" }
                  ],
                  "nextCursor": "opaque-page-2"
                }
                """),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.threads.list.v1",
            """{"limit":1}""");

        Assert.True(response.Ok, response.Error);
        Assert.Single(PayloadJson(response).GetProperty("sessions").EnumerateArray());
        Assert.Equal("opaque-page-2", PayloadJson(response).GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task ThreadsList_OmitsArchivedAndUnsupportedThreads()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = Json("""
                {
                  "data": [
                    { "id": "123e4567-e89b-12d3-a456-426614174001", "status": { "type": "idle" }, "source": "cli", "archived": true },
                    { "id": "123e4567-e89b-12d3-a456-426614174002", "status": { "type": "idle" }, "source": "exec" },
                    { "id": "123e4567-e89b-12d3-a456-426614174003", "status": { "type": "idle" }, "source": { "custom": "atlas" } },
                    { "id": "123e4567-e89b-12d3-a456-426614174004", "status": { "type": "idle" }, "source": { "custom": "integration" } }
                  ]
                }
                """),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(capability, "codex.appServer.threads.list.v1", "{}");

        Assert.True(response.Ok, response.Error);
        var sessions = PayloadJson(response).GetProperty("sessions");
        var session = Assert.Single(sessions.EnumerateArray());
        Assert.Equal("123e4567-e89b-12d3-a456-426614174003", session.GetProperty("threadId").GetString());
        Assert.Equal("atlas", session.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ThreadTurnsList_RechecksFreshCatalogEligibilityForEveryRead()
    {
        var client = new RecordingCatalogClient { TurnsResponse = Json("""{"data":[]}""") };
        client.EnqueueThreads(EligibleThreadsPage());
        client.EnqueueThreads(Json("""{"data":[]}"""));
        var capability = CreateCapability(client);

        var first = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}"}""");
        var second = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}"}""");

        Assert.True(first.Ok, first.Error);
        Assert.False(second.Ok);
        Assert.Equal("Codex session is not a non-archived interactive Codex session", second.Error);
        Assert.Equal(2, client.ThreadsCallCount);
        Assert.Equal(1, client.TurnsCallCount);
    }

    [Fact]
    public async Task ThreadTurnsList_RejectsEligibilityCursorCyclesBeforeTranscriptIo()
    {
        var client = new RecordingCatalogClient();
        client.EnqueueThreads(Json("""{"data":[],"nextCursor":"cycle"}"""));
        client.EnqueueThreads(Json("""{"data":[],"nextCursor":"cycle"}"""));
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}"}""");

        Assert.False(response.Ok);
        Assert.Equal("Codex session eligibility could not be verified", response.Error);
        Assert.Equal(2, client.ThreadsCallCount);
        Assert.Equal(0, client.TurnsCallCount);
    }

    [Fact]
    public async Task ThreadTurnsList_RejectsEligibilityThatExceedsThePageCap()
    {
        var client = new RecordingCatalogClient();
        for (var page = 1; page <= 100; page++)
            client.EnqueueThreads(Json($$"""{"data":[],"nextCursor":"page-{{page}}"}"""));
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}"}""");

        Assert.False(response.Ok);
        Assert.Equal("Codex session eligibility could not be verified", response.Error);
        Assert.Equal(100, client.ThreadsCallCount);
        Assert.Equal(0, client.TurnsCallCount);
    }

    [Fact]
    public async Task ThreadTurnsList_RejectsOneOversizedTextField()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = EligibleThreadsPage(),
            TurnsResponse = JsonSerializer.SerializeToElement(new
            {
                data = new[]
                {
                    new
                    {
                        id = "turn-1",
                        items = new[]
                        {
                            new
                            {
                                id = "item-1",
                                type = "agentMessage",
                                text = new string('x', 1_000_001),
                            },
                        },
                    },
                },
            }),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}"}""");

        Assert.False(response.Ok);
        Assert.Equal("Codex app-server transcript is unavailable", response.Error);
    }

    [Fact]
    public async Task ThreadTurnsList_RejectsAggregatePayloadOverTheByteLimit()
    {
        var text = new string('x', 1_000_000);
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = EligibleThreadsPage(),
            TurnsResponse = JsonSerializer.SerializeToElement(new
            {
                data = Enumerable.Range(1, 22).Select(index => new
                {
                    id = $"turn-{index}",
                    items = new[] { new { id = $"item-{index}", type = "agentMessage", text } },
                }),
            }),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}","limit":50}""");

        Assert.False(response.Ok);
        Assert.Equal("Codex app-server transcript is unavailable", response.Error);
    }

    [Fact]
    public async Task ThreadsList_SanitizesAndBoundsFallbackMetadata()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsResponse = JsonSerializer.SerializeToElement(new
            {
                data = new object[]
                {
                    new
                    {
                        id = ThreadId,
                        name = (string?)null,
                        preview = "Investigate\n\u001b[31mfailed\u001b[0m\r "
                            + "\u009b32msafely\u009b0m "
                            + "\u009dprivate terminal title\u009c"
                            + "run",
                        cwd = new string('c', 4097),
                        status = new
                        {
                            type = "active",
                            activeFlags = Enumerable.Range(1, 18)
                                .Select(index => $"flag-{index}"),
                        },
                        source = "cli",
                        modelProvider = new string('m', 501),
                    },
                },
            }),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(capability, "codex.appServer.threads.list.v1", "{}");

        Assert.True(response.Ok, response.Error);
        var session = Assert.Single(PayloadJson(response).GetProperty("sessions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("name").ValueKind);
        Assert.Equal("Investigate failed safely run", session.GetProperty("fallbackName").GetString());
        Assert.False(session.TryGetProperty("cwd", out _));
        Assert.Equal(16, session.GetProperty("activeFlags").GetArrayLength());
        Assert.Equal(500, session.GetProperty("modelProvider").GetString()!.Length);
    }

    [Theory]
    [InlineData("cursor", 4096)]
    [InlineData("searchTerm", 500)]
    [InlineData("cwd", 4096)]
    public async Task ThreadsList_RejectsOversizedTextBeforeAppServerIo(string field, int maxLength)
    {
        var client = new RecordingCatalogClient();
        var capability = CreateCapability(client);
        var args = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [field] = new string('x', maxLength + 1),
        });

        var response = await ExecuteAsync(capability, "codex.appServer.threads.list.v1", args);

        Assert.False(response.Ok);
        Assert.Equal($"{field} must be at most {maxLength} characters", response.Error);
        Assert.Empty(client.Methods);
    }

    [Fact]
    public async Task AppServerFailures_ReturnOnlyStableSanitizedErrors()
    {
        var client = new RecordingCatalogClient
        {
            ThreadsException = new InvalidOperationException(
                "private C:\\Users\\operator\\.codex path and transcript text"),
        };
        var capability = CreateCapability(client);

        var response = await ExecuteAsync(capability, "codex.appServer.threads.list.v1", "{}");

        Assert.False(response.Ok);
        Assert.Equal("Codex app-server catalog is unavailable", response.Error);
        Assert.DoesNotContain("operator", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1_200_000, true)]
    [InlineData(20 * 1024 * 1024, true)]
    [InlineData((20 * 1024 * 1024) + 1, false)]
    public async Task RealAdapter_BoundsTranscriptAfterJsonRpcEnvelopeOverhead(
        int rawResultBytes,
        bool expectedSuccess)
    {
        using var harness = new CatalogJsonlProcessHarness(
            BuildTranscriptPage(rawResultBytes));
        await using var client = await CodexAppServerClient.ConnectCatalogAsync(
            new CodexLaunchPlan(Path.Combine(Path.GetTempPath(), "codex.exe")),
            harness,
            CancellationToken.None);
        var capability = new CodexSessionCapability(
            NullLogger.Instance,
            new CodexSessionCatalogService(client));

        var response = await ExecuteAsync(
            capability,
            "codex.appServer.thread.turns.list.v1",
            $$"""{"threadId":"{{ThreadId}}","limit":50}""");

        Assert.Equal(expectedSuccess, response.Ok);
        Assert.Equal(
            expectedSuccess ? null : "Codex app-server transcript is unavailable",
            response.Error);
        Assert.Equal(
            ["initialize", "initialized", "thread/list", "thread/turns/list"],
            harness.RecordedMethods());
        Assert.DoesNotContain("thread/resume", harness.RecordedMethods());
        Assert.DoesNotContain("turn/steer", harness.RecordedMethods());
        Assert.DoesNotContain("turn/interrupt", harness.RecordedMethods());
        await harness.AssertAllProcessesExitedAfterDisposalAsync(client);
    }

    [Fact]
    public void CatalogTransportLimits_AreScopedAndIncludeSerializedJsonRpcFraming()
    {
        var result = Json("""{"data":[{"text":"escaped \\u009b and utf8 ☃"}]}""");
        var framedResponse = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = long.MinValue,
            result,
        });
        var rawResultBytes = Encoding.UTF8.GetByteCount(result.GetRawText());
        var serializedEnvelopeBytes = framedResponse.Length - rawResultBytes;

        Assert.InRange(
            serializedEnvelopeBytes,
            1,
            CodexSessionCatalogService.MaxJsonRpcEnvelopeBytes);
        Assert.Equal(1_048_576, CodexAppServerLimits.Default.MaxLineBytes);
        Assert.Equal(
            CodexSessionCatalogService.MaxTranscriptPageBytes
                + CodexSessionCatalogService.MaxJsonRpcEnvelopeBytes,
            CodexAppServerLimits.Catalog.MaxLineBytes);
        Assert.Equal(
            CodexAppServerLimits.Catalog.MaxLineBytes,
            CodexAppServerLimits.Catalog.MaxResponseBytes);
        Assert.True(
            CodexAppServerLimits.Catalog.MaxOperationBytes
                > CodexAppServerLimits.Catalog.MaxResponseBytes);
    }

    private static CodexSessionCapability CreateCapability(RecordingCatalogClient client) =>
        new(NullLogger.Instance, new CodexSessionCatalogService(client));

    private static Task<NodeInvokeResponse> ExecuteAsync(
        CodexSessionCapability capability,
        string command,
        string argsJson) =>
        capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "request-1",
            Command = command,
            Args = Json(argsJson),
        });

    private static JsonElement PayloadJson(NodeInvokeResponse response) =>
        JsonSerializer.SerializeToElement(response.Payload);

    private static JsonElement EligibleThreadsPage() => Json($$"""
        {
          "data": [
            {
              "id": "{{ThreadId}}",
              "sessionId": "cli-session-1",
              "name": "Remote task",
              "preview": "private preview",
              "cwd": "C:\\workspace\\project",
              "status": { "type": "idle" },
              "source": "cli",
              "modelProvider": "openai",
              "cliVersion": "1.2.3",
              "createdAt": 123,
              "updatedAt": 456,
              "recencyAt": 455,
              "gitInfo": { "branch": "feature/catalog" },
              "turns": []
            }
          ]
        }
        """);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string BuildTranscriptPage(int targetUtf8Bytes)
    {
        var builder = new StringBuilder(targetUtf8Bytes);
        builder.Append("{\"data\":[");
        for (var index = 0; ; index++)
        {
            var prefix = $"{(index == 0 ? "" : ",")}{{\"id\":\"turn-{index}\",\"items\":[{{\"id\":\"item-{index}\",\"type\":\"agentMessage\",\"text\":\"";
            const string itemSuffix = "\"}]}";
            const string pageSuffix = "]}";
            var remaining = targetUtf8Bytes
                - builder.Length
                - prefix.Length
                - itemSuffix.Length
                - pageSuffix.Length;
            var textLength = Math.Min(1_000_000, remaining);
            Assert.True(textLength >= 0, "Target transcript page is too small for its JSON structure.");
            builder.Append(prefix);
            builder.Append('x', textLength);
            builder.Append(itemSuffix);
            if (remaining <= 1_000_000)
                break;
        }
        builder.Append("]}");
        var page = builder.ToString();
        Assert.Equal(targetUtf8Bytes, Encoding.UTF8.GetByteCount(page));
        return page;
    }

    private static void AssertJsonEqual(string expectedJson, JsonElement actual)
    {
        var expected = Json(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expected, actual),
            $"Expected: {expected.GetRawText()}{Environment.NewLine}Actual: {actual.GetRawText()}");
    }

    private sealed class RecordingCatalogClient : ICodexSessionCatalogClient
    {
        private readonly Queue<JsonElement> _threadResponses = new();

        public JsonElement ThreadsResponse { get; set; } = Json("""{"data":[]}""");

        public JsonElement TurnsResponse { get; set; } = Json("""{"data":[]}""");

        public Exception? ThreadsException { get; set; }

        public List<string> Methods { get; } = [];

        public List<JsonElement> Parameters { get; } = [];

        public int ThreadsCallCount { get; private set; }

        public int TurnsCallCount { get; private set; }

        public void EnqueueThreads(JsonElement response) => _threadResponses.Enqueue(response.Clone());

        public Task<JsonElement> ListThreadsAsync(
            JsonElement parameters,
            CancellationToken cancellationToken = default)
        {
            ThreadsCallCount++;
            Methods.Add(CodexAppServerProtocol.ThreadListMethod);
            Parameters.Add(parameters.Clone());
            if (ThreadsException is not null)
                return Task.FromException<JsonElement>(ThreadsException);
            return Task.FromResult(
                (_threadResponses.Count > 0 ? _threadResponses.Dequeue() : ThreadsResponse).Clone());
        }

        public Task<JsonElement> ListThreadTurnsAsync(
            JsonElement parameters,
            CancellationToken cancellationToken = default)
        {
            TurnsCallCount++;
            Methods.Add(CodexAppServerProtocol.ThreadTurnsListMethod);
            Parameters.Add(parameters.Clone());
            return Task.FromResult(TurnsResponse.Clone());
        }
    }

    private sealed class CatalogJsonlProcessHarness : ICodexAppServerProcessFactory, IDisposable
    {
        private const string Script = """
            param([string]$RecordPath, [string]$PayloadPath)
            $ErrorActionPreference = 'Stop'
            function Read-Message {
              $line = [Console]::In.ReadLine()
              if ($null -eq $line) { exit 80 }
              $message = $line | ConvertFrom-Json
              Add-Content -LiteralPath $RecordPath -Value ([string]$message.method) -Encoding utf8
              return $message
            }
            function Write-Message($Value) {
              $json = $Value | ConvertTo-Json -Compress -Depth 10
              [Console]::Out.WriteLine($json)
              [Console]::Out.Flush()
            }

            $initialize = Read-Message
            if ($initialize.method -ne 'initialize') { exit 81 }
            Write-Message @{ id = [long]$initialize.id; result = @{} }
            $initialized = Read-Message
            if ($initialized.method -ne 'initialized') { exit 82 }
            $list = Read-Message
            if ($list.method -ne 'thread/list') { exit 83 }
            Write-Message @{
              id = [long]$list.id
              result = @{
                data = @(@{
                  id = '123e4567-e89b-12d3-a456-426614174000'
                  status = @{ type = 'idle' }
                  source = 'cli'
                })
              }
            }
            $turns = Read-Message
            if ($turns.method -ne 'thread/turns/list') { exit 84 }
            $payload = [IO.File]::ReadAllText($PayloadPath)
            [Console]::Out.Write('{"id":' + [long]$turns.id + ',"result":' + $payload + '}' + "`n")
            [Console]::Out.Flush()
            Start-Sleep -Seconds 30
            """;

        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-codex-catalog-jsonl-{Guid.NewGuid():N}");
        private readonly string _scriptPath;
        private readonly string _recordPath;
        private readonly string _payloadPath;
        private readonly List<int> _processIds = [];

        public CatalogJsonlProcessHarness(string transcriptPage)
        {
            Directory.CreateDirectory(_root);
            _scriptPath = Path.Combine(_root, "fake-catalog-app-server.ps1");
            _recordPath = Path.Combine(_root, "methods.txt");
            _payloadPath = Path.Combine(_root, "transcript.json");
            File.WriteAllText(_scriptPath, Script);
            File.WriteAllText(_payloadPath, transcriptPage, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public ICodexAppServerProcess Start(CodexLaunchPlan launchPlan)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add(_recordPath);
            startInfo.ArgumentList.Add(_payloadPath);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Fake catalog App Server did not start.");
            _processIds.Add(process.Id);
            return new CodexAppServerProcess(process);
        }

        public IReadOnlyList<string> RecordedMethods() =>
            File.Exists(_recordPath)
                ? File.ReadAllLines(_recordPath).Where(value => value.Length > 0).ToArray()
                : [];

        public async Task AssertAllProcessesExitedAfterDisposalAsync(CodexAppServerClient client)
        {
            await client.DisposeAsync();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2) && _processIds.Any(IsProcessRunning))
                await Task.Delay(20);
            Assert.All(_processIds, processId =>
                Assert.False(IsProcessRunning(processId), $"Process {processId} is still running."));
        }

        public void Dispose()
        {
            foreach (var processId in _processIds.Where(IsProcessRunning))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
                catch (ArgumentException)
                {
                }
            }
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
