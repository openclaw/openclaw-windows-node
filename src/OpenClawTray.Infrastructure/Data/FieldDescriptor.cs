using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Controls.Validation;

namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// Describes a single field within a decomposed type.
/// Unified descriptor used by PropertyGrid, DataGrid, and FormField.
/// </summary>
public record FieldDescriptor
{
    // ── Identity ────────────────────────────────────────────────

    /// <summary>Field name (used as key in accessors and Compose dictionary).</summary>
    public required string Name { get; init; }

    /// <summary>Display label shown in grids and form fields.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The CLR type of this field's value.</summary>
    public required Type FieldType { get; init; }

    // ── Access ──────────────────────────────────────────────────

    /// <summary>Gets the current value from the owner object.</summary>
    public required Func<object, object?> GetValue { get; init; }

    /// <summary>
    /// Sets the value on the owner object and returns the (possibly new) owner.
    /// For mutable properties: mutates in place and returns the same reference.
    /// For immutable properties: constructs a new object and returns it.
    /// Null for truly read-only fields (no setter, no constructor match).
    /// </summary>
    public Func<object, object?, object>? SetValue { get; init; }

    /// <summary>Whether this field is read-only in editors.</summary>
    public bool IsReadOnly { get; init; }

    // ── Metadata ────────────────────────────────────────────────

    /// <summary>Category for grouping. Null = default/uncategorized.</summary>
    public string? Category { get; init; }

    /// <summary>Help text shown as tooltip or description.</summary>
    public string? Description { get; init; }

    /// <summary>Declaration order for stable sorting.</summary>
    public int Order { get; init; }

    // ── Editing ─────────────────────────────────────────────────

    /// <summary>
    /// Custom editor factory for this specific field.
    /// Takes (value, onChange) and returns an Element.
    /// </summary>
    public Func<object, Action<object>, Element>? Editor { get; init; }

    // ── Validation ──────────────────────────────────────────────

    /// <summary>Synchronous validators for this field.</summary>
    public IReadOnlyList<IValidator>? Validators { get; init; }

    /// <summary>Asynchronous validators for this field.</summary>
    public IReadOnlyList<IAsyncValidator>? AsyncValidators { get; init; }

    // ── Grid-Specific ───────────────────────────────────────────

    /// <summary>Preferred column width in pixels.</summary>
    public double? Width { get; init; }

    /// <summary>Minimum column width in pixels.</summary>
    public double? MinWidth { get; init; }

    /// <summary>Maximum column width in pixels.</summary>
    public double? MaxWidth { get; init; }

    /// <summary>Flex grow factor for column sizing.</summary>
    public double? Flex { get; init; }

    /// <summary>Whether this column can be sorted. Default true.</summary>
    public bool Sortable { get; init; } = true;

    /// <summary>Whether this column can be filtered. Default true.</summary>
    public bool Filterable { get; init; } = true;

    /// <summary>Pin position for fixed columns.</summary>
    public PinPosition Pin { get; init; } = PinPosition.None;

    /// <summary>Custom cell renderer for grid display.</summary>
    public Func<object, Element>? CellRenderer { get; init; }

    /// <summary>Custom value formatter for grid display.</summary>
    public Func<object?, string>? FormatValue { get; init; }
}

/// <summary>
/// Controls column pinning in data grids.
/// </summary>
public enum PinPosition
{
    None,
    Left,
    Right,
}
