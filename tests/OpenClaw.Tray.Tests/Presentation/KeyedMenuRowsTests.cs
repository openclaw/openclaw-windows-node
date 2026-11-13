using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class KeyedMenuRowsTests
{
    private sealed class Row(string key)
    {
        public string Key { get; } = key;
    }

    [Fact]
    public void RepeatedSync_ReusesEveryRowInsteadOfReplacingIt()
    {
        var rows = new KeyedMenuRows();
        var items = new List<object>();
        var created = 0;
        Row Create(string key) { created++; return new Row(key); }

        var first = rows.Sync(items, ["main", "ops"], Create).ToArray();
        var second = rows.Sync(items, ["main", "ops"], Create).ToArray();

        Assert.Equal(2, created);
        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
        Assert.Equal(first, items);
    }

    [Fact]
    public void RemovedKey_LeavesTheMenuAndGetsItsOriginalRowWhenItReturns()
    {
        var rows = new KeyedMenuRows();
        var items = new List<object>();
        var original = rows.Sync(items, ["main", "ops"], key => new Row(key))[1];

        rows.Sync(items, ["main"], key => new Row(key));
        Assert.DoesNotContain(original, items);

        var restored = rows.Sync(items, ["ops", "main"], key => new Row(key));
        Assert.Same(original, restored[0]);
        Assert.Equal(["ops", "main"], items.Cast<Row>().Select(row => row.Key));
    }

    [Fact]
    public void DuplicateKeys_ShowOneRow()
    {
        var rows = new KeyedMenuRows();
        var items = new List<object>();

        rows.Sync(items, ["main", "main"], key => new Row(key));

        Assert.Single(items);
    }
}
