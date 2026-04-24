using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Generates TypeMetadata for CLR types by reflecting over public instance
/// properties and reading attributes. Used as the default fallback by TypeRegistry.
/// </summary>
public static class ReflectionTypeMetadataProvider
{
    private static readonly ConcurrentDictionary<Type, TypeMetadata> _cache = new();

    /// <summary>
    /// Generates TypeMetadata for a CLR type by reflecting over its
    /// public instance properties and reading attributes.
    /// </summary>
    public static TypeMetadata CreateMetadata(Type type)
#pragma warning disable IL2111 // Method with DynamicallyAccessedMembers parameter accessed via reflection
        => _cache.GetOrAdd(type, BuildMetadata);
#pragma warning restore IL2111

    /// <summary>
    /// Generates a FieldDescriptor for a single PropertyInfo (unbound — for registry use).
    /// </summary>
    public static FieldDescriptor CreateDescriptor(PropertyInfo property, int defaultOrder)
    {
        var attrs = ComputeAttributeData(property, defaultOrder);

        Func<object, object?, object>? setter = null;
        if (attrs.IsMutable && !attrs.IsReadOnly)
        {
            setter = (owner, val) =>
            {
                property.SetValue(owner, val);
                return owner;
            };
        }
        else if (!attrs.IsMutable && !attrs.IsReadOnly && property.CanWrite)
        {
            // init-only property — construct new object
            setter = BuildInitOnlySetter(property);
        }

        var (editorFromAttrs, rendererFromAttrs) = ResolveEditorFromDataAnnotations(property);

        return new FieldDescriptor
        {
            Name = property.Name,
            DisplayName = attrs.DisplayName,
            FieldType = property.PropertyType,
            GetValue = owner => property.GetValue(owner),
            SetValue = setter,
            Category = attrs.Category,
            Description = attrs.Description,
            Order = attrs.Order,
            IsReadOnly = attrs.IsReadOnly,
            Width = attrs.Width,
            MinWidth = attrs.MinWidth,
            MaxWidth = attrs.MaxWidth,
            Sortable = attrs.Sortable,
            Filterable = attrs.Filterable,
            Pin = attrs.Pin,
            Editor = editorFromAttrs,
            CellRenderer = rendererFromAttrs,
        };
    }

    /// <summary>
    /// Maps a small set of <see cref="System.ComponentModel.DataAnnotations"/>
    /// attributes onto Reactor editors/renderers:
    /// <list type="bullet">
    ///   <item><c>[DataType(DataType.Url)]</c> on string → URL text input + Hyperlink display</item>
    ///   <item><c>[DataType(DataType.Url)]</c> on <see cref="System.Uri"/> → Uri editor + Hyperlink display</item>
    ///   <item><c>[Range(min, max)]</c> on a numeric type → NumberBox with min/max bounds</item>
    /// </list>
    /// Returns null for either slot when no attribute dictates that slot, so
    /// TypeRegistry-driven defaults still apply.
    /// </summary>
    private static (Func<object, Action<object>, Element>? Editor, Func<object, Element>? CellRenderer)
        ResolveEditorFromDataAnnotations(PropertyInfo property)
    {
        Func<object, Action<object>, Element>? editor = null;
        Func<object, Element>? renderer = null;

        var dataType = property.GetCustomAttribute<DataTypeAttribute>();
        if (dataType is not null)
        {
            if (dataType.DataType == DataType.Url && property.PropertyType == typeof(string))
            {
                editor = Editors.Text(placeholder: "https://...");
                renderer = CellRenderers.Hyperlink();
            }
            else if (dataType.DataType == DataType.Url && property.PropertyType == typeof(global::System.Uri))
            {
                editor = Editors.Uri();
                renderer = CellRenderers.Hyperlink();
            }
        }

        var range = property.GetCustomAttribute<RangeAttribute>();
        if (range is not null && IsNumericType(property.PropertyType))
        {
            double? min = range.Minimum is IConvertible minC
                ? minC.ToDouble(global::System.Globalization.CultureInfo.InvariantCulture)
                : null;
            double? max = range.Maximum is IConvertible maxC
                ? maxC.ToDouble(global::System.Globalization.CultureInfo.InvariantCulture)
                : null;
            editor = Editors.Number(property.PropertyType, min, max);
        }

        return (editor, renderer);
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) ||
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static TypeMetadata BuildMetadata([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Where(p => !IsHidden(p))
            .OrderBy(p =>
            {
                var orderAttr = p.GetCustomAttribute<PropertyOrderAttribute>();
                return orderAttr?.Order ?? p.MetadataToken;
            })
            .ToArray();

        // Pre-compute attribute metadata once per property
        var attributeData = new PropertyAttributeData[properties.Length];
        for (int i = 0; i < properties.Length; i++)
            attributeData[i] = ComputeAttributeData(properties[i], i);

        bool hasImmutableProperties = properties.Any(p => !PropertyIsMutable(p));

        Func<object, IReadOnlyList<FieldDescriptor>> decompose = owner =>
        {
            var result = new List<FieldDescriptor>();
            for (int i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                var desc = CreateDescriptorBound(prop, attributeData[i], owner, type);
                result.Add(desc);
            }
            return result;
        };

        Func<object, IReadOnlyDictionary<string, object>, object>? compose = null;
        if (hasImmutableProperties)
            compose = BuildCompose(type, properties);

        return new TypeMetadata
        {
            Decompose = decompose,
            Compose = compose,
        };
    }

    /// <summary>
    /// Creates a FieldDescriptor with GetValue/SetValue bound to a specific owner instance.
    /// SetValue uses the return-new-owner pattern:
    ///   - Mutable: mutates in place, returns same reference
    ///   - Immutable: constructs new object, returns new reference
    ///   - Read-only: null
    /// </summary>
    private static FieldDescriptor CreateDescriptorBound(
        PropertyInfo property, PropertyAttributeData attrs, object owner,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type ownerType)
    {
        Func<object, object?, object>? setter = null;

        if (attrs.IsMutable && !attrs.IsReadOnly)
        {
            // Mutable: mutate in place, return same reference
            setter = (obj, val) =>
            {
                property.SetValue(obj, val);
                return obj;
            };
        }
        else if (!attrs.IsReadOnly && property.CanWrite && !attrs.IsMutable)
        {
            // Init-only: construct new object via Compose pattern
            var compose = BuildCompose(ownerType,
                ownerType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead).ToArray());
            if (compose is not null)
            {
                var propName = property.Name;
                setter = (obj, val) => compose(obj,
                    new Dictionary<string, object> { { propName, val! } });
            }
        }

        return new FieldDescriptor
        {
            Name = property.Name,
            DisplayName = attrs.DisplayName,
            FieldType = property.PropertyType,
            GetValue = obj => property.GetValue(obj),
            SetValue = setter,
            Category = attrs.Category,
            Description = attrs.Description,
            Order = attrs.Order,
            IsReadOnly = attrs.IsReadOnly,
            Width = attrs.Width,
            MinWidth = attrs.MinWidth,
            MaxWidth = attrs.MaxWidth,
            Sortable = attrs.Sortable,
            Filterable = attrs.Filterable,
            Pin = attrs.Pin,
        };
    }

    private static bool IsHidden(PropertyInfo property)
    {
        if (property.GetCustomAttribute<PropertyHiddenAttribute>() is not null)
            return true;
        var browsable = property.GetCustomAttribute<BrowsableAttribute>();
        if (browsable is not null && !browsable.Browsable)
            return true;
        return false;
    }

    internal static bool PropertyIsMutable(PropertyInfo property)
    {
        if (!property.CanWrite) return false;
        var setter = property.SetMethod!;
        // init-only setters have IsInitOnly metadata in newer runtimes,
        // but we detect them via the required custom modifier.
        var retParam = setter.ReturnParameter;
        var requiredMods = retParam.GetRequiredCustomModifiers();
        if (requiredMods.Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            return false;
        return setter.IsPublic;
    }

    private static PropertyAttributeData ComputeAttributeData(PropertyInfo property, int defaultOrder)
    {
        // Reactor-specific attributes (take precedence)
        var ductCategory = property.GetCustomAttribute<PropertyCategoryAttribute>();
        var ductDescription = property.GetCustomAttribute<PropertyDescriptionAttribute>();
        var ductDisplayName = property.GetCustomAttribute<PropertyDisplayNameAttribute>();
        var ductReadOnly = property.GetCustomAttribute<PropertyReadOnlyAttribute>();
        var ductOrder = property.GetCustomAttribute<PropertyOrderAttribute>();

        // Grid-specific attributes
        var colWidth = property.GetCustomAttribute<ColumnWidthAttribute>();
        var colPin = property.GetCustomAttribute<ColumnPinAttribute>();
        var notSortable = property.GetCustomAttribute<NotSortableAttribute>();
        var notFilterable = property.GetCustomAttribute<NotFilterableAttribute>();

        // global::System.ComponentModel fallback
        var scCategory = property.GetCustomAttribute<CategoryAttribute>();
        var scDescription = property.GetCustomAttribute<DescriptionAttribute>();
        var scDisplayName = property.GetCustomAttribute<DisplayNameAttribute>();
        var scReadOnly = property.GetCustomAttribute<ReadOnlyAttribute>();

        string? category = ductCategory?.Name ?? scCategory?.Category;
        string? description = ductDescription?.Text ?? scDescription?.Description;
        string? displayName = ductDisplayName?.Name ?? scDisplayName?.DisplayName;
        int order = ductOrder?.Order ?? defaultOrder;

        bool isMutable = PropertyIsMutable(property);
        bool isReadOnly = ductReadOnly is not null
            || scReadOnly?.IsReadOnly == true
            || (!isMutable && !property.CanWrite);

        return new PropertyAttributeData(
            category, description, displayName, order, isMutable, isReadOnly,
            colWidth?.Width, colWidth?.MinWidth, colWidth?.MaxWidth,
            notSortable is null, notFilterable is null,
            colPin?.Position ?? PinPosition.None);
    }

    private record PropertyAttributeData(
        string? Category,
        string? Description,
        string? DisplayName,
        int Order,
        bool IsMutable,
        bool IsReadOnly,
        double? Width,
        double? MinWidth,
        double? MaxWidth,
        bool Sortable,
        bool Filterable,
        PinPosition Pin);

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "BuildInitOnlySetter uses reflection to compose init-only properties.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "BuildInitOnlySetter uses reflection to compose init-only properties.")]
    internal static Func<object, object?, object>? BuildInitOnlySetter(PropertyInfo property)
    {
        var type = property.DeclaringType!;
        var compose = BuildCompose(type,
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead).ToArray());
        if (compose is null) return null;

        var propName = property.Name;
        return (owner, val) => compose(owner,
            new Dictionary<string, object> { { propName, val! } });
    }

    internal static Func<object, IReadOnlyDictionary<string, object>, object>? BuildCompose(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type, PropertyInfo[] properties)
    {
        // Try to find a constructor whose parameters match property names (case-insensitive)
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        foreach (var ctor in ctors.OrderByDescending(c => c.GetParameters().Length))
        {
            var ctorParams = ctor.GetParameters();
            if (ctorParams.Length == 0) continue;

            // Check if all ctor params match property names
            var paramToProperty = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in properties)
                paramToProperty[prop.Name] = prop;

            bool allMatch = ctorParams.All(p =>
                p.Name is not null && paramToProperty.ContainsKey(p.Name));

            if (!allMatch) continue;

            // Found a matching constructor
            var matchedCtor = ctor;
            var matchedParams = ctorParams;
            var matchedMap = paramToProperty;

            return (currentValue, updates) =>
            {
                var args = new object?[matchedParams.Length];
                for (int i = 0; i < matchedParams.Length; i++)
                {
                    var paramName = matchedParams[i].Name!;
                    var propertyName = matchedMap[paramName].Name;
                    if (updates.TryGetValue(propertyName, out var updatedValue))
                    {
                        args[i] = updatedValue;
                    }
                    else
                    {
                        args[i] = matchedMap[paramName].GetValue(currentValue);
                    }
                }
                return matchedCtor.Invoke(args);
            };
        }

        // Fallback: try Activator.CreateInstance + init-only setter reflection
        var parameterlessCtor = type.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor is not null)
        {
            return (currentValue, updates) =>
            {
                var newObj = Activator.CreateInstance(type)!;
                foreach (var prop in properties)
                {
                    if (!prop.CanWrite) continue;
                    var value = updates.TryGetValue(prop.Name, out var updated)
                        ? updated
                        : prop.GetValue(currentValue);
                    prop.SetValue(newObj, value);
                }
                return newObj;
            };
        }

        return null;
    }
}
