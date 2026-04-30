using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Fluent helpers for building A2UI v0.8 wire-format JSONL in tests.
///
/// The renderer pipeline expects messages like:
///   {"surfaceUpdate":{"surfaceId":"s","components":[ {id, component:{Name:{props}}} ]}}
///   {"beginRendering":{"surfaceId":"s","root":"id"}}
///   {"dataModelUpdate":{"surfaceId":"s","contents":[{key, value*}]}}
///
/// Composing those by string concatenation gets unreadable fast in larger
/// fixtures, so this builder produces JsonObjects then serializes once.
/// Properties (literal/path/children) are also helpers — see <see cref="Lit"/>,
/// <see cref="Path"/>, <see cref="Children"/>.
/// </summary>
public static class A2UI
{
    /// <summary>Wrap a string as a v0.8 literalString value.</summary>
    public static JsonObject Lit(string s) => new() { ["literalString"] = s };
    /// <summary>Wrap a number as a v0.8 literalNumber value.</summary>
    public static JsonObject Lit(double n) => new() { ["literalNumber"] = n };
    /// <summary>Wrap a bool as a v0.8 literalBoolean value.</summary>
    public static JsonObject Lit(bool b) => new() { ["literalBoolean"] = b };

    /// <summary>JSON Pointer reference into the surface's data model.</summary>
    public static JsonObject Path(string p) => new() { ["path"] = p };

    /// <summary>Wraps an explicit children list: <c>{ "explicitList": [...] }</c>.</summary>
    public static JsonObject Children(params string[] ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(JsonValue.Create(id));
        return new JsonObject { ["explicitList"] = arr };
    }

    /// <summary>Build one component declaration: <c>{id, component:{Name:{props}}}</c>.</summary>
    public static JsonObject Component(string id, string componentName, JsonObject? props = null)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["component"] = new JsonObject
            {
                [componentName] = props ?? new JsonObject(),
            },
        };
    }

    /// <summary>Convenience for an option in MultipleChoice: <c>{label, value}</c>.</summary>
    public static JsonObject Option(string label, string value) => new()
    {
        ["label"] = Lit(label),
        ["value"] = value,
    };

    /// <summary>One tab entry inside Tabs.tabItems.</summary>
    public static JsonObject Tab(string title, string childId) => new()
    {
        ["title"] = Lit(title),
        ["child"] = childId,
    };

    /// <summary>
    /// Build a JSONL string starting with a surfaceUpdate carrying all components,
    /// then a beginRendering line. <paramref name="styles"/> is optional and maps
    /// to the v0.8 surface theme — see <see cref="OpenClawTray.A2UI.Theming.A2UITheme"/>.
    /// </summary>
    public static string Surface(
        string surfaceId,
        string rootId,
        IEnumerable<JsonObject> components,
        JsonObject? styles = null)
    {
        var compArr = new JsonArray();
        foreach (var c in components) compArr.Add(c.DeepClone());

        var line1 = new JsonObject
        {
            ["surfaceUpdate"] = new JsonObject
            {
                ["surfaceId"] = surfaceId,
                ["components"] = compArr,
            },
        }.ToJsonString();

        var br = new JsonObject
        {
            ["surfaceId"] = surfaceId,
            ["root"] = rootId,
        };
        if (styles != null) br["styles"] = styles.DeepClone();
        var line2 = new JsonObject { ["beginRendering"] = br }.ToJsonString();

        return line1 + "\n" + line2;
    }

    /// <summary>
    /// Build a dataModelUpdate JSONL line. Each entry's value type is inferred
    /// from the JsonNode kind (string → valueString, number → valueNumber, bool → valueBoolean).
    /// </summary>
    public static string DataUpdate(string surfaceId, params (string key, JsonNode? value)[] entries)
    {
        var contents = new JsonArray();
        foreach (var (key, value) in entries)
        {
            var entry = new JsonObject { ["key"] = key };
            if (value is JsonValue jv)
            {
                if (jv.TryGetValue<string>(out var s)) entry["valueString"] = s;
                else if (jv.TryGetValue<bool>(out var b)) entry["valueBoolean"] = b;
                else if (jv.TryGetValue<double>(out var d)) entry["valueNumber"] = d;
            }
            contents.Add(entry);
        }
        return new JsonObject
        {
            ["dataModelUpdate"] = new JsonObject
            {
                ["surfaceId"] = surfaceId,
                ["contents"] = contents,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// Theme-styles object suitable for the <c>styles</c> argument of
    /// <see cref="Surface"/>. Mirrors what <see cref="OpenClawTray.A2UI.Theming.A2UITheme.Parse"/>
    /// reads on the wire.
    /// </summary>
    public static JsonObject Styles(
        string? primaryColor = null,
        string? font = null,
        double? radius = null,
        double? spacing = null,
        string? foreground = null,
        string? background = null,
        string? cardBackground = null)
    {
        var obj = new JsonObject();
        if (primaryColor != null) obj["primaryColor"] = primaryColor;
        if (font != null) obj["font"] = font;
        if (radius.HasValue) obj["radius"] = radius.Value;
        if (spacing.HasValue) obj["spacing"] = spacing.Value;

        if (foreground != null || background != null || cardBackground != null)
        {
            var colors = new JsonObject();
            if (foreground != null) colors["foreground"] = foreground;
            if (background != null) colors["background"] = background;
            if (cardBackground != null) colors["card"] = cardBackground;
            obj["colors"] = colors;
        }
        return obj;
    }
}
