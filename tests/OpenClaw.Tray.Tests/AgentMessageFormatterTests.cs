using OpenClawTray.A2UI.Actions;

namespace OpenClaw.Tray.Tests;

public class AgentMessageFormatterTests
{
    [Fact]
    public void Sanitize_PreservesAlphanumericAndAllowedPunctuation()
    {
        Assert.Equal("abc123", AgentMessageFormatter.SanitizeTagValue("abc123"));
        Assert.Equal("a_b-c.d:e", AgentMessageFormatter.SanitizeTagValue("a_b-c.d:e"));
    }

    [Fact]
    public void Sanitize_SpacesBecomeUnderscores()
    {
        Assert.Equal("My_PC", AgentMessageFormatter.SanitizeTagValue("My PC"));
        Assert.Equal("a_b_c", AgentMessageFormatter.SanitizeTagValue("a b c"));
    }

    [Fact]
    public void Sanitize_ReplacesDisallowedCharsWithUnderscore()
    {
        // Parens, equals, brackets, slashes — all should become _
        Assert.Equal("Windows_Node__Box_", AgentMessageFormatter.SanitizeTagValue("Windows Node (Box)"));
        Assert.Equal("a_b_c", AgentMessageFormatter.SanitizeTagValue("a=b=c"));
        Assert.Equal("a_b", AgentMessageFormatter.SanitizeTagValue("a/b"));
    }

    [Fact]
    public void Sanitize_EmptyOrWhitespace_BecomesDash()
    {
        Assert.Equal("-", AgentMessageFormatter.SanitizeTagValue(""));
        Assert.Equal("-", AgentMessageFormatter.SanitizeTagValue("   "));
        Assert.Equal("-", AgentMessageFormatter.SanitizeTagValue(null));
    }

    [Fact]
    public void Format_NoContext_ProducesAndroidShape()
    {
        var msg = AgentMessageFormatter.FormatAgentMessage(
            actionName: "submit",
            sessionKey: "main",
            surfaceId: "form",
            sourceComponentId: "btnSave",
            host: "My PC",
            instanceId: "abc123",
            contextJson: null);

        Assert.Equal(
            "CANVAS_A2UI action=submit session=main surface=form component=btnSave host=My_PC instance=abc123 default=update_canvas",
            msg);
    }

    [Fact]
    public void Format_WithContext_AppendsCtxBeforeDefault_OnInstanceLine()
    {
        // Matches Android: ctx is glued to the instance= token (no leading space
        // separator — the suffix carries its own leading space). default=… is
        // a separate token that follows.
        var msg = AgentMessageFormatter.FormatAgentMessage(
            actionName: "signIn",
            sessionKey: "main",
            surfaceId: "login",
            sourceComponentId: "btnGo",
            host: "My PC",
            instanceId: "abc",
            contextJson: """{"email":"a@b.co"}""");

        Assert.Equal(
            "CANVAS_A2UI action=signIn session=main surface=login component=btnGo host=My_PC instance=abc ctx={\"email\":\"a@b.co\"} default=update_canvas",
            msg);
    }

    [Fact]
    public void Format_BlankContextJson_IsTreatedAsAbsent()
    {
        var msg = AgentMessageFormatter.FormatAgentMessage(
            "x", "main", "s", "c", "h", "i", "   ");
        Assert.DoesNotContain(" ctx=", msg);
    }

    [Fact]
    public void Format_EmptyComponent_RendersAsDash()
    {
        var msg = AgentMessageFormatter.FormatAgentMessage(
            "submit", "main", "s", sourceComponentId: "", host: "h", instanceId: "i", contextJson: null);
        Assert.Contains("component=- ", msg);
    }

    [Fact]
    public void Format_ContextWithSpaces_PreservesContextBytes()
    {
        // ctx is a JSON blob; sanitize must NOT touch it. (Only the host/etc tags
        // are sanitized.)
        var ctx = """{"q":"hello world"}""";
        var msg = AgentMessageFormatter.FormatAgentMessage(
            "search", "main", "s", "c", "h", "i", ctx);
        Assert.Contains(ctx, msg);
    }
}
