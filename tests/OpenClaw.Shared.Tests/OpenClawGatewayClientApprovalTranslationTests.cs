using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Exercises the translation of top-level exec.approval.{requested,resolved}
/// envelopes into AgentEventInfo(Stream="approval") instances so the chat
/// data provider's existing approval banner code path (PR #567) lights up.
/// Without this translation the gateway HTML dashboard sees approvals but
/// the native chat silently drops them.
/// </summary>
public class OpenClawGatewayClientApprovalTranslationTests
{
    private static OpenClawGatewayClient NewClient() =>
        new("ws://localhost:18789", "test-token", new TestLogger());

    private static void InvokeHandleEvent(OpenClawGatewayClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleEventForConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { doc.RootElement, json.Length, 0L });
    }

    [Fact]
    public void ExecApprovalRequested_TopLevelEvent_IsTranslatedToApprovalAgentEvent()
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;

        // Real wire shape captured from a live gateway: a top-level
        // exec.approval.requested envelope carrying an opaque ``id`` and a
        // nested ``request`` object with command/host/sessionKey/agentId.
        const string json = """
            {
              "type": "event",
              "event": "exec.approval.requested",
              "payload": {
                "id": "4d6a4c38-5226-4ffe-a0e1-fb4acff1181d",
                "request": {
                  "command": "openclaw nodes invoke --node \"Windows Node\" --command system.run",
                  "host": "gateway",
                  "sessionKey": "agent:main:main",
                  "agentId": "main",
                  "security": "allowlist",
                  "ask": "always"
                },
                "createdAtMs": 1780691492368,
                "expiresAtMs": 1780693292368
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.NotNull(observed);
        Assert.Equal("approval", observed!.Stream);
        Assert.Equal("agent:main:main", observed.SessionKey);
        Assert.Equal("requested", observed.Data.GetProperty("phase").GetString());
        Assert.Equal("4d6a4c38-5226-4ffe-a0e1-fb4acff1181d", observed.Data.GetProperty("approvalId").GetString());
        Assert.Equal("gateway", observed.Data.GetProperty("host").GetString());
        Assert.Contains("system.run", observed.Data.GetProperty("command").GetString());
        Assert.Equal("main", observed.Data.GetProperty("agentId").GetString());
        // ask=always offers Allow once and Deny; this client never completed a handshake, so it has no grant.
        Assert.Equal(["allow-once", "deny"], observed.Data.GetProperty("allowedDecisions").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("missing", observed.Data.GetProperty("decisionAccess").GetString());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("cwd")]
    [InlineData("warningText")]
    public void ExecApprovalWithAdditionalConsent_TranslatesToDenyOnlyChat(string field)
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;
        var request = new System.Collections.Generic.Dictionary<string, object>
        { ["command"] = "echo preview", ["sessionKey"] = "agent:main:main" };
        request[field] = field == "scope"
            ? (object)new { kind = "standing-grant", automation = "nightly", command = "echo actual" }
            : "Additional reviewer context";
        InvokeHandleEvent(client, JsonSerializer.Serialize(new
        {
            type = "event", @event = "exec.approval.requested",
            payload = new { id = "scoped", createdAtMs = 1, expiresAtMs = 2, request },
        }));
        Assert.NotNull(observed);
        Assert.True(observed.Data.GetProperty("requiresInboxReview").GetBoolean());
        Assert.Equal(["deny"], observed.Data.GetProperty("allowedDecisions").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void ExecApprovalResolved_DenyDecision_MapsToDeniedPhase()
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;

        const string json = """
            {
              "type": "event",
              "event": "exec.approval.resolved",
              "payload": {
                "id": "4d6a4c38-5226-4ffe-a0e1-fb4acff1181d",
                "decision": "deny",
                "resolvedBy": "openclaw-control-ui",
                "ts": 1780691511789,
                "request": {
                  "command": "openclaw nodes invoke",
                  "host": "gateway",
                  "sessionKey": "agent:main:main",
                  "agentId": "main"
                }
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.NotNull(observed);
        Assert.Equal("approval", observed!.Stream);
        Assert.Equal("denied", observed.Data.GetProperty("phase").GetString());
        Assert.Equal("4d6a4c38-5226-4ffe-a0e1-fb4acff1181d", observed.Data.GetProperty("approvalId").GetString());
        Assert.Equal("deny", observed.Data.GetProperty("decision").GetString());
        Assert.Equal("agent:main:main", observed.SessionKey);
    }

    [Fact]
    public void ExecApprovalResolved_AllowOnceDecision_MapsToResolvedTerminalPhase()
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;

        const string json = """
            {
              "type": "event",
              "event": "exec.approval.resolved",
              "payload": {
                "id": "appr-1",
                "decision": "allow-once",
                "request": { "sessionKey": "agent:main:main" }
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.NotNull(observed);
        // Must be ``resolved`` (terminal in OpenClawChatDataProvider.IsTerminalApprovalPhase)
        // so the native banner clears when ALLOWED is reported from another
        // client (HTML dashboard, plugin auto-approve). ``allowed`` is NOT
        // terminal in the provider whitelist and would leak the banner.
        Assert.Equal("resolved", observed!.Data.GetProperty("phase").GetString());
        Assert.Equal("allow-once", observed.Data.GetProperty("decision").GetString());
    }

    [Fact]
    public void ExecApprovalResolved_AllowAlwaysDecision_MapsToResolvedTerminalPhase()
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;

        const string json = """
            {
              "type": "event",
              "event": "exec.approval.resolved",
              "payload": {
                "id": "appr-2",
                "decision": "allow-always",
                "request": { "sessionKey": "agent:main:main" }
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.NotNull(observed);
        Assert.Equal("resolved", observed!.Data.GetProperty("phase").GetString());
        Assert.Equal("allow-always", observed.Data.GetProperty("decision").GetString());
    }

    [Fact]
    public void ExecApprovalResolved_UnknownDecision_FallsThroughToResolvedTerminalPhase()
    {
        var client = NewClient();
        AgentEventInfo? observed = null;
        client.AgentEventReceived += (_, evt) => observed = evt;

        // A future/typoed decision string must not silently mis-classify as
        // ``allowed`` (the old ``StartsWith("allow")`` heuristic would have
        // matched ``allowlist-blocked``). The catch-all here is
        // intentionally terminal so the banner clears rather than leaking,
        // and the gateway client logs a warning.
        const string json = """
            {
              "type": "event",
              "event": "exec.approval.resolved",
              "payload": {
                "id": "appr-3",
                "decision": "allowlist-blocked",
                "request": { "sessionKey": "agent:main:main" }
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.NotNull(observed);
        Assert.Equal("resolved", observed!.Data.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task ResolveExecApprovalAsync_RejectsUnknownDecision()
    {
        var client = NewClient();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ResolveExecApprovalAsync("appr-1", "allow"));
        Assert.Contains("allow-once", ex.Message);
    }

    [Fact]
    public async Task ResolveExecApprovalAsync_ThrowsWhenDisconnected()
    {
        var client = NewClient();
        // No Connect() called, so IsConnected is false. The chat provider
        // relies on this throw to preserve the Allow/Deny banner for
        // retry; without it, ``SendTrackedRequestAsync`` would silently
        // no-op and the UI would dismiss the banner thinking the resolve
        // succeeded.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ResolveExecApprovalAsync("appr-1", "allow-once"));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public void ExecApproval_MissingPayload_DoesNotThrowAndDoesNotEmit()
    {
        var client = NewClient();
        var fired = false;
        client.AgentEventReceived += (_, _) => fired = true;

        const string json = """
            { "type": "event", "event": "exec.approval.requested" }
            """;

        InvokeHandleEvent(client, json);
        Assert.False(fired);
    }

    [Fact]
    public void ExecApproval_FirstSubscriberThrows_SecondSubscriberStillInvoked()
    {
        // Locks in the Round-1 invariant for HandleExecApprovalEvent: a
        // throwing handler must not abort the multicast. If someone "tidies"
        // the per-subscriber try/catch back into a single Invoke(), this
        // test fails — preventing a silent regression of the original
        // approval-banner leak bug pattern.
        var client = NewClient();
        var firstInvoked = false;
        var secondInvoked = false;

        client.AgentEventReceived += (_, _) =>
        {
            firstInvoked = true;
            throw new InvalidOperationException("intentional test throw");
        };
        client.AgentEventReceived += (_, _) =>
        {
            secondInvoked = true;
        };

        const string json = """
            {
              "type": "event",
              "event": "exec.approval.requested",
              "payload": {
                "id": "appr-multicast-test",
                "request": {
                  "command": "noop",
                  "host": "gateway",
                  "sessionKey": "agent:main:main",
                  "agentId": "main"
                }
              }
            }
            """;

        InvokeHandleEvent(client, json);

        Assert.True(firstInvoked, "first subscriber should have been invoked");
        Assert.True(secondInvoked, "second subscriber must be invoked even when first throws");
    }

    // ─── Approval-resolve response routing (PR #676 ClawSweeper P1) ────────
    // Drives HandleResponse against an approval registered through the
    // authoritative PendingRequestRegistry owner. This pins the contract that
    // an ok:false gateway response surfaces as an exception on the awaiting
    // caller, so the chat approval banner is preserved for retry. Without this
    // routing, ResolveExecApprovalAsync would hang until the 15s timeout
    // or, before this fix, return success and silently dismiss the banner.
    // ──────────────────────────────────────────────────────────────────────

    private static Task<bool> RegisterPendingApprovalResolve(
        OpenClawGatewayClient client,
        string requestId)
    {
        var fieldInfo = typeof(OpenClawGatewayClient).GetField(
            "_pendingRequests",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(fieldInfo);
        var registry = Assert.IsType<PendingRequestRegistry>(fieldInfo!.GetValue(client));
        registry.OpenConnection();
        return registry
            .RegisterApproval(requestId, "exec.approval.resolve")
            .Task;
    }

    private static void InvokeHandleResponse(OpenClawGatewayClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleResponseForConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { doc.RootElement, 0L });
    }

    [Fact]
    public async Task ResolveExecApproval_OkFalseResponse_SurfacesAsException()
    {
        // Regression: before the response-await fix, ResolveExecApprovalAsync
        // returned the moment the send completed, so an ok:false rejection
        // by the gateway was logged and dropped while the chat provider
        // happily cleared the banner. This test pins the new contract.
        var client = NewClient();
        var responseTask = RegisterPendingApprovalResolve(client, "req-rejected-1");

        const string json = """
            {
              "type": "res",
              "id": "req-rejected-1",
              "ok": false,
              "error": { "message": "approval not found" }
            }
            """;

        InvokeHandleResponse(client, json);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => responseTask);
        Assert.Contains("approval not found", ex.Message);
    }

    [Fact]
    public async Task ResolveExecApproval_OkTrueResponse_CompletesCaller()
    {
        var client = NewClient();
        var responseTask = RegisterPendingApprovalResolve(client, "req-ok-1");

        const string json = """
            { "type": "res", "id": "req-ok-1", "ok": true }
            """;

        InvokeHandleResponse(client, json);

        Assert.True(responseTask.IsCompletedSuccessfully);
        await responseTask;
    }

    [Fact]
    public async Task ResolveExecApproval_OkFalseWithoutErrorMessage_UsesFallback()
    {
        // The gateway is not contractually required to include an error
        // message body. Verify we still surface a useful exception so the
        // chat provider's catch branch fires and preserves the banner.
        var client = NewClient();
        var responseTask = RegisterPendingApprovalResolve(client, "req-rejected-2");

        const string json = """
            { "type": "res", "id": "req-rejected-2", "ok": false }
            """;

        InvokeHandleResponse(client, json);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => responseTask);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData("""{ "code": "INVALID_REQUEST", "message": "approval already resolved", "details": { "reason": "APPROVAL_ALREADY_RESOLVED" } }""")]
    [InlineData("""{ "code": "APPROVAL_NOT_FOUND", "message": "gone" }""")]
    [InlineData("""{ "code": "INVALID_REQUEST", "message": "unknown or expired approval id" }""")]
    public async Task ResolveApproval_StaleRejection_SurfacesAsStaleException(string error)
    {
        var client = NewClient();
        var responseTask = RegisterPendingApprovalResolve(client, "req-stale");

        InvokeHandleResponse(client, $$"""{ "type": "res", "id": "req-stale", "ok": false, "error": {{error}} }""");

        await Assert.ThrowsAsync<GatewayApprovalStaleException>(() => responseTask);
    }

    [Fact]
    public async Task ResolveApproval_OtherRejection_StaysRetryable()
    {
        var client = NewClient();
        var responseTask = RegisterPendingApprovalResolve(client, "req-deny-only");

        InvokeHandleResponse(client, """
            { "type": "res", "id": "req-deny-only", "ok": false, "error": { "code": "INVALID_REQUEST", "message": "This approval only permits Deny." } }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => responseTask);
        Assert.IsNotType<GatewayApprovalStaleException>(ex);
    }

    [Fact]
    public async Task ResolveApproval_Rejection_LogsOnlyBoundedCodes()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient("ws://localhost:18789", "test-token", logger);
        var responseTask = RegisterPendingApprovalResolve(client, "req-logged");

        InvokeHandleResponse(client, """
            { "type": "res", "id": "req-logged", "ok": false, "error": { "code": "CUSTOM_LEAK_CODE", "message": "rm -rf SECRET-MARKER-42", "details": { "reason": "SECRET-MARKER-42" } } }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => responseTask);
        var line = Assert.Single(logger.Logs, log => log.Contains("rejected by gateway", StringComparison.Ordinal));
        Assert.Contains("code=UNKNOWN", line);
        Assert.DoesNotContain(logger.Logs, log => log.Contains("SECRET-MARKER", StringComparison.Ordinal) ||
            log.Contains("CUSTOM_LEAK_CODE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveApprovalAsync_ThrowsWhenDisconnectedForEveryKind()
    {
        var client = NewClient();

        foreach (var kind in new[] { GatewayApprovalKind.Exec, GatewayApprovalKind.Plugin, GatewayApprovalKind.SystemAgent })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ResolveApprovalAsync(kind, "appr-1", "deny", connectionEpoch: 1));
            Assert.Contains(GatewayApprovalDecisions.ResolveMethod(kind), ex.Message);
        }
    }
}
