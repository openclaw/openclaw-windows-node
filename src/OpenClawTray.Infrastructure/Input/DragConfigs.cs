namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Immutable source-side drag configuration. Stored on <c>ElementModifiers.DragSource</c>;
/// the reconciler attaches <see cref="Microsoft.UI.Xaml.UIElement.DragStarting"/> and
/// <see cref="Microsoft.UI.Xaml.UIElement.DropCompleted"/> trampolines and auto-sets
/// <see cref="Microsoft.UI.Xaml.UIElement.CanDrag"/>.
/// </summary>
public sealed record DragSourceConfig(
    Func<DragData> GetData)
{
    /// <summary>Operations the source will allow. Null means "all".</summary>
    public DragOperations? AllowedOperations { get; init; }

    /// <summary>Optional guard — when returning false, the drag is cancelled in <c>DragStarting</c>.</summary>
    public Func<bool>? CanDrag { get; init; }

    /// <summary>Final callback when the drag ends (success or cancellation).</summary>
    public Action<DragEndContext>? OnEnd { get; init; }
}

/// <summary>
/// Immutable target-side drag configuration. Stored on <c>ElementModifiers.DropTarget</c>;
/// the reconciler attaches <see cref="Microsoft.UI.Xaml.UIElement.DragEnter"/>,
/// <see cref="Microsoft.UI.Xaml.UIElement.DragOver"/>, <see cref="Microsoft.UI.Xaml.UIElement.DragLeave"/>
/// and <see cref="Microsoft.UI.Xaml.UIElement.Drop"/> trampolines and auto-sets
/// <see cref="Microsoft.UI.Xaml.UIElement.AllowDrop"/>.
/// </summary>
public sealed record DropTargetConfig
{
    /// <summary>Raw drop handler invoked with full <see cref="DragTargetArgs"/>. Either this or
    /// <see cref="TypedDrop"/> is non-null.</summary>
    public Action<DragTargetArgs>? OnDrop { get; init; }

    /// <summary>Typed-drop convenience. The reconciler unwraps the typed payload and calls this
    /// when it's present, falling back to <see cref="OnDrop"/> otherwise.</summary>
    public Action<DragTargetArgs>? TypedDrop { get; init; }

    /// <summary>Invoked on <see cref="Microsoft.UI.Xaml.UIElement.DragEnter"/>.</summary>
    public Action<DragTargetArgs>? OnDragEnter { get; init; }

    /// <summary>Invoked on <see cref="Microsoft.UI.Xaml.UIElement.DragOver"/>.</summary>
    public Action<DragTargetArgs>? OnDragOver { get; init; }

    /// <summary>Invoked on <see cref="Microsoft.UI.Xaml.UIElement.DragLeave"/>.</summary>
    public Action<DragTargetArgs>? OnDragLeave { get; init; }

    /// <summary>Default accepted operation applied when <see cref="DragTargetArgs.AcceptedOperation"/>
    /// is left at <see cref="DragOperations.None"/> by the user callback. Matches source-declared
    /// <see cref="DragSourceConfig.AllowedOperations"/> via <see cref="DragOperationNegotiation"/>.</summary>
    public DragOperations AcceptedOperations { get; init; } = DragOperations.All;
}

/// <summary>
/// Negotiation helpers shared by the reconciler and tests.
/// </summary>
public static class DragOperationNegotiation
{
    /// <summary>
    /// Returns the first of <see cref="DragOperations.Move"/> / <see cref="DragOperations.Copy"/>
    /// / <see cref="DragOperations.Link"/> that both source and target agree on. The preference
    /// order (Move before Copy before Link) matches the Windows shell's default.
    /// </summary>
    public static DragOperations Negotiate(DragOperations source, DragOperations target)
    {
        var intersection = source & target;
        if ((intersection & DragOperations.Move) != 0) return DragOperations.Move;
        if ((intersection & DragOperations.Copy) != 0) return DragOperations.Copy;
        if ((intersection & DragOperations.Link) != 0) return DragOperations.Link;
        return DragOperations.None;
    }
}
