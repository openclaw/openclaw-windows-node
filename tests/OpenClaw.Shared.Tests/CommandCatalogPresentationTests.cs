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

    // ── Mac-parity presentation helpers ──

    [Fact]
    public void ArgTemplate_FormatsRequiredAndOptionalArgs()
    {
        Assert.Equal("", new GatewayCommand { Name = "a" }.ArgTemplate());
        var cmd = new GatewayCommand
        {
            Name = "a",
            Args = new[]
            {
                new GatewayCommandArg { Name = "message", Required = true },
                new GatewayCommandArg { Name = "level", Required = false },
            },
        };
        Assert.Equal("<message> [level]", cmd.ArgTemplate());
    }

    [Fact]
    public void OptionCount_CountsStaticChoicesOnFirstArgOnly()
    {
        Assert.Equal(0, new GatewayCommand { Name = "a" }.OptionCount());
        var withChoices = new GatewayCommand
        {
            Name = "a",
            Args = new[]
            {
                new GatewayCommandArg
                {
                    Name = "id",
                    Choices = new[]
                    {
                        new GatewayCommandArgChoice { Value = "fast" },
                        new GatewayCommandArgChoice { Value = "slow" },
                    },
                },
            },
        };
        Assert.Equal(2, withChoices.OptionCount());
        // Dynamic choices are not counted (resolved by the gateway at runtime).
        var dynamic = new GatewayCommand
        {
            Name = "a",
            Args = new[] { new GatewayCommandArg { Name = "id", IsDynamic = true, Choices = new[] { new GatewayCommandArgChoice { Value = "x" } } } },
        };
        Assert.Equal(0, dynamic.OptionCount());
    }

    [Fact]
    public void FirstArgChoices_ReturnsStaticChoicesElseEmpty()
    {
        Assert.Empty(new GatewayCommand { Name = "a" }.FirstArgChoices());
        var cmd = new GatewayCommand
        {
            Name = "a",
            Args = new[]
            {
                new GatewayCommandArg { Name = "id", Choices = new[] { new GatewayCommandArgChoice { Value = "fast", Label = "Fast" } } },
            },
        };
        Assert.Single(cmd.FirstArgChoices());
        Assert.Equal("fast", cmd.FirstArgChoices()[0].Value);
    }

    [Fact]
    public void BuildArgInsertionText_BuildsSlashNameValue()
    {
        var cmd = new GatewayCommand { Name = "model", NativeName = "/model" };
        Assert.Equal("/model gpt-5", cmd.BuildArgInsertionText("gpt-5"));
        Assert.Equal("/model gpt-5", cmd.BuildArgInsertionText("  gpt-5 "));
    }

    [Theory]
    [InlineData("model", true)]
    [InlineData("/model", true)]
    [InlineData("MODEL", true)]
    [InlineData("tldr", true)]   // text alias
    [InlineData("nope", false)]
    [InlineData("", false)]
    public void MatchesName_MatchesNativeNameAndAliases(string probe, bool expected)
    {
        var cmd = new GatewayCommand { Name = "model", NativeName = "/model", TextAliases = new[] { "/tldr" } };
        Assert.Equal(expected, cmd.MatchesName(probe));
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
    public void GroupBy_WithExplicitCategoryOrder_OrdersBySequenceThenAlphabetical()
    {
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "verbose", NativeName = "/verbose", Category = "options" },
            new GatewayCommand { Name = "stop", NativeName = "/stop", Category = "session" },
            new GatewayCommand { Name = "send", NativeName = "/send", Category = "management" },
            new GatewayCommand { Name = "zeta", NativeName = "/zeta", Category = "zzz-unknown" },
        });

        // Listed categories follow the supplied order; the unlisted one sorts last.
        var order = new[] { "session", "options", "management" };
        var categories = view.GroupByCategory(null, order).Select(g => g.Category).ToList();

        Assert.Equal(new[] { "session", "options", "management", "zzz-unknown" }, categories);
    }

    [Fact]
    public void GroupByCategory_NullCategoryOrder_StillOrdersAlphabetically()
    {
        var view = new ChatCommandCatalogView(Sample());
        var categories = view.GroupByCategory(null, null).Select(g => g.Category).ToList();
        Assert.Equal(categories.OrderBy(c => c, System.StringComparer.OrdinalIgnoreCase).ToList(), categories);
    }

    // ── CommandCategories: Mac 4-bucket mapping ──

    [Fact]
    public void GroupBy_MacBucket_GroupsRunOptionsUnderModel()
    {
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "verbose", NativeName = "/verbose", Category = "options" },
            new GatewayCommand { Name = "exec", NativeName = "/exec", Category = "options" },
            new GatewayCommand { Name = "usage", NativeName = "/usage", Category = "options" },   // override → tools
            new GatewayCommand { Name = "stop", NativeName = "/stop", Category = "session" },
            new GatewayCommand { Name = "send", NativeName = "/send", Category = "management" },   // → tools
            new GatewayCommand { Name = "steer", NativeName = "/steer", Category = "tools" },      // override → agents
        });

        var groups = view.GroupBy(CommandCategories.Bucket, null, CommandCategories.DisplayOrder);
        var keys = groups.Select(g => g.Category).ToList();

        // Mac order: session, model, tools, agents.
        Assert.Equal(new[] { "session", "model", "tools", "agents" }, keys);
        Assert.Equal(new[] { "/exec", "/verbose" }, groups.Single(g => g.Category == "model").Commands.Select(c => c.DisplayName()).ToArray());
        Assert.Contains(groups.Single(g => g.Category == "tools").Commands, c => c.Name == "usage");
        Assert.Contains(groups.Single(g => g.Category == "tools").Commands, c => c.Name == "send");
        Assert.Contains(groups.Single(g => g.Category == "agents").Commands, c => c.Name == "steer");
    }

    [Fact]
    public void GroupBy_PreservesSearchRelevanceWithinGroup_NotAlphabetical()
    {
        // Both commands bucket under "model" (raw category options). For query
        // "model", "model" is an exact match while "amodel" is only a contains
        // match — relevance must keep "model" first even though "amodel" sorts
        // first alphabetically. Guards against re-introducing a within-group sort.
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "amodel", NativeName = "/amodel", Category = "options" },
            new GatewayCommand { Name = "model", NativeName = "/model", Category = "options" },
        });

        var model = view.GroupBy(CommandCategories.Bucket, "model", CommandCategories.DisplayOrder)
            .Single(g => g.Category == "model");

        Assert.Equal(new[] { "/model", "/amodel" }, model.Commands.Select(c => c.DisplayName()).ToArray());
    }

    [Fact]
    public void GroupForPalette_DefaultSelection_PinsGlobalBestAcrossBuckets()
    {
        // "exec" is an exact match in the Model bucket; "reexec" is only a contains
        // match in the earlier Session bucket. Display grouping renders Session
        // first, so the weak match is row 0 — but DefaultSelectionIndex must point
        // at the global best (exec), so Enter/Tab still inserts the strongest match
        // rather than the first visible row. Regression guard for grouped selection.
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "reexec", NativeName = "/reexec", Category = "session" },
            new GatewayCommand { Name = "exec", NativeName = "/exec", Category = "options" },
        });

        var palette = view.GroupForPalette(CommandCategories.Bucket, "exec", CommandCategories.DisplayOrder);

        // Flattened (render/nav) order follows bucket order: weak Session match first.
        Assert.Equal(new[] { "reexec", "exec" }, palette.Flattened.Select(c => c.Name).ToArray());
        // But the default keyboard selection is the global best match, not row 0.
        Assert.Equal(1, palette.DefaultSelectionIndex);
        Assert.Equal("exec", palette.Flattened[palette.DefaultSelectionIndex].Name);
    }

    [Fact]
    public void GroupForPalette_DefaultSelection_IsZeroWhenNoMatches()
    {
        var view = new ChatCommandCatalogView(new[]
        {
            new GatewayCommand { Name = "exec", NativeName = "/exec", Category = "options" },
        });
        var palette = view.GroupForPalette(CommandCategories.Bucket, "zzznope", CommandCategories.DisplayOrder);
        Assert.Empty(palette.Flattened);
        Assert.Equal(0, palette.DefaultSelectionIndex);
    }

    [Fact]
    public void GroupForPalette_FlattenedMatchesGroupedOrder()
    {
        // Flattened must equal the groups concatenated in order, so the composer's
        // running render index stays aligned with the keyboard-navigation list.
        var view = new ChatCommandCatalogView(Sample());
        var palette = view.GroupForPalette(CommandCategories.Bucket, null, CommandCategories.DisplayOrder);
        Assert.Equal(
            palette.Groups.SelectMany(g => g.Commands).Select(c => c.Name).ToArray(),
            palette.Flattened.Select(c => c.Name).ToArray());
    }

    [Theory]
    [InlineData("verbose", "options", "model")]   // run option → Model
    [InlineData("exec", "options", "model")]
    [InlineData("trace", "options", "model")]     // no override; raw "options" → model
    [InlineData("usage", "options", "tools")]     // override wins over raw category
    [InlineData("think", "options", "model")]
    [InlineData("stop", "session", "session")]
    [InlineData("send", "management", "tools")]    // raw "management" → tools
    [InlineData("steer", "tools", "agents")]       // override → agents
    [InlineData("mystery", "weirdcat", "tools")]   // unknown name + unknown category → tools
    public void Bucket_MapsCommandToMacDisplayBucket(string name, string category, string expected)
    {
        var cmd = new GatewayCommand { Name = name, NativeName = "/" + name, Category = category };
        Assert.Equal(expected, CommandCategories.Bucket(cmd));
    }

    [Theory]
    [InlineData("session", "Session")]
    [InlineData("model", "Model")]
    [InlineData("tools", "Tools")]
    [InlineData("agents", "Agents")]
    [InlineData("custom", "Custom")]   // unknown → Title-cased
    [InlineData("", "Other")]
    [InlineData(null, "Other")]
    public void CommandCategories_Label_MapsKnownAndTitleCasesUnknown(string? bucket, string expected)
    {
        Assert.Equal(expected, CommandCategories.Label(bucket));
    }

    [Fact]
    public void CommandCategories_DisplayOrder_MatchesMac()
    {
        Assert.Equal(new[] { "session", "model", "tools", "agents" }, CommandCategories.DisplayOrder.ToArray());
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
