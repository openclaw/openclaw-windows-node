using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PinnedPageRowsTests
{
    private sealed record Row(string Tag);

    private static (PinnedPageRows Rows, List<object> Items, Header Header) Create()
    {
        var header = new Header();
        return (new PinnedPageRows(), [new Row("home"), header, new Row("sessions-sentinel")], header);
    }

    private sealed class Header;

    private static IReadOnlyList<object> Sync(PinnedPageRows rows, List<object> items, params string[] tags) =>
        rows.Reconcile(items, 2, tags, tag => new Row(tag));

    private static string[] PinTags(List<object> items) =>
        items.Skip(2).Take(items.Count - 3).Cast<Row>().Select(row => row.Tag).ToArray();

    [Fact]
    public void UnchangedList_KeepsEveryRowInstanceAndRemovesNothing()
    {
        var (rows, items, _) = Create();
        Sync(rows, items, "channels", "usage");
        var before = items.Skip(2).Take(2).ToArray();

        var removed = Sync(rows, items, "channels", "usage");

        Assert.Empty(removed);
        Assert.True(before.SequenceEqual(items.Skip(2).Take(2), ObjectReferenceComparer.Instance));
    }

    [Fact]
    public void AddingAPin_KeepsExistingRowsAndInsertsOnlyTheNewOne()
    {
        var (rows, items, _) = Create();
        Sync(rows, items, "channels");
        var channels = items[2];

        Sync(rows, items, "channels", "usage");

        Assert.Same(channels, items[2]);
        Assert.Equal(["channels", "usage"], PinTags(items));
    }

    [Fact]
    public void RemovingAPin_ReturnsOnlyThatRowAndKeepsTheRest()
    {
        var (rows, items, _) = Create();
        Sync(rows, items, "channels", "usage", "skills");
        var channels = items[2];
        var usage = items[3];
        var skills = items[4];

        var removed = Sync(rows, items, "channels", "skills");

        Assert.Same(usage, Assert.Single(removed));
        Assert.Same(channels, items[2]);
        Assert.Same(skills, items[3]);
    }

    [Fact]
    public void Reordering_MovesExistingRowsWithoutRecreatingThem()
    {
        var (rows, items, _) = Create();
        Sync(rows, items, "channels", "usage");
        var channels = items[2];
        var usage = items[3];

        var removed = Sync(rows, items, "usage", "channels");

        Assert.Empty(removed);
        Assert.Same(usage, items[2]);
        Assert.Same(channels, items[3]);
    }

    [Fact]
    public void Reconcile_PreservesLeadingHeaderAndTrailingSentinel()
    {
        var (rows, items, header) = Create();
        var home = items[0];
        var sentinel = items[^1];

        Sync(rows, items, "channels");
        Sync(rows, items);

        Assert.Same(home, items[0]);
        Assert.Same(header, items[1]);
        Assert.Same(sentinel, items[^1]);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void RemovedRow_IsReportedSoFocusOnItCanFallBack()
    {
        var (rows, items, _) = Create();
        Sync(rows, items, "channels");
        var focused = items[2];

        var removed = Sync(rows, items);

        Assert.Contains(focused, removed);
        Assert.DoesNotContain(focused, items);
    }

    private sealed class ObjectReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ObjectReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
