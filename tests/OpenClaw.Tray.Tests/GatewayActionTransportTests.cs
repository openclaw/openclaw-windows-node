using System.Text.Json.Nodes;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.Protocol;

namespace OpenClaw.Tray.Tests;

public class GatewayActionTransportTests
{
    private sealed class FakeContext : IGatewayActionContext
    {
        public string SessionKey { get; set; } = "main";
        public string Host { get; set; } = "Windows Node (DESKTOP-TEST)";
        public string InstanceId { get; set; } = "abc-123";
    }

    [Fact]
    public void BuildAgentRequestPayload_HasFiveTopLevelFields()
    {
        var action = new A2UIAction { Name = "submit", SurfaceId = "form", SourceComponentId = "btnSave" };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, new FakeContext());

        Assert.Equal(5, payload.Count);
        Assert.Contains("message", payload);
        Assert.Contains("sessionKey", payload);
        Assert.Contains("thinking", payload);
        Assert.Contains("deliver", payload);
        Assert.Contains("key", payload);
    }

    [Fact]
    public void BuildAgentRequestPayload_DeliverIsFalse_ThinkingIsLow()
    {
        var action = new A2UIAction { Name = "x", SurfaceId = "s", SourceComponentId = "c" };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, new FakeContext());

        Assert.False(payload["deliver"]!.GetValue<bool>());
        Assert.Equal("low", payload["thinking"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAgentRequestPayload_KeyMatchesActionId()
    {
        var action = new A2UIAction { Id = "fixed-id-42", Name = "x", SurfaceId = "s" };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, new FakeContext());

        Assert.Equal("fixed-id-42", payload["key"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAgentRequestPayload_MessageIsCanvasA2UITaggedString()
    {
        var action = new A2UIAction { Name = "signIn", SurfaceId = "login", SourceComponentId = "btnGo" };
        var ctx = new FakeContext { SessionKey = "main", Host = "My PC", InstanceId = "abc" };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, ctx);

        var message = payload["message"]!.GetValue<string>();
        Assert.StartsWith("CANVAS_A2UI ", message);
        Assert.Contains("action=signIn", message);
        Assert.Contains("session=main", message);
        Assert.Contains("surface=login", message);
        Assert.Contains("component=btnGo", message);
        Assert.Contains("host=My_PC", message);
        Assert.Contains("instance=abc", message);
        Assert.EndsWith(" default=update_canvas", message);
    }

    [Fact]
    public void BuildAgentRequestPayload_BlankSessionKey_FallsBackToMain()
    {
        var action = new A2UIAction { Name = "x", SurfaceId = "s" };
        var ctx = new FakeContext { SessionKey = "   " };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, ctx);

        Assert.Equal("main", payload["sessionKey"]!.GetValue<string>());
        Assert.Contains("session=main", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAgentRequestPayload_WithContext_EmbedsCtxJson()
    {
        var ctxObj = new JsonObject { ["email"] = "a@b.co" };
        var action = new A2UIAction
        {
            Name = "signIn",
            SurfaceId = "login",
            SourceComponentId = "btnGo",
            Context = ctxObj,
        };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, new FakeContext());

        var message = payload["message"]!.GetValue<string>();
        Assert.Contains("ctx={\"email\":\"a@b.co\"}", message);
    }

    [Fact]
    public void BuildAgentRequestPayload_NullContext_OmitsCtxToken()
    {
        var action = new A2UIAction { Name = "x", SurfaceId = "s", SourceComponentId = "c", Context = null };
        var payload = GatewayActionTransport.BuildAgentRequestPayload(action, new FakeContext());

        Assert.DoesNotContain(" ctx=", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public void A2UIAction_AutoGeneratesUniqueIds()
    {
        var a = new A2UIAction { Name = "x", SurfaceId = "s" };
        var b = new A2UIAction { Name = "x", SurfaceId = "s" };
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrEmpty(a.Id));
    }
}
