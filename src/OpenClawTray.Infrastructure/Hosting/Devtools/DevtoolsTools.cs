using System.Diagnostics;
using System.Text.Json;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Registers the Phase 2 MCP tools on a <see cref="DevtoolsMcpServer"/>.
/// Each tool's handler is written to be side-effect-free on the HTTP thread
/// and marshal UI work through <see cref="DevtoolsMcpServer.OnDispatcher{T}"/>
/// where required. The <c>reactor.*</c> names in spec prose are unprefixed on
/// the wire — agents see the bare names.
/// </summary>
/// <summary>
/// Enriched component descriptor returned by the <c>components</c> tool when the
/// host provides <see cref="DevtoolsTools.ToolHostContext.GetComponentsDetailed"/>.
/// Agents use <see cref="IsNested"/> to filter out inner helper components (e.g.
/// <c>ContextDemo+AccentBadge</c>) when picking the "main" demo to mount.
/// </summary>
internal sealed record ComponentInfo(
    string Name,
    string FullName,
    bool IsNested,
    bool IsPublic,
    string? Namespace);

internal static class DevtoolsTools
{
    /// <summary>Supplies data the tools need from the host: components, switch callback, reload request.</summary>
    internal sealed class ToolHostContext
    {
        public required Func<IReadOnlyList<string>> GetComponents { get; init; }
        public required Func<string?> GetCurrentComponent { get; init; }
        public required Func<string, bool> SwitchComponent { get; init; }
        public required Action RequestReload { get; init; }
        public required Action RequestShutdown { get; init; }
        public required WindowRegistry Windows { get; init; }

        /// <summary>
        /// Optional enriched component descriptor lookup. When present, the
        /// <c>components</c> tool returns structured entries ({ name, fullName,
        /// isNested, isPublic, namespace }); otherwise it falls back to the
        /// flat string list produced by <see cref="GetComponents"/>.
        /// </summary>
        public Func<IReadOnlyList<ComponentInfo>>? GetComponentsDetailed { get; init; }

        /// <summary>
        /// Optional node registry — set by the host bring-up so
        /// <c>switchComponent</c> can invalidate stale ids for the active window
        /// after a successful swap. Absent in minimal selftest harnesses that
        /// don't rely on post-switch tree walks.
        /// </summary>
        public NodeRegistry? Nodes { get; init; }
    }

    public static void RegisterCore(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        Register_Version(server);
        Register_Components(server, ctx);
        Register_SwitchComponent(server, ctx);
        Register_Reload(server, ctx);
        Register_Shutdown(server, ctx);
        Register_Windows(server, ctx);
    }

    // -- version -----------------------------------------------------------------

    private static void Register_Version(DevtoolsMcpServer server)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "version",
                Description: "Returns the running app's build tag, pid, and MCP port. Zero side effects.",
                InputSchema: new { type = "object", properties = new { }, additionalProperties = false }),
            _ => new
            {
                build = server.BuildTag,
                pid = Process.GetCurrentProcess().Id,
                mcpPort = server.Port,
            });
    }

    // -- components --------------------------------------------------------------

    private static void Register_Components(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "components",
                Description:
                    "Lists the Component class names in the loaded assembly, top-level first. Each entry carries " +
                    "`isNested` (true for inner helper components like `ContextDemo+AccentBadge`) so agents can pick " +
                    "the user-facing demo without guessing. Use `current` to verify what's mounted now.",
                InputSchema: new { type = "object", properties = new { }, additionalProperties = false }),
            _ =>
            {
                if (ctx.GetComponentsDetailed is { } detailed)
                {
                    var infos = detailed()
                        .OrderBy(c => c.IsNested ? 1 : 0)
                        .ThenBy(c => c.Name, StringComparer.Ordinal)
                        .ToArray();
                    return new
                    {
                        components = infos,
                        current = ctx.GetCurrentComponent(),
                    };
                }
                return new
                {
                    components = ctx.GetComponents().ToArray(),
                    current = ctx.GetCurrentComponent(),
                };
            });
    }

    // -- switchComponent ---------------------------------------------------------

    private static void Register_SwitchComponent(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "switchComponent",
                Description: "Switches the hosted root component. Invalidates every tree id in the target window.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Component class name" },
                    },
                    required = new[] { "name" },
                    additionalProperties = false,
                }),
            @params =>
            {
                var name = ReadString(@params, "name")
                    ?? throw new McpToolException("switchComponent requires a 'name' argument.",
                        JsonRpcErrorCodes.InvalidParams);

                // The switch itself (calling back into the host's `Mount(...)`) and
                // the follow-up `WindowRegistry.Snapshot()` both touch WinUI state
                // and must run on the UI dispatcher. Without this hop the handler
                // would land on the HTTP worker thread, where `host.Mount(new T())`
                // can hit a WinUI COM thread-apartment error that surfaces as an
                // empty-message exception — the selftest fixture saw this as a
                // -32603 InternalError and crashed on the missing `ok`.
                return server.OnDispatcher<object>(() =>
                {
                    var ok = ctx.SwitchComponent(name);
                    if (!ok)
                        throw new McpToolException($"Component '{name}' not found.",
                            JsonRpcErrorCodes.ToolExecution,
                            new { code = "unknown-component", available = ctx.GetComponents().ToArray() });

                    // The old tree is gone; invalidate its ids so a subsequent
                    // selector resolution against an old id returns `"gone"`
                    // rather than silently reaching a stale element. Scoped to
                    // every known active window — the swap replaces all roots.
                    if (ctx.Nodes is { } nodes)
                    {
                        foreach (var snap in ctx.Windows.Snapshot())
                            nodes.InvalidateWindow(snap.Id);
                    }

                    return new { ok = true, current = name };
                });
            });
    }

    // -- reload ------------------------------------------------------------------

    private static void Register_Reload(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "reload",
                Description:
                    "Flushes the response, closes listeners, and exits with sentinel code 42 so the " +
                    "`mur devtools` supervisor rebuilds and relaunches. Old node ids do not carry over.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        component = new { type = "string", description = "Optional component to focus after restart" },
                    },
                    additionalProperties = false,
                }),
            @params =>
            {
                // Return the response immediately; the reload fires after the HTTP write flushes.
                var exitingBuild = server.BuildTag;
                ctx.RequestReload();
                return new { ok = true, exitingBuild };
            });
    }

    // -- shutdown ----------------------------------------------------------------

    private static void Register_Shutdown(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "shutdown",
                Description:
                    "Closes the app cleanly. Flushes the HTTP response, disposes the MCP listener, " +
                    "closes the window, and exits with code 0 so the `mur devtools` supervisor " +
                    "returns without rebuilding. Use to release file locks on the build output.",
                InputSchema: new { type = "object", properties = new { }, additionalProperties = false }),
            @params =>
            {
                var exitingBuild = server.BuildTag;
                ctx.RequestShutdown();
                return new { ok = true, exitingBuild };
            });
    }

    // -- windows -----------------------------------------------------------------

    private static void Register_Windows(DevtoolsMcpServer server, ToolHostContext ctx)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "windows",
                Description:
                    "Lists active windows with their ids, titles, bounds, build tag, and the currently " +
                    "mounted Reactor component (per-window). The id is a stable handle — it does NOT " +
                    "reflect the window title, which changes on `switchComponent`. Scope selectors with " +
                    "`window` when more than one is active.",
                InputSchema: new { type = "object", properties = new { }, additionalProperties = false }),
            _ => server.OnDispatcher(() =>
            {
                var current = ctx.GetCurrentComponent();
                return new
                {
                    windows = ctx.Windows.Snapshot().Select(w => new
                    {
                        id = w.Id,
                        title = w.Title,
                        hwnd = w.Hwnd,
                        bounds = new
                        {
                            x = w.Bounds.X,
                            y = w.Bounds.Y,
                            width = w.Bounds.Width,
                            height = w.Bounds.Height,
                        },
                        isMain = w.IsMain,
                        buildTag = w.BuildTag,
                        // Only the main window reflects the component switch today;
                        // secondary windows report null. Agents can cross-check
                        // components.current against this value.
                        currentComponent = w.IsMain ? current : null,
                    }).ToArray(),
                };
            }));
    }

    // -- helpers -----------------------------------------------------------------

    internal static string? ReadString(JsonElement? args, string name)
    {
        if (args is not { } a || a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    internal static int? ReadInt(JsonElement? args, string name)
    {
        if (args is not { } a || a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;
    }

    internal static long? ReadLong(JsonElement? args, string name)
    {
        if (args is not { } a || a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;
    }

    internal static bool? ReadBool(JsonElement? args, string name)
    {
        if (args is not { } a || a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
