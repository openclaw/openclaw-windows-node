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

        var first = rows.Sync(items, 0, ["main", "ops"], Create).ToArray();
        var second = rows.Sync(items, 0, ["main", "ops"], Create).ToArray();

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
        var original = rows.Sync(items, 0, ["main", "ops"], key => new Row(key))[1];

        rows.Sync(items, 0, ["main"], key => new Row(key));
        Assert.DoesNotContain(original, items);

        var restored = rows.Sync(items, 0, ["ops", "main"], key => new Row(key));
        Assert.Same(original, restored[0]);
        Assert.Equal(["ops", "main"], items.Cast<Row>().Select(row => row.Key));
    }

    [Fact]
    public void DuplicateKeys_ShowOneRow()
    {
        var rows = new KeyedMenuRows();
        var items = new List<object>();

        rows.Sync(items, 0, ["main", "main"], key => new Row(key));

        Assert.Single(items);
    }

    [Fact]
    public void SharedMenu_KeepsItemsItDoesNotOwnAndPlacesRowsAtStart()
    {
        var rows = new KeyedMenuRows();
        var home = new Row("home");
        var header = new Row("header");
        var items = new List<object> { home, header };

        rows.Sync(items, 2, ["a", "b"], key => new Row(key));
        rows.Sync(items, 2, ["b"], key => new Row(key));

        Assert.Equal(["home", "header", "b"], items.Cast<Row>().Select(row => row.Key));
        Assert.Same(home, items[0]);
        Assert.Same(header, items[1]);
    }

    [Fact]
    public void AdoptedRow_IsReusedInsteadOfReplaced()
    {
        var rows = new KeyedMenuRows();
        var declared = new Row("main");
        var items = new List<object> { declared };
        rows.Adopt("main", declared);

        var shown = rows.Sync(items, 0, ["main", "ops"], key => new Row(key));

        Assert.Same(declared, shown[0]);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Pooling_RebindsARowRetiredByAnEarlierSyncForANewKey()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);
        var items = new List<object>();
        var created = 0;
        var rebound = new List<(string Key, object Row)>();
        Row Create(string key) { created++; return new Row(key); }
        void Reuse(string key, object row) => rebound.Add((key, row));

        var retiredRow = rows.Sync(items, 0, ["a", "b"], Create, Reuse)[1];
        rows.Sync(items, 0, ["a"], Create, Reuse);
        var shown = rows.Sync(items, 0, ["a", "c"], Create, Reuse);

        Assert.Equal(2, created);
        Assert.Same(retiredRow, shown[1]);
        Assert.Equal([("c", retiredRow)], rebound);
    }

    [Fact]
    public void Pooling_DoesNotRebindARowRetiredInTheSameSync()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);
        var items = new List<object>();
        var first = rows.Sync(items, 0, ["a"], key => new Row(key), (_, _) => { })[0];

        var shown = rows.Sync(items, 0, ["b"], key => new Row(key), (_, _) => Assert.Fail("rebound a row still in the menu"));

        Assert.NotSame(first, shown[0]);
        Assert.Equal([shown[0]], items);
    }

    [Fact]
    public void Pooling_KeepsRowCountNearThePeakVisibleCountAcrossManyKeys()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);
        var items = new List<object>();
        var created = 0;

        for (var i = 0; i < 100; i++)
            rows.Sync(items, 0, [$"s{i}", $"s{i + 1}"], key => { created++; return new Row(key); }, (_, _) => { });

        Assert.True(created <= 4, $"created {created} rows for two visible keys");
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Pooling_FailedReuseLeavesEveryRowOwnedSoTheNextSyncCleansUp()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);
        var items = new List<object>();
        rows.Sync(items, 0, ["a", "b"], key => new Row(key), (_, _) => { });
        rows.Sync(items, 0, ["a"], key => new Row(key), (_, _) => { });

        Assert.Throws<InvalidOperationException>(() =>
            rows.Sync(items, 0, ["c"], key => new Row(key), (_, _) => throw new InvalidOperationException()));
        var shown = rows.Sync(items, 0, ["d"], key => new Row(key), (_, _) => { });

        Assert.Equal(shown, items);
    }

    [Fact]
    public void Pooling_FailedRebindIsRetriedForTheSameKey()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);
        var items = new List<object>();
        rows.Sync(items, 0, ["a", "b"], key => new Row(key), (_, _) => { });
        rows.Sync(items, 0, ["a"], key => new Row(key), (_, _) => { });
        Assert.Throws<InvalidOperationException>(() =>
            rows.Sync(items, 0, ["a", "c"], key => new Row(key), (_, _) => throw new InvalidOperationException()));

        var rebound = new List<string>();
        rows.Sync(items, 0, ["a", "c"], key => new Row(key), (key, _) => rebound.Add(key));

        Assert.Equal(["c"], rebound);
    }

    [Fact]
    public void Pooling_RequiresAReuseCallback()
    {
        var rows = new KeyedMenuRows(poolRetiredRows: true);

        Assert.Throws<ArgumentNullException>(() => rows.Sync(new List<object>(), 0, ["a"], key => new Row(key)));
    }
}
