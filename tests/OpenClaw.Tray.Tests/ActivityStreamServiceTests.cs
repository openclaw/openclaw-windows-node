using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

[CollectionDefinition(ActivityStreamServiceCollection.Name, DisableParallelization = true)]
public sealed class ActivityStreamServiceCollection
{
    public const string Name = "ActivityStreamService";
}

[Collection(ActivityStreamServiceCollection.Name)]
public class ActivityStreamServiceTests : IDisposable
{
    public ActivityStreamServiceTests()
    {
        ActivityStreamService.Clear();
    }

    public void Dispose()
    {
        ActivityStreamService.Clear();
    }

    [Fact]
    public void Add_TrimsToFourHundredMostRecentItems()
    {
        for (var i = 0; i < ActivityStreamService.MaxStoredItems + 5; i++)
        {
            ActivityStreamService.Add("test", $"item-{i:000}");
        }

        var items = ActivityStreamService.GetItems();

        Assert.Equal(ActivityStreamService.MaxStoredItems, items.Count);
        Assert.Equal("item-404", items[0].Title);
        Assert.Equal("item-005", items[^1].Title);
        Assert.DoesNotContain(items, item => item.Title == "item-004");
    }

    [Fact]
    public void BuildSupportBundle_DefaultIncludesStoredActivityWindow()
    {
        for (var i = 0; i < ActivityStreamService.MaxStoredItems; i++)
        {
            ActivityStreamService.Add("test", $"bundle-item-{i:000}");
        }

        var bundle = ActivityStreamService.BuildSupportBundle();

        Assert.Contains($"Items: {ActivityStreamService.MaxStoredItems}", bundle);
        Assert.Contains("bundle-item-399", bundle);
        Assert.Contains("bundle-item-000", bundle);
    }

    // ── Add: field defaults ──

    [Fact]
    public void Add_WithAllOptionalFields_StoresFieldValues()
    {
        ActivityStreamService.Add(
            "sessions",
            "My Session",
            details: "some detail",
            icon: "🔥",
            dashboardPath: "/dashboard/abc",
            sessionKey: "sk-123",
            nodeId: "node-xyz");

        var items = ActivityStreamService.GetItems();

        Assert.Single(items);
        var item = items[0];
        Assert.Equal("sessions", item.Category);
        Assert.Equal("My Session", item.Title);
        Assert.Equal("some detail", item.Details);
        Assert.Equal("🔥", item.Icon);
        Assert.Equal("/dashboard/abc", item.DashboardPath);
        Assert.Equal("sk-123", item.SessionKey);
        Assert.Equal("node-xyz", item.NodeId);
    }

    [Fact]
    public void Add_WithNullCategory_DefaultsToGeneral()
    {
        ActivityStreamService.Add(null!, "Title");

        var items = ActivityStreamService.GetItems();

        Assert.Equal("general", items[0].Category);
    }

    [Fact]
    public void Add_WithWhitespaceCategory_DefaultsToGeneral()
    {
        ActivityStreamService.Add("   ", "Title");

        var items = ActivityStreamService.GetItems();

        Assert.Equal("general", items[0].Category);
    }

    [Fact]
    public void Add_WithEmptyTitle_DoesNotAddItem()
    {
        ActivityStreamService.Add("test", "");

        var items = ActivityStreamService.GetItems();

        Assert.Empty(items);
    }

    [Fact]
    public void Add_WithWhitespaceTitle_DoesNotAddItem()
    {
        ActivityStreamService.Add("test", "   ");

        var items = ActivityStreamService.GetItems();

        Assert.Empty(items);
    }

    [Fact]
    public void Add_StoresItemsNewestFirst()
    {
        ActivityStreamService.Add("test", "first");
        ActivityStreamService.Add("test", "second");

        var items = ActivityStreamService.GetItems();

        Assert.Equal("second", items[0].Title);
        Assert.Equal("first", items[1].Title);
    }

    // ── GetItems: filtering and limiting ──

    [Fact]
    public void GetItems_WithMaxItemsLessThanStored_ReturnsOnlyNewest()
    {
        ActivityStreamService.Add("test", "old");
        ActivityStreamService.Add("test", "newer");
        ActivityStreamService.Add("test", "newest");

        var items = ActivityStreamService.GetItems(maxItems: 2);

        Assert.Equal(2, items.Count);
        Assert.Equal("newest", items[0].Title);
        Assert.Equal("newer", items[1].Title);
    }

    [Fact]
    public void GetItems_WithMaxItemsZero_ReturnsEmpty()
    {
        ActivityStreamService.Add("test", "item");

        var items = ActivityStreamService.GetItems(maxItems: 0);

        Assert.Empty(items);
    }

    [Fact]
    public void GetItems_WithCategoryFilter_ReturnsOnlyMatchingCategory()
    {
        ActivityStreamService.Add("sessions", "session item");
        ActivityStreamService.Add("nodes", "node item");
        ActivityStreamService.Add("sessions", "another session");

        var items = ActivityStreamService.GetItems(category: "sessions");

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("sessions", item.Category));
    }

    [Fact]
    public void GetItems_WithCategoryAll_ReturnsEverything()
    {
        ActivityStreamService.Add("sessions", "s");
        ActivityStreamService.Add("nodes", "n");

        var items = ActivityStreamService.GetItems(category: "all");

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void GetItems_CategoryFilter_IsCaseInsensitive()
    {
        ActivityStreamService.Add("Sessions", "uppercase S");

        var items = ActivityStreamService.GetItems(category: "sessions");

        Assert.Single(items);
    }

    [Fact]
    public void GetItems_CategoryFilter_MatchesDotPrefixSubcategories()
    {
        ActivityStreamService.Add("sessions.chat", "chat session");
        ActivityStreamService.Add("sessions.mcp", "mcp session");
        ActivityStreamService.Add("nodes", "unrelated");

        var items = ActivityStreamService.GetItems(category: "sessions");

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.Category == "nodes");
    }

    [Fact]
    public void GetItems_CategoryFilter_DoesNotMatchPartialPrefix()
    {
        ActivityStreamService.Add("sessionsExtra", "should not match");
        ActivityStreamService.Add("sessions.sub", "should match");

        var items = ActivityStreamService.GetItems(category: "sessions");

        Assert.Single(items);
        Assert.Equal("sessions.sub", items[0].Category);
    }

    // ── Clear ──

    [Fact]
    public void Clear_RemovesAllItems()
    {
        ActivityStreamService.Add("test", "item1");
        ActivityStreamService.Add("test", "item2");

        ActivityStreamService.Clear();

        Assert.Empty(ActivityStreamService.GetItems());
    }

    [Fact]
    public void Clear_RaisesUpdatedEvent()
    {
        var raised = false;
        EventHandler handler = (_, _) => raised = true;
        ActivityStreamService.Updated += handler;
        try
        {
            ActivityStreamService.Clear();
            Assert.True(raised);
        }
        finally
        {
            ActivityStreamService.Updated -= handler;
        }
    }

    [Fact]
    public void Add_RaisesUpdatedEvent()
    {
        var raised = false;
        EventHandler handler = (_, _) => raised = true;
        ActivityStreamService.Updated += handler;
        try
        {
            ActivityStreamService.Add("test", "item");
            Assert.True(raised);
        }
        finally
        {
            ActivityStreamService.Updated -= handler;
        }
    }

    // ── BuildSupportBundle ──

    [Fact]
    public void BuildSupportBundle_WithMaxItemsParam_LimitsOutput()
    {
        for (var i = 0; i < 10; i++)
            ActivityStreamService.Add("test", $"item-{i}");

        var bundle = ActivityStreamService.BuildSupportBundle(maxItems: 3);

        Assert.Contains("Items: 3", bundle);
    }

    [Fact]
    public void BuildSupportBundle_IncludesSessionKeyAndNodeIdWhenPresent()
    {
        ActivityStreamService.Add("test", "title", sessionKey: "sk-abc", nodeId: "nd-xyz");

        var bundle = ActivityStreamService.BuildSupportBundle();

        Assert.Contains("session=sk-abc", bundle);
        Assert.Contains("node=nd-xyz", bundle);
    }

    [Fact]
    public void BuildSupportBundle_TruncatesLongNodeId()
    {
        var longNodeId = new string('a', 32);
        ActivityStreamService.Add("test", "title", nodeId: longNodeId);

        var bundle = ActivityStreamService.BuildSupportBundle();

        Assert.Contains("node=" + new string('a', 16) + "...", bundle);
        Assert.DoesNotContain("node=" + longNodeId, bundle);
    }

    [Fact]
    public void BuildSupportBundle_OmitsSessionAndNodeFieldsWhenAbsent()
    {
        ActivityStreamService.Add("test", "plain item");

        var bundle = ActivityStreamService.BuildSupportBundle();

        Assert.DoesNotContain("session=", bundle);
        Assert.DoesNotContain("node=", bundle);
    }
}
