using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Presentation;

/// <summary>Rows removed by a reconcile and whether the row owning keyboard focus was among them.</summary>
internal sealed record PinnedRowsChange(IReadOnlyList<object> Removed, bool FocusedRowRemoved);

/// <summary>
/// Keeps one sidebar row per visible pinned page, keyed by tag. Rows for unchanged pins are reused
/// in place so keyboard/Narrator focus and any focus captured on them survive unrelated refreshes.
/// </summary>
internal sealed class PinnedPageRows
{
    private readonly Dictionary<string, object> _rows = new(System.StringComparer.Ordinal);

    public IReadOnlyCollection<object> Rows => _rows.Values;

    /// <summary>
    /// Reconciles rows to <paramref name="desiredTags"/> inside <paramref name="items"/> starting at
    /// <paramref name="start"/>. <paramref name="focusedElement"/> is read before any row is detached,
    /// because the framework may move focus synchronously on removal; <paramref name="owns"/> decides
    /// whether a row owns that element (the row itself or a descendant).
    /// </summary>
    public PinnedRowsChange Reconcile(
        IList<object> items, int start, IReadOnlyList<string> desiredTags, System.Func<string, object> create,
        System.Func<object?>? focusedElement = null, System.Func<object, object, bool>? owns = null)
    {
        var focused = focusedElement?.Invoke();
        owns ??= static (row, element) => ReferenceEquals(row, element);

        var removed = new List<object>();
        foreach (var tag in _rows.Keys.Where(tag => !desiredTags.Contains(tag, System.StringComparer.Ordinal)).ToArray())
        {
            removed.Add(_rows[tag]);
            _rows.Remove(tag);
        }
        var focusedRowRemoved = focused is not null && removed.Any(row => owns(row, focused));
        foreach (var row in removed) items.Remove(row);

        var desired = new List<object>(desiredTags.Count);
        foreach (var tag in desiredTags)
        {
            if (!_rows.TryGetValue(tag, out var row))
                _rows[tag] = row = create(tag);
            desired.Add(row);
        }
        WorkspaceItemsSync.Arrange(items, start, desired);
        return new PinnedRowsChange(removed, focusedRowRemoved);
    }
}
