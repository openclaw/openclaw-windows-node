using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// <c>reactor.fire</c> — escape hatch for cases where no UIA pattern reaches the
/// behavior (drag sequences, custom gestures, awaited async tests). Resolves a
/// live <see cref="Component"/> by class name, finds a method matching the event
/// name (case-insensitive), and invokes it on the UI dispatcher.
///
/// Scope: v1 walks the root component only. A future pass (§3.8 full wiring)
/// can expand to child components once the reconciler exposes a component
/// registry. Unknown component or unknown event produces a structured error;
/// invoked handlers log <c>via: "reactor-event-injection"</c> so the shortcut
/// is visible in traces.
/// </summary>
internal static class DevtoolsFireTool
{
    public static void Register(DevtoolsMcpServer server, Func<Component?> rootComponent)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "fire",
                // Description doubles as the back-link the spec §3.8 open item
                // asked for: it names the tool as an escape hatch, points at
                // the UIA-first rule from spec §11, and documents the via tag
                // so anyone reading log lines can recognize the shortcut.
                // Agents that surface tool descriptions show this verbatim.
                Description:
                    "Invokes a NAMED METHOD on a live Component class by event name. " +
                    "GOTCHA: inline-lambda handlers (the default in Reactor demos, e.g. " +
                    "`Button(\"+\", () => setCount(...))`) are NOT reachable — fire resolves by reflection " +
                    "against declared public/private methods on the Component class. To make a handler " +
                    "reachable, give it a method: `void IncrementCount() { ... }` then `fire { event: \"IncrementCount\" }`. " +
                    "ESCAPE HATCH — prefer UIA patterns (click/invoke/toggle/type/select/scroll) first " +
                    "per spec §11 'Automation verbs'. Use fire only when no UIA peer reaches the behavior " +
                    "(custom gestures, awaited async paths, unit-of-work handlers). " +
                    "Responses carry `via: \"reactor-event-injection\"` so traces make the shortcut visible.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        component = new { type = "string" },
                        @event = new { type = "string" },
                        args = new { type = "array", description = "Optional positional args for the handler." },
                    },
                    required = new[] { "component", "event" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var componentName = DevtoolsTools.ReadString(@params, "component")
                    ?? throw new McpToolException("Missing 'component'.", JsonRpcErrorCodes.InvalidParams);
                var eventName = DevtoolsTools.ReadString(@params, "event")
                    ?? throw new McpToolException("Missing 'event'.", JsonRpcErrorCodes.InvalidParams);

                var (instance, handler) = ResolveTarget(rootComponent(), componentName, eventName);
                var argsArray = ExtractArgs(@params);
                try
                {
                    handler.Invoke(instance, argsArray);
                }
                catch (TargetInvocationException ex)
                {
                    throw new McpToolException(
                        $"Handler '{eventName}' threw: {ex.InnerException?.Message ?? ex.Message}",
                        JsonRpcErrorCodes.ToolExecution,
                        new { code = "handler-threw" });
                }

                return new { ok = true, via = "reactor-event-injection" };
            }));
    }

    /// <summary>
    /// Resolves the (component, handler) pair to invoke, throwing the same
    /// structured errors the live tool path emits. Extracted so unit tests
    /// can exercise the error shapes without a live MCP server.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools fire tool uses reflection to discover and invoke component methods.")]
    internal static (Component instance, MethodInfo handler) ResolveTarget(
        Component? root, string componentName, string eventName)
    {
        if (root is null)
            throw new McpToolException(
                "No root component is mounted.",
                JsonRpcErrorCodes.ToolExecution,
                new { code = "not-ready" });

        var instance = FindComponent(root, componentName)
            ?? throw new McpToolException(
                $"Component '{componentName}' is not the root (child-component search not implemented in v1).",
                JsonRpcErrorCodes.ToolExecution,
                new { code = "unknown-component", available = new[] { root.GetType().Name } });

        var handler = FindHandler(instance, eventName);
        if (handler is null)
        {
            // List the reachable method names on the component type so the
            // agent can spot typos AND explain why inline-lambda handlers
            // don't show up here — the most common field-feedback confusion.
            var reachable = ListReachableHandlers(instance);
            throw new McpToolException(
                $"Component '{componentName}' has no named handler '{eventName}'.",
                JsonRpcErrorCodes.ToolExecution,
                new
                {
                    code = "unknown-event",
                    component = componentName,
                    @event = eventName,
                    reachableMethods = reachable,
                    hint = "fire resolves by reflection against declared methods on the Component class. " +
                           "Inline-lambda handlers (the common pattern in demos) are NOT reachable — " +
                           "turn the handler into a method to make it callable.",
                });
        }

        return (instance, handler);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools fire tool uses reflection to discover component methods.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Devtools fire tool uses reflection to discover component methods.")]
    internal static string[] ListReachableHandlers(Component instance)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        return instance.GetType().GetMethods(flags)
            .Where(m => !m.IsSpecialName && !ForbiddenMethods.Contains(m.Name))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    internal static Component? FindComponent(Component root, string name)
    {
        // v1 only resolves the root. Keeps the tool well-defined without
        // requiring reconciler-level component registration.
        return string.Equals(root.GetType().Name, name, StringComparison.OrdinalIgnoreCase)
            ? root
            : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Devtools fire tool uses reflection to find handler methods.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Devtools fire tool uses reflection to find handler methods.")]
    internal static MethodInfo? FindHandler(Component instance, string eventName)
    {
        // Reactor lifecycle / render / hook machinery: firing these from outside
        // the reconciler can corrupt hook state, double-render, or bypass
        // disposal ordering. `fire` is an escape hatch for user-authored
        // handlers, not for the framework's internal surface — refuse by name.
        if (ForbiddenMethods.Contains(eventName))
            throw new McpToolException(
                $"Method '{eventName}' is a framework lifecycle / render entry point; fire it via UIA patterns instead.",
                JsonRpcErrorCodes.ToolExecution,
                new { code = "forbidden-method", @event = eventName });

        var type = instance.GetType();
        // Prefer an exact-cased public or internal instance method; fall back to
        // case-insensitive. Only parameter shapes compatible with our passed args
        // will succeed at invoke time — we don't filter by signature here.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type.GetMethods(flags)
            .FirstOrDefault(m =>
                string.Equals(m.Name, eventName, StringComparison.OrdinalIgnoreCase) &&
                !ForbiddenMethods.Contains(m.Name));
    }

    /// <summary>
    /// Framework-owned method names that <c>fire</c> must refuse. These either
    /// run inside the reconciler's render pass or manage the hook table —
    /// invoking them externally leaves the component in an inconsistent state.
    /// </summary>
    internal static readonly HashSet<string> ForbiddenMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Render",
        "OnInitialized",
        "OnMounted",
        "OnUnmounted",
        "OnDisposed",
        "Dispose",
        "UseState",
        "UseEffect",
        "UseRef",
        "UseMemo",
        "UseCallback",
        "UseContext",
        "UseReducer",
    };

    internal static object?[] ExtractArgs(JsonElement? @params)
    {
        if (@params is not { } p || p.ValueKind != JsonValueKind.Object) return Array.Empty<object?>();
        if (!p.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array) return Array.Empty<object?>();

        var list = new List<object?>(argsEl.GetArrayLength());
        foreach (var item in argsEl.EnumerateArray())
        {
            list.Add(JsonElementToClr(item));
        }
        return list.ToArray();
    }

    private static object? JsonElementToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };
}
