namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Drag-and-drop operation flags. Source declares what it allows via
/// <see cref="ElementExtensions.OnDragStart"/>; target negotiates the final
/// operation by setting <see cref="DragTargetArgs.AcceptedOperation"/>.
/// </summary>
[Flags]
public enum DragOperations
{
    /// <summary>No operation — used to reject a drop.</summary>
    None = 0,
    /// <summary>Copy the payload, leaving the source intact.</summary>
    Copy = 1 << 0,
    /// <summary>Move the payload — source should remove the item on successful drop.</summary>
    Move = 1 << 1,
    /// <summary>Create a link/reference to the payload.</summary>
    Link = 1 << 2,
    /// <summary>Any of Copy, Move, or Link.</summary>
    All = Copy | Move | Link,
}

/// <summary>
/// Delivered to the source's <c>onEnd</c> callback when the drag concludes. Distinguishes
/// successful completion from cancellation, and identifies the final operation the
/// target accepted so the source can apply the matching side-effect (e.g. remove on Move).
/// </summary>
/// <param name="CompletedOperation">Final operation negotiated with the target, or <see cref="DragOperations.None"/> on cancellation.</param>
/// <param name="WasCancelled">True when the drag was cancelled (ESC key, dropped outside any valid target, system abort).</param>
public readonly record struct DragEndContext(
    DragOperations CompletedOperation,
    bool WasCancelled);
