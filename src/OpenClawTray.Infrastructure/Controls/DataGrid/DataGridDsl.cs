using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Controls;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// DSL factory methods for DataGrid.
/// </summary>
public static class DataGridDsl
{
    /// <summary>
    /// Creates a DataGrid with explicit column definitions.
    /// </summary>
    public static Element DataGrid<T>(
        IDataSource<T> source,
        IReadOnlyList<FieldDescriptor> columns,
        TypeRegistry? registry = null,
        SelectionMode selectionMode = SelectionMode.None,
        Action<IReadOnlySet<RowKey>>? onSelectionChanged = null,
        Func<RowKey, T, Task>? onRowChanged = null,
        double? rowHeight = 40,
        bool editable = false,
        EditMode editMode = EditMode.Cell,
        Func<CellContext<T>, Element>? cellTemplate = null,
        Func<RowContext<T>, Element>? rowTemplate = null,
        Func<HeaderContext, Element>? headerTemplate = null,
        Element? loadingTemplate = null,
        Element? emptyTemplate = null,
        bool showSearch = false,
        Func<T, RowKey, Element>? rowDetailTemplate = null,
        Func<FieldDescriptor, double, Element>? placeholderCellTemplate = null)
    {
        var props = new DataGridElement<T>
        {
            Source = source,
            Columns = columns,
            Registry = registry,
            SelectionMode = selectionMode,
            OnSelectionChanged = onSelectionChanged,
            OnRowChanged = onRowChanged,
            RowHeight = rowHeight,
            Editable = editable,
            EditMode = editMode,
            CellTemplate = cellTemplate,
            RowTemplate = rowTemplate,
            HeaderTemplate = headerTemplate,
            LoadingTemplate = loadingTemplate,
            EmptyTemplate = emptyTemplate,
            ShowSearch = showSearch,
            RowDetailTemplate = rowDetailTemplate,
            PlaceholderCellTemplate = placeholderCellTemplate,
        };

        return Component<DataGridComponent<T>, DataGridElement<T>>(props)
            .WithKey($"dg-{typeof(T).Name}-{source.GetHashCode()}");
    }

    /// <summary>
    /// Creates a DataGrid with auto-generated columns from TypeRegistry + reflection.
    /// </summary>
    public static Element DataGrid<T>(
        IDataSource<T> source,
        TypeRegistry registry,
        Func<FieldDescriptor, FieldDescriptor>? columnOverrides = null,
        SelectionMode selectionMode = SelectionMode.None,
        Action<IReadOnlySet<RowKey>>? onSelectionChanged = null,
        Func<RowKey, T, Task>? onRowChanged = null,
        double? rowHeight = 40,
        bool editable = false,
        EditMode editMode = EditMode.Cell,
        Element? loadingTemplate = null,
        Element? emptyTemplate = null)
    {
        var props = new DataGridElement<T>
        {
            Source = source,
            Registry = registry,
            ColumnOverrides = columnOverrides,
            SelectionMode = selectionMode,
            OnSelectionChanged = onSelectionChanged,
            OnRowChanged = onRowChanged,
            RowHeight = rowHeight,
            Editable = editable,
            EditMode = editMode,
            LoadingTemplate = loadingTemplate,
            EmptyTemplate = emptyTemplate,
        };

        return Component<DataGridComponent<T>, DataGridElement<T>>(props)
            .WithKey($"dg-{typeof(T).Name}-{source.GetHashCode()}");
    }
}
