using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Presentation;

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
    /// <paramref name="start"/>. Returns the rows that were removed so callers can repair focus.
    /// </summary>
    public IReadOnlyList<object> Reconcile(
        IList<object> items, int start, IReadOnlyList<string> desiredTags, System.Func<string, object> create)
    {
        var removed = new List<object>();
        foreach (var tag in _rows.Keys.Where(tag => !desiredTags.Contains(tag, System.StringComparer.Ordinal)).ToArray())
        {
            removed.Add(_rows[tag]);
            items.Remove(_rows[tag]);
            _rows.Remove(tag);
        }

        var desired = new List<object>(desiredTags.Count);
        foreach (var tag in desiredTags)
        {
            if (!_rows.TryGetValue(tag, out var row))
                _rows[tag] = row = create(tag);
            desired.Add(row);
        }
        WorkspaceItemsSync.Arrange(items, start, desired);
        return removed;
    }
}
