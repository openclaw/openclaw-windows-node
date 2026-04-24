using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Controls;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Element record for the DataGrid component.
/// Defines the full API surface for data grid rendering and interaction.
/// </summary>
public record DataGridElement<T> : Element
{
    /// <summary>The data source providing rows.</summary>
    public required IDataSource<T> Source { get; init; }

    /// <summary>Column definitions. If null, auto-generated from TypeRegistry + reflection.</summary>
    public IReadOnlyList<FieldDescriptor>? Columns { get; init; }

    /// <summary>Type registry for editor/renderer resolution. If null, uses built-in defaults.</summary>
    public TypeRegistry? Registry { get; init; }

    /// <summary>Column override function for reflection-generated columns.</summary>
    public Func<FieldDescriptor, FieldDescriptor>? ColumnOverrides { get; init; }

    /// <summary>Selection mode.</summary>
    public SelectionMode SelectionMode { get; init; } = SelectionMode.None;

    /// <summary>Callback when selection changes.</summary>
    public Action<IReadOnlySet<RowKey>>? OnSelectionChanged { get; init; }

    /// <summary>Callback when a row is edited and committed.</summary>
    public Func<RowKey, T, Task>? OnRowChanged { get; init; }

    /// <summary>
    /// Fixed row height. When set, all rows have this exact height and the
    /// virtualizer uses O(1) offset calculation. When null, rows are measured.
    /// </summary>
    public double? RowHeight { get; init; } = 40;

    /// <summary>Estimated row height for variable-height mode.</summary>
    public double EstimatedRowHeight { get; init; } = 40;

    /// <summary>Editing mode: Cell (one cell at a time) or Row (whole row).</summary>
    public EditMode EditMode { get; init; } = EditMode.Cell;

    /// <summary>Whether to show column headers.</summary>
    public bool ShowHeaders { get; init; } = true;

    /// <summary>Whether rows are editable (enables inline editing).</summary>
    public bool Editable { get; init; }

    /// <summary>Whether columns can be reordered via drag.</summary>
    public bool AllowColumnReorder { get; init; } = true;

    /// <summary>Whether columns can be resized.</summary>
    public bool AllowColumnResize { get; init; } = true;

    /// <summary>Whether to show the search bar above the grid.</summary>
    public bool ShowSearch { get; init; }

    /// <summary>Callback for row detail content when expanded.</summary>
    public Func<T, RowKey, Element>? RowDetailTemplate { get; init; }

    // ── Template overrides ────────────────────────────────────────

    /// <summary>Custom cell template override.</summary>
    public Func<CellContext<T>, Element>? CellTemplate { get; init; }

    /// <summary>Custom row template override.</summary>
    public Func<RowContext<T>, Element>? RowTemplate { get; init; }

    /// <summary>Custom header template override.</summary>
    public Func<HeaderContext, Element>? HeaderTemplate { get; init; }

    /// <summary>Element to show when data is loading.</summary>
    public Element? LoadingTemplate { get; init; }

    /// <summary>Element to show when data is empty.</summary>
    public Element? EmptyTemplate { get; init; }

    /// <summary>
    /// Custom cell content for placeholder (unloaded) rows. Receives the column
    /// descriptor and column width; returns the element to render in that cell.
    /// When null, uses a default shimmer-style gray bar.
    /// </summary>
    public Func<FieldDescriptor, double, Element>? PlaceholderCellTemplate { get; init; }
}

// ── Enums ────────────────────────────────────────────────────────

public enum SelectionMode { None, Single, Multiple }

public enum EditMode { Cell, Row }

// ── Context records for template overrides ───────────────────────

public record CellContext<T>(
    T Row,
    RowKey Key,
    FieldDescriptor Column,
    object? Value,
    bool IsEditing,
    Action<object?> SetValue);

public record RowContext<T>(
    T Row,
    RowKey Key,
    int RowIndex,
    bool IsSelected,
    bool IsEditing,
    IReadOnlyList<Element> Cells);

public record HeaderContext(
    FieldDescriptor Column,
    SortDirection? CurrentSort,
    Action ToggleSort,
    Action<double> Resize);
