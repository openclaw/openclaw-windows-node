using System.Diagnostics.Tracing;

namespace OpenClawTray.Infrastructure.Core.Diagnostics;

/// <summary>
/// ETW/EventPipe provider for Reactor internals. Emits reconcile, render,
/// state-change, lifecycle, and MCP-dispatch events.
///
/// This provider is a managed <see cref="EventSource"/>, so it surfaces on
/// both EventPipe (<c>dotnet-trace</c>) and classic ETW (xperf / PerfView /
/// WPA). <c>Microsoft-Windows-XAML</c>, however, is a <b>native</b> ETW
/// provider emitted from WinUI's C++ code — it does not flow through
/// EventPipe, so correlated Reactor + XAML captures require an ETW-based
/// tool. Use <c>dotnet-trace</c> for Reactor-only traces, or xperf / PerfView
/// when you want the full render pipeline on one timeline.
///
/// EventPipe example (Reactor only):
///   dotnet-trace collect --process-id &lt;pid&gt; \
///       --providers Microsoft-UI-Reactor
///
/// ETW example (Reactor + WinUI correlated):
///   xperf -start ReactorSession \
///     -on "&lt;Reactor GUID&gt;:0x3F:5+531A35AB-63CE-4BCF-AA98-F88C7A89E455:0x9240:5" \
///     -f trace.etl
///
/// Keywords (see <see cref="Keywords"/>) let consumers pick subsets without
/// paying for the rest. Emit sites at hot paths (reconcile, render, state
/// writes, MCP dispatch) call <see cref="EventSource.IsEnabled(EventLevel, EventKeywords)"/>
/// at the call site before computing payloads (timestamps, type names) so
/// the disabled path avoids allocation and Stopwatch overhead. The
/// <c>WriteEvent</c> overloads are additionally guarded inside this class
/// for defense in depth.
/// </summary>
[EventSource(Name = "Microsoft-UI-Reactor")]
internal sealed class ReactorEventSource : EventSource
{
    public static readonly ReactorEventSource Log = new();

    private ReactorEventSource() { }

    public static class Keywords
    {
        public const EventKeywords Reconcile = (EventKeywords)0x1;
        public const EventKeywords Render = (EventKeywords)0x2;
        public const EventKeywords State = (EventKeywords)0x4;
        public const EventKeywords Mcp = (EventKeywords)0x8;
        public const EventKeywords Lifecycle = (EventKeywords)0x10;
        public const EventKeywords Errors = (EventKeywords)0x20;
        public const EventKeywords EventDispatch = (EventKeywords)0x40;
    }

    public static class Tasks
    {
        public const EventTask Reconcile = (EventTask)1;
        public const EventTask ComponentRender = (EventTask)2;
        public const EventTask McpCall = (EventTask)3;
        public const EventTask EffectsFlush = (EventTask)4;
        public const EventTask ChildReconcile = (EventTask)5;
    }

    // ── Reconcile pass boundaries ────────────────────────────────────────

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Reconcile,
        Task = Tasks.Reconcile, Opcode = EventOpcode.Start,
        Message = "Reconcile start (root={rootElementType})")]
    public void ReconcileStart(string rootElementType)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Reconcile))
            WriteEvent(1, rootElementType ?? string.Empty);
    }

    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Reconcile,
        Task = Tasks.Reconcile, Opcode = EventOpcode.Stop,
        Message = "Reconcile stop (diffed={elementsDiffed}, skipped={elementsSkipped}, created={uiElementsCreated}, modified={uiElementsModified})")]
    public void ReconcileStop(int elementsDiffed, int elementsSkipped, int uiElementsCreated, int uiElementsModified)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Reconcile))
            WriteEvent(2, elementsDiffed, elementsSkipped, uiElementsCreated, uiElementsModified);
    }

    // ── Component render boundaries ──────────────────────────────────────

    [Event(3, Level = EventLevel.Informational, Keywords = Keywords.Render,
        Task = Tasks.ComponentRender, Opcode = EventOpcode.Start,
        Message = "Render start (component={componentName}, trigger={trigger})")]
    public void ComponentRenderStart(string componentName, string trigger)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Render))
            WriteEvent(3, componentName ?? string.Empty, trigger ?? string.Empty);
    }

    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.Render,
        Task = Tasks.ComponentRender, Opcode = EventOpcode.Stop,
        Message = "Render stop (component={componentName}, elapsedUs={elapsedMicroseconds})")]
    public void ComponentRenderStop(string componentName, long elapsedMicroseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Render))
            WriteEvent(4, componentName ?? string.Empty, elapsedMicroseconds);
    }

    // ── State writes ─────────────────────────────────────────────────────

    [Event(5, Level = EventLevel.Verbose, Keywords = Keywords.State,
        Message = "State change (hook={hookKind}, type={valueType}, changed={changed})")]
    public void StateChange(string hookKind, string valueType, bool changed)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.State))
            WriteEvent(5, hookKind ?? string.Empty, valueType ?? string.Empty, changed);
    }

    // ── Component lifecycle ──────────────────────────────────────────────

    [Event(6, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle,
        Message = "Component unmount (component={componentName})")]
    public void ComponentUnmount(string componentName)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Lifecycle))
            WriteEvent(6, componentName ?? string.Empty);
    }

    // ── MCP dispatch ─────────────────────────────────────────────────────

    [Event(7, Level = EventLevel.Informational, Keywords = Keywords.Mcp,
        Task = Tasks.McpCall, Opcode = EventOpcode.Start,
        Message = "MCP call start (tool={toolName}, selector={selector})")]
    public void McpCallStart(string toolName, string selector)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Mcp))
            WriteEvent(7, toolName ?? string.Empty, selector ?? string.Empty);
    }

    [Event(8, Level = EventLevel.Informational, Keywords = Keywords.Mcp,
        Task = Tasks.McpCall, Opcode = EventOpcode.Stop,
        Message = "MCP call stop (tool={toolName}, success={success}, resultCode={resultCode}, elapsedMs={elapsedMilliseconds})")]
    public void McpCallStop(string toolName, bool success, int resultCode, long elapsedMilliseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Mcp))
            WriteEvent(8, toolName ?? string.Empty, success, resultCode, elapsedMilliseconds);
    }

    // ── Effects flush (UseEffect callbacks after render) ────────────────

    [Event(10, Level = EventLevel.Informational, Keywords = Keywords.Render,
        Task = Tasks.EffectsFlush, Opcode = EventOpcode.Start,
        Message = "Effects flush start (component={componentName})")]
    public void EffectsFlushStart(string componentName)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Render))
            WriteEvent(10, componentName ?? string.Empty);
    }

    [Event(11, Level = EventLevel.Informational, Keywords = Keywords.Render,
        Task = Tasks.EffectsFlush, Opcode = EventOpcode.Stop,
        Message = "Effects flush stop (component={componentName}, elapsedUs={elapsedMicroseconds})")]
    public void EffectsFlushStop(string componentName, long elapsedMicroseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Render))
            WriteEvent(11, componentName ?? string.Empty, elapsedMicroseconds);
    }

    // ── Child reconciliation (keyed LIS over element arrays) ────────────

    [Event(12, Level = EventLevel.Informational, Keywords = Keywords.Reconcile,
        Task = Tasks.ChildReconcile, Opcode = EventOpcode.Start,
        Message = "Children reconcile start (oldCount={oldCount}, newCount={newCount})")]
    public void ChildReconcileStart(int oldCount, int newCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Reconcile))
            WriteEvent(12, oldCount, newCount);
    }

    [Event(13, Level = EventLevel.Informational, Keywords = Keywords.Reconcile,
        Task = Tasks.ChildReconcile, Opcode = EventOpcode.Stop,
        Message = "Children reconcile stop")]
    public void ChildReconcileStop()
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Reconcile))
            WriteEvent(13);
    }

    // ── Event trampoline lifecycle ───────────────────────────────────────

    [Event(14, Level = EventLevel.Verbose, Keywords = Keywords.EventDispatch,
        Message = "Event trampoline attached (event={eventName}, controlType={controlType})")]
    public void EventTrampolineAttached(string eventName, string controlType)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.EventDispatch))
            WriteEvent(14, eventName ?? string.Empty, controlType ?? string.Empty);
    }

    [Event(15, Level = EventLevel.Verbose, Keywords = Keywords.EventDispatch,
        Message = "Event trampoline dispatched (event={eventName})")]
    public void EventTrampolineDispatch(string eventName)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.EventDispatch))
            WriteEvent(15, eventName ?? string.Empty);
    }

    // ── Errors ───────────────────────────────────────────────────────────

    [Event(9, Level = EventLevel.Error, Keywords = Keywords.Errors,
        Message = "Render error (component={componentName}, exception={exceptionType}: {message})")]
    public void RenderError(string componentName, string exceptionType, string message)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Errors))
            WriteEvent(9, componentName ?? string.Empty, exceptionType ?? string.Empty, message ?? string.Empty);
    }
}
