using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// One implementation per A2UI v0.8 component type. Catalog-strict: registry
/// is populated at construction time. To extend, add a renderer class and
/// register it in <see cref="ComponentRendererRegistry.BuildDefault"/>.
/// </summary>
public interface IComponentRenderer
{
    /// <summary>The A2UI component name (case-sensitive — matches the wire), e.g. "Button", "Column".</summary>
    string ComponentName { get; }

    /// <summary>
    /// Build a XAML element for the component. Renderers are responsible for
    /// resolving their own children via <see cref="RenderContext.BuildChild"/>;
    /// there is no central adjacency walker. Bindings to the data model
    /// register subscriptions in <see cref="RenderContext.Subscriptions"/> so
    /// they can be torn down on rebuild.
    /// </summary>
    FrameworkElement Render(A2UIComponentDef component, RenderContext ctx);
}

/// <summary>
/// Per-render-call context. Renderers hold no state across calls; the
/// surface host owns lifetime and rebuilds the tree when the root or
/// component definitions change.
/// </summary>
public sealed class RenderContext
{
    public required string SurfaceId { get; init; }
    public required DataModelObservable DataModel { get; init; }
    public required IActionSink Actions { get; init; }
    public required A2UITheme Theme { get; init; }
    public required Func<string, FrameworkElement?> BuildChild { get; init; }
    /// <summary>
    /// Surface-scoped subscription store. Renderers should not access this
    /// dictionary directly — use <see cref="WatchValue"/> (or
    /// <see cref="RegisterSubscription"/> for non-value subs). Exposed here for
    /// the surface host's lifecycle management.
    /// </summary>
    public required IDictionary<string, IDisposable> Subscriptions { get; init; }
    /// <summary>Surface-scoped logger; <c>null</c> if the host did not supply one.</summary>
    public OpenClaw.Shared.IOpenClawLogger? Logger { get; init; }
    public MediaLoadBudget? MediaBudget { get; init; }
    /// <summary>
    /// Surface-scoped set of JSON Pointer paths that hold sensitive values.
    /// Populated by renderers (e.g., obscured TextField); consulted by
    /// <see cref="BuildActionContext"/> and the surface dump path.
    /// </summary>
    public ISet<string>? SecretPaths { get; init; }

    /// <summary>
    /// Pull the named property from the component as an A2UI value (tagged
    /// union of literals + path). Returns null if the property is absent.
    /// </summary>
    public A2UIValue? GetValue(A2UIComponentDef c, string propertyKey) =>
        A2UIValue.From(c.Properties[propertyKey]);

    /// <summary>Read the current resolved string for a value.</summary>
    public string? ResolveString(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralString != null) return value.LiteralString;
        if (value.LiteralNumber.HasValue) return value.LiteralNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.LiteralBoolean.HasValue) return value.LiteralBoolean.Value ? "true" : "false";
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
            return node?.ToString();
        }
        return null;
    }

    public double? ResolveNumber(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralNumber.HasValue) return value.LiteralNumber.Value;
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
            if (node is JsonValue jv2 && jv2.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    public bool? ResolveBoolean(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralBoolean.HasValue) return value.LiteralBoolean.Value;
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        }
        return null;
    }

    /// <summary>Subscribe a UI update to changes on a value's path. No-op for literals.</summary>
    public void WatchValue(string componentId, string subKey, A2UIValue? value, Action update)
    {
        if (value == null || !value.HasPath) return;
        RegisterSubscription(componentId, subKey, DataModel.Subscribe(value.Path!, update));
    }

    /// <summary>
    /// Register an arbitrary disposable subscription scoped to (componentId, subKey).
    /// Replaces any prior subscription under the same key (the prior one is disposed).
    /// The host disposes all registered subscriptions on rebuild.
    /// </summary>
    public void RegisterSubscription(string componentId, string subKey, IDisposable subscription)
    {
        // Use a delimiter that can't appear inside a JSON Pointer-derived
        // componentId (nor inside subKeys we control). Previous "::" was
        // collidable: component "x::label" + sub "value" hashed to the same
        // bucket as "x" + "label::value". (L2 in unified review.)
        var key = $"{componentId}{subKey}";
        if (Subscriptions.TryGetValue(key, out var prev))
        {
            try { prev.Dispose(); } catch { }
            Subscriptions.Remove(key);
        }
        Subscriptions[key] = subscription;
    }

    /// <summary>
    /// Read children IDs from a container's <c>children</c> property
    /// (<c>{ "explicitList": [...] }</c>). Template form is not yet supported;
    /// it returns an empty list and logs once at the router level.
    /// </summary>
    public IReadOnlyList<string> GetExplicitChildren(A2UIComponentDef c, string key = "children")
    {
        if (c.Properties[key] is not JsonObject child) return Array.Empty<string>();
        if (child["explicitList"] is JsonArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add(s);
            return list;
        }
        return Array.Empty<string>();
    }

    public string? GetSingleChild(A2UIComponentDef c, string key)
    {
        if (c.Properties[key] is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    /// <summary>Mark a path as secret for the lifetime of the current render. No-op if no secret store is wired.</summary>
    public void MarkSecretPath(string? path)
    {
        if (SecretPaths == null) return;
        if (string.IsNullOrEmpty(path)) return;
        SecretPaths.Add(NormalizePath(path));
    }

    /// <summary>True if <paramref name="path"/> is a secret path (registered or matches denylist).</summary>
    public bool IsSecretPath(string? path)
    {
        if (SecretPaths == null) return SecretRedactor.IsSecret(path, System.Collections.Frozen.FrozenSet<string>.Empty);
        return SecretRedactor.IsSecret(path, (IReadOnlySet<string>)SecretPaths);
    }

    /// <summary>
    /// Build the action context array → flat object payload, scoped to the
    /// source component's declared <c>dataBinding</c> (or, in its absence,
    /// the set of paths the component references in any other property).
    /// Paths outside that scope are silently dropped to prevent unrelated
    /// surface state from leaking into the agent envelope. Secret paths
    /// (registered via obscured TextField, or matching the SecretRedactor
    /// denylist) are always dropped — even when the component declares an
    /// explicit <c>dataBinding</c>. <c>dataBinding: ["/"]</c> is a root-level
    /// wildcard that opts in to everything; allowing secret paths through it
    /// (or through any explicit binding) lets a malicious surface drain
    /// credentials into the action envelope. The component can still bind
    /// secret values for display; it just cannot exfiltrate them.
    /// </summary>
    public JsonObject? BuildActionContext(A2UIComponentDef sourceComponent, JsonNode? actionNode)
    {
        if (actionNode is not JsonObject actionObj) return null;
        if (actionObj["context"] is not JsonArray ctxArr) return null;

        var (allowed, _) = CollectAllowedBindingPaths(sourceComponent);
        var result = new JsonObject();
        foreach (var item in ctxArr)
        {
            if (item is not JsonObject e) continue;
            var key = e["key"] is JsonValue kj && kj.TryGetValue<string>(out var k) ? k : null;
            if (key == null) continue;
            var val = A2UIValue.From(e["value"]);
            if (val == null) { result[key] = null; continue; }
            if (val.LiteralString != null) result[key] = val.LiteralString;
            else if (val.LiteralNumber.HasValue) result[key] = val.LiteralNumber.Value;
            else if (val.LiteralBoolean.HasValue) result[key] = val.LiteralBoolean.Value;
            else if (val.HasPath)
            {
                var path = val.Path!;
                if (!IsAllowedPath(path, allowed)) continue;
                if (IsSecretPath(path)) continue;
                result[key] = DataModel.Read(path)?.DeepClone();
            }
        }
        return result;
    }

    private static (HashSet<string> paths, bool isExplicit) CollectAllowedBindingPaths(A2UIComponentDef source)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var props = source.Properties;

        // Explicit declaration takes precedence: array of strings or {path: "..."} objects.
        if (props["dataBinding"] is JsonArray db)
        {
            foreach (var item in db)
            {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    result.Add(NormalizePath(s));
                else if (item is JsonObject obj && obj["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) && !string.IsNullOrEmpty(p))
                    result.Add(NormalizePath(p));
            }
            return (result, true);
        }

        // Implicit: any A2UIValue path that appears in the component's other properties.
        // The action's own "context" array doesn't count — that's what we're scoping.
        foreach (var kv in props)
        {
            if (kv.Key == "action" && kv.Value is JsonObject actionObj)
            {
                foreach (var ak in actionObj)
                {
                    if (ak.Key == "context") continue;
                    CollectPaths(ak.Value, result);
                }
            }
            else
            {
                CollectPaths(kv.Value, result);
            }
        }
        return (result, false);
    }

    private static void CollectPaths(JsonNode? node, HashSet<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["path"] is JsonValue jv && jv.TryGetValue<string>(out var p) && !string.IsNullOrEmpty(p))
                    result.Add(NormalizePath(p));
                foreach (var kv in obj) CollectPaths(kv.Value, result);
                break;
            case JsonArray arr:
                foreach (var item in arr) CollectPaths(item, result);
                break;
        }
    }

    private static bool IsAllowedPath(string path, HashSet<string> allowed)
    {
        if (allowed.Count == 0) return false;
        var normalized = NormalizePath(path);
        if (allowed.Contains(normalized)) return true;
        foreach (var p in allowed)
        {
            if (p == "/") return true; // explicit root binding opts in to everything
            if (normalized.StartsWith(p + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizePath(string p) =>
        string.IsNullOrEmpty(p) ? "/" : (p[0] == '/' ? p : "/" + p);

    // EmptySet was a hand-rolled stand-in for an empty IReadOnlySet<string>.
    // FrozenSet<string>.Empty is a single shared, allocation-free instance
    // with the same behavior — see IsSecretPath above.
}

public sealed class MediaLoadBudget
{
    internal const int MaxImageLoadsPerRender = 128;
    private int _imageLoadsRemaining = MaxImageLoadsPerRender;

    public bool TryReserveImageLoad() =>
        System.Threading.Interlocked.Decrement(ref _imageLoadsRemaining) >= 0;
}
