using System.Collections.ObjectModel;
using OpenClawTray.Chat.Controls;

namespace OpenClaw.Tray.Tests;

public sealed class StableRowCollectionTests
{
    [Fact]
    public void Sync_ReordersWithoutReplacingCollection()
    {
        var rows = new ObservableCollection<Row>
        {
            new("a"),
            new("b"),
            new("c"),
        };

        StableRowCollection.Sync(rows, new[] { new Row("c"), new Row("a"), new Row("d") }, row => row.Key);

        Assert.Equal(new[] { "c", "a", "d" }, rows.Select(row => row.Key).ToArray());
    }

    [Fact]
    public void Sync_RemovesMissingKeys()
    {
        var rows = new ObservableCollection<Row>
        {
            new("a"),
            new("b"),
            new("c"),
        };

        StableRowCollection.Sync(rows, new[] { new Row("b") }, row => row.Key);

        Assert.Equal(new[] { "b" }, rows.Select(row => row.Key).ToArray());
    }

    private sealed record Row(string Key);
}
