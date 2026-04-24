using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Registers property-inspection, resource-browsing, and style-inspection
/// MCP tools: <c>properties</c>, <c>setProperty</c>, <c>resources</c>,
/// <c>setResource</c>, <c>styles</c>, <c>ancestors</c>.
/// </summary>
internal static class DevtoolsPropertyTools
{
    public static void Register(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        Register_Properties(server, resolver);
        Register_SetProperty(server, resolver);
        Register_Resources(server, resolver);
        Register_SetResource(server, resolver);
        Register_Styles(server, resolver);
        Register_Ancestors(server, resolver);
    }

    // -- properties --------------------------------------------------------------

    private static void Register_Properties(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "properties",
                Description: "Read dependency properties on a UI element. Pass `name` to read a single property, or omit to enumerate all. Returns value, type, and whether the value is locally set.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Element selector." },
                        name = new { type = "string", description = "Optional DP name (e.g. 'Width', 'Margin'). Omit to list all." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var name = DevtoolsTools.ReadString(@params, "name");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);

                if (name is not null)
                {
                    var (dp, field) = FindDependencyProperty(el, name);
                    var value = el.GetValue(dp);
                    bool isLocal;
                    try { isLocal = !Equals(el.ReadLocalValue(dp), DependencyProperty.UnsetValue); }
                    catch { isLocal = false; }
                    return new
                    {
                        name,
                        value = FormatValue(value),
                        valueType = value?.GetType().Name ?? "null",
                        declaringType = field.DeclaringType?.Name,
                        isLocal,
                    };
                }

                // Enumerate all DPs via reflection on the type hierarchy.
                var props = EnumerateDependencyProperties(el);
                return (object)new
                {
                    count = props.Count,
                    properties = props,
                };
            }));
    }

    // -- setProperty -------------------------------------------------------------

    private static void Register_SetProperty(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "setProperty",
                Description: "Set a dependency property on a UI element. Value is parsed from string (supports Thickness, CornerRadius, Brush hex, enums, bool, double, int).",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Element selector." },
                        name = new { type = "string", description = "DP name (e.g. 'Width', 'Margin', 'Background')." },
                        value = new { type = "string", description = "Value as string (e.g. '10', '1,2,3,4', '#FF0000', 'Visible')." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    required = new[] { "selector", "name", "value" },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var name = RequiredString(@params, "name");
                var raw = RequiredString(@params, "value");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);

                var (dp, field) = FindDependencyProperty(el, name);

                // Determine the target type from the DP field's declaring context.
                // WinUI DPs don't expose PropertyType directly, so we infer from the
                // current value's type, or fall back to the raw string.
                var currentValue = el.GetValue(dp);
                var targetType = currentValue?.GetType();
                var parsed = ParseValue(raw, targetType);
                el.SetValue(dp, parsed);

                return new
                {
                    ok = true,
                    name,
                    newValue = FormatValue(el.GetValue(dp)),
                };
            }));
    }

    // -- resources ---------------------------------------------------------------

    private static void Register_Resources(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "resources",
                Description: "Browse XAML resources. Walks the ResourceDictionary chain from element → ancestor elements → window → application (including MergedDictionaries and ThemeDictionaries). Filter by regex on key.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Element selector (optional — starts walk from this element's Resources)." },
                        scope = new { type = "string", description = "'element', 'window', or 'app' (default 'app'). Controls how far up the chain to walk." },
                        filter = new { type = "string", description = "Regex filter on resource key." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var selector = DevtoolsTools.ReadString(@params, "selector");
                var scope = DevtoolsTools.ReadString(@params, "scope") ?? "app";
                var filter = DevtoolsTools.ReadString(@params, "filter");
                var windowId = DevtoolsTools.ReadString(@params, "window");

                // Validate scope.
                if (scope is not ("element" or "window" or "app"))
                    throw new McpToolException($"Invalid scope '{scope}'. Must be 'element', 'window', or 'app'.", JsonRpcErrorCodes.InvalidParams);

                if (scope is "element" or "window" && selector is null)
                    throw new McpToolException($"Scope '{scope}' requires a selector.", JsonRpcErrorCodes.InvalidParams);

                Regex? filterRe = null;
                if (filter is not null)
                {
                    try { filterRe = new Regex(filter, RegexOptions.IgnoreCase); }
                    catch { throw new McpToolException($"Invalid regex: {filter}", JsonRpcErrorCodes.InvalidParams); }
                }

                var results = new List<object>();

                // Resolve starting element if given.
                FrameworkElement? startEl = null;
                if (selector is not null)
                    startEl = resolver.Resolve(selector, windowId) as FrameworkElement;

                // Walk resource scopes: element → ancestor elements → window/root → app.
                if (startEl is not null && (scope is "element" or "window" or "app"))
                    CollectResources(startEl.Resources, "element", filterRe, results);

                if (scope is "window" or "app")
                {
                    // Walk visual tree ancestors, collecting each element's Resources.
                    if (startEl is not null)
                    {
                        var parent = VisualTreeHelper.GetParent(startEl) as FrameworkElement;
                        while (parent is not null)
                        {
                            CollectResources(parent.Resources, $"ancestor:{parent.GetType().Name}", filterRe, results);
                            parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                        }
                    }
                }

                if (scope is "app")
                    CollectResources(Application.Current.Resources, "app", filterRe, results);

                return new { count = results.Count, resources = results };
            }));
    }

    // -- setResource -------------------------------------------------------------

    private static void Register_SetResource(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "setResource",
                Description: "Set or add a XAML resource in a ResourceDictionary at the specified scope.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string", description = "Resource key." },
                        value = new { type = "string", description = "Value as string (same parsing as setProperty)." },
                        scope = new { type = "string", description = "'element', 'window', or 'app' (default 'app')." },
                        selector = new { type = "string", description = "Element selector (required when scope is 'element')." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    required = new[] { "key", "value" },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var key = RequiredString(@params, "key");
                var raw = RequiredString(@params, "value");
                var scope = DevtoolsTools.ReadString(@params, "scope") ?? "app";
                var selector = DevtoolsTools.ReadString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");

                ResourceDictionary dict;
                bool existedAtScope;
                if (scope is "element")
                {
                    if (selector is null)
                        throw new McpToolException("selector is required when scope is 'element'.", JsonRpcErrorCodes.InvalidParams);
                    var el = resolver.Resolve(selector, windowId) as FrameworkElement
                        ?? throw new McpToolException("Element is not a FrameworkElement.", JsonRpcErrorCodes.ToolExecution);
                    dict = el.Resources;
                }
                else if (scope is "window")
                {
                    // Walk up to root from selector, or use app resources.
                    FrameworkElement? root = null;
                    if (selector is not null)
                    {
                        root = resolver.Resolve(selector, windowId) as FrameworkElement;
                        if (root is not null)
                        {
                            var parent = VisualTreeHelper.GetParent(root) as FrameworkElement;
                            while (parent is not null) { root = parent; parent = VisualTreeHelper.GetParent(root) as FrameworkElement; }
                        }
                    }
                    dict = root?.Resources ?? Application.Current.Resources;
                }
                else
                {
                    dict = Application.Current.Resources;
                }

                existedAtScope = dict.ContainsKey(key);

                // Try to infer target type from existing value.
                Type? targetType = null;
                if (dict.ContainsKey(key))
                    targetType = dict[key]?.GetType();

                var parsed = ParseValue(raw, targetType);
                dict[key] = parsed;

                return new { ok = true, key, newValue = FormatValue(parsed), replaced = existedAtScope };
            }));
    }

    // -- styles ------------------------------------------------------------------

    private static void Register_Styles(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "styles",
                Description: "Inspect the explicitly-assigned Style on a UI element: TargetType, Setters (property + value), and the BasedOn chain. Note: returns null when only a default/theme style is active — WinUI does not expose the resolved implicit style.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Element selector." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);

                if (el is not FrameworkElement fe)
                    throw new McpToolException("Element is not a FrameworkElement.", JsonRpcErrorCodes.ToolExecution);

                var style = fe.Style;
                if (style is null)
                    return (object)new { hasStyle = false };

                return (object)new { hasStyle = true, style = DescribeStyle(style) };
            }));
    }

    // -- ancestors ---------------------------------------------------------------

    private static void Register_Ancestors(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "ancestors",
                Description: "Walk the visual tree upward from the matched element to the root. Returns type, name, and automationId for each ancestor.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Element selector." },
                        window = new { type = "string", description = "Window id (omit for default)." },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            (@params) => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);

                var chain = new List<object>();
                var current = VisualTreeHelper.GetParent(el);
                while (current is not null)
                {
                    var fe = current as FrameworkElement;
                    chain.Add(new
                    {
                        type = current.GetType().Name,
                        name = fe?.Name,
                        automationId = fe is not null
                            ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(fe)
                            : null,
                    });
                    current = VisualTreeHelper.GetParent(current);
                }
                return new { count = chain.Count, ancestors = chain };
            }));
    }

    // -- helpers ------------------------------------------------------------------

    private static string RequiredString(JsonElement? args, string key) =>
        DevtoolsTools.ReadString(args, key)
            ?? throw new McpToolException($"Missing required argument '{key}'.",
                JsonRpcErrorCodes.InvalidParams);

    /// <summary>Find a static DependencyProperty field on the element's type hierarchy,
    /// or on an owner type for attached properties (e.g. "Grid.Row").</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools uses reflection to discover DependencyProperty fields.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Devtools reflection for property discovery.")]
    private static (DependencyProperty dp, FieldInfo field) FindDependencyProperty(UIElement el, string name)
    {
        // Support attached property syntax: "Grid.Row" → look on Grid type.
        if (name.Contains('.'))
        {
            var parts = name.Split('.', 2);
            var ownerName = parts[0];
            var propName = parts[1];
            var fieldName = propName.EndsWith("Property", StringComparison.Ordinal) ? propName : propName + "Property";

            // Search well-known WinUI namespaces for the owner type.
            var ownerType = FindTypeByName(ownerName);
            if (ownerType is not null)
            {
                var field = ownerType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (field is not null && field.FieldType == typeof(DependencyProperty))
                    return ((DependencyProperty)field.GetValue(null)!, field);
            }
            throw new McpToolException(
                $"No attached DependencyProperty '{name}' found. Check the owner type name.",
                JsonRpcErrorCodes.InvalidParams);
        }

        // Convention: property "Foo" maps to static field "FooProperty".
        var dpFieldName = name.EndsWith("Property", StringComparison.Ordinal) ? name : name + "Property";

        for (var type = el.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(dpFieldName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field is not null && field.FieldType == typeof(DependencyProperty))
            {
                var dp = (DependencyProperty)field.GetValue(null)!;
                return (dp, field);
            }
        }

        throw new McpToolException(
            $"No DependencyProperty '{name}' found on {el.GetType().Name} or its base types. For attached properties, use 'OwnerType.Property' syntax (e.g. 'Grid.Row').",
            JsonRpcErrorCodes.InvalidParams);
    }

    /// <summary>Resolve a short type name to a WinUI type (Grid, Canvas, ToolTipService, etc.).</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools uses Assembly.GetType to resolve WinUI type names at runtime.")]
    private static Type? FindTypeByName(string name)
    {
        // Search the Microsoft.UI.Xaml assemblies.
        var candidates = new[]
        {
            typeof(Grid).Assembly,          // Microsoft.WinUI
            typeof(UIElement).Assembly,      // Microsoft.WinUI
        };
        foreach (var asm in candidates.Distinct())
        {
            foreach (var ns in new[] { "Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml", "Microsoft.UI.Xaml.Media", "Microsoft.UI.Xaml.Controls.Primitives" })
            {
                var type = asm.GetType($"{ns}.{name}");
                if (type is not null) return type;
            }
        }
        return null;
    }

    /// <summary>Enumerate all public static DependencyProperty fields on the element's type chain.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools uses reflection to enumerate DependencyProperty fields.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Devtools reflection for property enumeration.")]
    private static List<object> EnumerateDependencyProperties(UIElement el)
    {
        var seen = new HashSet<string>();
        var results = new List<object>();

        for (var type = el.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var f in fields)
            {
                if (f.FieldType != typeof(DependencyProperty)) continue;
                var dp = (DependencyProperty)f.GetValue(null)!;
                var propName = f.Name.EndsWith("Property", StringComparison.Ordinal)
                    ? f.Name[..^8]
                    : f.Name;

                if (!seen.Add(propName)) continue;

                object? value;
                try { value = el.GetValue(dp); }
                catch { value = "<error>"; }

                var isLocal = false;
                try { isLocal = !Equals(el.ReadLocalValue(dp), DependencyProperty.UnsetValue); }
                catch { }

                results.Add(new
                {
                    name = propName,
                    value = FormatValue(value),
                    valueType = value?.GetType().Name ?? "null",
                    declaringType = type.Name,
                    isLocal,
                });
            }
        }

        return results;
    }

    /// <summary>Format a DP value to a JSON-friendly string.</summary>
    internal static string? FormatValue(object? value)
    {
        if (value is null) return null;
        return value switch
        {
            SolidColorBrush b => $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
            Thickness t => string.Create(
                CultureInfo.InvariantCulture,
                $"{t.Left},{t.Top},{t.Right},{t.Bottom}"),
            CornerRadius cr => string.Create(
                CultureInfo.InvariantCulture,
                $"{cr.TopLeft},{cr.TopRight},{cr.BottomRight},{cr.BottomLeft}"),
            global::Windows.UI.Color c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            Brush _ => value.GetType().Name, // LinearGradientBrush etc. — just report type
            IFormattable f => f.ToString(format: null, formatProvider: CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>Parse a string value into a typed object, guided by an optional target type hint.</summary>
    internal static object? ParseValue(string raw, Type? targetType)
    {
        // Enum parse first if we have a target type that's an enum.
        if (targetType is not null && targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, raw, ignoreCase: true, out var enumVal))
                return enumVal;
        }

        // Well-known WinUI types.
        if (targetType == typeof(Visibility) || raw.Equals("Visible", StringComparison.OrdinalIgnoreCase))
            if (Enum.TryParse<Visibility>(raw, ignoreCase: true, out var vis)) return vis;

        if (targetType == typeof(HorizontalAlignment))
            if (Enum.TryParse<HorizontalAlignment>(raw, ignoreCase: true, out var ha)) return ha;

        if (targetType == typeof(VerticalAlignment))
            if (Enum.TryParse<VerticalAlignment>(raw, ignoreCase: true, out var va)) return va;

        // Bool.
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        // Thickness: "1" → uniform, "1,2" → (left/right, top/bottom), "1,2,3,4"
        if (targetType == typeof(Thickness) || (raw.Contains(',') && TryParseThickness(raw, out var thickness)))
            if (TryParseThickness(raw, out thickness)) return thickness;

        // CornerRadius: same pattern.
        if (targetType == typeof(CornerRadius) || (raw.Contains(',') && TryParseCornerRadius(raw, out var cr)))
            if (TryParseCornerRadius(raw, out cr)) return cr;

        // Brush / Color: hex string.
        if (raw.StartsWith('#'))
        {
            if (TryParseColor(raw, out var color))
                return new SolidColorBrush(color);
        }

        // Double.
        if (targetType == typeof(double) || raw.Contains('.'))
            if (double.TryParse(raw, CultureInfo.InvariantCulture, out var d)) return d;

        // Int.
        if (targetType == typeof(int))
            if (int.TryParse(raw, CultureInfo.InvariantCulture, out var i)) return i;

        // Double fallback for numeric strings.
        if (double.TryParse(raw, CultureInfo.InvariantCulture, out var dbl)) return dbl;

        // If a targetType was specified and we fell through, the input is invalid for that type.
        if (targetType is not null)
            throw new McpToolException($"Cannot parse '{raw}' as {targetType.Name}.", JsonRpcErrorCodes.InvalidParams);

        // String fallback.
        return raw;
    }

    internal static bool TryParseThickness(string raw, out Thickness result)
    {
        result = default;
        var parts = raw.Split(',');
        switch (parts.Length)
        {
            case 1 when double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var u):
                result = new Thickness(u);
                return true;
            case 2 when double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var lr) && double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var tb):
                result = new Thickness(lr, tb, lr, tb);
                return true;
            case 4 when double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var l) && double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var t)
                     && double.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, out var r) && double.TryParse(parts[3].Trim(), CultureInfo.InvariantCulture, out var b):
                result = new Thickness(l, t, r, b);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryParseCornerRadius(string raw, out CornerRadius result)
    {
        result = default;
        var parts = raw.Split(',');
        switch (parts.Length)
        {
            case 1 when double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var u):
                result = new CornerRadius(u);
                return true;
            case 4 when double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var tl) && double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var tr)
                     && double.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, out var br) && double.TryParse(parts[3].Trim(), CultureInfo.InvariantCulture, out var bl):
                result = new CornerRadius(tl, tr, br, bl);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryParseColor(string hex, out global::Windows.UI.Color color)
    {
        color = default;
        var h = hex.TrimStart('#');
        try
        {
            switch (h.Length)
            {
                case 3:
                    // Expand #RGB → #RRGGBB
                    color = global::Windows.UI.Color.FromArgb(0xFF,
                        byte.Parse($"{h[0]}{h[0]}", NumberStyles.HexNumber),
                        byte.Parse($"{h[1]}{h[1]}", NumberStyles.HexNumber),
                        byte.Parse($"{h[2]}{h[2]}", NumberStyles.HexNumber));
                    return true;
                case 6:
                    color = global::Windows.UI.Color.FromArgb(0xFF,
                        byte.Parse(h[0..2], NumberStyles.HexNumber),
                        byte.Parse(h[2..4], NumberStyles.HexNumber),
                        byte.Parse(h[4..6], NumberStyles.HexNumber));
                    return true;
                case 8:
                    color = global::Windows.UI.Color.FromArgb(
                        byte.Parse(h[0..2], NumberStyles.HexNumber),
                        byte.Parse(h[2..4], NumberStyles.HexNumber),
                        byte.Parse(h[4..6], NumberStyles.HexNumber),
                        byte.Parse(h[6..8], NumberStyles.HexNumber));
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static void CollectResources(ResourceDictionary dict, string scope, Regex? filter, List<object> results)
    {
        foreach (var key in dict.Keys)
        {
            var keyStr = key?.ToString() ?? "";
            if (filter is not null && !filter.IsMatch(keyStr)) continue;

            object? val;
            try { val = dict[key]; }
            catch { val = null; }

            results.Add(new
            {
                key = keyStr,
                valueType = val?.GetType().Name ?? "null",
                value = FormatValue(val),
                scope,
            });
        }

        // Walk MergedDictionaries.
        foreach (var merged in dict.MergedDictionaries)
            CollectResources(merged, scope + "/merged", filter, results);

        // Walk ThemeDictionaries.
        foreach (var kvp in dict.ThemeDictionaries)
        {
            if (kvp.Value is ResourceDictionary themeDict)
            {
                var themeName = kvp.Key?.ToString() ?? "unknown";
                CollectResources(themeDict, $"{scope}/theme:{themeName}", filter, results);
            }
        }
    }

    private static object DescribeStyle(Style style)
    {
        var setters = new List<object>();
        foreach (var setterBase in style.Setters)
        {
            if (setterBase is Setter setter)
            {
                setters.Add(new
                {
                    property = setter.Property?.ToString() ?? "unknown",
                    value = FormatValue(setter.Value),
                    valueType = setter.Value?.GetType().Name ?? "null",
                });
            }
        }

        return new
        {
            targetType = style.TargetType?.Name,
            setterCount = setters.Count,
            setters,
            basedOn = style.BasedOn is not null ? DescribeStyle(style.BasedOn) : null,
        };
    }
}
