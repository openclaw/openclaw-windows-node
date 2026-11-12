using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayChatStreamProjectionTests
{
    [Fact]
    public void FullSnapshotWinsOverReplacementFlagAndKeepsItsOwnSignature()
    {
        var projected = new GatewayChatStreamProjection().Project(Frame("""
            {"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"ignored",
             "message":{"role":"assistant","content":[{"type":"text","text":"actual",
               "textSignature":"{\"v\":1,\"phase\":\"final_answer\"}"}]}}
            """), 1, out _);
        Assert.Equal("actual", Text(projected));
        Assert.False(projected!.Value.GetProperty("payload").TryGetProperty("replace", out _));
        Assert.Equal("""{"v":1,"phase":"final_answer"}""",
            projected.Value.GetProperty("payload").GetProperty("message").GetProperty("content")[0]
                .GetProperty("textSignature").GetString());
    }

    [Fact]
    public void ReplacementIsUnsignedAndAppendPreservesMixedPhases()
    {
        var owner = new GatewayChatStreamProjection();
        owner.Project(Frame("""
            {"sessionKey":"s","runId":"r","state":"delta","message":{"role":"assistant","content":[
            {"type":"text","text":"old","textSignature":"{\"v\":1,\"id\":\"a\",\"phase\":\"final_answer\"}"}]}}
            """), 1, out _);
        var replaced = owner.Project(Frame("""
            {"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"new"}
            """), 1, out _);
        Assert.False(replaced!.Value.GetProperty("payload").GetProperty("message").GetProperty("content")[0]
            .TryGetProperty("textSignature", out _));
        owner.Project(Frame("""
            {"sessionKey":"s","runId":"r","state":"delta","message":{"role":"assistant","content":[
            {"type":"text","text":"work","textSignature":"{\"v\":1,\"phase\":\"commentary\"}"},
            {"type":"output_text","text":"answer","textSignature":"{\"v\":1,\"phase\":\"final_answer\"}"}]}}
            """), 1, out _);
        var appended = owner.Project(Frame("""
            {"sessionKey":"s","runId":"r","state":"delta","deltaText":" finished"}
            """), 1, out _);
        var blocks = appended!.Value.GetProperty("payload").GetProperty("message").GetProperty("content");
        Assert.Equal("work", blocks[0].GetProperty("text").GetString());
        Assert.Equal("answer finished", blocks[1].GetProperty("text").GetString());
        Assert.Equal("""{"v":1,"phase":"final_answer"}""", blocks[1].GetProperty("textSignature").GetString());
    }

    private static JsonElement Frame(string payload) => JsonDocument.Parse(
        """{"event":"chat","payload":""" + payload + "}").RootElement.Clone();
    private static string Text(JsonElement? frame) => frame!.Value.GetProperty("payload")
        .GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public void LegacyTextDelta_PassesThroughWithoutCreatingAnAppendBaseline()
    {
        var owner = new GatewayChatStreamProjection();
        var legacy = Frame("""{"sessionKey":"s","runId":"r","state":"delta","text":"legacy"}""");
        var projected = owner.Project(legacy, 1, out var missing);
        Assert.False(missing);
        Assert.Equal(legacy.GetRawText(), projected!.Value.GetRawText());
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","deltaText":"append"}"""),
            1, out missing));
        Assert.True(missing);
    }

    [Fact]
    public void SnapshotIncludesItsDelta_AppendAndReplacePreserveNonTextBlocks()
    {
        var owner = new GatewayChatStreamProjection();
        var first = owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","deltaText":"hello","message":{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"image","data":"image"}]}}"""), 1, out var missing);
        Assert.False(missing);
        Assert.Equal("hello", Text(first));
        var next = owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","deltaText":" world"}"""), 1, out missing);
        Assert.Equal("hello world", Text(next));
        var replaced = owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"new"}"""), 1, out missing);
        Assert.Equal("new", Text(replaced));
        Assert.Equal("image", replaced!.Value.GetProperty("payload").GetProperty("message").GetProperty("content")[1].GetProperty("type").GetString());
    }

    [Fact]
    public void Baselines_AreConnectionSessionAndAgentScoped()
    {
        var owner = new GatewayChatStreamProjection();
        owner.Project(Frame("""{"sessionKey":"s","agentId":"a","runId":"r","state":"delta","replace":true,"deltaText":"first"}"""), 1, out _);
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","agentId":"b","runId":"r","state":"delta","deltaText":"wrong agent"}"""), 1, out var missing));
        Assert.True(missing);
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","agentId":"a","runId":"r","state":"delta","deltaText":"old baseline"}"""), 2, out missing));
        Assert.True(missing);
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"old connection"}"""), 1, out _));
    }

    [Theory]
    [InlineData("final")]
    [InlineData("error")]
    [InlineData("aborted")]
    public void TerminalEvent_RetiresBaseline(string status)
    {
        var owner = new GatewayChatStreamProjection();
        owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"first"}"""), 1, out _);
        owner.Project(Frame(JsonSerializer.Serialize(new { sessionKey = "s", runId = "r", state = status })), 1, out _);
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","deltaText":"late"}"""), 1, out var missing));
        Assert.True(missing);
    }

    [Fact]
    public void RetentionLimit_AndReplacement_AreMeasuredWithoutLosingNonTextContent()
    {
        var owner = new GatewayChatStreamProjection();
        var frame = Frame(JsonSerializer.Serialize(new
        {
            sessionKey = "s", runId = "r", state = "delta", replace = true,
            deltaText = new string('x', 2 * 1024 * 1024 - 1),
        }));
        Assert.NotNull(owner.Project(frame, 1, out _));
        Assert.Null(owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","deltaText":"ab"}"""),
            1, out var missing));
        Assert.True(missing);
        Assert.NotNull(owner.Project(Frame("""{"sessionKey":"s","runId":"r","state":"delta","replace":true,"deltaText":"recovered"}"""),
            1, out missing));
        Assert.False(missing);
    }
}
