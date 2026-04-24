using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Controls;
using OpenClawTray.Infrastructure.Controls.Validation;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// DSL for defining DataGrid columns. Provides a fluent Column&lt;T&gt; builder
/// and auto-generation from TypeRegistry + reflection.
/// </summary>
public static class ColumnDsl
{
    /// <summary>
    /// Define a column from a property accessor expression.
    /// </summary>
    public static ColumnBuilder<T> Column<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        bool editable = false,
        string? displayName = null,
        string? format = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
    {
        var fieldType = InferFieldType<T>(name, accessor);

        // Auto-generate SetValue from reflection when editable
        Func<object, object?, object>? setter = null;
        if (editable)
        {
            setter = BuildSetValue<T>(name);
        }

        var descriptor = new FieldDescriptor
        {
            Name = name,
            DisplayName = displayName ?? name,
            FieldType = fieldType,
            GetValue = obj => accessor((T)obj),
            SetValue = setter,
            IsReadOnly = !editable || setter is null,
            Width = width,
            Pin = pin,
            FormatValue = format is not null ? val => FormatWithSpec(val, format) : null,
        };

        return new ColumnBuilder<T>(descriptor);
    }

    /// <summary>
    /// Auto-generate columns from a type using reflection and TypeRegistry.
    /// </summary>
    public static IReadOnlyList<FieldDescriptor> AutoColumns<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        TypeRegistry? registry = null,
        Func<FieldDescriptor, FieldDescriptor>? overrides = null)
    {
        var meta = registry?.Resolve(typeof(T))
            ?? ReflectionTypeMetadataProvider.CreateMetadata(typeof(T));

        if (meta.Decompose is null)
            return Array.Empty<FieldDescriptor>();

        // Create a dummy instance to get the descriptors (reflection-based)
        // For reflection metadata, Decompose needs an owner but only reads property metadata.
        // We use the unbound CreateDescriptor path instead.
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Where(p => p.GetCustomAttribute<PropertyHiddenAttribute>() is null)
            .ToArray();

        var descriptors = new List<FieldDescriptor>();
        for (int i = 0; i < properties.Length; i++)
        {
            var desc = ReflectionTypeMetadataProvider.CreateDescriptor(properties[i], i);

            // Apply cell renderer/formatter from registry
            if (registry is not null)
            {
                var cellRenderer = registry.GetCellRenderer(properties[i].PropertyType);
                var formatter = registry.GetFormatter(properties[i].PropertyType);
                if (cellRenderer is not null || formatter is not null)
                {
                    desc = desc with
                    {
                        CellRenderer = cellRenderer ?? desc.CellRenderer,
                        FormatValue = formatter ?? desc.FormatValue,
                    };
                }
            }

            if (overrides is not null)
                desc = overrides(desc);

            descriptors.Add(desc);
        }

        return descriptors;
    }

    private static Type InferFieldType<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string name, Func<T, object?> accessor)
    {
        var prop = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop?.PropertyType ?? typeof(object);
    }

    /// <summary>
    /// Build a SetValue delegate from reflection. For mutable properties, mutates in place.
    /// For init-only (record) properties, uses the copy constructor.
    /// </summary>
    private static Func<object, object?, object>? BuildSetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string propertyName)
    {
        var prop = typeof(T).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return null;

        // Check if the setter is a regular set (mutable) or init-only
        var setMethod = prop.SetMethod;
        if (setMethod is null) return null;

        // Check for init-only by looking at required custom modifiers
        var isInitOnly = setMethod.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        if (!isInitOnly)
        {
            // Mutable: set in place, return same reference
            return (owner, val) =>
            {
                prop.SetValue(owner, val);
                return owner;
            };
        }

        // Init-only (record): use the synthesized copy constructor <Clone>$ if available
        return ReflectionTypeMetadataProvider.BuildInitOnlySetter(prop);
    }

    private static string FormatWithSpec(object? value, string format)
    {
        if (value is null) return "";
        if (value is IFormattable formattable)
            return formattable.ToString(format, null);
        return value.ToString() ?? "";
    }
}

/// <summary>
/// Fluent builder for column definitions. Supports validation chaining.
/// </summary>
public class ColumnBuilder<T>
{
    private FieldDescriptor _descriptor;

    internal ColumnBuilder(FieldDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <summary>Add validators to this column.</summary>
    public ColumnBuilder<T> Validate(params IValidator[] validators)
    {
        var existing = _descriptor.Validators?.ToList() ?? new List<IValidator>();
        existing.AddRange(validators);
        _descriptor = _descriptor with { Validators = existing };
        return this;
    }

    /// <summary>Add async validators to this column.</summary>
    public ColumnBuilder<T> ValidateAsync(params IAsyncValidator[] validators)
    {
        var existing = _descriptor.AsyncValidators?.ToList() ?? new List<IAsyncValidator>();
        existing.AddRange(validators);
        _descriptor = _descriptor with { AsyncValidators = existing };
        return this;
    }

    /// <summary>Set a custom cell renderer for this column.</summary>
    public ColumnBuilder<T> CellRenderer(Func<object, Element> renderer)
    {
        _descriptor = _descriptor with { CellRenderer = renderer };
        return this;
    }

    /// <summary>
    /// Set a custom inline editor for this column. The delegate receives the
    /// current value and an onChange callback and returns the editor Element.
    /// See <see cref="Editors"/> for pre-built factories.
    /// </summary>
    public ColumnBuilder<T> WithEditor(Func<object, Action<object>, Element> editor)
    {
        _descriptor = _descriptor with { Editor = editor };
        return this;
    }

    /// <summary>Width / min / max / flex for the column.</summary>
    public ColumnBuilder<T> Width(double? width = null, double? min = null, double? max = null, double? flex = null)
    {
        _descriptor = _descriptor with
        {
            Width = width ?? _descriptor.Width,
            MinWidth = min ?? _descriptor.MinWidth,
            MaxWidth = max ?? _descriptor.MaxWidth,
            Flex = flex ?? _descriptor.Flex,
        };
        return this;
    }

    /// <summary>Set sortable state.</summary>
    public ColumnBuilder<T> NotSortable()
    {
        _descriptor = _descriptor with { Sortable = false };
        return this;
    }

    /// <summary>Set filterable state.</summary>
    public ColumnBuilder<T> NotFilterable()
    {
        _descriptor = _descriptor with { Filterable = false };
        return this;
    }

    /// <summary>Build the FieldDescriptor.</summary>
    public FieldDescriptor Build() => _descriptor;

    /// <summary>Implicit conversion to FieldDescriptor for clean DSL usage.</summary>
    public static implicit operator FieldDescriptor(ColumnBuilder<T> builder)
        => builder._descriptor;
}
