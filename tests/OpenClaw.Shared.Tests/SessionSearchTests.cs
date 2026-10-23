using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Payload_SearchesVisibleRosterBeforeLimit_NotLoadedKeysOrTitleSearch(bool background)
    {
        var payload = JsonSerializer.SerializeToElement(new SessionSearchRequest("  lighthouse  ", background).ToPayload());
        Assert.Equal("lighthouse", payload.GetProperty("query").GetString());
        Assert.Equal(25, payload.GetProperty("limit").GetInt32());
        Assert.False(payload.TryGetProperty("sessionKeys", out _));
        var scope = payload.GetProperty("scope");
        Assert.False(scope.GetProperty("archived").GetBoolean());
        Assert.True(scope.GetProperty("includeUnknown").GetBoolean());
        Assert.True(scope.GetProperty("configuredAgentsOnly").GetBoolean());
        Assert.False(scope.TryGetProperty("search", out _));
        Assert.False(scope.TryGetProperty("limit", out _));
        Assert.Equal(!background, scope.TryGetProperty("excludeCron", out _));
        Assert.Equal(!background, scope.TryGetProperty("excludeSystem", out _));
        if (!background)
        {
            Assert.True(scope.GetProperty("excludeCron").GetBoolean());
            Assert.True(scope.GetProperty("excludeSystem").GetBoolean());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyQueriesAreRejected(string query)
        => Assert.Throws<ArgumentException>(() => new SessionSearchRequest(query).ToPayload());

    [Fact]
    public void MaximumQueryLengthIsEnforced()
    {
        _ = new SessionSearchRequest(new string('x', 4096)).ToPayload();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSearchRequest(new string('x', 4097)).ToPayload());
    }

    [Theory]
    [InlineData("""{"key":"key","label":"Human label","displayName":"Display"}""", "Human label")]
    [InlineData("""{"key":"key","displayName":"Display"}""", "Display")]
    [InlineData("""{"key":"key","label":"  "}""", "key")]
    public void ScopedSessionRowsSupplyHumanTitleWithoutChangingNavigationKey(string session, string expected)
    {
        using var doc = JsonDocument.Parse("""
            {"results":[{"sessionKey":"key","sessionId":"session","messageId":"message",
             "role":"user","timestamp":1,"snippet":"text","score":1}],
             "sessions":[
            """ + session + "]}");
        var hit = Assert.Single(OpenClawGatewayClient.ParseSessionSearch(doc.RootElement).Results);
        Assert.Equal(expected, hit.DisplayTitle);
        Assert.Equal("key", hit.SessionKey);
    }

    [Fact]
    public void ResponsePreservesProvenanceFlagsAndUntrustedSnippetAsData()
    {
        using var doc = JsonDocument.Parse("""
            {"results":[{"sessionKey":"agent:research:other","sessionId":"session-1","messageId":"message-7",
             "role":"assistant","timestamp":1790000000000,"snippet":"<script>not executed</script>","score":0.75}],
             "indexing":true,"truncated":true,"archivedTranscriptsExcluded":3}
            """);
        var result = OpenClawGatewayClient.ParseSessionSearch(doc.RootElement);
        var hit = Assert.Single(result.Results);
        Assert.Equal("agent:research:other", hit.SessionKey);
        Assert.Equal("session-1", hit.SessionId);
        Assert.Equal("message-7", hit.MessageId);
        Assert.Equal("assistant", hit.Role);
        Assert.Equal(1790000000000, hit.Timestamp);
        Assert.Equal("<script>not executed</script>", hit.Snippet);
        Assert.Equal(0.75, hit.Score);
        Assert.True(result.Indexing);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.ArchivedTranscriptsExcluded);
    }

    [Theory]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":[{}]}""")]
    [InlineData("""{"results":[],"indexing":"true"}""")]
    [InlineData("""{"results":[],"archivedTranscriptsExcluded":-1}""")]
    [InlineData("""{"results":[],"archivedTranscriptsExcluded":"1"}""")]
    [InlineData("""{"results":[],"sessions":{}}""")]
    [InlineData("""{"results":[],"sessions":[{"key":"duplicate"},{"key":"duplicate"}]}""")]
    public void MalformedResponsesNeverBecomeEmptySuccess(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => OpenClawGatewayClient.ParseSessionSearch(doc.RootElement));
    }

    [Theory]
    [InlineData("role", "\"system\"")]
    [InlineData("timestamp", "-1")]
    [InlineData("timestamp", "\"1\"")]
    [InlineData("score", "\"1\"")]
    [InlineData("snippet", "null")]
    [InlineData("messageId", "\"\"")]
    public void InvalidMatchIsRejected(string field, string value)
    {
        var row = JsonNode.Parse("""
            {"sessionKey":"agent:main:main","sessionId":"session-1","messageId":"message-1",
             "role":"user","timestamp":1,"snippet":"text","score":1}
            """)!.AsObject();
        row[field] = JsonNode.Parse(value);
        var response = JsonSerializer.SerializeToElement(new { results = new[] { row } });
        Assert.Throws<JsonException>(() => OpenClawGatewayClient.ParseSessionSearch(response));
    }
}
