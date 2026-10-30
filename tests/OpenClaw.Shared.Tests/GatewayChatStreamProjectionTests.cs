using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayChatStreamProjectionTests
{
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
