using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class GatewaySearchTests
{
    [Fact]
    public async Task AutomationSearchCollectsLaterPagesWithoutChangingTheQuery()
    {
        var offsets = new List<int>();
        var result = await GatewaySearchProtocol.ReadAutomationPagesAsync((input, _) =>
        {
            var args = (JsonObject)input!;
            var offset = args["offset"]!.GetValue<int>();
            offsets.Add(offset);
            Assert.Equal("deploy", args["query"]!.GetValue<string>());
            return Task.FromResult(JsonSerializer.SerializeToElement(offset == 0
                ? new { jobs = new[] { new { id = "first" } }, total = 2, snapshotRevision = "same", hasMore = true, nextOffset = 1 }
                : new { jobs = new[] { new { id = "second" } }, total = 2, snapshotRevision = "same", hasMore = false, nextOffset = 2 }));
        }, new { query = "deploy", includeDisabled = true }, default);
        Assert.Equal([0, 1], offsets);
        Assert.Equal(["first", "second"], result.GetProperty("jobs").EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        Assert.False(result.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AutomationSearchRejectsNonAdvancingPagination()
    {
        var page = JsonSerializer.SerializeToElement(new
        {
            jobs = new[] { new { id = "job" } }, snapshotRevision = "same", hasMore = true, nextOffset = 0
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewaySearchProtocol.ReadAutomationPagesAsync((_, _) => Task.FromResult(page), null, default));
        Assert.Contains("did not advance", error.Message);
    }

    [Fact]
    public async Task AutomationSearchRejectsChangedSnapshotInsteadOfMixingInventories()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewaySearchProtocol.ReadAutomationPagesAsync((_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                jobs = new[] { new { id = "job" } }, snapshotRevision = (++calls).ToString(),
                total = 2, hasMore = true, nextOffset = calls
            })), null, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AutomationPaginationWithoutRevisionCannotClaimAConsistentInventory()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewaySearchProtocol.ReadAutomationPagesAsync((_, _) =>
            {
                calls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    jobs = new[] { new { id = "first" } }, total = 2, hasMore = true, nextOffset = 1,
                }));
            }, null, default));
        Assert.Contains("snapshot revision", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LegacySinglePageAutomationInventoryDoesNotRequirePaginationMetadata()
    {
        var result = await GatewaySearchProtocol.ReadAutomationPagesAsync((_, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { jobs = new[] { new { id = "legacy" } } })),
            null, default);
        Assert.Equal("legacy", Assert.Single(result.GetProperty("jobs").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public void TranscriptParserPreservesHitsAndIndependentPartialFlags()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            results = new[] { new { sessionKey = "agent:main:one", snippet = "deploy ... failed", score = 0.75 } },
            indexing = true, truncated = true, archivedTranscriptsExcluded = 3
        });
        var result = GatewaySearchProtocol.ParseTranscript(payload, []);
        Assert.Equal("deploy ... failed", Assert.Single(result.Hits).Snippet);
        Assert.Empty(result.Sessions);
        Assert.True(result.Indexing);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.ArchivedExcluded);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[{\"sessionKey\":\"a\",\"score\":1}]}")]
    public void InvalidTranscriptPayloadIsNotAValidEmptyResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => GatewaySearchProtocol.ParseTranscript(document.RootElement, []));
    }

    [Fact]
    public void VisibleRosterScopeDoesNotRestrictResultsToTheLocalSidebar()
    {
        var scope = JsonSerializer.SerializeToElement(GatewaySearchProtocol.Scope());
        Assert.False(scope.GetProperty("includeGlobal").GetBoolean());
        Assert.False(scope.GetProperty("includeUnknown").GetBoolean());
        Assert.True(scope.GetProperty("configuredAgentsOnly").GetBoolean());
        Assert.True(scope.GetProperty("excludeSubagents").GetBoolean());
        Assert.True(scope.GetProperty("excludeCron").GetBoolean());
        Assert.True(scope.GetProperty("excludeSystem").GetBoolean());
        Assert.True(scope.GetProperty("excludeDock").GetBoolean());
        Assert.False(scope.TryGetProperty("agentId", out _));
        Assert.False(scope.TryGetProperty("sessionKeys", out _));
    }
}
