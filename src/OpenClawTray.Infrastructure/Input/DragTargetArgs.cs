using global::Windows.Foundation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.ApplicationModel.DataTransfer.DragDrop;

namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Mutable hooks on <see cref="DragTargetArgs.UIOverride"/> that let drag-over handlers
/// tweak the drop indicator (caption, glyph, preview). Values written here are applied
/// by the reconciler onto the underlying WinUI <see cref="DragUIOverride"/> after the
/// user callback returns.
/// </summary>
public sealed class DragUIOverrideHandle
{
    /// <summary>Override caption text (e.g. "Move to Inbox"). Null leaves WinUI's default.</summary>
    public string? Caption { get; set; }

    /// <summary>When false, hides the caption entirely.</summary>
    public bool IsCaptionVisible { get; set; } = true;

    /// <summary>When false, hides the drag-source content preview.</summary>
    public bool IsContentVisible { get; set; } = true;

    /// <summary>When false, hides the operation glyph (Copy/Move/Link icon).</summary>
    public bool IsGlyphVisible { get; set; } = true;
}

/// <summary>
/// Drop-target callback argument. Delivered to <c>.OnDragEnter</c>/<c>.OnDragOver</c>/
/// <c>.OnDragLeave</c>/raw <c>.OnDrop</c> handlers.
/// </summary>
public sealed class DragTargetArgs
{
    public DragTargetArgs(
        DragData data,
        Point position,
        DragOperations allowedOperations,
        DragDropModifiers modifiers,
        DragUIOverrideHandle uiOverride)
    {
        Data = data;
        Position = position;
        AllowedOperations = allowedOperations;
        Modifiers = modifiers;
        UIOverride = uiOverride;
        AcceptedOperation = DragOperations.None;
    }

    /// <summary>The drag payload as advertised by the source.</summary>
    public DragData Data { get; }

    /// <summary>Pointer position in target-element-local space.</summary>
    public Point Position { get; }

    /// <summary>Union of operations the source declared it allows.</summary>
    public DragOperations AllowedOperations { get; }

    /// <summary>Active Ctrl/Shift/Alt/mouse-button modifiers at the time of the event.</summary>
    public DragDropModifiers Modifiers { get; }

    /// <summary>Operation the target wants to accept. Default <see cref="DragOperations.None"/>
    /// rejects the drop; set to <see cref="DragOperations.Copy"/>/<c>Move</c>/<c>Link</c> to accept.</summary>
    public DragOperations AcceptedOperation { get; set; }

    /// <summary>Mutable UI override — caption/glyph visibility tweaks.</summary>
    public DragUIOverrideHandle UIOverride { get; }
}
