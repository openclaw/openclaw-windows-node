using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class AgentDisplayIdentityTests
{
    [Fact]
    public void Parse_UsesResolvedDisplayFieldsAndNotAvatarSource()
    {
        var identity = Parse("""
            {"agentId":"research","name":"Workspace researcher","emoji":"sparkle",
             "avatar":"data:image/png;base64,AAAA","avatarSource":"/private/gateway/avatar.png",
             "avatarStatus":"data","nameSource":"workspace"}
            """);
        Assert.Equal(new("research", "Workspace researcher", "sparkle", "data:image/png;base64,AAAA"), identity);
    }

    [Fact]
    public void Parse_OptionalDisplayFieldsMayBeAbsent()
    {
        Assert.Equal(new("research", null, null, null), Parse("""{"agentId":"research"}"""));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"agentId":"main","name":"wrong owner"}""")]
    [InlineData("""{"agentId":3}""")]
    [InlineData("""{"agentId":"research","name":null}""")]
    [InlineData("""{"agentId":"research","name":""}""")]
    [InlineData("""{"agentId":"research","emoji":true}""")]
    [InlineData("""{"agentId":"research","avatar":{}}""")]
    public void Parse_RejectsNullMalformedAndForeignAgentIdentity(string json)
    {
        Assert.Throws<InvalidOperationException>(() => Parse(json));
    }

    private static AgentDisplayIdentity Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OpenClawGatewayClient.ParseAgentDisplayIdentity(document.RootElement, "research");
    }
}
