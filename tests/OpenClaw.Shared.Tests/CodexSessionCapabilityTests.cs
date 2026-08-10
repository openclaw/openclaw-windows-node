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
        AssertJsonEqual(
            $$"""{"threadId":"{{ThreadId}}","cursor":"turns-page-1","limit":25,"sortDirection":"desc","itemsView":"full"}""",
            client.Parameters[1]);
        Assert.DoesNotContain("thread/resume", client.Methods);
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
                        preview = "Investigate\n\u001b[31mfailed\u001b[0m\r run",
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
        Assert.Equal("Investigate failed run", session.GetProperty("fallbackName").GetString());
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
}
