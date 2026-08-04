using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Real WebSocket round-trip proof for the gateway protocol client APIs.
/// Unlike <see cref="GatewayProtocolModelsTests"/> (which exercise
/// the parsers against crafted <see cref="JsonElement"/> payloads), these tests
/// connect the <b>real</b> <see cref="OpenClawGatewayClient"/> over a <b>real</b>
/// loopback WebSocket to a stub gateway, invoke each new typed method, and:
///   1. capture the exact JSON request frame the client serialized onto the wire
///      (proving it matches the upstream openclaw/openclaw schema — param names,
///      method names, and the tri-state sessions.patch null/value encoding), and
///   2. feed back schema-shaped responses and assert the typed DTOs parse.
///
/// This exercises the full method → SerializeRequest → socket send → server
/// receive → response → parse → typed-DTO stack end-to-end. The captured frames
/// are written to test output so they can serve as redacted behavior proof.
/// </summary>
public sealed class GatewayProtocolLiveRoundTripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _identityDir;

    public GatewayProtocolLiveRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        _identityDir = Path.Combine(Path.GetTempPath(), "openclaw-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_identityDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_identityDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NewProtocolMethods_RealWebSocketRoundTrip_SendCorrectWireFramesAndParseResponses()
    {
        using var server = new LoopbackGatewayServer();
        ConfigureResponders(server);

        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            tokenIsBootstrapToken: false, bootstrapPairAsNode: false,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            const string key = "agent:main:main";
            // Generous timeout: the happy path resolves as soon as the response
            // arrives, but a large ceiling prevents load-induced flakiness on
            // shared CI runners (the loopback server can be scheduling-starved
            // when many test classes run in parallel).
            const int rpc = 20000;

            // ── 1. commands.list (typed catalog read) ──
            var catalog = await client.ListCommandsAsync(timeoutMs: rpc);
            Assert.True(catalog.IsSupported);
            var cmd = Assert.Single(catalog.Commands);
            Assert.Equal("model", cmd.Name);
            var arg = Assert.Single(cmd.Args);
            Assert.Equal("gpt-5", Assert.Single(arg.Choices).Value);

            // ── 2. sessions.files.get (read; param must be sessionKey, response nested under "file") ──
            var file = await client.GetSessionFileAsync(key, "src/a.cs", timeoutMs: rpc);
            Assert.True(file.Found);
            Assert.Equal("hello world", file.Content);
            Assert.Contains("\"sessionKey\"", server.FrameFor("sessions.files.get"));

            // ── 3. chat.history (real client transcript export path) ──
            var history = await client.RequestChatHistoryAsync(key, timeoutMs: rpc);
            Assert.Equal("sid-1", history.SessionId);
            var message = Assert.Single(history.Messages);
            Assert.Equal("assistant", message.Role);
            Assert.Equal("done", message.Text);
            Assert.Contains("\"sessionKey\"", server.FrameFor("chat.history"));

            // ── 4. sessions.compaction.list + branch (param key/checkpointId; branch returns sourceKey + new key) ──
            var checkpoints = await client.ListCompactionCheckpointsAsync(key, timeoutMs: rpc);
            Assert.True(checkpoints.IsSupported);
            Assert.Equal("cp1", Assert.Single(checkpoints.Checkpoints).Id);

            var branch = await client.BranchCompactionCheckpointAsync(key, "cp1", timeoutMs: rpc);
            Assert.True(branch.Ok);
            Assert.Equal("agent:main:main", branch.SourceKey);
            Assert.Equal("agent:main:branch-1", branch.ResultSessionKey);
            Assert.Contains("\"checkpointId\"", server.FrameFor("sessions.compaction.branch"));

            // ── 5. sessions.patch SET then CLEAR (the tri-state proof) ──
            // PatchSessionAsync is fire-and-tracked (returns on send, not on
            // response), so wait for the captured frame to arrive on the server.
            var setOk = await client.PatchSessionAsync(key, new SessionPatch { Model = "gpt-5", FastMode = SessionFastMode.Auto });
            Assert.True(setOk);
            var setFrame = await server.WaitFrameAsync("sessions.patch", occurrence: 0, timeoutMs: rpc);
            Assert.Contains("\"model\":\"gpt-5\"", setFrame);
            Assert.Contains("\"fastMode\":\"auto\"", setFrame);

            var clearOk = await client.PatchSessionAsync(key, new SessionPatch { Model = SessionPatch.Clear });
            Assert.True(clearOk);
            var clearFrame = await server.WaitFrameAsync("sessions.patch", occurrence: 1, timeoutMs: rpc);
            Assert.Contains("\"model\":null", clearFrame);

            PrintProof(server);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task CheckHealth_TrackedResponse_PublishesHealthAndPreservesDeepRequest()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("health", _ => new
        {
            uptimeMs = 1234,
            channels = new
            {
                telegram = new { status = "ready", configured = true },
            },
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);
        var channelHealth = new TaskCompletionSource<ChannelHealth[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gatewaySelf = new TaskCompletionSource<GatewaySelfInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChannelHealthUpdated += (_, health) => channelHealth.TrySetResult(health);
        client.GatewaySelfUpdated += (_, self) => gatewaySelf.TrySetResult(self);

        try
        {
            await ConnectAndWaitAsync(client, server);

            await client.CheckHealthAsync();

            var channels = await channelHealth.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var self = await gatewaySelf.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var frame = await server.WaitFrameAsync("health", occurrence: 0, timeoutMs: 20000);
            using var document = JsonDocument.Parse(frame);
            Assert.True(document.RootElement.GetProperty("params").GetProperty("deep").GetBoolean());
            Assert.Equal("telegram", Assert.Single(channels).Name);
            Assert.Equal(1234, self.UptimeMs);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task CronRunDetailed_FallsBackToLegacyIdPayload_WhenJobIdPayloadIsRejected()
    {
        using var server = new LoopbackGatewayServer();
        var requestCount = 0;
        var observedParameters = new ConcurrentQueue<JsonElement>();
        server.OnMethod("cron.run", parameters =>
        {
            requestCount++;
            observedParameters.Enqueue(parameters.Clone());
            if (requestCount == 1)
                return LoopbackResponse.Fail("invalid cron.run params: unexpected property jobId");

            return new { ok = true, enqueued = true, runId = "manual:job-legacy:1" };
        });

        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            tokenIsBootstrapToken: false, bootstrapPairAsNode: false,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            var result = await client.RunCronJobDetailedAsync("job-legacy", timeoutMs: 20000);

            Assert.True(result.Accepted);
            Assert.True(result.Enqueued);
            Assert.Equal("manual:job-legacy:1", result.RunId);
            Assert.Equal(2, requestCount);
            await server.WaitFrameAsync("cron.run", occurrence: 1, timeoutMs: 20000);

            var payloads = observedParameters.ToArray();
            Assert.Equal(2, payloads.Length);
            Assert.True(payloads[0].TryGetProperty("jobId", out var jobId));
            Assert.Equal("job-legacy", jobId.GetString());
            Assert.False(payloads[0].TryGetProperty("id", out _));
            Assert.True(payloads[1].TryGetProperty("id", out var id));
            Assert.Equal("job-legacy", id.GetString());
            Assert.True(payloads[1].TryGetProperty("force", out var force));
            Assert.True(force.GetBoolean());
            Assert.False(payloads[1].TryGetProperty("jobId", out _));
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ChatSend_UsesCallerProvidedIdempotencyKeyOnWire()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("chat.send", parameters =>
        {
            Assert.Equal("agent:main:main", parameters.GetProperty("sessionKey").GetString());
            Assert.Equal("Hello", parameters.GetProperty("message").GetString());
            Assert.Equal("send-run-123", parameters.GetProperty("idempotencyKey").GetString());
            return new { runId = "send-run-123", sessionKey = "agent:main:main", status = "started" };
        });

        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            tokenIsBootstrapToken: false, bootstrapPairAsNode: false,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            var result = await client.SendChatMessageForRunAsync(
                "Hello",
                sessionKey: "agent:main:main",
                idempotencyKey: "send-run-123");

            Assert.Equal("send-run-123", result.RunId);
            Assert.Equal("started", result.Status);
            var frame = await server.WaitFrameAsync("chat.send", occurrence: 0, timeoutMs: 20000);
            Assert.Contains("\"idempotencyKey\":\"send-run-123\"", frame);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task SessionsList_RealWebSocket_PagesOneThousandAndSearchesRowNineHundred()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("sessions.list", parameters =>
        {
            var offset = parameters.GetProperty("offset").GetInt32();
            var limit = parameters.GetProperty("limit").GetInt32();
            var search = parameters.TryGetProperty("search", out var searchProperty)
                ? searchProperty.GetString()
                : null;
            if (!string.IsNullOrEmpty(search))
            {
                return new
                {
                    sessions = new[] { new { key = "agent:main:900", label = "Session 900" } },
                    count = 1,
                    totalCount = 1,
                    limitApplied = limit,
                    offset,
                    nextOffset = (int?)null,
                    hasMore = false,
                };
            }

            var rows = Enumerable.Range(offset, Math.Min(limit, 1000 - offset))
                .Select(index => new { key = $"agent:main:{index}", label = $"Session {index}" })
                .ToArray();
            var hasMore = offset + rows.Length < 1000;
            return new
            {
                sessions = rows,
                count = rows.Length,
                totalCount = 1000,
                limitApplied = limit,
                offset,
                nextOffset = hasMore ? offset + rows.Length : (int?)null,
                hasMore,
            };
        });
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            var recent = await client.QuerySessionsAsync(new SessionQuery { IncludeBackground = true });
            var search = await client.QuerySessionsAsync(new SessionQuery
            {
                Search = "Session 900",
                IncludeBackground = true,
            });

            Assert.Equal(1000, recent.Sessions.Count);
            Assert.Equal(10, recent.PagesRead);
            Assert.Equal("agent:main:900", Assert.Single(search.Sessions).Key);
            using var firstFrame = JsonDocument.Parse(server.FrameFor("sessions.list", 0));
            var parameters = firstFrame.RootElement.GetProperty("params");
            Assert.Equal(["limit", "offset"], parameters.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(100, parameters.GetProperty("limit").GetInt32());
            Assert.Equal(0, parameters.GetProperty("offset").GetInt32());
            Assert.DoesNotContain("agent:main:900", logger.Logs);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Bootstrap_SubscribesBeforeSlowPaging_AndChangedEventSupersedesStaleSnapshot()
    {
        using var server = new LoopbackGatewayServer
        {
            SendChallengeOnConnect = true,
            ProcessRequestsConcurrently = true,
        };
        var firstPageStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listCalls = 0;
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok",
            protocol = 3,
            server = new { version = "test" },
        });
        server.OnMethod("health", _ => new { ok = true });
        server.OnMethodAsync("sessions.list", async parameters =>
        {
            var call = Interlocked.Increment(ref listCalls);
            var offset = parameters.GetProperty("offset").GetInt32();
            if (call == 1)
            {
                firstPageStarted.TrySetResult();
                await releaseFirstPage.Task;
                return SessionPage(
                    Enumerable.Range(0, 100)
                        .Select(index => new { key = $"agent:main:{index}", label = "Stale" })
                        .ToArray(),
                    offset: 0,
                    nextOffset: 100,
                    hasMore: true);
            }

            if (offset == 0)
            {
                return SessionPage(
                    Enumerable.Range(0, 100)
                        .Select(index => new
                        {
                            key = $"agent:main:{index}",
                            label = index == 0 ? "Changed during paging" : $"Session {index}",
                        })
                        .ToArray(),
                    offset: 0,
                    nextOffset: 100,
                    hasMore: true);
            }

            return SessionPage(
                Enumerable.Range(offset, 100)
                    .Select(index => new
                    {
                        key = $"agent:main:{index}",
                        label = $"Session {index}",
                    })
                    .ToArray(),
                offset,
                nextOffset: offset < 900 ? offset + 100 : null,
                hasMore: offset < 900);
        });
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            identityPath: _identityDir);
        var publications = new ConcurrentQueue<SessionInfo[]>();
        var changedPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) =>
        {
            publications.Enqueue(sessions);
            if (sessions.Any(session => session.Label == "Changed during paging"))
                changedPublished.TrySetResult();
        };

        try
        {
            await ConnectAndWaitAsync(client, server);
            await firstPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(
                () =>
                {
                    var startupMethods = server.AllMethods.ToArray();
                    return startupMethods.Contains("usage.status")
                           && startupMethods.Contains("node.list")
                           && startupMethods.Contains("agents.list");
                },
                TimeSpan.FromSeconds(20));

            var methods = server.AllMethods.ToArray();
            var healthIndex = Array.IndexOf(methods, "health");
            var subscribeIndex = Array.IndexOf(methods, "sessions.subscribe");
            var listIndex = Array.IndexOf(methods, "sessions.list");
            Assert.True(healthIndex >= 0, "health was not sent.");
            Assert.True(subscribeIndex >= 0, "sessions.subscribe was not sent.");
            Assert.True(listIndex >= 0, "sessions.list was not sent.");
            Assert.True(healthIndex < subscribeIndex, "health must precede sessions.subscribe.");
            Assert.True(subscribeIndex < listIndex, "sessions.subscribe must precede sessions.list.");
            Assert.Contains("usage.status", methods);
            Assert.Contains("node.list", methods);
            Assert.Contains("agents.list", methods);

            await server.SendEventAsync("sessions.changed", new { });
            await WaitUntilAsync(
                () => logger.Logs.Any(log =>
                    log.Contains("sessions.changed received", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20));
            releaseFirstPage.TrySetResult();
            await changedPublished.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var publication = Assert.Single(publications);
            Assert.Equal(1000, publication.Length);
            Assert.Equal(
                "Changed during paging",
                Assert.Single(publication, session => session.Key == "agent:main:0").Label);
            Assert.Equal(11, listCalls);
        }
        finally
        {
            releaseFirstPage.TrySetResult();
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Bootstrap_DisconnectCancelsSlowPagingWithoutLatePublication()
    {
        using var server = new LoopbackGatewayServer
        {
            SendChallengeOnConnect = true,
            ProcessRequestsConcurrently = true,
        };
        var firstPageStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listCalls = 0;
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok",
            protocol = 3,
            server = new { version = "test" },
        });
        server.OnMethod("health", _ => new { ok = true });
        server.OnMethodAsync("sessions.list", async _ =>
        {
            Interlocked.Increment(ref listCalls);
            firstPageStarted.TrySetResult();
            await releaseFirstPage.Task;
            return SessionPage(
                Enumerable.Range(0, 100)
                    .Select(index => new { key = $"agent:main:{index}", label = "Late stale row" })
                    .ToArray(),
                offset: 0,
                nextOffset: 100,
                hasMore: true);
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);
        var publications = new ConcurrentQueue<SessionInfo[]>();
        client.SessionsUpdated += (_, sessions) => publications.Enqueue(sessions);

        try
        {
            await ConnectAndWaitAsync(client, server);
            await firstPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("sessions.subscribe", server.AllMethods);
            await WaitUntilAsync(
                () =>
                {
                    var startupMethods = server.AllMethods.ToArray();
                    return startupMethods.Contains("usage.status")
                           && startupMethods.Contains("node.list")
                           && startupMethods.Contains("agents.list");
                },
                TimeSpan.FromSeconds(20));

            var disconnect = client.DisconnectAsync();
            Assert.False(client.HasHandshakeSnapshot);
            releaseFirstPage.TrySetResult();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(100);

            Assert.Empty(publications);
            Assert.Equal(1, listCalls);
            var methods = server.AllMethods.ToArray();
            Assert.Contains("usage.status", methods);
            Assert.Contains("node.list", methods);
            Assert.Contains("agents.list", methods);
        }
        finally
        {
            releaseFirstPage.TrySetResult();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Bootstrap_SlowTwentyPageCatchUp_DoesNotBlockUnrelatedStartupReads()
    {
        using var server = new LoopbackGatewayServer
        {
            SendChallengeOnConnect = true,
            ProcessRequestsConcurrently = true,
        };
        var secondPageStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondPage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listCalls = 0;
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok",
            protocol = 3,
            server = new { version = "test" },
        });
        server.OnMethod("health", _ => new { ok = true });
        server.OnMethodAsync("sessions.list", async parameters =>
        {
            Interlocked.Increment(ref listCalls);
            var offset = parameters.GetProperty("offset").GetInt32();
            if (offset == 100)
            {
                secondPageStarted.TrySetResult();
                await releaseSecondPage.Task;
            }
            return SessionPage(
                Enumerable.Range(offset, 100)
                    .Select(index => new { key = $"agent:main:{index}", label = $"Session {index}" })
                    .ToArray(),
                offset,
                nextOffset: offset < 1900 ? offset + 100 : null,
                hasMore: offset < 1900);
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);
        var published = new TaskCompletionSource<SessionInfo[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) =>
        {
            if (sessions.Length == SessionQueryCoordinator.MaximumMaterializedSessions)
                published.TrySetResult(sessions);
        };

        try
        {
            await ConnectAndWaitAsync(client, server);
            await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(
                () =>
                {
                    var startupMethods = server.AllMethods.ToArray();
                    return startupMethods.Contains("usage.status")
                           && startupMethods.Contains("node.list")
                           && startupMethods.Contains("agents.list");
                },
                TimeSpan.FromSeconds(20));

            var methods = server.AllMethods.ToArray();
            Assert.True(
                Array.IndexOf(methods, "health") < Array.IndexOf(methods, "sessions.subscribe"),
                "health must precede sessions.subscribe.");
            Assert.True(
                Array.IndexOf(methods, "sessions.subscribe") < Array.IndexOf(methods, "sessions.list"),
                "sessions.subscribe must precede sessions.list.");
            Assert.DoesNotContain("models.list", methods);
            Assert.Equal(2, listCalls);

            releaseSecondPage.TrySetResult();
            Assert.Equal(
                SessionQueryCoordinator.MaximumMaterializedSessions,
                (await published.Task.WaitAsync(TimeSpan.FromSeconds(20))).Length);
            Assert.Equal(SessionQueryCoordinator.MaximumPages, listCalls);
        }
        finally
        {
            releaseSecondPage.TrySetResult();
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Bootstrap_SessionPagingFailure_DoesNotBlockUnrelatedStartupReads()
    {
        using var server = new LoopbackGatewayServer { SendChallengeOnConnect = true };
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok",
            protocol = 3,
            server = new { version = "test" },
        });
        server.OnMethod("health", _ => new { ok = true });
        server.OnMethod(
            "sessions.list",
            _ => LoopbackResponse.Fail(new { code = "SERVER_ERROR", message = "paging failed" }));
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", logger,
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);
            await WaitUntilAsync(
                () =>
                {
                    var methods = server.AllMethods.ToArray();
                    return methods.Contains("usage.status")
                           && methods.Contains("node.list")
                           && methods.Contains("agents.list")
                           && logger.Logs.Any(log =>
                               log.Contains("sessions.list failed: paging failed", StringComparison.Ordinal));
                },
                TimeSpan.FromSeconds(20));

            var methods = server.AllMethods.ToArray();
            Assert.True(
                Array.IndexOf(methods, "sessions.subscribe") < Array.IndexOf(methods, "sessions.list"),
                "sessions.subscribe must precede sessions.list.");
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task SessionsList_InvalidExpandedProperty_FallsBackExactlyOnceAndRemembersLegacy()
    {
        using var server = new LoopbackGatewayServer();
        var calls = 0;
        server.OnMethod("sessions.list", parameters =>
        {
            calls++;
            if (calls == 1)
            {
                return LoopbackResponse.Fail(new
                {
                    code = "INVALID_REQUEST",
                    message = "invalid sessions.list params: unexpected property limit",
                });
            }
            return new
            {
                sessions = Enumerable.Range(0, 25)
                    .Select(index => new { key = $"agent:main:{index}" })
                    .ToArray(),
            };
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);
            var first = await client.ListSessionsPageAsync(new SessionListRequest
            {
                AgentId = "main",
                Limit = 100,
                Offset = 0,
            });
            var second = await client.ListSessionsPageAsync(new SessionListRequest
            {
                AgentId = "main",
                Limit = 100,
                Offset = 100,
            });

            Assert.True(first.IsLegacyResponse);
            Assert.True(second.IsLegacyResponse);
            Assert.Equal(3, calls);
            using var expandedFrame = JsonDocument.Parse(server.FrameFor("sessions.list", 0));
            Assert.Equal(
                ["agentId", "limit", "offset"],
                expandedFrame.RootElement.GetProperty("params").EnumerateObject().Select(p => p.Name).ToArray());
            foreach (var occurrence in new[] { 1, 2 })
            {
                using var legacyFrame = JsonDocument.Parse(server.FrameFor("sessions.list", occurrence));
                var legacyParameters = legacyFrame.RootElement.GetProperty("params");
                Assert.Equal(["agentId"], legacyParameters.EnumerateObject().Select(p => p.Name).ToArray());
            }
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task SessionsList_ConnectionGenerationAdvance_RejectsLateFallbackResponse()
    {
        using var server = new LoopbackGatewayServer();
        var firstRequestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        server.OnMethodAsync("sessions.list", async parameters =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstRequestReceived.TrySetResult();
                await releaseFirstResponse.Task;
                return LoopbackResponse.Fail(new
                {
                    code = "INVALID_REQUEST",
                    message = "unexpected property limit",
                });
            }

            Assert.True(parameters.TryGetProperty("limit", out _));
            Assert.True(parameters.TryGetProperty("offset", out _));
            return new { sessions = Array.Empty<object>() };
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);
            var stale = client.ListSessionsPageAsync(
                new SessionListRequest { Limit = 100, Offset = 0 },
                timeoutMs: 20000);
            await firstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var resetCapability = typeof(OpenClawGatewayClient).GetMethod(
                "ResetSessionListCapability",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            resetCapability!.Invoke(client, null);
            releaseFirstResponse.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            var current = await client.ListSessionsPageAsync(
                new SessionListRequest { Limit = 100, Offset = 0 },
                timeoutMs: 20000);

            Assert.False(current.IsLegacyResponse);
            Assert.Equal(2, calls);
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
            await client.DisconnectAsync();
        }
    }

    [Theory]
    [InlineData("AUTH_REQUIRED", "unexpected property limit")]
    [InlineData("SERVER_ERROR", "unexpected property limit")]
    [InlineData("INVALID_REQUEST", "validation failed")]
    [InlineData("INVALID_REQUEST", "unknown parameter archived")]
    [InlineData("INVALID_REQUEST", "unknown parameter value for search")]
    public async Task SessionsList_DoesNotBroadlyFallback(string code, string message)
    {
        using var server = new LoopbackGatewayServer();
        var calls = 0;
        server.OnMethod("sessions.list", _ =>
        {
            calls++;
            return LoopbackResponse.Fail(new { code, message });
        });
        var client = new OpenClawGatewayClient(
            server.WebSocketUrl, "test-token", new TestLogger(),
            identityPath: _identityDir);

        try
        {
            await ConnectAndWaitAsync(client, server);

            var error = await Assert.ThrowsAsync<GatewayRequestException>(() =>
                client.ListSessionsPageAsync(new SessionListRequest { Limit = 100, Offset = 0 }));

            Assert.Equal(code, error.Code);
            Assert.Equal(1, calls);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    private static void ConfigureResponders(LoopbackGatewayServer server)
    {
        // NOTE: intentionally NO hello-ok responder. The new methods only require
        // an open socket (IsConnectedToGateway), not the full handshake. Skipping
        // hello-ok avoids the client's post-handshake auto-request storm
        // (health/sessions.list/subscribe/usage/nodes/agents), keeping this test
        // lightweight so it does not add scheduler/socket contention that could
        // destabilize other timing-sensitive socket tests under parallel load.

        server.OnMethod("commands.list", _ => new
        {
            commands = new object[]
            {
                new
                {
                    name = "model",
                    description = "Change the active model",
                    source = "native",
                    scope = "both",
                    acceptsArgs = true,
                    args = new object[]
                    {
                        new
                        {
                            name = "id",
                            description = "Model id",
                            type = "string",
                            choices = new object[] { new { value = "gpt-5", label = "GPT-5" } }
                        }
                    }
                }
            }
        });

        server.OnMethod("sessions.files.get", _ => new
        {
            sessionKey = "agent:main:main",
            root = "/work/repo",
            file = new
            {
                path = "src/a.cs",
                name = "a.cs",
                kind = "modified",
                missing = false,
                size = 11,
                updatedAtMs = 1700000000000L,
                content = "hello world"
            }
        });

        server.OnMethod("chat.history", _ => new
        {
            sessionId = "sid-1",
            messages = new object[]
            {
                new
                {
                    role = "assistant",
                    content = "done",
                    timestamp = 1700000000001L
                }
            }
        });

        server.OnMethod("sessions.compaction.list", _ => new
        {
            ok = true,
            key = "agent:main:main",
            checkpoints = new object[]
            {
                new { checkpointId = "cp1", sessionKey = "agent:main:main", sessionId = "sid-1", createdAt = 1700000000000L, reason = "manual" }
            }
        });

        server.OnMethod("sessions.compaction.branch", _ => new
        {
            ok = true,
            sourceKey = "agent:main:main",
            key = "agent:main:branch-1",
            sessionId = "sid-branch",
            checkpoint = new { checkpointId = "cp1" }
        });

        // sessions.patch is a tracked (non-wizard) request; an ok response with a
        // key payload completes it.
        server.OnMethod("sessions.patch", _ => new { key = "agent:main:main" });
    }

    private static async Task ConnectAndWaitAsync(OpenClawGatewayClient client, LoopbackGatewayServer server)
    {
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? _, ConnectionStatus s)
        {
            if (s == ConnectionStatus.Connected) connected.TrySetResult(true);
        }
        client.StatusChanged += OnStatus;
        try
        {
            await client.ConnectAsync();
            // The new methods only require an open socket (IsConnectedToGateway),
            // not the full hello-ok handshake. Poll readiness with a generous
            // ceiling so a load-starved runner doesn't cause a false failure;
            // the loop exits as soon as the socket is open. The Connected event
            // (hello-ok) is a fast-path signal but not required.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!client.IsConnectedToGateway && DateTime.UtcNow < deadline)
            {
                var completed = await Task.WhenAny(connected.Task, Task.Delay(100));
                if (completed == connected.Task) break;
            }
            // Give the socket a final moment to flip to Open if the event fired.
            for (var i = 0; i < 100 && !client.IsConnectedToGateway; i++)
                await Task.Delay(50);
            Assert.True(client.IsConnectedToGateway, "client did not reach connected state within timeout");
        }
        finally
        {
            client.StatusChanged -= OnStatus;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition(), "Condition was not satisfied before timeout.");
    }

    private static object SessionPage<T>(
        T[] sessions,
        int offset,
        int? nextOffset,
        bool hasMore) => new
    {
        sessions,
        count = sessions.Length,
        totalCount = 101,
        limitApplied = 100,
        offset,
        nextOffset,
        hasMore,
    };

    private void PrintProof(LoopbackGatewayServer server)
    {
        _output.WriteLine("===== Gateway protocol live round-trip: captured request frames =====");
        foreach (var frame in server.AllFrames)
        {
            _output.WriteLine(frame);
            Console.WriteLine("[gateway-rx] " + frame);
        }
        _output.WriteLine("====================================================================");
    }

    /// <summary>
    /// Minimal real loopback WebSocket "gateway": accepts the client connection,
    /// records every request frame, and replies with a per-method payload. Uses
    /// the same HttpListener-on-127.0.0.1 pattern as the repo's other loopback
    /// test servers (no admin/urlacl needed for an explicit loopback prefix).
    /// </summary>
    private sealed class LoopbackGatewayServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Dictionary<string, Func<JsonElement, Task<object>>> _responders = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Method, string Frame)> _frames = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private WebSocket? _activeSocket;

        public int Port { get; }
        public string WebSocketUrl => $"ws://127.0.0.1:{Port}/";
        public bool SendChallengeOnConnect { get; init; }
        public bool ProcessRequestsConcurrently { get; init; }

        public LoopbackGatewayServer()
        {
            // FindFreePort + HttpListener.Start has a TOCTOU race: another process
            // can grab the port between probe and bind, especially when many test
            // classes run in parallel. Retry on a fresh port a few times.
            Exception? last = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = FindFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = candidate;
                    _loop = Task.Run(AcceptLoopAsync);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { listener.Close(); } catch { /* ignore */ }
                }
            }
            throw new InvalidOperationException("Could not bind a loopback HttpListener after multiple attempts", last);
        }

        public void OnMethod(string method, Func<JsonElement, object> responder) =>
            _responders[method] = parameters => Task.FromResult(responder(parameters));

        public void OnMethodAsync(string method, Func<JsonElement, Task<object>> responder) =>
            _responders[method] = responder;

        public IEnumerable<string> AllFrames
        {
            get
            {
                foreach (var f in _frames) yield return f.Frame;
            }
        }

        public IEnumerable<string> AllMethods => _frames.Select(frame => frame.Method);

        public async Task SendEventAsync(string eventName, object payload)
        {
            var socket = Volatile.Read(ref _activeSocket);
            if (socket is null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("No active gateway socket.");
            await SendTextAsync(socket, JsonSerializer.Serialize(new
            {
                type = "event",
                @event = eventName,
                payload,
            }));
        }

        /// <summary>Returns the captured request frame for the Nth occurrence of a method.</summary>
        public string FrameFor(string method, int occurrence = 0)
        {
            if (TryGetFrame(method, occurrence, out var frame))
                return frame;
            throw new InvalidOperationException($"No captured frame for method '{method}' occurrence {occurrence}");
        }

        /// <summary>Waits for the Nth occurrence of a method's request frame to arrive.</summary>
        public async Task<string> WaitFrameAsync(string method, int occurrence, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (TryGetFrame(method, occurrence, out var frame))
                    return frame;
                await Task.Delay(25);
            }
            throw new InvalidOperationException($"Timed out waiting for method '{method}' occurrence {occurrence}");
        }

        private bool TryGetFrame(string method, int occurrence, out string frame)
        {
            var seen = 0;
            foreach (var f in _frames)
            {
                if (f.Method != method) continue;
                if (seen == occurrence) { frame = f.Frame; return true; }
                seen++;
            }
            frame = "";
            return false;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }

                _ = Task.Run(() => ServeAsync(ctx));
            }
        }

        private async Task ServeAsync(HttpListenerContext ctx)
        {
            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
            catch { return; }

            var socket = wsCtx.WebSocket;
            Volatile.Write(ref _activeSocket, socket);
            var buffer = new byte[16 * 1024];
            var sb = new StringBuilder();

            try
            {
                if (SendChallengeOnConnect)
                {
                    await SendTextAsync(socket, JsonSerializer.Serialize(new
                    {
                        type = "event",
                        @event = "connect.challenge",
                        payload = new
                        {
                            nonce = "loopback-challenge",
                            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        },
                    }));
                }

                while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var frame = sb.ToString();
                    if (ProcessRequestsConcurrently)
                        _ = HandleFrameSafelyAsync(socket, frame);
                    else
                        await HandleFrameAsync(socket, frame);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch { /* client closed */ }
            finally
            {
                Interlocked.CompareExchange(ref _activeSocket, null, socket);
            }
        }

        private async Task HandleFrameSafelyAsync(WebSocket socket, string frame)
        {
            try { await HandleFrameAsync(socket, frame); }
            catch { /* client closed or test shutting down */ }
        }

        private async Task HandleFrameAsync(WebSocket socket, string frame)
        {
            string? id = null, method = null;
            JsonElement parameters = default;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() != "req") return;
                id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
                if (root.TryGetProperty("params", out var p)) parameters = p.Clone();
            }
            catch { return; }

            if (method is null || id is null) return;

            _frames.Enqueue((method, frame));

            object payload = _responders.TryGetValue(method, out var responder)
                ? await responder(parameters)
                : new { };

            var response = payload is LoopbackResponse loopbackResponse
                ? loopbackResponse.Ok
                    ? JsonSerializer.Serialize(new { type = "res", id, ok = true, payload = loopbackResponse.Payload })
                    : JsonSerializer.Serialize(new { type = "res", id, ok = false, error = loopbackResponse.Error })
                : JsonSerializer.Serialize(new { type = "res", id, ok = true, payload });
            await SendTextAsync(socket, response);
        }

        private async Task SendTextAsync(WebSocket socket, string text)
        {
            await _sendGate.WaitAsync(_cts.Token);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private static int FindFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _sendGate.Dispose();
            _cts.Dispose();
        }
    }

    private sealed record LoopbackResponse(bool Ok, object? Payload = null, object? Error = null)
    {
        public static LoopbackResponse Fail(object error) => new(false, Error: error);
    }
}
