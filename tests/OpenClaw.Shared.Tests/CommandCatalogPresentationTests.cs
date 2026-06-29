using System.Linq;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Tests for the UI-facing presentation helpers layered on top of the
// GatewayCommand DTO (GatewayCommandPresentation + the ranked
// ChatCommandCatalogView). The wire DTOs / parsing / CommandCatalogQuery are
// covered separately by GatewayProtocolModelsTests.
public class CommandCatalogPresentationTests
{
    private static GatewayCommand[] Sample() => new[]
    {
        new GatewayCommand { Name = "clear", NativeName = "/clear", Description = "Clear the conversation", Category = "Session", Source = "native", Scope = "session" },
        new GatewayCommand
        {
            Name = "model", NativeName = "/model", Description = "Switch the active model", Category = "Session",
            Source = "native", Scope = "session", AcceptsArgs = true,
            Args = new[] { new GatewayCommandArg { Name = "id", Required = true, Type = "string" } }
        },
        new GatewayCommand { Name = "summarize", TextAliases = new[] { "/summarize", "/tldr" }, Description = "Summarize the thread", Category = "Text", Source = "text" },
        new GatewayCommand { Name = "deploy", Description = "Run the deploy skill", Source = "skill", Category = "Skills" },
        new GatewayCommand { Name = "jira", NativeName = "/jira", Source = "plugin" },
    };

    // ── GatewayCommandPresentation ──

    [Fact]
    public void DisplayName_PrefersNativeThenAliasThenName()
    {
        Assert.Equal("/clear", new GatewayCommand { Name = "clear", NativeName = "/clear" }.DisplayName());
        Assert.Equal("/tldr", new GatewayCommand { Name = "summarize", TextAliases = new[] { "/tldr" } }.DisplayName());
        Assert.Equal("/bare", new GatewayCommand { Name = "bare" }.DisplayName());
        // Already-prefixed names are not double-slashed.
        Assert.Equal("/x", new GatewayCommand { Name = "/x" }.DisplayName());
    }

    [Theory]
    [InlineData("native", "Native")]
    [InlineData("skill", "Skill")]
    [InlineData("plugin", "Plugin")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SourceLabel_CapitalizesSource(string? source, string expected)
    {
        Assert.Equal(expected, new GatewayCommand { Name = "x", Source = source }.SourceLabel());
    }

    [Fact]
    public void RequiresArgs_TrueWhenAcceptsArgsOrRequiredArg()
    {
        Assert.False(new GatewayCommand { Name = "a" }.RequiresArgs());
        Assert.True(new GatewayCommand { Name = "a", AcceptsArgs = true }.RequiresArgs());
        Assert.True(new GatewayCommand
        {
            Name = "a",
            Args = new[] { new GatewayCommandArg { Name = "x", Required = true } }
        }.RequiresArgs());
        // Optional-only arg, AcceptsArgs not set → no required input.
        Assert.False(new GatewayCommand
        {
            Name = "a",
            Args = new[] { new GatewayCommandArg { Name = "x", Required = false } }
        }.RequiresArgs());
    }

    [Fact]
    public void BuildInsertionText_AppendsSpaceOnlyWhenArgsNeeded()
    {
        Assert.Equal("/clear", new GatewayCommand { Name = "clear", NativeName = "/clear" }.BuildInsertionText());
        Assert.Equal("/model ", new GatewayCommand { Name = "model", NativeName = "/model", AcceptsArgs = true }.BuildInsertionText());
        var withRequired = new GatewayCommand
        {
            Name = "m", NativeName = "/m",
            Args = new[] { new GatewayCommandArg { Name = "id", Required = true } }
        };
        Assert.Equal("/m ", withRequired.BuildInsertionText());
    }

    // ── ChatCommandCatalogView search ──

    [Fact]
    public void Search_EmptyQuery_ReturnsAllOrderedByDisplayName()
    {
        var view = new ChatCommandCatalogView(Sample());
        var all = view.Search("");
        Assert.Equal(5, all.Count);
        var names = all.Select(c => c.DisplayName()).ToList();
        Assert.Equal(names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("CLE")]
    [InlineData("/clear")]
    public void Search_MatchesByNameNativeAndIsCaseInsensitive(string query)
    {
        var view = new ChatCommandCatalogView(Sample());
        Assert.Contains(view.Search(query), c => c.Name == "clear");
    }

    [Fact]
    public void Search_MatchesByAlias()
    {
        var view = new ChatCommandCatalogView(Sample());
        Assert.Contains(view.Search("tldr"), c => c.Name == "summarize");
    }

    [Fact]
    public void Search_MatchesByDescriptionAndCategory()
    {
        var view = new ChatCommandCatalogView(Sample());
        Assert.Contains(view.Search("Switch the active"), c => c.Name == "model");
        Assert.Contains(view.Search("Skills"), c => c.Name == "deploy");
    }

    [Fact]
    public void Search_RanksPrefixMatchesAboveContainsMatches()
    {
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "preview", NativeName = "/preview" },
            new GatewayCommand { Name = "view", NativeName = "/view" },
        });
        var results = view.Search("view");
        // "/view" is a prefix match; "/preview" only a contains match → view first.
        Assert.Equal("view", results[0].Name);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var view = new ChatCommandCatalogView(Sample());
        Assert.Empty(view.Search("zzzznotacommand"));
    }

    // ── ChatCommandCatalogView grouping ──

    [Fact]
    public void GroupByCategory_GroupsAndOrders()
    {
        var view = new ChatCommandCatalogView(Sample());
        var groups = view.GroupByCategory();
        var categories = groups.Select(g => g.Category).ToList();
        Assert.Contains("Session", categories);
        Assert.Contains("Text", categories);
        Assert.Contains("Skills", categories);
        Assert.Equal(categories.OrderBy(c => c, System.StringComparer.OrdinalIgnoreCase).ToList(), categories);
        var session = groups.Single(g => g.Category == "Session");
        Assert.Equal(new[] { "/clear", "/model" }, session.Commands.Select(c => c.DisplayName()).ToArray());
    }

    [Fact]
    public void GroupByCategory_FallsBackToSourceLabelThenOther()
    {
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "a", Source = "plugin" },  // no category → "Plugin"
            new GatewayCommand { Name = "b" },                       // no category/source → "Other"
        });
        var categories = view.GroupByCategory().Select(g => g.Category).ToList();
        Assert.Contains("Plugin", categories);
        Assert.Contains("Other", categories);
    }

    [Fact]
    public void GroupByCategory_RespectsSearchFilter()
    {
        var view = new ChatCommandCatalogView(Sample());
        var all = view.GroupByCategory("model").SelectMany(g => g.Commands).ToList();
        Assert.Single(all);
        Assert.Equal("model", all[0].Name);
    }

    [Fact]
    public void View_NullCommands_IsEmpty()
    {
        var view = new ChatCommandCatalogView(null);
        Assert.Equal(0, view.Count);
        Assert.Empty(view.Search("anything"));
        Assert.Empty(view.GroupByCategory());
    }
}
