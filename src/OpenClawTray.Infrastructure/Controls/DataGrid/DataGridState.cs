using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Controls.Validation;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Headless state machine for the DataGrid. Manages sort state, selection,
/// column order/sizing/visibility, and editing state. Pure logic, no UI
/// dependencies — fully testable without rendering.
/// </summary>
public class DataGridState<T>
{
    private readonly IDataSource<T> _source;
    private readonly SelectionMode _selectionMode;
    private readonly int _blockSize;

    // ── Sort state ────────────────────────────────────────────────

    private List<SortDescriptor> _sorts = new();
    private List<FilterDescriptor> _filters = new();

    /// <summary>Current sort descriptors (ordered by priority).</summary>
    public IReadOnlyList<SortDescriptor> Sorts => _sorts;

    /// <summary>Current filter descriptors.</summary>
    public IReadOnlyList<FilterDescriptor> Filters => _filters;

    // ── Selection state ──────────────────────────────────────────

    private readonly HashSet<RowKey> _selectedKeys = new();
    private int _selectionVersion;

    /// <summary>Currently selected row keys.</summary>
    public IReadOnlySet<RowKey> SelectedKeys => _selectedKeys;

    /// <summary>Monotonically increasing version number, bumped on every selection change.</summary>
    public int SelectionVersion => _selectionVersion;

    /// <summary>Anchor key for shift-click range selection.</summary>
    public RowKey? AnchorKey { get; private set; }

    /// <summary>Currently focused row key (for keyboard navigation).</summary>
    public RowKey? FocusedKey { get; private set; }

    // ── Focus state (cell-level) ─────────────────────────────────

    private int _focusedRowIndex = -1;
    private int _focusedColIndex = -1;

    /// <summary>Row index of the focused cell, or -1 if no focus.</summary>
    public int FocusedRowIndex => _focusedRowIndex;

    /// <summary>Column index of the focused cell, or -1 if no focus.</summary>
    public int FocusedColIndex => _focusedColIndex;

    // ── Editing state ────────────────────────────────────────────

    private RowKey? _editingRowKey;
    private string? _editingColumnName;
    private object? _editingValue;
    private int _editingVersion;

    // Row-mode editing: pending values for all editable columns in the row.
    private Dictionary<string, object?>? _rowEditValues;
    private bool _isRowEditing;

    // Validation context for the currently editing cell/row.
    private ValidationContext? _editValidation;

    /// <summary>Row key of the cell currently being edited, or null.</summary>
    public RowKey? EditingRowKey => _editingRowKey;

    /// <summary>Column name of the cell currently being edited, or null.</summary>
    public string? EditingColumnName => _editingColumnName;

    /// <summary>The pending (uncommitted) value for the cell being edited.</summary>
    public object? EditingValue => _editingValue;

    /// <summary>Whether a cell is currently in edit mode.</summary>
    public bool IsEditing => _editingRowKey is not null;

    /// <summary>Whether the entire row is in edit mode (vs single cell).</summary>
    public bool IsRowEditing => _isRowEditing;

    /// <summary>Gets the pending row-edit value for a specific column, or null if not in row edit.</summary>
    public object? GetRowEditValue(string columnName)
        => _rowEditValues?.TryGetValue(columnName, out var v) == true ? v : null;

    /// <summary>Monotonically increasing version, bumped on every editing state change.</summary>
    public int EditingVersion => _editingVersion;

    /// <summary>The validation context for the current edit, or null if not editing.</summary>
    public ValidationContext? EditValidation => _editValidation;

    /// <summary>Whether the current edit has validation errors.</summary>
    public bool HasValidationErrors => _editValidation is not null && !_editValidation.IsValid();

    // ── Async commit state ─────────────────────────────────────────

    private readonly Dictionary<RowKey, T> _pendingCommitOriginals = new();
    private readonly HashSet<RowKey> _committingRows = new();
    private readonly Dictionary<RowKey, string> _commitErrors = new();

    /// <summary>Whether a specific row is currently being committed asynchronously.</summary>
    public bool IsCommitting(RowKey key) => _committingRows.Contains(key);

    /// <summary>Gets the commit error for a row, or null if no error.</summary>
    public string? GetCommitError(RowKey key) => _commitErrors.TryGetValue(key, out var err) ? err : null;

    /// <summary>Whether any rows are currently being committed.</summary>
    public bool HasPendingCommits => _committingRows.Count > 0;

    // ── Column state ─────────────────────────────────────────────

    private List<FieldDescriptor> _columns;
    private readonly Dictionary<string, double> _columnWidths = new();
    private readonly HashSet<string> _hiddenColumns = new();

    /// <summary>Current column definitions (in display order), excluding hidden columns.</summary>
    public IReadOnlyList<FieldDescriptor> Columns =>
        _hiddenColumns.Count == 0 ? _columns : _columns.Where(c => !_hiddenColumns.Contains(c.Name)).ToList();

    /// <summary>All column definitions, including hidden ones.</summary>
    public IReadOnlyList<FieldDescriptor> AllColumns => _columns;

    /// <summary>Set of currently hidden column names.</summary>
    public IReadOnlySet<string> HiddenColumns => _hiddenColumns;

    // ── Data state ───────────────────────────────────────────────

    private List<T> _loadedItems = new();
    private string[] _rowKeyCache = Array.Empty<string>();
    private int? _totalCount;
    private bool _isLoading;

    // ── Incremental paging (Phase 4) ─────────────────────────────
    private DataPageCache<T>? _cache;
    private readonly Dictionary<int, T> _mutations = new();
    private readonly Dictionary<int, string> _keyOverrides = new();

    // ── Hook-based paging (Phase 3 migration, feature-flagged) ───
    // When set, data accessors read from this resource instead of the legacy _cache /
    // _loadedItems fields. Populated by DataGridComponent under ReactorFeatureFlags.UseHookBasedPaging.
    // The resource is owned by the UseInfiniteResource hook slot — DataGridState neither
    // disposes it nor drives its fetches directly.
    private InfiniteResource<T>? _hookResource;

    /// <summary>The hook-owned <see cref="InfiniteResource{T}"/> when running under
    /// <c>ReactorFeatureFlags.UseHookBasedPaging</c>, or null in legacy mode.</summary>
    public InfiniteResource<T>? HookResource => _hookResource;

    /// <summary>
    /// Attach (or replace) the hook-owned <see cref="InfiniteResource{T}"/> backing this
    /// grid's data. When attached, the legacy <see cref="DataPageCache{T}"/> path is
    /// bypassed — <see cref="ItemCount"/>, <see cref="GetItemAt"/>, <see cref="GetRowKeyAt"/>,
    /// and <see cref="EnsureRangeLoaded"/> read from the resource.
    /// </summary>
    /// <remarks>
    /// Pass <c>null</c> to detach. Deps-change in the hook creates a new resource; call
    /// this each render so the latest reference is used. Re-attaching the same reference
    /// is a no-op.
    /// </remarks>
    public void SetHookResource(InfiniteResource<T>? resource)
    {
        if (ReferenceEquals(_hookResource, resource)) return;
        _hookResource = resource;
        // Deps change invalidated the old resource and reset the mutation overlay — any
        // carried-over row edits would now point at rows that no longer exist.
        _mutations.Clear();
        _keyOverrides.Clear();
    }

    /// <summary>
    /// Delegate installed by <see cref="Controls.DataGridComponent{T}"/> each render to
    /// route row-commit dispatch through a <c>UseMutation</c> hook. Static helpers in
    /// the component (<c>HandleKeyDown</c>, <c>RenderRow</c>) invoke it instead of
    /// spinning up their own <c>Task.Run</c>. When null — e.g. in headless unit tests —
    /// callers fall back to invoking <c>OnRowChanged</c> themselves.
    /// </summary>
    public Action<RowKey, T, T?>? CommitDispatcher { get; set; }

    /// <summary>
    /// Currently loaded items. In paged mode, returns items from all loaded cache blocks
    /// plus any mutations overlay. Prefer GetItemAt for index-based access.
    /// </summary>
    public IReadOnlyList<T> LoadedItems
    {
        get
        {
            if (_hookResource is not null)
            {
                // Hook-based paging: flatten the resource's sparse Items into loaded rows.
                var items = _hookResource.Items;
                var result = new List<T>();
                for (int i = 0; i < items.Count; i++)
                {
                    if (_mutations.TryGetValue(i, out var mutated))
                    {
                        result.Add(mutated);
                        continue;
                    }
                    var it = items[i];
                    if (it is not null) result.Add(it);
                }
                return result;
            }

            if (_cache is null) return _loadedItems;

            // Materialize items from loaded cache blocks + mutations.
            var total = _cache.TotalCount ?? 0;
            var blockSize = _cache.BlockSize;
            var cacheResult = new List<T>();
            var blockCount = (total + blockSize - 1) / blockSize;
            for (int b = 0; b < blockCount; b++)
            {
                if (!_cache.IsLoaded(b * blockSize)) continue;
                var block = _cache.GetBlock(b);
                if (block.Status != BlockStatus.Loaded) continue;
                for (int i = 0; i < block.Items.Count; i++)
                {
                    var rowIndex = b * blockSize + i;
                    if (_mutations.TryGetValue(rowIndex, out var mutated))
                        cacheResult.Add(mutated);
                    else
                        cacheResult.Add(block.Items[i]);
                }
            }
            return cacheResult;
        }
    }

    /// <summary>Pre-computed row key strings (legacy — prefer GetRowKeyAt for paginated access).</summary>
    public string[] RowKeyCache => _rowKeyCache;

    /// <summary>Total item count from the data source.</summary>
    public int? TotalCount => _hookResource is not null
        ? _hookResource.TotalCount
        : _totalCount;

    /// <summary>Whether data is currently being fetched.</summary>
    public bool IsLoading => _hookResource is not null
        ? _hookResource.LoadState is LoadState.Loading && _hookResource.TotalCount is null
        : _isLoading;

    /// <summary>
    /// Total number of items in the data set. In paged mode, this is the total count
    /// from the data source (even if not all pages are loaded yet). The VirtualList
    /// uses this for the full scrollbar extent. Unloaded items render as placeholders.
    /// </summary>
    public int ItemCount
    {
        get
        {
            if (_hookResource is not null)
            {
                // Stable count across load transitions. Before the first page completes,
                // TotalCount is null — report 0 so the grid shows the loading template
                // instead of a placeholder-only list whose count will jump on completion.
                // ItemsRepeater doesn't reliably re-realize across a big expansion like
                // 60 → 250 000, which otherwise leaves the data area blank.
                if (_hookResource.TotalCount is { } total) return total;
                if (_hookResource.LoadState is LoadState.Loading) return 0;
                return _hookResource.Items.Count;
            }
            return _cache?.TotalCount ?? _loadedItems.Count;
        }
    }

    /// <summary>The underlying page cache, or null if using legacy eager loading.</summary>
    public DataPageCache<T>? PageCache => _cache;

    /// <summary>
    /// Request that blocks covering the given row range be loaded.
    /// Prefetches one block before and one block after the visible range
    /// so that small scrolls don't hit loading placeholders.
    /// </summary>
    public void EnsureRangeLoaded(int firstRow, int lastRow)
    {
        if (_hookResource is not null)
        {
            // Resource tracks page size internally; it dedups already-loaded / in-flight pages.
            // Mirror the legacy prefetch-one-block-each-direction behaviour by widening the range.
            var total = _hookResource.TotalCount ?? _hookResource.Items.Count;
            if (total == 0) return;
            const int prefetch = 50; // conservative prefetch; resource coalesces per-page.
            var startRow = Math.Max(0, firstRow - prefetch);
            var endRow = Math.Min(lastRow + prefetch, total - 1);
            _hookResource.EnsureRange(startRow, endRow);
            return;
        }

        if (_cache is null) return;
        var blockSize = _cache.BlockSize;
        var total2 = _cache.TotalCount ?? 0;
        if (total2 == 0) return;

        // Expand range by one block in each direction for smooth scrolling
        var startRow2 = Math.Max(0, firstRow - blockSize);
        var endRow2 = Math.Min(lastRow + blockSize, total2 - 1);

        for (int b = startRow2 / blockSize; b <= endRow2 / blockSize; b++)
        {
            if (!_cache.IsLoaded(b * blockSize))
                _cache.RequestBlock(b);
        }
    }

    /// <summary>
    /// Get the item at a specific row index. Returns default(T) if the item's
    /// block hasn't been loaded yet. Does not trigger fetches — ItemCount is
    /// bounded to loaded items, so indices within range are always available.
    /// </summary>
    public T? GetItemAt(int index)
    {
        if (_hookResource is not null)
        {
            if (_mutations.TryGetValue(index, out var mutated))
                return mutated;
            if (index < 0 || index >= _hookResource.Items.Count) return default;
            return _hookResource.Items[index];
        }
        if (_cache is not null)
        {
            if (_mutations.TryGetValue(index, out var mutated))
                return mutated;
            return _cache.PeekItem(index);
        }
        if ((uint)index >= (uint)_loadedItems.Count) return default;
        return _loadedItems[index];
    }

    /// <summary>
    /// Get the row key string for a specific index. Returns null if the item
    /// is not yet loaded.
    /// </summary>
    public string? GetRowKeyAt(int index)
    {
        if (_hookResource is not null)
        {
            if (_keyOverrides.TryGetValue(index, out var overridden))
                return overridden;
            if (index < 0 || index >= _hookResource.Items.Count) return null;
            var item = _hookResource.Items[index];
            if (item is null) return null;
            return _source.GetRowKey(item).Value;
        }
        if (_cache is not null)
        {
            if (_keyOverrides.TryGetValue(index, out var overridden))
                return overridden;
            var item = _cache.PeekItem(index);
            if (item is null) return null;
            return _source.GetRowKey(item).Value;
        }
        if ((uint)index >= (uint)_rowKeyCache.Length) return null;
        return _rowKeyCache[index];
    }

    /// <summary>Whether the item at a specific row index is loaded.</summary>
    public bool IsItemLoaded(int index)
    {
        if (_hookResource is not null)
        {
            if (_mutations.ContainsKey(index)) return true;
            if (index < 0 || index >= _hookResource.Items.Count) return false;
            return _hookResource.Items[index] is not null;
        }
        if (_cache is not null)
            return _mutations.ContainsKey(index) || _cache.IsLoaded(index);
        return (uint)index < (uint)_loadedItems.Count;
    }

    /// <summary>Fires when state changes requiring a re-render.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Timestamp (Stopwatch ticks) of the last scroll event. Set by the
    /// onVisibleRangeChanged callback so the StateChanged handler can
    /// defer re-renders during active scrolling.
    /// </summary>
    public long LastScrollTick;

    /// <summary>
    /// Timestamp set when a deferred forceRender is dispatched. If
    /// LastScrollTick moves past this value before RefreshRealizedItems
    /// runs, the reconciliation is skipped (scroll restarted).
    /// </summary>
    public long RenderDispatchTick;

    /// <summary>
    /// Last visible range reported by onVisibleRangeChanged. Used to
    /// re-request blocks after scroll settles, in case the final position
    /// wasn't covered by requests made during rapid scrolling.
    /// </summary>
    public int LastVisibleFirst;
    public int LastVisibleLast;

    /// <param name="blockSize">
    /// Page cache block size. When 0 (default), the cache uses its built-in default (50).
    /// Pass a viewport-derived value to ensure the first block fills the screen.
    /// </param>
    public DataGridState(IDataSource<T> source, IReadOnlyList<FieldDescriptor> columns, SelectionMode selectionMode, int blockSize = 0)
    {
        _source = source;
        _columns = new List<FieldDescriptor>(columns);
        _selectionMode = selectionMode;
        _blockSize = blockSize;
    }

    // ── Sort operations ──────────────────────────────────────────

    /// <summary>
    /// Toggles sort on a column: None -> Ascending -> Descending -> None.
    /// When additive is true (Ctrl+click), adds to multi-sort. Otherwise replaces.
    /// </summary>
    public void ToggleSort(string field, bool additive = false)
    {
        var existing = _sorts.FindIndex(s => s.Field == field);

        if (!additive)
        {
            if (existing >= 0)
            {
                var current = _sorts[existing];
                _sorts.Clear();
                if (current.Direction == SortDirection.Ascending)
                    _sorts.Add(new SortDescriptor(field, SortDirection.Descending));
                // Descending -> None: list stays empty
            }
            else
            {
                _sorts.Clear();
                _sorts.Add(new SortDescriptor(field, SortDirection.Ascending));
            }
        }
        else
        {
            // Additive (multi-sort)
            if (existing >= 0)
            {
                var current = _sorts[existing];
                if (current.Direction == SortDirection.Ascending)
                    _sorts[existing] = new SortDescriptor(field, SortDirection.Descending);
                else
                    _sorts.RemoveAt(existing);
            }
            else
            {
                _sorts.Add(new SortDescriptor(field, SortDirection.Ascending));
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Gets the current sort direction for a column, or null if unsorted.</summary>
    public SortDirection? GetSortDirection(string field)
        => _sorts.FirstOrDefault(s => s.Field == field)?.Direction;

    // ── Filter operations ───────────────────────────────────────

    /// <summary>Set a filter on a column. Replaces any existing filter on the same field.</summary>
    public void SetFilter(FilterDescriptor filter)
    {
        _filters.RemoveAll(f => f.Field == filter.Field);
        _filters.Add(filter);
        StateChanged?.Invoke();
    }

    /// <summary>Remove the filter for a column.</summary>
    public void ClearFilter(string field)
    {
        if (_filters.RemoveAll(f => f.Field == field) > 0)
            StateChanged?.Invoke();
    }

    /// <summary>Remove all filters.</summary>
    public void ClearAllFilters()
    {
        if (_filters.Count > 0)
        {
            _filters.Clear();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Gets the active filter for a column, or null.</summary>
    public FilterDescriptor? GetFilter(string field)
        => _filters.FirstOrDefault(f => f.Field == field);

    // ── Search state ────────────────────────────────────────────

    private string? _searchQuery;

    /// <summary>Current text search query.</summary>
    public string? SearchQuery => _searchQuery;

    /// <summary>Set the text search query. Triggers a state change for data reload.</summary>
    public void SetSearchQuery(string? query)
    {
        _searchQuery = string.IsNullOrWhiteSpace(query) ? null : query;
        StateChanged?.Invoke();
    }

    // ── Selection operations ─────────────────────────────────────

    /// <summary>Handles a row click with optional modifier keys.</summary>
    public void HandleRowClick(RowKey key, bool ctrlKey = false, bool shiftKey = false, IReadOnlyList<RowKey>? visibleOrder = null)
    {
        if (_selectionMode == SelectionMode.None) return;

        if (_selectionMode == SelectionMode.Single)
        {
            _selectedKeys.Clear();
            _selectedKeys.Add(key);
            AnchorKey = key;
            FocusedKey = key;
            _selectionVersion++;
            StateChanged?.Invoke();
            return;
        }

        // Multiple selection mode — use internal key cache for range selection if no explicit order
        var order = visibleOrder;
        if (order is null && _rowKeyCache.Length > 0)
            order = _rowKeyCache.Select(k => new RowKey(k)).ToList();

        if (shiftKey && AnchorKey is not null && order is not null)
        {
            SelectRange(AnchorKey.Value, key, order);
        }
        else if (ctrlKey)
        {
            if (!_selectedKeys.Remove(key))
                _selectedKeys.Add(key);
            AnchorKey = key;
        }
        else
        {
            _selectedKeys.Clear();
            _selectedKeys.Add(key);
            AnchorKey = key;
        }

        FocusedKey = key;
        _selectionVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Select a range of rows between two keys based on visible order.</summary>
    public void SelectRange(RowKey from, RowKey to, IReadOnlyList<RowKey> visibleOrder)
    {
        var fromIndex = -1;
        var toIndex = -1;
        for (int i = 0; i < visibleOrder.Count; i++)
        {
            if (visibleOrder[i].Equals(from)) fromIndex = i;
            if (visibleOrder[i].Equals(to)) toIndex = i;
        }

        if (fromIndex < 0 || toIndex < 0) return;

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);

        _selectedKeys.Clear();
        for (int i = start; i <= end; i++)
            _selectedKeys.Add(visibleOrder[i]);

        _selectionVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Select all provided keys.</summary>
    public void SelectAll(IReadOnlyList<RowKey> allKeys)
    {
        if (_selectionMode != SelectionMode.Multiple) return;
        _selectedKeys.Clear();
        foreach (var key in allKeys)
            _selectedKeys.Add(key);
        _selectionVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Clear all selection.</summary>
    public void ClearSelection()
    {
        _selectedKeys.Clear();
        AnchorKey = null;
        _selectionVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Check if a row is selected.</summary>
    public bool IsSelected(RowKey key) => _selectedKeys.Contains(key);

    // ── Column operations ────────────────────────────────────────

    /// <summary>Gets the effective width for a column.</summary>
    public double GetColumnWidth(string columnName)
    {
        if (_columnWidths.TryGetValue(columnName, out var width))
            return width;

        var col = _columns.FirstOrDefault(c => c.Name == columnName);
        return col?.Width ?? 120;
    }

    /// <summary>Resize a column and trigger a re-render.</summary>
    public void ResizeColumn(string columnName, double newWidth)
    {
        var col = _columns.FirstOrDefault(c => c.Name == columnName);
        var minWidth = col?.MinWidth ?? 40;
        var maxWidth = col?.MaxWidth ?? double.MaxValue;
        _columnWidths[columnName] = Math.Clamp(newWidth, minWidth, maxWidth);
        StateChanged?.Invoke();
    }


    /// <summary>Reorder a column to a new position.</summary>
    public void ReorderColumn(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _columns.Count) return;
        if (toIndex < 0 || toIndex >= _columns.Count) return;
        if (fromIndex == toIndex) return;

        var col = _columns[fromIndex];
        _columns.RemoveAt(fromIndex);
        _columns.Insert(toIndex, col);
        StateChanged?.Invoke();
    }

    /// <summary>Hide a column.</summary>
    public void HideColumn(string columnName)
    {
        if (_hiddenColumns.Add(columnName))
            StateChanged?.Invoke();
    }

    /// <summary>Show a previously hidden column.</summary>
    public void ShowColumn(string columnName)
    {
        if (_hiddenColumns.Remove(columnName))
            StateChanged?.Invoke();
    }

    /// <summary>Toggle column visibility.</summary>
    public void ToggleColumnVisibility(string columnName)
    {
        if (!_hiddenColumns.Remove(columnName))
            _hiddenColumns.Add(columnName);
        StateChanged?.Invoke();
    }

    /// <summary>Check if a column is visible.</summary>
    public bool IsColumnVisible(string columnName) => !_hiddenColumns.Contains(columnName);

    /// <summary>Get visible columns grouped by pin position.</summary>
    public (IReadOnlyList<FieldDescriptor> Left, IReadOnlyList<FieldDescriptor> Center, IReadOnlyList<FieldDescriptor> Right) GetPinnedColumnGroups()
    {
        var visible = Columns;
        var left = new List<FieldDescriptor>();
        var center = new List<FieldDescriptor>();
        var right = new List<FieldDescriptor>();

        foreach (var col in visible)
        {
            switch (col.Pin)
            {
                case PinPosition.Left: left.Add(col); break;
                case PinPosition.Right: right.Add(col); break;
                default: center.Add(col); break;
            }
        }

        return (left, center, right);
    }

    /// <summary>Pin a column to a position at runtime.</summary>
    public void PinColumn(string columnName, PinPosition position)
    {
        var idx = _columns.FindIndex(c => c.Name == columnName);
        if (idx < 0) return;
        _columns[idx] = _columns[idx] with { Pin = position };
        StateChanged?.Invoke();
    }

    // ── Row detail expand/collapse ──────────────────────────────

    private readonly HashSet<RowKey> _expandedRows = new();

    /// <summary>Set of currently expanded row keys.</summary>
    public IReadOnlySet<RowKey> ExpandedRows => _expandedRows;

    /// <summary>Check if a row is expanded.</summary>
    public bool IsExpanded(RowKey key) => _expandedRows.Contains(key);

    /// <summary>Toggle the expanded state of a row.</summary>
    public void ToggleRowExpansion(RowKey key)
    {
        if (!_expandedRows.Remove(key))
            _expandedRows.Add(key);
        StateChanged?.Invoke();
    }

    /// <summary>Expand a row.</summary>
    public void ExpandRow(RowKey key)
    {
        if (_expandedRows.Add(key))
            StateChanged?.Invoke();
    }

    /// <summary>Collapse a row.</summary>
    public void CollapseRow(RowKey key)
    {
        if (_expandedRows.Remove(key))
            StateChanged?.Invoke();
    }

    /// <summary>Collapse all expanded rows.</summary>
    public void CollapseAllRows()
    {
        if (_expandedRows.Count > 0)
        {
            _expandedRows.Clear();
            StateChanged?.Invoke();
        }
    }

    // ── Focus navigation ──────────────────────────────────────────

    /// <summary>Set cell focus to a specific row and column index.</summary>
    public void SetFocus(int rowIndex, int colIndex)
    {
        var rowCount = ItemCount;
        var colCount = _columns.Count;
        if (rowCount == 0 || colCount == 0) return;

        _focusedRowIndex = Math.Clamp(rowIndex, 0, rowCount - 1);
        _focusedColIndex = Math.Clamp(colIndex, 0, colCount - 1);

        // Sync FocusedKey with the row index
        var key = GetRowKeyAt(_focusedRowIndex);
        if (key is not null)
            FocusedKey = new RowKey(key);

        StateChanged?.Invoke();
    }

    /// <summary>Move focus by a delta. Used for arrow key navigation.</summary>
    public void MoveFocus(int rowDelta, int colDelta)
    {
        if (ItemCount == 0 || _columns.Count == 0) return;

        // If no focus yet, start at (0, 0)
        if (_focusedRowIndex < 0) { SetFocus(0, 0); return; }

        SetFocus(_focusedRowIndex + rowDelta, _focusedColIndex + colDelta);
    }

    /// <summary>Move focus to the first column in the current row.</summary>
    public void FocusHome()
    {
        if (_focusedRowIndex < 0) { SetFocus(0, 0); return; }
        SetFocus(_focusedRowIndex, 0);
    }

    /// <summary>Move focus to the last column in the current row.</summary>
    public void FocusEnd()
    {
        if (_focusedRowIndex < 0) { SetFocus(0, _columns.Count - 1); return; }
        SetFocus(_focusedRowIndex, _columns.Count - 1);
    }

    /// <summary>Move focus to the next cell (left to right, top to bottom). Returns false at the end.</summary>
    public bool FocusNextCell()
    {
        var totalRows = ItemCount;
        if (totalRows == 0 || _columns.Count == 0) return false;
        if (_focusedRowIndex < 0) { SetFocus(0, 0); return true; }

        var nextCol = _focusedColIndex + 1;
        if (nextCol < _columns.Count)
        {
            SetFocus(_focusedRowIndex, nextCol);
            return true;
        }

        // Wrap to next row
        var nextRow = _focusedRowIndex + 1;
        if (nextRow < totalRows)
        {
            SetFocus(nextRow, 0);
            return true;
        }

        return false; // At the very end
    }

    /// <summary>Move focus to the previous cell (right to left, bottom to top). Returns false at the start.</summary>
    public bool FocusPrevCell()
    {
        if (ItemCount == 0 || _columns.Count == 0) return false;
        if (_focusedRowIndex < 0) { SetFocus(0, 0); return true; }

        var prevCol = _focusedColIndex - 1;
        if (prevCol >= 0)
        {
            SetFocus(_focusedRowIndex, prevCol);
            return true;
        }

        // Wrap to previous row
        var prevRow = _focusedRowIndex - 1;
        if (prevRow >= 0)
        {
            SetFocus(prevRow, _columns.Count - 1);
            return true;
        }

        return false; // At the very start
    }

    /// <summary>
    /// Get the row index for a given row key, or -1 if not found.
    /// Searches the key cache (legacy mode) or scans loaded cache blocks (paged mode).
    /// </summary>
    public int GetRowIndex(RowKey key)
    {
        var keyStr = key.Value;

        if (_hookResource is not null)
        {
            // Mutation overlay first.
            foreach (var (idx, item) in _mutations)
            {
                if (_source.GetRowKey(item).Value == keyStr) return idx;
            }
            foreach (var (idx, k) in _keyOverrides)
            {
                if (k == keyStr) return idx;
            }
            var items = _hookResource.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (_mutations.ContainsKey(i)) continue;
                var it = items[i];
                if (it is null) continue;
                if (_source.GetRowKey(it).Value == keyStr) return i;
            }
            return -1;
        }

        if (_cache is not null)
        {
            // Check mutation overlay first
            foreach (var (idx, item) in _mutations)
            {
                if (_source.GetRowKey(item).Value == keyStr) return idx;
            }
            // Check key overrides
            foreach (var (idx, k) in _keyOverrides)
            {
                if (k == keyStr) return idx;
            }
            // Scan loaded cache blocks
            var total = _cache.TotalCount ?? 0;
            var blockSize = _cache.BlockSize;
            for (int b = 0; b * blockSize < total; b++)
            {
                if (!_cache.IsLoaded(b * blockSize)) continue;
                var block = _cache.GetBlock(b);
                if (block.Status != BlockStatus.Loaded) continue;
                for (int i = 0; i < block.Items.Count; i++)
                {
                    var rowIndex = b * blockSize + i;
                    if (_mutations.ContainsKey(rowIndex)) continue; // already checked
                    if (_source.GetRowKey(block.Items[i]).Value == keyStr) return rowIndex;
                }
            }
            return -1;
        }

        for (int i = 0; i < _rowKeyCache.Length; i++)
            if (_rowKeyCache[i] == keyStr) return i;
        return -1;
    }

    // ── Editing operations ──────────────────────────────────────

    /// <summary>Begin editing the currently focused cell. Returns false if the cell is not editable.</summary>
    public bool BeginEdit()
    {
        if (_focusedRowIndex < 0 || _focusedColIndex < 0) return false;
        return BeginEdit(_focusedRowIndex, _focusedColIndex);
    }

    /// <summary>Begin editing a specific cell.</summary>
    public bool BeginEdit(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || rowIndex >= ItemCount) return false;
        if (colIndex < 0 || colIndex >= _columns.Count) return false;

        var col = _columns[colIndex];
        if (col.IsReadOnly || col.SetValue is null) return false;

        var item = GetItemAt(rowIndex);
        if (item is null) return false;
        var keyStr = GetRowKeyAt(rowIndex);
        if (keyStr is null) return false;
        var rowKey = new RowKey(keyStr);
        var currentValue = col.GetValue(item!);

        _editingRowKey = rowKey;
        _editingColumnName = col.Name;
        _editingValue = currentValue;
        _focusedRowIndex = rowIndex;
        _focusedColIndex = colIndex;
        FocusedKey = rowKey;

        // Set up cell-level validation
        _editValidation = new ValidationContext();
        _editValidation.RegisterField(col.Name);
        _editValidation.SetInitialValue(col.Name, currentValue);

        _editingVersion++;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Update the pending value during editing.</summary>
    /// <summary>Update the pending value during editing. Does NOT trigger a re-render —
    /// the editor control manages its own visual state. The value is stored for
    /// later use by CommitEdit.</summary>
    public void UpdateEditingValue(object? value)
    {
        if (!IsEditing) return;
        _editingValue = value;
        _editingVersion++;

        // Run cell-level validation
        if (_editValidation is not null && _editingColumnName is not null)
        {
            ValidateField(_editingColumnName, value);
        }
        // No StateChanged here — the editor handles its own display.
        // Re-render only on BeginEdit/CommitEdit/CancelEdit.
    }

    /// <summary>
    /// Commit the current edit. Applies SetValue to produce the new item,
    /// updates the loaded items list, and returns the (row key, new item) for async commit.
    /// Returns null if no edit is active.
    /// </summary>
    public (RowKey Key, T NewItem)? CommitEdit()
    {
        if (!IsEditing) return null;

        // Block commit if there are validation errors
        if (_editValidation is not null && !_editValidation.IsValid())
            return null;

        var rowKey = _editingRowKey!.Value;
        var colName = _editingColumnName!;
        var newValue = _editingValue;

        // Find the row and column
        var rowIndex = GetRowIndex(rowKey);
        if (rowIndex < 0) { CancelEdit(); return null; }

        var col = _columns.FirstOrDefault(c => c.Name == colName);
        if (col?.SetValue is null) { CancelEdit(); return null; }

        var item = GetItemAt(rowIndex);
        if (item is null) { CancelEdit(); return null; }

        // Apply return-new-owner SetValue
        var newOwner = col.SetValue(item!, newValue);
        var newItem = (T)newOwner;

        // Update in-memory state
        if (_cache is not null || _hookResource is not null)
        {
            _mutations[rowIndex] = newItem;
            _keyOverrides[rowIndex] = _source.GetRowKey(newItem).Value;
        }
        else
        {
            _loadedItems[rowIndex] = newItem;
            _rowKeyCache[rowIndex] = _source.GetRowKey(newItem).Value;
        }

        // Clear editing state
        var savedKey = rowKey;
        _editingRowKey = null;
        _editingColumnName = null;
        _editingValue = null;
        _editValidation = null;
        _editingVersion++;
        StateChanged?.Invoke();

        return (savedKey, newItem);
    }

    /// <summary>Cancel the current edit, discarding pending changes.</summary>
    public void CancelEdit()
    {
        if (_isRowEditing) { CancelRowEdit(); return; }
        if (!IsEditing) return;

        _editingRowKey = null;
        _editingColumnName = null;
        _editingValue = null;
        _editValidation = null;
        _editingVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Commit the current edit and move focus to the next cell. Starts editing if the next cell is editable.</summary>
    public (RowKey Key, T NewItem)? CommitAndMoveNext()
    {
        var result = CommitEdit();
        FocusNextCell();
        return result;
    }

    // ── Row-mode editing ────────────────────────────────────────

    /// <summary>
    /// Begin editing an entire row. All editable columns switch to editors simultaneously.
    /// Returns false if the row index is invalid or there are no editable columns.
    /// </summary>
    public bool BeginRowEdit(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= ItemCount) return false;

        var item = GetItemAt(rowIndex);
        if (item is null) return false;
        var keyStr = GetRowKeyAt(rowIndex);
        if (keyStr is null) return false;
        var rowKey = new RowKey(keyStr);

        // Snapshot current values for all editable columns
        var values = new Dictionary<string, object?>();
        foreach (var col in _columns)
        {
            if (!col.IsReadOnly && col.SetValue is not null)
                values[col.Name] = col.GetValue(item!);
        }

        if (values.Count == 0) return false;

        _editingRowKey = rowKey;
        _editingColumnName = null; // null signals row mode
        _editingValue = null;
        _rowEditValues = values;
        _isRowEditing = true;
        _focusedRowIndex = rowIndex;
        FocusedKey = rowKey;

        // Set up row-level validation
        _editValidation = new ValidationContext();
        foreach (var (colName, colValue) in values)
        {
            _editValidation.RegisterField(colName);
            _editValidation.SetInitialValue(colName, colValue);
        }

        _editingVersion++;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Update a pending column value during row editing.</summary>
    public void UpdateRowEditValue(string columnName, object? value)
    {
        if (!_isRowEditing || _rowEditValues is null) return;
        _rowEditValues[columnName] = value;
        _editingVersion++;

        // Run column-level validation
        ValidateField(columnName, value);
    }

    /// <summary>
    /// Commit the entire row edit. Applies all pending SetValue calls to produce
    /// the new item. Returns the (row key, new item) for async commit.
    /// </summary>
    public (RowKey Key, T NewItem)? CommitRowEdit()
    {
        if (!_isRowEditing || _rowEditValues is null) return null;

        // Block commit if there are validation errors
        if (_editValidation is not null && !_editValidation.IsValid())
            return null;

        var rowKey = _editingRowKey!.Value;
        var rowIndex = GetRowIndex(rowKey);
        if (rowIndex < 0) { CancelRowEdit(); return null; }

        var item = GetItemAt(rowIndex);
        if (item is null) { CancelRowEdit(); return null; }
        var current = item!;

        // Apply all pending values via return-new-owner SetValue
        foreach (var (colName, newValue) in _rowEditValues)
        {
            var col = _columns.FirstOrDefault(c => c.Name == colName);
            if (col?.SetValue is null) continue;
            current = (T)col.SetValue(current, newValue);
        }

        // Update in-memory state
        if (_cache is not null || _hookResource is not null)
        {
            _mutations[rowIndex] = current;
            _keyOverrides[rowIndex] = _source.GetRowKey(current).Value;
        }
        else
        {
            _loadedItems[rowIndex] = current;
            _rowKeyCache[rowIndex] = _source.GetRowKey(current).Value;
        }

        // Clear row editing state
        var savedKey = rowKey;
        _editingRowKey = null;
        _editingColumnName = null;
        _editingValue = null;
        _rowEditValues = null;
        _isRowEditing = false;
        _editingVersion++;
        StateChanged?.Invoke();

        return (savedKey, current);
    }

    /// <summary>Cancel the row edit, discarding all pending changes.</summary>
    public void CancelRowEdit()
    {
        if (!_isRowEditing) return;

        _editingRowKey = null;
        _editingColumnName = null;
        _editingValue = null;
        _rowEditValues = null;
        _isRowEditing = false;
        _editValidation = null;
        _editingVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>Check if a specific column is being edited in row mode.</summary>
    public bool IsColumnInRowEdit(RowKey rowKey, string columnName)
        => _isRowEditing && _editingRowKey?.Equals(rowKey) == true
           && _rowEditValues?.ContainsKey(columnName) == true;

    // ── Async commit lifecycle ──────────────────────────────────

    /// <summary>
    /// Begin an async commit for a row. Stores the original item for potential revert.
    /// Call after CommitEdit/CommitRowEdit to mark the row as committing.
    /// </summary>
    public void BeginAsyncCommit(RowKey key, T originalItem)
    {
        _pendingCommitOriginals[key] = originalItem;
        _committingRows.Add(key);
        _commitErrors.Remove(key);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Mark an async commit as successfully completed. Clears the pending state.
    /// </summary>
    public void CompleteAsyncCommit(RowKey key)
    {
        _pendingCommitOriginals.Remove(key);
        _committingRows.Remove(key);
        _commitErrors.Remove(key);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Mark an async commit as failed. Reverts the item to its pre-edit state
    /// and stores the error message.
    /// </summary>
    public void FailAsyncCommit(RowKey key, string errorMessage)
    {
        _committingRows.Remove(key);
        _commitErrors[key] = errorMessage;

        // Revert the optimistic update
        if (_pendingCommitOriginals.TryGetValue(key, out var original))
        {
            var rowIndex = GetRowIndex(key);
            if (rowIndex >= 0)
            {
                if (_cache is not null || _hookResource is not null)
                {
                    _mutations[rowIndex] = original;
                    _keyOverrides[rowIndex] = _source.GetRowKey(original).Value;
                }
                else
                {
                    _loadedItems[rowIndex] = original;
                    _rowKeyCache[rowIndex] = _source.GetRowKey(original).Value;
                }
            }
            _pendingCommitOriginals.Remove(key);
        }

        StateChanged?.Invoke();
    }

    /// <summary>Dismiss the error for a specific row.</summary>
    public void DismissCommitError(RowKey key)
    {
        if (_commitErrors.Remove(key))
            StateChanged?.Invoke();
    }

    // ── Validation ──────────────────────────────────────────────

    /// <summary>
    /// Run synchronous validators for a field and update the validation context.
    /// Called automatically when editing values change.
    /// </summary>
    private void ValidateField(string fieldName, object? value)
    {
        if (_editValidation is null) return;

        var col = _columns.FirstOrDefault(c => c.Name == fieldName);
        if (col is null) return;

        _editValidation.ClearInternal(fieldName);
        _editValidation.NotifyValueChanged(fieldName, value);

        if (col.Validators is { Count: > 0 })
        {
            foreach (var validator in col.Validators)
            {
                var msg = validator.Validate(value, fieldName);
                if (msg is not null)
                    _editValidation.Add(msg);
            }
        }
    }

    /// <summary>
    /// Run async validators for a field. Returns when all validators complete.
    /// </summary>
    public async Task ValidateFieldAsync(string fieldName, object? value, CancellationToken cancellationToken = default)
    {
        if (_editValidation is null) return;

        var col = _columns.FirstOrDefault(c => c.Name == fieldName);
        if (col?.AsyncValidators is not { Count: > 0 }) return;

        foreach (var validator in col.AsyncValidators)
        {
            var msg = await validator.ValidateAsync(value, fieldName, cancellationToken);
            if (msg is not null)
                _editValidation.Add(msg);
        }
    }

    /// <summary>Get validation messages for a specific field in the current edit.</summary>
    public IReadOnlyList<ValidationMessage> GetValidationMessages(string fieldName)
        => _editValidation?.GetMessages(fieldName) ?? Array.Empty<ValidationMessage>();

    /// <summary>Get all validation messages for the current edit.</summary>
    public IReadOnlyList<ValidationMessage> GetAllValidationMessages()
        => _editValidation?.GetAllMessages() ?? Array.Empty<ValidationMessage>();

    // ── Data loading ─────────────────────────────────────────────

    /// <summary>Load data from the source using current sort/filter state.</summary>
    public async Task LoadDataAsync(CancellationToken cancellationToken = default)
    {
        // Hook-based paging owns loading through UseInfiniteResource — the grid just
        // passes sort/filter state into the hook's deps. This method becomes a no-op.
        if (_hookResource is not null) return;

        _isLoading = true;
        StateChanged?.Invoke();

        try
        {
            var caps = _source.Capabilities;
            var serverSort = caps.HasFlag(DataSourceCapabilities.ServerSort);
            var serverFilter = caps.HasFlag(DataSourceCapabilities.ServerFilter);
            var serverSearch = caps.HasFlag(DataSourceCapabilities.ServerSearch);

            var needsClientSort = !serverSort && _sorts.Count > 0;
            var needsClientFilter = !serverFilter && _filters.Count > 0;

            if (needsClientSort || needsClientFilter)
            {
                // Client-side fallback: source can't sort/filter server-side,
                // so we must load all rows and apply locally.
                _cache = null;
                _mutations.Clear();
                _keyOverrides.Clear();

                var request = new DataRequest
                {
                    PageSize = int.MaxValue,
                    Sort = serverSort && _sorts.Count > 0 ? _sorts : null,
                    Filters = serverFilter && _filters.Count > 0 ? _filters : null,
                    SearchQuery = serverSearch ? _searchQuery : null,
                };

                var page = await _source.GetPageAsync(request, cancellationToken);
                _loadedItems = new List<T>(page.Items);
                _totalCount = page.TotalCount;

                if (needsClientFilter)
                {
                    _loadedItems = ApplyClientFilters(_loadedItems, _filters);
                    _totalCount = _loadedItems.Count;
                }

                if (needsClientSort)
                {
                    _loadedItems = ApplyClientSort(_loadedItems, _sorts);
                }

                // Pre-cache row key strings so getItemKey during scroll is a simple array lookup.
                var keys = new string[_loadedItems.Count];
                for (int i = 0; i < _loadedItems.Count; i++)
                    keys[i] = _source.GetRowKey(_loadedItems[i]).Value;
                _rowKeyCache = keys;
            }
            else
            {
                // Incremental paging: use DataPageCache for block-based fetching.
                // Only the pages needed for the visible viewport are loaded.
                if (_cache is null)
                {
                    _cache = _blockSize > 0
                        ? new DataPageCache<T>(_source, blockSize: _blockSize)
                        : new DataPageCache<T>(_source);
                    _cache.BlockLoaded += OnBlockLoaded;
                }

                var state = new DataRequest
                {
                    Sort = _sorts.Count > 0 ? _sorts : null,
                    Filters = _filters.Count > 0 ? _filters : null,
                    SearchQuery = _searchQuery,
                };
                _cache.SetState(state);
                _mutations.Clear();
                _keyOverrides.Clear();

                // Clear legacy collections — paged mode uses cache accessors.
                _loadedItems = new List<T>();
                _rowKeyCache = Array.Empty<string>();

                // Pre-fetch block 0 to get initial data + total count.
                await _cache.GetBlockAsync(0, cancellationToken);
                _totalCount = _cache.TotalCount;
            }
        }
        finally
        {
            _isLoading = false;
            StateChanged?.Invoke();
        }
    }

    private void OnBlockLoaded(int blockIndex)
    {
        // Update total count from the latest response.
        if (_cache?.TotalCount is int tc)
            _totalCount = tc;
        StateChanged?.Invoke();
    }

    // ── Client-side sort/filter fallback ─────────────────────────

#pragma warning disable IL2090 // Generic type parameter flows through without DynamicallyAccessedMembers annotation
    private static List<T> ApplyClientSort(List<T> items, List<SortDescriptor> sorts)
    {
        if (sorts.Count == 0 || items.Count == 0) return items;

        IOrderedEnumerable<T>? ordered = null;
        foreach (var sort in sorts)
        {
            var prop = typeof(T).GetProperty(sort.Field, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Instance);
            if (prop is null) continue;

            if (ordered is null)
            {
                ordered = sort.Direction == SortDirection.Ascending
                    ? items.OrderBy(x => prop.GetValue(x))
                    : items.OrderByDescending(x => prop.GetValue(x));
            }
            else
            {
                ordered = sort.Direction == SortDirection.Ascending
                    ? ordered.ThenBy(x => prop.GetValue(x))
                    : ordered.ThenByDescending(x => prop.GetValue(x));
            }
        }

        return ordered?.ToList() ?? items;
    }

#pragma warning disable IL2090 // Generic type parameter flows through without DynamicallyAccessedMembers annotation
    private static List<T> ApplyClientFilters(List<T> items, List<FilterDescriptor> filters)
    {
        if (filters.Count == 0 || items.Count == 0) return items;

        IEnumerable<T> query = items;
        foreach (var filter in filters)
        {
            var prop = typeof(T).GetProperty(filter.Field, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Instance);
            if (prop is null) continue;

            query = filter.Operator switch
            {
                FilterOperator.Equals => query.Where(x => Equals(prop.GetValue(x), filter.Value)),
                FilterOperator.NotEquals => query.Where(x => !Equals(prop.GetValue(x), filter.Value)),
                FilterOperator.Contains => query.Where(x => prop.GetValue(x)?.ToString()?.Contains(filter.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true),
                FilterOperator.GreaterThan => query.Where(x => x is not null && SafeCompare(prop.GetValue(x), filter.Value) > 0),
                FilterOperator.LessThan => query.Where(x => x is not null && SafeCompare(prop.GetValue(x), filter.Value) < 0),
                FilterOperator.IsNull => query.Where(x => prop.GetValue(x) is null),
                FilterOperator.IsNotNull => query.Where(x => prop.GetValue(x) is not null),
                _ => query,
            };
        }

        return query.ToList();
    }
#pragma warning restore IL2090

    private static int SafeCompare(object? a, object? b)
    {
        if (a is IComparable ca && b is not null)
        {
            try { return ca.CompareTo(Convert.ChangeType(b, a.GetType())); }
            catch { return 0; }
        }
        return 0;
    }
}
