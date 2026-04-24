using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Foundation;
using OpenClawTray.Infrastructure.Input;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Core;

public sealed partial class Reconciler
{
    /// <summary>
    /// Per-element drag-and-drop dispatch state (spec 027 Tier 6 / Phase 6a).
    /// Mirrors <see cref="EventHandlerState"/>'s trampoline pattern so per-render
    /// updates of <see cref="ElementModifiers.DragSource"/> / <see cref="ElementModifiers.DropTarget"/>
    /// touch only a mutable field — the underlying WinUI events stay subscribed
    /// for the element's lifetime.
    /// </summary>
    internal sealed class DragDropState
    {
        public DragSourceConfig? Source;
        public DropTargetConfig? Target;

        // Current in-flight drag (source side).
        public Guid ActiveTransferId;

        // Stable trampolines — attached once.
        public TypedEventHandler<UIElement, DragStartingEventArgs>? DragStartingTrampoline;
        public TypedEventHandler<UIElement, DropCompletedEventArgs>? DropCompletedTrampoline;
        public DragEventHandler? DragEnterTrampoline;
        public DragEventHandler? DragOverTrampoline;
        public DragEventHandler? DragLeaveTrampoline;
        public DragEventHandler? DropTrampoline;
    }

    private static readonly global::System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, DragDropState> _dndStates = new();

    private static DragDropState GetOrCreateDndState(FrameworkElement fe)
    {
        if (!_dndStates.TryGetValue(fe, out var state))
        {
            state = new DragDropState();
            _dndStates.AddOrUpdate(fe, state);
        }
        return state;
    }

    private static void ApplyDragDropHandlers(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m)
    {
        if (m.DragSource is null && m.DropTarget is null
            && oldM?.DragSource is null && oldM?.DropTarget is null)
            return;

        var state = GetOrCreateDndState(fe);
        state.Source = m.DragSource;
        state.Target = m.DropTarget;

        // ── Source side ───────────────────────────────────────────────
        if (m.DragSource is not null)
        {
            fe.CanDrag = true;

            if (state.DragStartingTrampoline is null)
            {
                state.DragStartingTrampoline = (s, e) => OnDragStarting(state, s, e);
                fe.DragStarting += state.DragStartingTrampoline;
            }
            if (state.DropCompletedTrampoline is null)
            {
                state.DropCompletedTrampoline = (s, e) => OnDropCompleted(state, s, e);
                fe.DropCompleted += state.DropCompletedTrampoline;
            }
        }
        else if (oldM?.DragSource is not null)
        {
            fe.CanDrag = false;
        }

        // ── Target side ───────────────────────────────────────────────
        if (m.DropTarget is not null)
        {
            fe.AllowDrop = true;

            // AddHandler(handledEventsToo:true) — Button and other Controls may mark drag
            // events handled internally; without this, .OnDrop on a Button would silently
            // no-op because the trampoline never sees handled events via the += operator.
            if (state.DragEnterTrampoline is null)
            {
                state.DragEnterTrampoline = (s, e) => OnDragEnter(fe, state, e);
                fe.AddHandler(UIElement.DragEnterEvent, state.DragEnterTrampoline, handledEventsToo: true);
            }
            if (state.DragOverTrampoline is null)
            {
                state.DragOverTrampoline = (s, e) => OnDragOver(fe, state, e);
                fe.AddHandler(UIElement.DragOverEvent, state.DragOverTrampoline, handledEventsToo: true);
            }
            if (state.DragLeaveTrampoline is null)
            {
                state.DragLeaveTrampoline = (s, e) => OnDragLeave(fe, state, e);
                fe.AddHandler(UIElement.DragLeaveEvent, state.DragLeaveTrampoline, handledEventsToo: true);
            }
            if (state.DropTrampoline is null)
            {
                state.DropTrampoline = (s, e) => OnDrop(fe, state, e);
                fe.AddHandler(UIElement.DropEvent, state.DropTrampoline, handledEventsToo: true);
            }
        }
        else if (oldM?.DropTarget is not null)
        {
            fe.AllowDrop = false;
        }
    }

    // ── Source-side handlers ─────────────────────────────────────────

    private static void OnDragStarting(DragDropState state, UIElement sender, DragStartingEventArgs e)
    {
        if (state.Source is not { } src) return;

        // Gate: DraggableWhen.
        if (src.CanDrag is { } guard && !guard())
        {
            e.Cancel = true;
            return;
        }

        var data = src.GetData();
        if (data is null)
        {
            e.Cancel = true;
            return;
        }

        // Register transfer so same-process target can recover the typed payload.
        var transferId = DragData.Register(data);
        state.ActiveTransferId = transferId;

        // Map allowed operations.
        var allowed = src.AllowedOperations ?? DragOperations.All;
        e.AllowedOperations = ToWinUI(allowed);

        // Mark the DataPackage so same-process targets can look up DragData.
        e.Data.Properties[DragData.TransferIdFormatId] = transferId.ToString("N");
        e.Data.Properties[DragData.ProcIdFormatId] =
            data.OriginProcessId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);

        // Populate the DataPackage with eager formats (text / uri / html / rtf / files /
        // bitmap / custom) and register DataProviderHandler adapters for lazy providers.
        // Cross-process consumers see those formats natively; same-process consumers still
        // prefer the in-memory transfer registry.
        data.PopulatePackage(e.Data);
    }

    private static void OnDropCompleted(DragDropState state, UIElement sender, DropCompletedEventArgs e)
    {
        if (state.Source is not { } src) return;
        var transferId = state.ActiveTransferId;
        state.ActiveTransferId = Guid.Empty;

        try
        {
            src.OnEnd?.Invoke(BuildDragEndContext(e.DropResult));
        }
        finally
        {
            if (transferId != Guid.Empty)
                DragData.Unregister(transferId);
        }
    }

    /// <summary>
    /// Maps a final <see cref="DataPackageOperation"/> onto the user-facing
    /// <see cref="DragEndContext"/>. <see cref="DataPackageOperation.None"/> means the
    /// drop target refused the drop (ESC, drop outside a valid target, system abort) —
    /// <see cref="DragEndContext.WasCancelled"/> is true and
    /// <see cref="DragEndContext.CompletedOperation"/> is <see cref="DragOperations.None"/>.
    /// Any other value is a successful drop and flows through untouched.
    /// </summary>
    internal static DragEndContext BuildDragEndContext(DataPackageOperation dropResult)
    {
        var completed = FromWinUI(dropResult);
        var cancelled = dropResult == DataPackageOperation.None;
        return new DragEndContext(completed, cancelled);
    }

    // ── Target-side handlers ─────────────────────────────────────────

    private static DragData? ResolveDragData(DragEventArgs e)
    {
        // Prefer the in-memory transfer registry (same-process path).
        if (e.DataView.Properties.TryGetValue(DragData.TransferIdFormatId, out var idObj)
            && idObj is string idStr
            && Guid.TryParseExact(idStr, "N", out var id))
        {
            var registered = DragData.Resolve(id);
            if (registered is not null) return registered;
        }

        // Cross-process path: wrap the DataPackageView so TryGetText/GetTextAsync/… work.
        return BuildViewBackedDragData(e.DataView);
    }

    private static DragData BuildViewBackedDragData(DataPackageView view)
    {
        var data = new DragData();
        foreach (var format in view.AvailableFormats)
        {
            if (format == StandardDataFormats.Text)
                data.WithText(async ct => await view.GetTextAsync().AsTask(ct).ConfigureAwait(false));
            else if (format == StandardDataFormats.WebLink)
                data.WithUri(async ct => await view.GetWebLinkAsync().AsTask(ct).ConfigureAwait(false));
            else if (format == StandardDataFormats.Html)
                data.WithHtml(async ct => await view.GetHtmlFormatAsync().AsTask(ct).ConfigureAwait(false));
            else if (format == StandardDataFormats.Rtf)
                data.WithRtf(async ct => await view.GetRtfAsync().AsTask(ct).ConfigureAwait(false));
            else if (format == StandardDataFormats.StorageItems)
                data.WithFiles(async ct =>
                {
                    var items = await view.GetStorageItemsAsync().AsTask(ct).ConfigureAwait(false);
                    return (IEnumerable<global::Windows.Storage.IStorageItem>)items;
                });
            else if (format == StandardDataFormats.Bitmap)
                data.WithBitmap(async ct => await view.GetBitmapAsync().AsTask(ct).ConfigureAwait(false));
        }
        return data;
    }

    private static void InvokeTargetCallback(
        FrameworkElement fe,
        DropTargetConfig cfg,
        DragEventArgs e,
        Action<DragTargetArgs>? callback)
    {
        if (callback is null) return;

        var data = ResolveDragData(e) ?? new DragData();
        var uiOverride = new DragUIOverrideHandle();
        var pos = e.GetPosition(fe);
        var args = new DragTargetArgs(
            data: data,
            position: pos,
            allowedOperations: FromWinUI(e.AllowedOperations),
            modifiers: e.Modifiers,
            uiOverride: uiOverride);

        // Pre-populate with the negotiated default: leaving AcceptedOperation untouched
        // (e.g. when the handler only adjusts UIOverride) keeps the drop accepted per
        // DropTargetConfig.AcceptedOperations. Callers wanting to reject still set None explicitly.
        args.AcceptedOperation = DragOperationNegotiation.Negotiate(
            FromWinUI(e.AllowedOperations),
            cfg.AcceptedOperations);

        callback(args);

        // Propagate whatever the callback left on args — including None for an explicit reject.
        e.AcceptedOperation = ToWinUI(args.AcceptedOperation);

        // Propagate UI override (caption, visibility flags).
        if (uiOverride.Caption is not null)
            e.DragUIOverride.Caption = uiOverride.Caption;
        e.DragUIOverride.IsCaptionVisible = uiOverride.IsCaptionVisible;
        e.DragUIOverride.IsContentVisible = uiOverride.IsContentVisible;
        e.DragUIOverride.IsGlyphVisible = uiOverride.IsGlyphVisible;
    }

    private static void OnDragEnter(FrameworkElement fe, DragDropState state, DragEventArgs e)
    {
        if (state.Target is not { } cfg) return;
        // If no explicit callback, still mark drop acceptance based on cfg.AcceptedOperations.
        if (cfg.OnDragEnter is not null)
        {
            InvokeTargetCallback(fe, cfg, e, cfg.OnDragEnter);
        }
        else
        {
            // Default: accept the configured operations, negotiated with source-allowed.
            var negotiated = DragOperationNegotiation.Negotiate(
                FromWinUI(e.AllowedOperations),
                cfg.AcceptedOperations);
            e.AcceptedOperation = ToWinUI(negotiated);
        }
    }

    private static void OnDragOver(FrameworkElement fe, DragDropState state, DragEventArgs e)
    {
        if (state.Target is not { } cfg) return;
        if (cfg.OnDragOver is not null)
        {
            InvokeTargetCallback(fe, cfg, e, cfg.OnDragOver);
        }
        else
        {
            var negotiated = DragOperationNegotiation.Negotiate(
                FromWinUI(e.AllowedOperations),
                cfg.AcceptedOperations);
            e.AcceptedOperation = ToWinUI(negotiated);
        }
    }

    private static void OnDragLeave(FrameworkElement fe, DragDropState state, DragEventArgs e)
    {
        if (state.Target is not { } cfg) return;
        if (cfg.OnDragLeave is not null)
            InvokeTargetCallback(fe, cfg, e, cfg.OnDragLeave);
    }

    private static void OnDrop(FrameworkElement fe, DragDropState state, DragEventArgs e)
    {
        if (state.Target is not { } cfg) return;
        // TypedDrop runs first if set — it does payload unwrapping internally.
        var callback = cfg.TypedDrop ?? cfg.OnDrop;
        if (callback is not null)
        {
            InvokeTargetCallback(fe, cfg, e, callback);
        }
    }

    // ── Enum mapping ─────────────────────────────────────────────────

    internal static DataPackageOperation ToWinUI(DragOperations ops)
    {
        DataPackageOperation result = DataPackageOperation.None;
        if ((ops & DragOperations.Copy) != 0) result |= DataPackageOperation.Copy;
        if ((ops & DragOperations.Move) != 0) result |= DataPackageOperation.Move;
        if ((ops & DragOperations.Link) != 0) result |= DataPackageOperation.Link;
        return result;
    }

    internal static DragOperations FromWinUI(DataPackageOperation ops)
    {
        DragOperations result = DragOperations.None;
        if ((ops & DataPackageOperation.Copy) != 0) result |= DragOperations.Copy;
        if ((ops & DataPackageOperation.Move) != 0) result |= DragOperations.Move;
        if ((ops & DataPackageOperation.Link) != 0) result |= DragOperations.Link;
        return result;
    }
}
