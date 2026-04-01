using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for NodeCapabilityBase arg parsing, CanHandle, Success/Error helpers,
/// and NodeInvokeRequest/Response models.
/// </summary>
public class NodeCapabilityBaseTests
{
    private class TestCapability : NodeCapabilityBase
    {
        public override string Category => "test";
        private static readonly string[] _cmds = { "test.one", "test.two" };
        public override IReadOnlyList<string> Commands => _cmds;

        public TestCapability() : base(NullLogger.Instance) { }

        public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(Success(new { echo = request.Command }));

        // Expose protected helpers for testing
        public string? PubGetStringArg(JsonElement args, string name, string? def = null) => GetStringArg(args, name, def);
        public int PubGetIntArg(JsonElement args, string name, int def = 0) => GetIntArg(args, name, def);
        public bool PubGetBoolArg(JsonElement args, string name, bool def = false) => GetBoolArg(args, name, def);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_ReturnsTrue_ForRegisteredCommand()
    {
        var cap = new TestCapability();
        Assert.True(cap.CanHandle("test.one"));
        Assert.True(cap.CanHandle("test.two"));
    }

    [Fact]
    public void CanHandle_ReturnsFalse_ForUnknownCommand()
    {
        var cap = new TestCapability();
        Assert.False(cap.CanHandle("test.three"));
        Assert.False(cap.CanHandle("other.command"));
        Assert.False(cap.CanHandle(""));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOk_WithPayload()
    {
        var cap = new TestCapability();
        var req = new NodeInvokeRequest { Id = "1", Command = "test.one" };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
        Assert.Null(res.Error);
        
        // Verify payload echoes the command back
        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("test.one", doc.RootElement.GetProperty("echo").GetString());
    }

    [Fact]
    public void GetStringArg_ReturnsValue_WhenPresent()
    {
        var cap = new TestCapability();
        var args = Parse("""{"url":"https://example.com","empty":""}""");
        Assert.Equal("https://example.com", cap.PubGetStringArg(args, "url"));
        Assert.Equal("", cap.PubGetStringArg(args, "empty"));
    }

    [Fact]
    public void GetStringArg_ReturnsDefault_WhenMissing()
    {
        var cap = new TestCapability();
        var args = Parse("""{"other":123}""");
        Assert.Null(cap.PubGetStringArg(args, "url"));
        Assert.Equal("fallback", cap.PubGetStringArg(args, "url", "fallback"));
    }

    [Fact]
    public void GetStringArg_ReturnsDefault_WhenWrongType()
    {
        var cap = new TestCapability();
        var args = Parse("""{"url":42}""");
        Assert.Null(cap.PubGetStringArg(args, "url"));
    }

    [Fact]
    public void GetIntArg_ReturnsValue_WhenPresent()
    {
        var cap = new TestCapability();
        var args = Parse("""{"width":1920,"zero":0}""");
        Assert.Equal(1920, cap.PubGetIntArg(args, "width"));
        Assert.Equal(0, cap.PubGetIntArg(args, "zero"));
    }

    [Fact]
    public void GetIntArg_ReturnsDefault_WhenMissing()
    {
        var cap = new TestCapability();
        var args = Parse("""{}""");
        Assert.Equal(800, cap.PubGetIntArg(args, "width", 800));
    }

    [Fact]
    public void GetIntArg_ReturnsDefault_WhenWrongType()
    {
        var cap = new TestCapability();
        var args = Parse("""{"width":"not-a-number"}""");
        Assert.Equal(99, cap.PubGetIntArg(args, "width", 99));
    }

    [Fact]
    public void GetIntArg_ReturnsDefault_WhenValueOverflowsInt32()
    {
        var cap = new TestCapability();
        // Value exceeds Int32.MaxValue — should gracefully return default
        var args = Parse("""{"big":9999999999999}""");
        Assert.Equal(42, cap.PubGetIntArg(args, "big", 42));
    }

    [Fact]
    public void GetBoolArg_ReturnsValue_WhenPresent()
    {
        var cap = new TestCapability();
        var args = Parse("""{"enabled":true,"disabled":false}""");
        Assert.True(cap.PubGetBoolArg(args, "enabled"));
        Assert.False(cap.PubGetBoolArg(args, "disabled"));
    }

    [Fact]
    public void GetBoolArg_ReturnsDefault_WhenMissing()
    {
        var cap = new TestCapability();
        var args = Parse("""{}""");
        Assert.True(cap.PubGetBoolArg(args, "enabled", true));
        Assert.False(cap.PubGetBoolArg(args, "enabled", false));
    }

    [Fact]
    public void GetBoolArg_ReturnsDefault_WhenWrongType()
    {
        var cap = new TestCapability();
        var args = Parse("""{"enabled":"yes"}""");
        Assert.False(cap.PubGetBoolArg(args, "enabled"));
    }

    [Fact]
    public void GetStringArg_HandlesDefaultJsonElement()
    {
        var cap = new TestCapability();
        JsonElement args = default;
        Assert.Null(cap.PubGetStringArg(args, "url"));
        Assert.Equal("def", cap.PubGetStringArg(args, "url", "def"));
    }
}

public class NodeInvokeResponseTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var res = new NodeInvokeResponse();
        Assert.Equal("", res.Id);
        Assert.False(res.Ok);
        Assert.Null(res.Payload);
        Assert.Null(res.Error);
    }

    [Fact]
    public void CanSet_AllProperties()
    {
        var res = new NodeInvokeResponse
        {
            Id = "abc",
            Ok = true,
            Payload = new { test = 42 },
            Error = "nope"
        };
        Assert.Equal("abc", res.Id);
        Assert.True(res.Ok);
        Assert.Equal("nope", res.Error);
        
        // Verify payload content is preserved
        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("test").GetInt32());
    }
}

public class NodeRegistrationTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var reg = new NodeRegistration();
        Assert.Equal("", reg.Id);
        Assert.Equal("windows", reg.Platform);
        Assert.Empty(reg.Capabilities);
        Assert.Empty(reg.Commands);
        Assert.Empty(reg.Permissions);
    }
}
