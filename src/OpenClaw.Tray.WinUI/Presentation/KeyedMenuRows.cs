// Keeps one NavigationView menu row per key and never releases a row once created.
//
// WinUI's NavigationViewItem registers SplitView property callbacks that capture the raw item. After
// a row is removed from a menu and reference tracking collects it, the next pane change (for example
// NavigationView.IsPaneVisible = false) calls into the collected item and fails with E_FAIL or an
// access violation (microsoft/microsoft-ui-xaml#10757, fixed upstream by #12235 but not in the
// Windows App SDK this app ships). Rebuilding a menu by clearing and recreating its rows triggers it.
//
// Usage:
//   var rows = new KeyedMenuRows();
//   rows.Adopt("main", rowDeclaredInXaml);
//   var shown = rows.Sync(parent.MenuItems, start: 0, ids, id => new NavigationViewItem { Tag = id });
//   // shown[i] is the row for ids[i]; update its content in place.
//
//   // For open-ended keys such as sessions, pool rows so memory stays near the most rows shown at once:
//   var sessions = new KeyedMenuRows(poolRetiredRows: true);
//   sessions.Sync(menu, start, keys, Create, reuse: (key, row) => Rebind(row, key));
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Presentation;

internal sealed class KeyedMenuRows(bool poolRetiredRows = false)
{
    // Every row this instance created or adopted stays referenced so WinUI never calls into a collected
    // item. Without pooling, a retired row stays bound to its key and comes back if the key returns;
    // growth is bounded by the distinct keys ever shown. With pooling, rows retired by an earlier Sync
    // are rebound to new keys, so the total is bounded by the most rows shown across two consecutive syncs.
    private readonly Dictionary<string, object> _rows = new(StringComparer.Ordinal);
    private readonly Stack<object> _pool = new();

    /// <summary>
    /// Takes ownership of a row that already exists, for example one declared in XAML, so a later
    /// <see cref="Sync"/> reuses it for <paramref name="key"/> instead of releasing it.
    /// </summary>
    public void Adopt(string key, object row) => _rows.TryAdd(key, row);

    /// <summary>
    /// Places one row per distinct key in <paramref name="keys"/> at
    /// <paramref name="items"/>[<paramref name="start"/>..], in key order, and removes this
    /// instance's rows for other keys. Items it does not own are left alone. A key keeps its row while
    /// it stays shown. A new key takes a pooled row through <paramref name="reuse"/> when pooling is on;
    /// otherwise <paramref name="create"/> makes one. Returns the rows in key order.
    /// </summary>
    public IReadOnlyList<object> Sync(
        IList<object> items, int start, IReadOnlyList<string> keys, Func<string, object> create,
        Action<string, object>? reuse = null)
    {
        if (poolRetiredRows && reuse is null)
            throw new ArgumentNullException(nameof(reuse), "A pooling instance needs reuse, or its pool never drains.");
        var distinct = keys.Distinct(StringComparer.Ordinal).ToList();
        // Retired keys keep their rows until the new rows are assigned, so every row stays owned (and
        // removable from the menu) even if create or reuse throws.
        var retiredKeys = poolRetiredRows
            ? _rows.Keys.Except(distinct, StringComparer.Ordinal).ToArray()
            : [];
        var desired = new List<object>(distinct.Count);
        foreach (var key in distinct)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                // Only rows retired by an earlier Sync are in the pool, so a reused row is never in the menu.
                if (reuse is not null && _pool.TryPop(out var pooled))
                {
                    _rows[key] = pooled;
                    try { reuse(key, pooled); }
                    catch
                    {
                        // Keep the row owned, but unbound, so the next Sync for this key rebinds it.
                        _rows.Remove(key);
                        _pool.Push(pooled);
                        throw;
                    }
                    row = pooled;
                }
                else
                {
                    _rows[key] = row = create(key);
                }
            }
            desired.Add(row);
        }
        foreach (var key in retiredKeys)
        {
            _pool.Push(_rows[key]);
            _rows.Remove(key);
        }
        var owned = new HashSet<object>(_rows.Values.Concat(_pool), ReferenceEqualityComparer.Instance);
        var kept = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        foreach (var stale in items.Where(item => owned.Contains(item) && !kept.Contains(item)).ToArray())
            items.Remove(stale);
        WorkspaceItemsSync.Arrange(items, start, desired);
        return desired;
    }
}
