using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Registers the UIA-driven automation tools: <c>tree</c>, <c>click</c>, <c>type</c>,
/// <c>focus</c>, <c>waitFor</c>. Each handler marshals onto the UI dispatcher and
/// surfaces structured errors via <see cref="McpToolException"/>.
/// </summary>
internal static class DevtoolsUiaTools
{
    public static void RegisterUiaTools(
        DevtoolsMcpServer server,
        NodeRegistry nodes,
        WindowRegistry windows)
    {
        var resolver = new SelectorResolver(nodes, windows);

        Register_Tree(server, nodes, windows);
        Register_Click(server, resolver);
        Register_Type(server, resolver);
        Register_Focus(server, resolver);
        Register_WaitFor(server, resolver);
        Register_Screenshot(server, resolver, windows);
        Register_Invoke(server, resolver);
        Register_Toggle(server, resolver);
        Register_Select(server, resolver);
        Register_Scroll(server, resolver);
        Register_Expand(server, resolver);
        DevtoolsPropertyTools.Register(server, resolver);
    }

    // -- invoke / toggle / select / scroll ---------------------------------------

    private static void Register_Invoke(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "invoke",
                Description: "Calls IInvokeProvider.Invoke directly; errors if the element does not expose the pattern.",
                InputSchema: new
                {
                    type = "object",
                    properties = new { selector = new { type = "string" }, window = new { type = "string" } },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var el = resolver.Resolve(RequiredString(@params, "selector"), DevtoolsTools.ReadString(@params, "window"));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                {
                    invoke.Invoke();
                    return new { ok = true };
                }
                throw new McpToolException(
                    "Element does not expose the Invoke pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "Invoke" });
            }));
    }

    private static void Register_Toggle(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "toggle",
                Description: "Calls IToggleProvider.Toggle; returns the resulting state.",
                InputSchema: new
                {
                    type = "object",
                    properties = new { selector = new { type = "string" }, window = new { type = "string" } },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var el = resolver.Resolve(RequiredString(@params, "selector"), DevtoolsTools.ReadString(@params, "window"));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
                if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                {
                    toggle.Toggle();
                    var state = toggle.ToggleState switch
                    {
                        global::Microsoft.UI.Xaml.Automation.ToggleState.On => "on",
                        global::Microsoft.UI.Xaml.Automation.ToggleState.Off => "off",
                        _ => "indeterminate",
                    };
                    return new { ok = true, state };
                }
                throw new McpToolException(
                    "Element does not expose the Toggle pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "Toggle" });
            }));
    }

    private static void Register_Select(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "select",
                Description:
                    "Calls ISelectionItemProvider.Select on the item matched by itemSelector. When the container is " +
                    "a closed ComboBox (or other ExpandCollapse surface), it is expanded automatically before the " +
                    "item is resolved so the popup's items materialize.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Container selector (ListView, ComboBox, etc.)." },
                        itemSelector = new { type = "string", description = "Selector of the descendant item to select." },
                        window = new { type = "string" },
                    },
                    required = new[] { "selector", "itemSelector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var container = resolver.Resolve(RequiredString(@params, "selector"), windowId);

                // Auto-expand closed ExpandCollapse containers (ComboBox is the
                // common one). Items inside a closed ComboBox popup aren't in
                // the live visual tree, so itemSelector resolution would miss
                // them. Expand first, then resolve the item.
                TryExpand(container);

                var item = resolver.Resolve(RequiredString(@params, "itemSelector"), windowId);

                // Selector resolution typically lands on the inner content
                // (TextBlock, Image, etc.). The *selectable container* is one
                // of its visual-tree ancestors: ListViewItem, ComboBoxItem,
                // ListBoxItem, GridViewItem — all inherit from SelectorItem
                // and expose an `IsSelected` property. WinUI's own
                // SelectorItemAutomationPeer routes `ISelectionItemProvider`
                // back through that property, but only when the peer is
                // rooted by its parent Selector's peer — a bare
                // `CreatePeerForElement(listViewItem)` returns a peer whose
                // `GetPattern(SelectionItem)` answers null. Walk up, find the
                // first SelectorItem ancestor, and flip `IsSelected` directly.
                // That fires the Selector's SelectionChanged like a real
                // click would.
                var selectorItem = FindSelectorItemAncestor(item);
                if (selectorItem is not null)
                {
                    selectorItem.IsSelected = true;
                    return new { ok = true, selected = selectorItem.IsSelected };
                }

                // Non-Selector containers (custom selectables) may still route
                // through UIA's SelectionItem pattern on some ancestor — fall
                // back to walking for the pattern directly.
                var (peer, _) = FindAncestorWithPattern(item, PatternInterface.SelectionItem);
                if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel)
                {
                    sel.Select();
                    return new { ok = true, selected = sel.IsSelected };
                }

                throw new McpToolException(
                    "No SelectorItem ancestor and no element exposing the SelectionItem UIA pattern (walked up from resolved element to window root).",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "SelectionItem" });
            }));
    }

    /// <summary>
    /// Walks the visual tree upward from <paramref name="start"/> looking for
    /// the first <see cref="global::Microsoft.UI.Xaml.Controls.Primitives.SelectorItem"/>
    /// ancestor — that's the common base class for ListViewItem, ComboBoxItem,
    /// ListBoxItem, GridViewItem, TabViewItem, etc.
    /// </summary>
    private static global::Microsoft.UI.Xaml.Controls.Primitives.SelectorItem? FindSelectorItemAncestor(UIElement start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is global::Microsoft.UI.Xaml.Controls.Primitives.SelectorItem si)
                return si;
            current = global::Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// Walks the visual tree upward from <paramref name="start"/> looking for
    /// an element whose automation peer exposes <paramref name="pattern"/>.
    /// Returns the peer + the element the peer was built for. Used by tools
    /// like <c>select</c> where the selector lands on the item's inner content
    /// but the target pattern lives on the wrapping container (ListViewItem,
    /// ComboBoxItem, etc.).
    /// </summary>
    private static (AutomationPeer? Peer, UIElement? Host) FindAncestorWithPattern(
        UIElement start, PatternInterface pattern)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is UIElement ui)
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(ui);
                if (peer?.GetPattern(pattern) is not null)
                    return (peer, ui);
            }
            current = global::Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return (null, null);
    }

    // Best-effort expand — swallows failures because auto-expand is a
    // convenience, not a contract. The dedicated `expand` tool surfaces
    // structured errors when the caller needs them.
    private static bool TryExpand(UIElement container)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
            if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider ec
                && ec.ExpandCollapseState == global::Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed)
            {
                ec.Expand();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void Register_Expand(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "expand",
                Description:
                    "Opens an ExpandCollapse-aware element (ComboBox popup, TreeViewItem, MenuFlyoutItem, Expander). " +
                    "Errors with `no-pattern` when the element doesn't expose IExpandCollapseProvider. Returns the " +
                    "new state.",
                InputSchema: new
                {
                    type = "object",
                    properties = new { selector = new { type = "string" }, window = new { type = "string" } },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var el = resolver.Resolve(RequiredString(@params, "selector"), DevtoolsTools.ReadString(@params, "window"));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
                if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider ec)
                {
                    ec.Expand();
                    return new { ok = true, state = FormatExpandState(ec.ExpandCollapseState) };
                }
                throw new McpToolException(
                    "Element does not expose the ExpandCollapse pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "ExpandCollapse" });
            }));

        server.Tools.Register(
            new McpToolDescriptor(
                Name: "collapse",
                Description:
                    "Closes an ExpandCollapse-aware element (ComboBox popup, TreeViewItem, Expander). Errors with " +
                    "`no-pattern` when the element doesn't expose IExpandCollapseProvider.",
                InputSchema: new
                {
                    type = "object",
                    properties = new { selector = new { type = "string" }, window = new { type = "string" } },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var el = resolver.Resolve(RequiredString(@params, "selector"), DevtoolsTools.ReadString(@params, "window"));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
                if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider ec)
                {
                    ec.Collapse();
                    return new { ok = true, state = FormatExpandState(ec.ExpandCollapseState) };
                }
                throw new McpToolException(
                    "Element does not expose the ExpandCollapse pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "ExpandCollapse" });
            }));
    }

    private static string FormatExpandState(global::Microsoft.UI.Xaml.Automation.ExpandCollapseState s) => s switch
    {
        global::Microsoft.UI.Xaml.Automation.ExpandCollapseState.Expanded => "expanded",
        global::Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed => "collapsed",
        global::Microsoft.UI.Xaml.Automation.ExpandCollapseState.PartiallyExpanded => "partial",
        _ => "leaf",
    };

    private static void Register_Scroll(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "scroll",
                Description:
                    "Scrolls a container. `by.horizontal` / `by.vertical` are PERCENTAGE deltas (0–100) added to the " +
                    "current scroll percent and clamped — NOT pixels. For virtualized lists prefer `to: <itemSelector>` " +
                    "which uses IScrollItemProvider.ScrollIntoView. The response carries both `scrollPercent` and " +
                    "`scrollOffsetPx` (resolved from the underlying ScrollViewer when available); an axis that isn't " +
                    "scrollable reports null rather than the UIA -1 sentinel.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string" },
                        by = new
                        {
                            type = "object",
                            description = "Percentage deltas (0–100) added to the current scroll percent, clamped to [0, 100].",
                            properties = new
                            {
                                horizontal = new { type = "number", description = "Percent delta (0–100)." },
                                vertical = new { type = "number", description = "Percent delta (0–100)." },
                            },
                        },
                        to = new { type = "string", description = "Descendant selector to scroll into view (takes precedence over `by`)." },
                        window = new { type = "string" },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher<object>(() =>
            {
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var container = resolver.Resolve(RequiredString(@params, "selector"), windowId);

                // "to" takes precedence if both are given.
                var toSel = DevtoolsTools.ReadString(@params, "to");
                if (!string.IsNullOrEmpty(toSel))
                {
                    var item = resolver.Resolve(toSel!, windowId);
                    var itemPeer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
                    if (itemPeer?.GetPattern(PatternInterface.ScrollItem) is IScrollItemProvider scrollItem)
                    {
                        scrollItem.ScrollIntoView();
                        return new { ok = true };
                    }
                    throw new McpToolException(
                        "Target does not expose the ScrollItem pattern.",
                        JsonRpcErrorCodes.ToolExecution,
                        new { code = "no-pattern", pattern = "ScrollItem" });
                }

                // By-offset on the container's Scroll pattern.
                var containerPeer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
                if (containerPeer?.GetPattern(PatternInterface.Scroll) is IScrollProvider scroller)
                {
                    // UIA sentinel: -1 means "this axis is unavailable" (content
                    // fits inside the viewport, scrollbar hidden, etc.). Calling
                    // SetScrollPercent with a non-NoScroll value on a NoScroll
                    // axis throws a bare COM exception ("Cannot perform the
                    // operation") — translate those into structured errors the
                    // tool surface can reason about.
                    const double NoScroll = -1.0;
                    double horizCur = scroller.HorizontalScrollPercent;
                    double vertCur = scroller.VerticalScrollPercent;

                    double horizTarget = horizCur;
                    double vertTarget = vertCur;
                    bool horizRequested = false;
                    bool vertRequested = false;

                    if (@params is { } p && p.TryGetProperty("by", out var byEl) && byEl.ValueKind == JsonValueKind.Object)
                    {
                        if (byEl.TryGetProperty("horizontal", out var hx) && hx.ValueKind == JsonValueKind.Number)
                        {
                            horizRequested = hx.GetDouble() != 0;
                            horizTarget = horizRequested
                                ? Math.Clamp((horizCur < 0 ? 0 : horizCur) + hx.GetDouble(), 0, 100)
                                : horizCur;
                        }
                        if (byEl.TryGetProperty("vertical", out var vy) && vy.ValueKind == JsonValueKind.Number)
                        {
                            vertRequested = vy.GetDouble() != 0;
                            vertTarget = vertRequested
                                ? Math.Clamp((vertCur < 0 ? 0 : vertCur) + vy.GetDouble(), 0, 100)
                                : vertCur;
                        }

                        if (horizRequested && horizCur == NoScroll)
                            throw new McpToolException(
                                "Container's horizontal axis is not scrollable (HorizontallyScrollable=false).",
                                JsonRpcErrorCodes.ToolExecution,
                                new { code = "not-scrollable", axis = "horizontal" });
                        if (vertRequested && vertCur == NoScroll)
                            throw new McpToolException(
                                "Container's vertical axis is not scrollable (VerticallyScrollable=false).",
                                JsonRpcErrorCodes.ToolExecution,
                                new { code = "not-scrollable", axis = "vertical" });

                        // If an axis wasn't requested but is unavailable, pass
                        // NoScroll through so SetScrollPercent treats it as
                        // "leave alone" rather than a request to scroll to 0.
                        if (horizCur == NoScroll && !horizRequested) horizTarget = NoScroll;
                        if (vertCur == NoScroll && !vertRequested) vertTarget = NoScroll;

                        if (horizRequested || vertRequested)
                            scroller.SetScrollPercent(horizTarget, vertTarget);
                    }

                    // Report percent (UIA native) and pixel offsets (dug out of
                    // the underlying ScrollViewer when we can reach it, so the
                    // agent can verify "did I actually land where I expected?"
                    // without a second round-trip). An axis that isn't
                    // scrollable surfaces as null rather than the UIA -1
                    // sentinel — agents were seeing horizontal: -1 leak
                    // through and parsing it as a position.
                    double? horizPctOut = scroller.HorizontalScrollPercent == NoScroll
                        ? null
                        : scroller.HorizontalScrollPercent;
                    double? vertPctOut = scroller.VerticalScrollPercent == NoScroll
                        ? null
                        : scroller.VerticalScrollPercent;
                    double? horizPxOut = null, vertPxOut = null;
                    double? scrollableWidth = null, scrollableHeight = null;
                    var sv = FindScrollViewer(container);
                    if (sv is not null)
                    {
                        horizPxOut = sv.HorizontalOffset;
                        vertPxOut = sv.VerticalOffset;
                        scrollableWidth = sv.ScrollableWidth;
                        scrollableHeight = sv.ScrollableHeight;
                    }

                    return new
                    {
                        ok = true,
                        scrollPercent = new { horizontal = horizPctOut, vertical = vertPctOut },
                        scrollOffsetPx = new { horizontal = horizPxOut, vertical = vertPxOut },
                        scrollableSizePx = new { width = scrollableWidth, height = scrollableHeight },
                    };
                }

                throw new McpToolException(
                    "Container does not expose the Scroll pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern", pattern = "Scroll" });
            }));
    }

    // -- screenshot --------------------------------------------------------------

    private static void Register_Screenshot(DevtoolsMcpServer server, SelectorResolver resolver, WindowRegistry windows)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "screenshot",
                Description: "Captures a PNG of the window (or a selector-scoped region). Base64-encoded result.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Optional region to crop to." },
                        window = new { type = "string" },
                        waitIdle = new { type = "boolean", description = "Force a layout pass before capture (default true)." },
                        includeChrome = new { type = "boolean", description = "Include the non-client titlebar frame (default false)." },
                    },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var selector = DevtoolsTools.ReadString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var waitIdle = DevtoolsTools.ReadBool(@params, "waitIdle") ?? true;
                var includeChrome = DevtoolsTools.ReadBool(@params, "includeChrome") ?? false;

                var w = ResolveWindowForTools(windows, windowId);

                (double x, double y, double w, double h)? crop = null;
                if (!string.IsNullOrEmpty(selector))
                {
                    var el = resolver.Resolve(selector!, windowId);
                    if (el is FrameworkElement fe)
                    {
                        // Element bounds relative to the window client area.
                        var transform = fe.TransformToVisual(w.Content);
                        var origin = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
                        crop = (origin.X, origin.Y, fe.ActualWidth, fe.ActualHeight);
                    }
                }

                if (waitIdle)
                {
                    // A no-op UpdateLayout on the content tree forces pending layout
                    // to run before we capture.
                    if (w.Content is FrameworkElement rootFe) rootFe.UpdateLayout();
                }

                var capture = ScreenshotCapture.CaptureWindow(w, includeChrome, crop);
                return new
                {
                    png = Convert.ToBase64String(capture.Png),
                    bounds = new
                    {
                        x = capture.X,
                        y = capture.Y,
                        width = capture.Width,
                        height = capture.Height,
                    },
                };
            }, timeoutMs: 10_000));
    }

    // -- tree --------------------------------------------------------------------

    private static void Register_Tree(DevtoolsMcpServer server, NodeRegistry nodes, WindowRegistry windows)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "tree",
                Description:
                    "Walks the visual tree and returns a flat array of nodes. view=full adds layout/context/visual " +
                    "fields for layout debugging. `includeReactorSource` is reserved for Phase 3 — setting it to " +
                    "true currently returns a not-implemented error instead of silently no-opping.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string", description = "Optional scope; if omitted, walks the whole window." },
                        window = new { type = "string" },
                        view = new { type = "string", @enum = new[] { "summary", "full" } },
                        includeReactorSource = new { type = "boolean", description = "Reserved; lands with the Phase 3 source map. Setting true is a hard error today." },
                    },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var selector = DevtoolsTools.ReadString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var viewStr = DevtoolsTools.ReadString(@params, "view");
                var includeReactorSource = DevtoolsTools.ReadBool(@params, "includeReactorSource") ?? false;
                if (includeReactorSource)
                    throw new McpToolException(
                        "includeReactorSource requires the Phase 3 source map.",
                        JsonRpcErrorCodes.ToolExecution,
                        new { code = "not-implemented", flag = "includeReactorSource", phase = 3 });
                var view = string.Equals(viewStr, "full", StringComparison.OrdinalIgnoreCase)
                    ? TreeView.Full
                    : TreeView.Summary;
                var w = ResolveWindowForTools(windows, windowId);
                var walker = new TreeWalker(WindowIdFor(windows, w), nodes, view);

                UIElement? root = w.Content;
                if (!string.IsNullOrEmpty(selector))
                {
                    var resolver = new SelectorResolver(nodes, windows);
                    root = resolver.Resolve(selector!, windowId);
                }

                return new TreeResult
                {
                    Schema = TreeWalker.SchemaVersion,
                    WindowId = WindowIdFor(windows, w),
                    Nodes = walker.Walk(root),
                };
            }));
    }

    // -- click -------------------------------------------------------------------

    private static void Register_Click(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "click",
                Description: "Clicks the element matching the selector. Prefers IInvokeProvider, falls back to Toggle → SelectionItem → pointer.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string" },
                        window = new { type = "string" },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el)
                    ?? throw new McpToolException(
                        "Element has no automation peer.",
                        JsonRpcErrorCodes.ToolExecution,
                        new { code = "no-peer" });

                if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                {
                    invoke.Invoke();
                    return new { ok = true, via = "invoke" };
                }
                if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                {
                    toggle.Toggle();
                    return new { ok = true, via = "toggle" };
                }
                if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel)
                {
                    sel.Select();
                    return new { ok = true, via = "selection" };
                }

                throw new McpToolException(
                    "No UIA pattern available to click this element.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern" });
            }));
    }

    // -- type --------------------------------------------------------------------

    private static void Register_Type(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "type",
                Description: "Sets text on a value-bearing control. Clears first when clear=true.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string" },
                        text = new { type = "string" },
                        clear = new { type = "boolean" },
                        window = new { type = "string" },
                    },
                    required = new[] { "selector", "text" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var text = RequiredString(@params, "text");
                var clear = DevtoolsTools.ReadBool(@params, "clear") ?? false;
                var windowId = DevtoolsTools.ReadString(@params, "window");

                var el = resolver.Resolve(selector, windowId);

                if (el is TextBox tb)
                {
                    tb.Text = clear ? text : tb.Text + text;
                    return new { ok = true, via = "value" };
                }

                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
                if (peer?.GetPattern(PatternInterface.Value) is IValueProvider value)
                {
                    var current = value.Value ?? string.Empty;
                    value.SetValue(clear ? text : current + text);
                    return new { ok = true, via = "value" };
                }

                throw new McpToolException(
                    "Element does not expose a value pattern.",
                    JsonRpcErrorCodes.ToolExecution,
                    new { code = "no-pattern" });
            }));
    }

    // -- focus -------------------------------------------------------------------

    private static void Register_Focus(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "focus",
                Description: "Programmatically focuses the selected element.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        selector = new { type = "string" },
                        window = new { type = "string" },
                    },
                    required = new[] { "selector" },
                    additionalProperties = false,
                }),
            @params => server.OnDispatcher(() =>
            {
                var selector = RequiredString(@params, "selector");
                var windowId = DevtoolsTools.ReadString(@params, "window");
                var el = resolver.Resolve(selector, windowId);
                bool focused = false;
                if (el is Control ctl) focused = ctl.Focus(FocusState.Programmatic);
                return new { ok = focused };
            }));
    }

    // -- waitFor -----------------------------------------------------------------

    private static void Register_WaitFor(DevtoolsMcpServer server, SelectorResolver resolver)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "waitFor",
                Description: "Polls a predicate against the live tree until it matches or times out.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        predicate = new
                        {
                            type = "object",
                            properties = new
                            {
                                selector = new { type = "string" },
                                textEquals = new { type = "string" },
                                textMatches = new { type = "string" },
                                visible = new { type = "boolean" },
                                count = new { type = "integer" },
                            },
                        },
                        timeoutMs = new { type = "integer" },
                        window = new { type = "string" },
                    },
                    required = new[] { "predicate" },
                    additionalProperties = false,
                }),
            @params =>
            {
                if (@params is not { } p || p.ValueKind != JsonValueKind.Object)
                    throw new McpToolException("waitFor requires a 'predicate' object.", JsonRpcErrorCodes.InvalidParams);
                if (!p.TryGetProperty("predicate", out var predEl) || predEl.ValueKind != JsonValueKind.Object)
                    throw new McpToolException("'predicate' must be an object.", JsonRpcErrorCodes.InvalidParams);

                var pred = WaitForPredicate.FromJson(predEl);
                int timeoutMs = DevtoolsTools.ReadInt(@params, "timeoutMs") ?? 5000;
                var windowId = DevtoolsTools.ReadString(@params, "window");

                var sw = global::System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    var observed = server.OnDispatcher(() => WaitForPredicate.Evaluate(pred, resolver, windowId));
                    if (observed.Satisfied)
                        return new { ok = true, elapsedMs = sw.ElapsedMilliseconds };
                    Thread.Sleep(50);
                }

                var final = server.OnDispatcher(() => WaitForPredicate.Evaluate(pred, resolver, windowId));
                return new
                {
                    ok = false,
                    reason = "timeout",
                    elapsedMs = sw.ElapsedMilliseconds,
                    observed = new
                    {
                        count = final.Count,
                        text = final.Text,
                        visible = final.Visible,
                    },
                };
            });
    }

    // -- helpers -----------------------------------------------------------------

    private static string RequiredString(JsonElement? args, string key) =>
        DevtoolsTools.ReadString(args, key)
            ?? throw new McpToolException($"Missing required argument '{key}'.",
                JsonRpcErrorCodes.InvalidParams);

    private static Window ResolveWindowForTools(WindowRegistry windows, string? explicitWindowId)
    {
        if (!string.IsNullOrEmpty(explicitWindowId))
        {
            var w = windows.Resolve(explicitWindowId!);
            return w ?? throw new McpToolException(
                $"Window '{explicitWindowId}' not found.", JsonRpcErrorCodes.ToolExecution,
                new { code = "unknown-window" });
        }
        var @default = windows.TryDefault(out var activeIds);
        if (@default is not null) return @default;
        throw new McpToolException(
            "Multiple windows are active — pass 'window'.",
            JsonRpcErrorCodes.InvalidParams,
            new { code = "window-required", activeIds });
    }

    private static string WindowIdFor(WindowRegistry windows, Window w)
    {
        foreach (var snap in windows.Snapshot())
        {
            if (windows.Resolve(snap.Id) == w) return snap.Id;
        }
        return "main";
    }

    // Walks the visual tree from `root` looking for the first ScrollViewer so
    // the scroll tool can report pixel offsets alongside UIA scroll percents.
    // Returns null for containers that aren't backed by a ScrollViewer
    // (custom scrolling surfaces exposing IScrollProvider by hand).
    private static ScrollViewer? FindScrollViewer(UIElement element)
    {
        if (element is ScrollViewer sv) return sv;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < n; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i) is UIElement child)
            {
                var found = FindScrollViewer(child);
                if (found is not null) return found;
            }
        }
        return null;
    }
}

/// <summary>Predicate IR + evaluator for <c>reactor.waitFor</c>.</summary>
internal sealed record WaitForPredicate(
    string? Selector,
    string? TextEquals,
    string? TextMatches,
    bool? Visible,
    int? Count)
{
    public static WaitForPredicate FromJson(JsonElement el) => new(
        Selector: el.TryGetProperty("selector", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
        TextEquals: el.TryGetProperty("textEquals", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString() : null,
        TextMatches: el.TryGetProperty("textMatches", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString() : null,
        Visible: el.TryGetProperty("visible", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null,
        Count: el.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null);

    public readonly record struct Observation(bool Satisfied, int Count, string? Text, bool Visible);

    public static Observation Evaluate(WaitForPredicate pred, SelectorResolver resolver, string? windowId)
    {
        if (string.IsNullOrEmpty(pred.Selector))
            return new Observation(Satisfied: true, Count: 0, Text: null, Visible: false);

        // Count-first: count=0 means "wait for element to disappear", so we
        // must allow resolution failure to succeed in that case.
        try
        {
            var element = resolver.Resolve(pred.Selector!, windowId);
            var text = ExtractText(element);
            var visible = element is UIElement u && u.Visibility == Visibility.Visible;

            bool ok = true;
            if (pred.Count is int c && c != 1) ok = false; // a single Resolve() match means count=1
            if (pred.TextEquals is not null && pred.TextEquals != text) ok = false;
            if (pred.TextMatches is not null)
            {
                try { if (!Regex.IsMatch(text ?? string.Empty, pred.TextMatches)) ok = false; }
                catch (RegexParseException) { ok = false; }
            }
            if (pred.Visible is bool vb && vb != visible) ok = false;

            return new Observation(ok, Count: 1, Text: text, Visible: visible);
        }
        catch (McpToolException ex) when (IsUnknownSelector(ex))
        {
            // Element absent — only satisfies count==0.
            bool ok = pred.Count == 0;
            return new Observation(ok, Count: 0, Text: null, Visible: false);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JSON serialization to inspect MCP tool exception payload.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JSON serialization to inspect MCP tool exception payload.")]
    private static bool IsUnknownSelector(McpToolException ex)
    {
        if (ex.Payload is null) return false;
        var json = JsonSerializer.Serialize(ex.Payload, DevtoolsMcpServer.JsonOpts);
        return json.Contains("unknown-selector");
    }

    private static string? ExtractText(UIElement element) => element switch
    {
        TextBlock tb => tb.Text,
        TextBox tx => tx.Text,
        Button b => b.Content?.ToString(),
        ContentControl cc => cc.Content?.ToString(),
        _ => null,
    };
}
