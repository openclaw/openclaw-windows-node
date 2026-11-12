using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class SessionRosterCollectionTests
{
    [Theory]
    [InlineData("""{"sessions":[]}""", true, 0)]
    [InlineData("""{"sessions":{}}""", true, 0)]
    [InlineData("""{}""", false, 0)]
    [InlineData("""{"sessions":null}""", false, 0)]
    [InlineData("""{"sessions":{"count":1}}""", false, 0)]
    [InlineData("""{"sessions":{"agent:research:one":{}}}""", true, 1)]
    [InlineData("""{"agent:research:one":{"sessionId":"wire-id"}}""", true, 1)]
    [InlineData("""{"sessions":{"agent:research:one":"Research","count":1}}""", true, 1)]
    [InlineData("""{"sessions":{"agent:research:one":null}}""", false, 0)]
    [InlineData("""{"sessions":{"agent:research:one":17}}""", false, 0)]
    public void AuthoritativeCount_PreservesLegacyMapsWithoutAdmittingMissingCollections(
        string json, bool expectedValid, int expectedCount)
    {
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement;
        var rows = payload.TryGetProperty("sessions", out var collection) ? collection : payload;
        Assert.Equal(expectedValid, SessionRosterCollection.TryGetAuthoritativeRowCount(payload, rows, out var count));
        Assert.Equal(expectedCount, count);
    }

    [Fact]
    public void LegacyRows_UseTheLiveRosterMetadataAndPathFilters()
    {
        using var document = JsonDocument.Parse("""
            {"global":{},"agent:research:one":{"sessionId":"wire-id"},
             "agent:research:path":"/cache/transcript.jsonl", "sessionCount":17,
             "count":2,"defaults":{},"recent":[],"path":"/cache","ts":99}
            """);
        var rows = SessionRosterCollection.EnumerateLegacyRows(document.RootElement).ToArray();
        Assert.Equal(["global", "agent:research:one"], rows.Select(row => row.Name));
        Assert.Equal("wire-id", rows[1].Value.GetProperty("sessionId").GetString());
    }
}
