using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Describes how to edit values of a given type in the PropertyGrid.
/// A type either has an Editor (leaf/atomic) or a Decomposition (composite),
/// or both (e.g., a Color has a hex editor AND can expand to R/G/B).
/// </summary>
public record TypeMetadata
{
    /// <summary>
    /// Creates an editor Element for a value of this type.
    /// Null if this type is only editable through decomposition.
    /// </summary>
    public Func<object, Action<object>, Element>? Editor { get; init; }

    /// <summary>
    /// Compact editor variant for grid inline cells.
    /// Falls back to Editor when null.
    /// </summary>
    public Func<object, Action<object>, Element>? CompactEditor { get; init; }

    /// <summary>
    /// Full editor variant for expanded/flyout editing.
    /// Null when no expanded editing is available.
    /// </summary>
    public Func<object, Action<object>, Element>? FullEditor { get; init; }

    /// <summary>
    /// Breaks a value into named sub-properties for recursive editing.
    /// Null if this type is atomic (edited only via Editor).
    /// </summary>
    public Func<object, IReadOnlyList<FieldDescriptor>>? Decompose { get; init; }

    /// <summary>
    /// Reconstructs a value from its decomposed parts. Required for
    /// immutable types that have a Decompose. For mutable types where
    /// Decompose returns descriptors with working setters, this is null.
    /// </summary>
    public Func<object, IReadOnlyDictionary<string, object>, object>? Compose { get; init; }

    /// <summary>
    /// Display name for the type (used in array item headers, etc.).
    /// Falls back to Type.Name if null.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Extended metadata for array/list types.
/// </summary>
public record ArrayTypeMetadata : TypeMetadata
{
    /// <summary>
    /// Factory to create a new element for "Add" operations.
    /// Null means add is disabled.
    /// </summary>
    public Func<Task<object?>>? CreateElement { get; init; }
}
