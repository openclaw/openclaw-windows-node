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
//   var shown = rows.Sync(parent.MenuItems, ids, id => new NavigationViewItem { Tag = id });
//   // shown[i] is the row for ids[i]; update its content in place.
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Presentation;

internal sealed class KeyedMenuRows
{
    // Rows for keys no longer shown stay referenced so WinUI never calls into a collected item, and a
    // returning key gets its original row back. Growth is bounded by the distinct keys ever shown.
    private readonly Dictionary<string, object> _rows = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes <paramref name="items"/> contain exactly one row per distinct key in
    /// <paramref name="keys"/>, in that order. Existing rows are reused; <paramref name="create"/>
    /// runs only for a key seen for the first time. Returns the rows in key order.
    /// </summary>
    public IReadOnlyList<object> Sync(IList<object> items, IReadOnlyList<string> keys, Func<string, object> create)
    {
        var desired = new List<object>(keys.Count);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!_rows.TryGetValue(key, out var row))
                _rows[key] = row = create(key);
            desired.Add(row);
        }
        foreach (var stale in items.Where(item => !desired.Any(row => ReferenceEquals(row, item))).ToArray())
            items.Remove(stale);
        WorkspaceItemsSync.Arrange(items, 0, desired);
        return desired;
    }
}
