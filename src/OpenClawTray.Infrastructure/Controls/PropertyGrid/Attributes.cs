namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Assigns a property to a named category group in the PropertyGrid.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyCategoryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Provides tooltip/help text for a property in the PropertyGrid.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyDescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>
/// Overrides the display name for a property in the PropertyGrid.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyDisplayNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Marks a property as hidden from the PropertyGrid.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyHiddenAttribute : Attribute { }

/// <summary>
/// Marks a property as read-only in the PropertyGrid,
/// even if it has a public setter.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyReadOnlyAttribute : Attribute { }

/// <summary>
/// Explicitly controls declaration order when the default
/// MetadataToken ordering is insufficient (e.g., inherited properties).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyOrderAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>
/// Applied to a type to specify a custom editor for all properties
/// of that type. The referenced type must have a static method:
///   static Element CreateEditor(object value, Action&lt;object&gt; onChange)
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class PropertyEditorAttribute(Type editorType) : Attribute
{
    public Type EditorType { get; } = editorType;
}
