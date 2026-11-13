using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Presentation;

/// <summary>Rows removed by a reconcile and whether the row owning keyboard focus was among them.</summary>
internal sealed record PinnedRowsChange(IReadOnlyList<object> Removed, bool FocusedRowRemoved);

/// <summary>
/// Keeps one sidebar row per visible pinned page, keyed by tag. Rows for unchanged pins are reused
/// in place so keyboard/Narrator focus and any focus captured on them survive unrelated refreshes.
/// Rows for removed pins are never released (see <see cref="KeyedMenuRows"/>); re-pinning a page
/// shows its original row again.
/// </summary>
internal sealed class PinnedPageRows
{
    private readonly KeyedMenuRows _cache = new();
    private readonly Dictionary<string, object> _rows = new(System.StringComparer.Ordinal);

    /// <summary>Rows for the currently pinned pages.</summary>
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

        var removed = _rows.Where(entry => !desiredTags.Contains(entry.Key, System.StringComparer.Ordinal))
            .Select(entry => entry.Value).ToList();
        var focusedRowRemoved = focused is not null && removed.Any(row => owns(row, focused));

        var shown = _cache.Sync(items, start, desiredTags, create);
        _rows.Clear();
        foreach (var (tag, row) in desiredTags.Distinct(System.StringComparer.Ordinal).Zip(shown))
            _rows[tag] = row;
        return new PinnedRowsChange(removed, focusedRowRemoved);
    }
}
