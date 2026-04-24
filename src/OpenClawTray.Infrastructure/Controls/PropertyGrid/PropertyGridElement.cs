using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Element record for the PropertyGrid component. Serves as both the
/// virtual DOM element and the typed props for PropertyGridComponent.
/// </summary>
public record PropertyGridElement : Element
{
    /// <summary>The target object whose properties are edited.</summary>
    public required object Target { get; init; }

    /// <summary>The type registry for resolving editors.</summary>
    public required TypeRegistry Registry { get; init; }

    /// <summary>Callback when the root object is replaced (for immutable roots).</summary>
    public Action<object>? OnRootChanged { get; init; }

    /// <summary>Optional filter predicate — return false to hide a property.</summary>
    public Func<FieldDescriptor, bool>? Filter { get; init; }

    /// <summary>Whether to show the search/filter box.</summary>
    public bool ShowSearch { get; init; }

    // Template overrides (null = use defaults)
    public CategoryTemplate? CategoryTemplate { get; init; }
    public PropertyRowTemplate? PropertyRowTemplate { get; init; }
    public PropertyLabelTemplate? PropertyLabelTemplate { get; init; }
    public ArrayItemTemplate? ArrayItemTemplate { get; init; }
    public ArrayToolbarTemplate? ArrayToolbarTemplate { get; init; }
}

// ── Template delegates ───────────────────────────────────────────

public delegate Element CategoryTemplate(
    string name, bool isExpanded, Action<bool> onExpandedChanged, Element[] children);

public delegate Element PropertyRowTemplate(
    FieldDescriptor descriptor, Element label, Element editor, int indentLevel);

public delegate Element PropertyLabelTemplate(
    FieldDescriptor descriptor, int indentLevel);

public delegate Element ArrayItemTemplate(
    int index, string summary, bool isExpanded, Action<bool> onExpandedChanged,
    Action? onMoveUp, Action? onMoveDown, Action? onRemove);

public delegate Element ArrayToolbarTemplate(
    string propertyName, int count, Func<Task>? onAdd);
