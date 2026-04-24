using System.Diagnostics.CodeAnalysis;
using OpenClawTray.Infrastructure.Core;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Maps CLR types to TypeMetadata for the PropertyGrid.
/// Provides a fluent Register API and a Resolve method with built-in fallbacks.
/// </summary>
public class TypeRegistry
{
    private readonly Dictionary<Type, TypeMetadata> _map = new();
    private readonly Dictionary<Type, Func<object, Element>> _cellRenderers = new();
    private readonly Dictionary<Type, Func<object?, string>> _formatters = new();

    /// <summary>Register metadata for a type.</summary>
    public TypeRegistry Register<T>(TypeMetadata metadata)
    {
        _map[typeof(T)] = metadata;
        return this;
    }

    /// <summary>Register a custom cell renderer for grid display.</summary>
    public TypeRegistry RegisterCellRenderer<T>(Func<object, Element> renderer)
    {
        _cellRenderers[typeof(T)] = renderer;
        return this;
    }

    /// <summary>Register a custom value formatter for grid display.</summary>
    public TypeRegistry RegisterFormatter<T>(Func<object?, string> formatter)
    {
        _formatters[typeof(T)] = formatter;
        return this;
    }

    /// <summary>
    /// Try to get a cell renderer for a type. Falls back to built-in defaults
    /// for common primitive/date/uri/color types so <c>AutoColumns</c> picks
    /// up sensible display renderers without requiring explicit registration.
    /// </summary>
    public Func<object, Element>? GetCellRenderer(Type type)
    {
        if (_cellRenderers.TryGetValue(type, out var renderer))
            return renderer;
        // TryResolveCellRendererFallback constructs a fresh delegate each call
        // (CellRenderers.* factory methods return lambdas). Cache the first
        // resolution so DataGrid's per-cell render path doesn't re-allocate.
        renderer = TryResolveCellRendererFallback(type);
        if (renderer is not null)
            _cellRenderers[type] = renderer;
        return renderer;
    }

    /// <summary>Try to get a registered formatter for a type.</summary>
    public Func<object?, string>? GetFormatter(Type type)
        => _formatters.TryGetValue(type, out var formatter) ? formatter : null;

    /// <summary>
    /// Resolve metadata for a type. Falls back to built-in rules:
    /// 1. Exact match in registry
    /// 2. Enum — auto-generated ComboBox editor
    /// 3. CLR primitive — built-in editor
    /// 4. Array/IList&lt;T&gt; — array editor
    /// 5. Record/class/struct — reflection-based decomposition
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Resolve may use Activator.CreateInstance on runtime-determined types.")]
    public TypeMetadata Resolve(Type type)
    {
        // 1. Exact match
        if (_map.TryGetValue(type, out var metadata))
            return metadata;

        // 2. Enum
        if (type.IsEnum)
            return ResolveEnum(type);

        // 3. CLR primitives
        if (TryResolvePrimitive(type, out var primitive))
            return primitive;

        // 4. Array / IList<T>
        if (TryResolveArray(type, out var array))
            return array;

        // 5. Reflection fallback
        return ReflectionTypeMetadataProvider.CreateMetadata(type);
    }

    /// <summary>
    /// Resolves the editor for a type using tiered resolution:
    /// For compact (grid): CompactEditor ?? Editor ?? built-in
    /// For standard (PropertyGrid/FormField): Editor ?? built-in
    /// For full (expand): FullEditor (null when not registered)
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ResolveEditor calls Resolve; acceptable for non-AOT builds.")]
    public Func<object, Action<object>, Element>? ResolveEditor(Type type, EditorTier tier)
    {
        var meta = Resolve(type);
        return tier switch
        {
            EditorTier.Compact => meta.CompactEditor ?? meta.Editor,
            EditorTier.Full => meta.FullEditor,
            _ => meta.Editor,
        };
    }

    private static TypeMetadata ResolveEnum(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        return new TypeMetadata
        {
            Editor = (value, onChange) =>
            {
                var currentIndex = Array.IndexOf(names, value.ToString());
                return ComboBox(names, currentIndex >= 0 ? currentIndex : 0,
                    index => onChange(Enum.Parse(enumType, names[index])));
            }
        };
    }

    private static bool TryResolvePrimitive(Type type, [NotNullWhen(true)] out TypeMetadata? metadata)
    {
        metadata = null;

        if (type == typeof(string))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    TextField((string)(value ?? ""), s => onChange(s))
            };
            return true;
        }

        if (type == typeof(bool))
        {
            // Density-aware: PropertyGrid (Standard) renders a ToggleSwitch — it's
            // roomier and reads better as a standalone field. DataGrid (Compact)
            // renders a CheckBox so the cell stays dense. Both are wired via the
            // shared Editors catalog so behavior stays consistent with any typed
            // column factory a caller uses explicitly.
            metadata = new TypeMetadata
            {
                Editor = Editors.Toggle(),
                CompactEditor = Editors.CheckBox(),
            };
            return true;
        }

        if (type == typeof(global::System.DateTime))
        {
            metadata = new TypeMetadata { Editor = Editors.Date() };
            return true;
        }

        if (type == typeof(global::System.DateTimeOffset))
        {
            metadata = new TypeMetadata { Editor = Editors.DateOffset() };
            return true;
        }

        if (type == typeof(global::System.DateOnly))
        {
            metadata = new TypeMetadata { Editor = Editors.DateOnly() };
            return true;
        }

        if (type == typeof(global::System.TimeSpan))
        {
            metadata = new TypeMetadata { Editor = Editors.TimeSpanEditor() };
            return true;
        }

        if (type == typeof(global::System.TimeOnly))
        {
            metadata = new TypeMetadata { Editor = Editors.TimeOnlyEditor() };
            return true;
        }

        if (type == typeof(global::System.Uri))
        {
            metadata = new TypeMetadata { Editor = Editors.Uri() };
            return true;
        }

        if (type == typeof(global::Windows.UI.Color))
        {
            // Standard tier (PropertyGrid) gets the full ColorPicker inline;
            // Compact tier (DataGrid cells) gets swatch + hex text input so a
            // cell doesn't blow up into a huge picker at row height.
            metadata = new TypeMetadata
            {
                Editor = Editors.Color(),
                CompactEditor = Editors.ColorCompact(),
            };
            return true;
        }

        if (type == typeof(int))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((int)(value ?? 0), v => onChange((int)v))
            };
            return true;
        }

        if (type == typeof(long))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((long)(value ?? 0L), v => onChange((long)v))
            };
            return true;
        }

        if (type == typeof(short))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((short)(value ?? (short)0), v => onChange((short)v))
            };
            return true;
        }

        if (type == typeof(byte))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((byte)(value ?? (byte)0), v => onChange((byte)v))
            };
            return true;
        }

        if (type == typeof(float))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((float)(value ?? 0f), v => onChange((float)v))
            };
            return true;
        }

        if (type == typeof(double))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((double)(value ?? 0d), v => onChange(v))
            };
            return true;
        }

        if (type == typeof(decimal))
        {
            metadata = new TypeMetadata
            {
                Editor = (value, onChange) =>
                    NumberBox((double)(decimal)(value ?? 0m), v => onChange((decimal)v))
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Default cell renderers for common display types. Mirrors the fallback
    /// pattern of <see cref="TryResolvePrimitive"/> for editors — so a
    /// <c>AutoColumns&lt;T&gt;()</c> call on a type with a Uri or Color
    /// property gets a hyperlink / color swatch display without any explicit
    /// registration step.
    /// </summary>
    private static Func<object, Element>? TryResolveCellRendererFallback(Type type)
    {
        if (type == typeof(bool)) return CellRenderers.CheckMark();
        if (type == typeof(global::System.DateTime)) return CellRenderers.Date();
        if (type == typeof(global::System.DateTimeOffset)) return CellRenderers.Date();
        if (type == typeof(global::System.DateOnly)) return CellRenderers.Date();
        if (type == typeof(global::System.TimeSpan)) return CellRenderers.Time();
        if (type == typeof(global::System.TimeOnly)) return CellRenderers.Time();
        if (type == typeof(global::System.Uri)) return CellRenderers.Hyperlink();
        if (type == typeof(global::Windows.UI.Color)) return CellRenderers.ColorSwatch();
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "TryResolveArray uses Activator.CreateInstance; acceptable for non-AOT builds.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "TryResolveArray uses reflection to check constructors on element types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2065", Justification = "TryResolveArray uses reflection to check constructors on element types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = "TryResolveArray creates instances of element types via Activator.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "TryResolveArray creates instances of element types via Activator.")]
    private static bool TryResolveArray(Type type, [NotNullWhen(true)] out TypeMetadata? metadata)
    {
        metadata = null;
        Type? elementType = null;

        if (type.IsArray)
        {
            elementType = type.GetElementType();
        }
        else if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) || genDef == typeof(IList<>))
                elementType = type.GetGenericArguments()[0];
        }

        if (elementType is null)
            return false;

        Func<Task<object?>>? createElement = null;
        if (elementType.GetConstructor(Type.EmptyTypes) is not null)
        {
            createElement = () => Task.FromResult<object?>(Activator.CreateInstance(elementType));
        }

        metadata = new ArrayTypeMetadata
        {
            CreateElement = createElement,
        };
        return true;
    }
}

/// <summary>
/// Specifies which editor tier to resolve.
/// </summary>
public enum EditorTier
{
    /// <summary>Standard editor for PropertyGrid/FormField.</summary>
    Standard,
    /// <summary>Compact editor for grid inline cells.</summary>
    Compact,
    /// <summary>Full editor for expanded/flyout editing.</summary>
    Full,
}
