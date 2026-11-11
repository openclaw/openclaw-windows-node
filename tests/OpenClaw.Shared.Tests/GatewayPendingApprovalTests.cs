using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// The typed pending-approval observation that feeds the Inbox: top-level
/// exec/plugin/openclaw approval events and *.approval.list replies parse into
/// one record shape, mirroring the web Control UI's exec-approval parser.
/// </summary>
public class GatewayPendingApprovalTests
{
    private const string ExecPayload = """
        {
          "id": "approval-1",
          "request": {
            "command": "pay --to tap 612",
            "cwd": "C:\\work",
            "host": "gateway",
            "sessionKey": "agent:main:travel",
            "agentId": "main",
            "warningText": "Moves money.",
            "allowedDecisions": ["allow-once", "deny", "bogus"],
            "scope": { "kind": "payment", "amount": "612", "currency": "USD", "target": "TAP Air Portugal" }
          },
          "createdAtMs": 1780691492368,
          "expiresAtMs": 1780691792368
        }
        """;

    [Theory]
    [InlineData("exec.approval.requested", GatewayApprovalKind.Exec, true)]
    [InlineData("exec.approval.resolved", GatewayApprovalKind.Exec, false)]
    [InlineData("plugin.approval.requested", GatewayApprovalKind.Plugin, true)]
    [InlineData("openclaw.approval.resolved", GatewayApprovalKind.SystemAgent, false)]
    public void TryGetEventKind_MapsApprovalEventNames(string name, GatewayApprovalKind kind, bool requested)
    {
        Assert.True(GatewayPendingApprovalParser.TryGetEventKind(name, out var actualKind, out var actualRequested));
        Assert.Equal(kind, actualKind);
        Assert.Equal(requested, actualRequested);
    }

    [Theory]
    [InlineData("exec.approval.list")]
    [InlineData("node.approval.requested")]
    [InlineData("approval.requested")]
    [InlineData(null)]
    public void TryGetEventKind_RejectsOtherEvents(string? name) =>
        Assert.False(GatewayPendingApprovalParser.TryGetEventKind(name, out _, out _));

    [Fact]
    public void TryParse_ReadsExecApprovalWithScopeAndKnownDecisions()
    {
        using var doc = JsonDocument.Parse(ExecPayload);

        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out var approval));

        Assert.Equal("approval-1", approval.Id);
        Assert.Equal("pay --to tap 612", approval.Command);
        Assert.Null(approval.Title);
        Assert.Equal(@"C:\work", approval.Cwd);
        Assert.Equal("gateway", approval.Host);
        Assert.Equal("agent:main:travel", approval.SessionKey);
        Assert.Equal("Moves money.", approval.WarningText);
        Assert.Equal(1780691792368, approval.ExpiresAtMs);
        Assert.Equal(["allow-once", "deny"], approval.AllowedDecisions);
        var scope = Assert.IsType<GatewayApprovalScope>(approval.Scope);
        Assert.Equal(GatewayApprovalScopeKind.Payment, scope.Kind);
        Assert.Equal(("612", "USD", "TAP Air Portugal"), (scope.Amount, scope.Currency, scope.Target));
    }

    [Theory]
    [InlineData("""{ "id": "a", "request": { "command": "x" }, "createdAtMs": 1 }""")]
    [InlineData("""{ "id": "a", "request": { "command": " " }, "createdAtMs": 1, "expiresAtMs": 2 }""")]
    [InlineData("""{ "request": { "command": "x" }, "createdAtMs": 1, "expiresAtMs": 2 }""")]
    [InlineData("""{ "id": "a", "createdAtMs": 1, "expiresAtMs": 2 }""")]
    public void TryParse_RejectsIncompleteExecRecords(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out _));
    }

    [Theory]
    [InlineData(9007199254740991L, 9007199254740991L)]
    [InlineData(1780691492368L, 9007199254740991L)]
    [InlineData(253402300800000L, 253402300800001L)]
    public void TryParse_RejectsTimestampsBeyondTheRepresentableRange(long createdAtMs, long expiresAtMs)
    {
        using var doc = JsonDocument.Parse($$"""
            { "id": "a", "request": { "command": "x" }, "createdAtMs": {{createdAtMs}}, "expiresAtMs": {{expiresAtMs}} }
            """);

        Assert.False(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out _));
    }

    [Fact]
    public void TryParse_RejectsHugeFractionalTimestampsWithoutThrowing()
    {
        using var doc = JsonDocument.Parse("""
            { "id": "a", "request": { "command": "x" }, "createdAtMs": 1.5e300, "expiresAtMs": 2.5 }
            """);

        Assert.False(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, doc.RootElement, out _));
    }

    [Fact]
    public void GatewayUnixTime_ClampsInsteadOfThrowing()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(GatewayUnixTime.MaxMs), GatewayUnixTime.ToDateTimeOffset(long.MaxValue));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(GatewayUnixTime.MinMs), GatewayUnixTime.ToDateTimeOffset(long.MinValue));
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(GatewayUnixTime.MaxMs, GatewayUnixTime.Add(now, long.MaxValue).ToUnixTimeMilliseconds());
        Assert.Equal(GatewayUnixTime.MinMs, GatewayUnixTime.Add(now, long.MinValue).ToUnixTimeMilliseconds());
        Assert.Equal(now.AddSeconds(5), GatewayUnixTime.Add(now, 5_000));
    }

    [Fact]
    public void TryParse_PluginUsesTitleAsCommandAndSystemAgentRequiresProposal()
    {
        using var plugin = JsonDocument.Parse("""
            { "id": "p", "request": { "title": "Publish post", "description": "To the blog" }, "createdAtMs": 1, "expiresAtMs": 2 }
            """);
        using var systemAgent = JsonDocument.Parse("""
            { "id": "s", "request": { "title": "Change config", "command": "config set" }, "createdAtMs": 1, "expiresAtMs": 2 }
            """);

        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Plugin, plugin.RootElement, out var approval));
        Assert.Equal(("Publish post", "Publish post", "To the blog"), (approval.Title, approval.Command, approval.Description));
        Assert.False(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.SystemAgent, systemAgent.RootElement, out _));
    }

    [Fact]
    public void ParseList_SkipsInvalidEntries()
    {
        using var doc = JsonDocument.Parse($"[{ExecPayload}, {{ \"id\": \"broken\" }}]");

        var approvals = GatewayPendingApprovalParser.ParseList(GatewayApprovalKind.Exec, doc.RootElement);

        Assert.Equal(["approval-1"], approvals.Select(approval => approval.Id));
    }

    [Theory]
    [InlineData("exec.approval.requested")]
    [InlineData("plugin.approval.requested")]
    public void Client_RaisesTypedPendingApprovalForRequestedEvents(string eventName)
    {
        var client = NewClient();
        GatewayPendingApproval? observed = null;
        client.PendingApprovalRequested += (_, approval) => observed = approval;
        var payload = eventName.StartsWith("plugin", StringComparison.Ordinal)
            ? """{ "id": "approval-1", "request": { "title": "Publish" }, "createdAtMs": 1, "expiresAtMs": 2 }"""
            : ExecPayload;

        InvokeHandleEvent(client, $$"""{ "type": "event", "event": "{{eventName}}", "payload": {{payload}} }""");

        Assert.NotNull(observed);
        Assert.Equal("approval-1", observed!.Id);
    }

    [Fact]
    public void Client_RaisesResolvedIdAndKeepsChatTranslationForExec()
    {
        var client = NewClient();
        string? resolved = null;
        AgentEventInfo? chat = null;
        client.PendingApprovalResolved += (_, id) => resolved = id;
        client.AgentEventReceived += (_, evt) => chat = evt;

        InvokeHandleEvent(client, """
            { "type": "event", "event": "exec.approval.resolved",
              "payload": { "id": "approval-1", "decision": "deny", "request": { "sessionKey": "agent:main:main" } } }
            """);

        Assert.Equal("approval-1", resolved);
        Assert.Equal("denied", chat?.Data.GetProperty("phase").GetString());
    }

    [Fact]
    public void Client_PluginApprovalDoesNotReachChatTranslation()
    {
        var client = NewClient();
        AgentEventInfo? chat = null;
        client.AgentEventReceived += (_, evt) => chat = evt;

        InvokeHandleEvent(client, """
            { "type": "event", "event": "plugin.approval.requested",
              "payload": { "id": "p", "request": { "title": "Publish" }, "createdAtMs": 1, "expiresAtMs": 2 } }
            """);

        Assert.Null(chat);
    }

    private static OpenClawGatewayClient NewClient() =>
        new("ws://localhost:18789", "test-token", new TestLogger());

    private static void InvokeHandleEvent(OpenClawGatewayClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleEventForConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, [doc.RootElement, json.Length, 0L]);
    }
}
