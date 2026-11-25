using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayTaskSuggestionsTests
{
    [Theory]
    [InlineData("repo", false)]
    [InlineData(".", false)]
    [InlineData("~/x", false)]
    [InlineData("C:relative", false)]
    [InlineData(@"\single-root", false)]
    [InlineData(@"\\server", false)]
    [InlineData(@"C:\repo", true)]
    [InlineData("D:/repo", true)]
    [InlineData(@"\\server\share\repo", true)]
    [InlineData("/repo", true)]
    public void RemoteWorktreePathSyntaxDoesNotDependOnTheLocalOs(string path, bool absolute) =>
        Assert.Equal(absolute, GatewayTaskSuggestions.IsAbsoluteGatewayPath(path));

    private const string Suggestion = """
        {"id":"task_1","title":"Repair tests","prompt":"Run the regression tests.","tldr":"Restore coverage.","cwd":"/repo","sessionKey":"agent:main:main","agentId":"main","createdAt":17}
        """;

    [Fact]
    public void ListPreservesOpaqueGatewayPathsAndSourceIdentity()
    {
        using var json = JsonDocument.Parse("{\"suggestions\":[" + Suggestion + "]}");
        var result = GatewayTaskSuggestions.ParseList(json.RootElement);
        var row = Assert.Single(result.Suggestions);
        Assert.True(result.IsSupported);
        Assert.Equal("task_1", row.Id);
        Assert.Equal("/repo", row.Cwd);
        Assert.Equal("agent:main:main", row.SessionKey);
        Assert.Equal("main", row.AgentId);
        Assert.Equal(17, row.CreatedAt);
        Assert.Equal("Run the regression tests.", row.Prompt);
    }

    [Theory]
    [InlineData("title", "")]
    [InlineData("prompt", "   ")]
    [InlineData("createdAt", -1)]
    public void InvalidSuggestionIsRejected(string field, object value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Suggestion)!;
        json[field] = JsonSerializer.SerializeToNode(value);
        using var document = JsonDocument.Parse(json.ToJsonString());
        Assert.Throws<JsonException>(() => GatewayTaskSuggestions.ParseSuggestion(document.RootElement));
    }

    [Fact]
    public void DuplicateIdentityIsRejected()
    {
        using var json = JsonDocument.Parse("{\"suggestions\":[" + Suggestion + "," + Suggestion + "]}");
        Assert.Throws<JsonException>(() => GatewayTaskSuggestions.ParseList(json.RootElement));
    }

    [Fact]
    public void UnrelatedAcceptanceKeyIsNeverTreatedAsOurLaunch()
    {
        using var json = JsonDocument.Parse("""{"taskId":"another-task","key":"agent:main:dashboard:new"}""");
        Assert.Throws<JsonException>(() => GatewayTaskSuggestions.ParseAccepted(json.RootElement, "task_1"));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("worktree")]
    [InlineData("session")]
    [InlineData("cloud")]
    public void ProducerAcceptanceModesAreSupported(string mode) =>
        GatewayTaskSuggestions.Validate(new("task_1", mode, mode == "cloud" ? "worker-profile" : null));

    [Theory]
    [InlineData("invented", null, null)]
    [InlineData("cloud", null, null)]
    [InlineData("session", null, "/repo")]
    public void UnsupportedOrConflictingOptionsAreRejected(string mode, string? profile, string? cwd) =>
        Assert.Throws<ArgumentException>(() => GatewayTaskSuggestions.Validate(new("task_1", mode, profile, cwd)));
}
