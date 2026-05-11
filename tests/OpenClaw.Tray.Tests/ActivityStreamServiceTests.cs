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
}
