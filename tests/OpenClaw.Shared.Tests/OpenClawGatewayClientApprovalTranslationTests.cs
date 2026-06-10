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
            "HandleEvent",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { doc.RootElement, json.Length });
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
    // Drives HandleResponse via reflection against a pre-registered TCS in
    // _pendingApprovalResolves. This pins the contract that an ok:false
    // gateway response surfaces as an exception on the awaiting caller — so
    // the chat approval banner is preserved for retry. Without this routing,
    // ResolveExecApprovalAsync would hang until the 5s timeout (best case)
    // or, before this fix, return success and silently dismiss the banner.
    // ──────────────────────────────────────────────────────────────────────

    private static TaskCompletionSource<bool> RegisterPendingApprovalResolve(OpenClawGatewayClient client, string requestId)
    {
        var fieldInfo = typeof(OpenClawGatewayClient).GetField(
            "_pendingApprovalResolves",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(fieldInfo);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>)fieldInfo!.GetValue(client)!;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dict[requestId] = tcs;

        // Also seed the request-method tracker so HandleResponse's id lookup
        // mirrors what TrackPendingRequest would have done in production.
        var trackMethod = typeof(OpenClawGatewayClient).GetMethod(
            "TrackPendingRequest",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(trackMethod);
        trackMethod!.Invoke(client, new object[] { requestId, "exec.approval.resolve" });
        return tcs;
    }

    private static void InvokeHandleResponse(OpenClawGatewayClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleResponse",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { doc.RootElement });
    }

    [Fact]
    public async Task ResolveExecApproval_OkFalseResponse_SurfacesAsException()
    {
        // Regression: before the response-await fix, ResolveExecApprovalAsync
        // returned the moment the send completed, so an ok:false rejection
        // by the gateway was logged and dropped while the chat provider
        // happily cleared the banner. This test pins the new contract.
        var client = NewClient();
        var tcs = RegisterPendingApprovalResolve(client, "req-rejected-1");

        const string json = """
            {
              "type": "res",
              "id": "req-rejected-1",
              "ok": false,
              "error": { "message": "approval not found" }
            }
            """;

        InvokeHandleResponse(client, json);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tcs.Task);
        Assert.Contains("approval not found", ex.Message);
    }

    [Fact]
    public async Task ResolveExecApproval_OkTrueResponse_CompletesCaller()
    {
        var client = NewClient();
        var tcs = RegisterPendingApprovalResolve(client, "req-ok-1");

        const string json = """
            { "type": "res", "id": "req-ok-1", "ok": true }
            """;

        InvokeHandleResponse(client, json);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        await tcs.Task;
    }

    [Fact]
    public async Task ResolveExecApproval_OkFalseWithoutErrorMessage_UsesFallback()
    {
        // The gateway is not contractually required to include an error
        // message body. Verify we still surface a useful exception so the
        // chat provider's catch branch fires and preserves the banner.
        var client = NewClient();
        var tcs = RegisterPendingApprovalResolve(client, "req-rejected-2");

        const string json = """
            { "type": "res", "id": "req-rejected-2", "ok": false }
            """;

        InvokeHandleResponse(client, json);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tcs.Task);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
