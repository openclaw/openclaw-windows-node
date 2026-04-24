using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Hand-rolled IXamlMetadataProvider that covers system primitives, core Microsoft.UI.Xaml
/// types, common value structs, and the enums referenced by XamlControlsResources' internal
/// XAML. Needed under native AOT because the default XamlControlsXamlMetaDataProvider only
/// covers types in the Microsoft.UI.Xaml.Controls namespace. Under JIT, the XAML parser
/// falls back to reflective type resolution for unknown names; under AOT that path throws,
/// producing a STATUS_APPLICATION_INTERNAL_EXCEPTION crash inside Microsoft.UI.Xaml.dll
/// during Application bootstrap (when XamlControlsResources loads its theme dictionaries).
/// </summary>
internal sealed partial class ReactorCoreXamlMetaDataProvider : IXamlMetadataProvider
{
    private static readonly (string Name, Type Type)[] s_entries =
    [
        // System primitives — WinUI queries these during Setter Value resolution and schema checks.
        ("Object",   typeof(object)),
        ("Boolean",  typeof(bool)),
        ("Byte",     typeof(byte)),
        ("Int16",    typeof(short)),
        ("Int32",    typeof(int)),
        ("Int64",    typeof(long)),
        ("Single",   typeof(float)),
        ("Double",   typeof(double)),
        ("Char",     typeof(char)),
        ("String",   typeof(string)),
        ("DateTime", typeof(DateTime)),
        ("TimeSpan", typeof(TimeSpan)),
        ("Guid",     typeof(Guid)),
        ("Uri",      typeof(Uri)),

        // Core Microsoft.UI.Xaml — not in XamlControlsXamlMetaDataProvider because
        // that one only covers Microsoft.UI.Xaml.Controls.*
        ("Microsoft.UI.Xaml.DependencyObject",   typeof(DependencyObject)),
        ("Microsoft.UI.Xaml.UIElement",          typeof(UIElement)),
        ("Microsoft.UI.Xaml.FrameworkElement",   typeof(FrameworkElement)),
        ("Microsoft.UI.Xaml.ResourceDictionary", typeof(ResourceDictionary)),
        ("Microsoft.UI.Xaml.Style",              typeof(Style)),
        ("Microsoft.UI.Xaml.Setter",             typeof(Setter)),
        ("Microsoft.UI.Xaml.SetterBase",         typeof(SetterBase)),
        ("Microsoft.UI.Xaml.DataTemplate",       typeof(DataTemplate)),
        ("Microsoft.UI.Xaml.FrameworkTemplate",  typeof(FrameworkTemplate)),

        // Enums referenced by Setter values in theme dictionaries.
        ("Microsoft.UI.Xaml.Visibility",          typeof(Visibility)),
        ("Microsoft.UI.Xaml.HorizontalAlignment", typeof(HorizontalAlignment)),
        ("Microsoft.UI.Xaml.VerticalAlignment",   typeof(VerticalAlignment)),
        ("Microsoft.UI.Xaml.TextAlignment",       typeof(TextAlignment)),
        ("Microsoft.UI.Xaml.TextWrapping",        typeof(TextWrapping)),
        ("Microsoft.UI.Xaml.TextTrimming",        typeof(TextTrimming)),
        ("Microsoft.UI.Xaml.FlowDirection",       typeof(FlowDirection)),
        ("Microsoft.UI.Xaml.GridUnitType",        typeof(GridUnitType)),
        ("Microsoft.UI.Xaml.Controls.Orientation",typeof(Orientation)),
        ("Microsoft.UI.Xaml.Controls.ControlTemplate", typeof(ControlTemplate)),

        // Structs serialized in XAML attribute form.
        ("Microsoft.UI.Xaml.Thickness",    typeof(Thickness)),
        ("Microsoft.UI.Xaml.CornerRadius", typeof(CornerRadius)),
        ("Microsoft.UI.Xaml.GridLength",   typeof(GridLength)),
        ("Microsoft.UI.Xaml.Duration",     typeof(Duration)),

        // Media primitives.
        ("Microsoft.UI.Xaml.Media.Brush",           typeof(Brush)),
        ("Microsoft.UI.Xaml.Media.SolidColorBrush", typeof(SolidColorBrush)),

        // Windows namespace structs used in XAML.
        ("Windows.UI.Color",         typeof(global::Windows.UI.Color)),
        ("Windows.Foundation.Size",  typeof(global::Windows.Foundation.Size)),
        ("Windows.Foundation.Point", typeof(global::Windows.Foundation.Point)),
        ("Windows.Foundation.Rect",  typeof(global::Windows.Foundation.Rect)),
    ];

    private static readonly Dictionary<string, Type> s_byName = BuildNameMap();
    private static readonly Dictionary<Type, string> s_byType = BuildTypeMap();

    private static Dictionary<string, Type> BuildNameMap()
    {
        var map = new Dictionary<string, Type>(s_entries.Length, StringComparer.Ordinal);
        foreach (var (name, type) in s_entries)
            map[name] = type;
        return map;
    }

    private static Dictionary<Type, string> BuildTypeMap()
    {
        var map = new Dictionary<Type, string>(s_entries.Length);
        foreach (var (name, type) in s_entries)
            map[type] = name;
        return map;
    }

    public IXamlType? GetXamlType(Type type)
        => s_byType.TryGetValue(type, out var name) ? new CoreXamlType(name, type) : null;

    public IXamlType? GetXamlType(string fullName)
        => s_byName.TryGetValue(fullName, out var type) ? new CoreXamlType(fullName, type) : null;

    public XmlnsDefinition[] GetXmlnsDefinitions() => [];

    /// <summary>
    /// Minimal IXamlType that satisfies schema-level lookups. WinUI's XAML loader calls
    /// GetXamlType during parsing to verify that types referenced in XAML exist; for system
    /// and schema-only types, returning a non-null stub with correct FullName + UnderlyingType
    /// is sufficient. Activation and member access are unreachable for these types because
    /// Reactor apps do not construct them from XAML markup — they only appear as schema
    /// references inside the WinUI theme dictionaries.
    /// </summary>
    private sealed partial class CoreXamlType : IXamlType
    {
        public CoreXamlType(string fullName, Type underlyingType)
        {
            FullName = fullName;
            UnderlyingType = underlyingType;
        }

        public string FullName { get; }
        public Type UnderlyingType { get; }
        public IXamlType? BaseType => null;
        public IXamlMember? ContentProperty => null;
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => false;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => false;
        public bool IsReturnTypeStub => false;
        public bool IsLocalType => false;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public IXamlMember? GetMember(string name) => null;
        public object ActivateInstance() => throw new NotSupportedException($"{FullName} is schema-only; cannot activate from XAML.");
        public void AddToMap(object instance, object key, object item) => throw new NotSupportedException();
        public void AddToVector(object instance, object item) => throw new NotSupportedException();
        public void RunInitializer() { }

        public object CreateFromString(string input)
        {
            if (UnderlyingType.IsEnum)
                return Enum.Parse(UnderlyingType, input, ignoreCase: true);
            throw new NotSupportedException($"Cannot parse '{input}' for schema-only type {FullName}.");
        }
    }
}
