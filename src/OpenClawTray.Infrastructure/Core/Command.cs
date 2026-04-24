namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Immutable command descriptor that bundles an action with its metadata (label, icon,
/// keyboard accelerator, enabled state). Define once, use in any surface:
///   var save = new Command { Label = "Save", Execute = () => Save(), Icon = SymbolIcon("Save") };
///   AppBarButton(save)   // toolbar
///   MenuItem(save)       // menu
///   Button(save)         // inline
/// </summary>
public sealed record Command
{
    /// <summary>Human-readable label shown on buttons, menu items, tooltips, etc.</summary>
    public required string Label { get; init; }

    /// <summary>Synchronous action. Mutually exclusive with <see cref="ExecuteAsync"/>.</summary>
    public Action? Execute { get; init; }

    /// <summary>Asynchronous action. Use with <see cref="RenderContext.UseCommand"/> to get
    /// automatic IsExecuting tracking and re-entrance guards.</summary>
    public Func<Task>? ExecuteAsync { get; init; }

    /// <summary>Whether the command's action can be invoked. Defaults to true.</summary>
    public bool CanExecute { get; init; } = true;

    /// <summary>Whether the command is currently executing an async operation.
    /// Managed by <see cref="RenderContext.UseCommand"/>.</summary>
    public bool IsExecuting { get; init; }

    /// <summary>Icon to display alongside the command.</summary>
    public IconData? Icon { get; init; }

    /// <summary>Tooltip / accessibility description.</summary>
    public string? Description { get; init; }

    /// <summary>Keyboard shortcut for this command.</summary>
    public KeyboardAcceleratorData? Accelerator { get; init; }

    /// <summary>Access key (Alt+key) for this command.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Computed: the command is enabled only when it can execute and is not currently executing.</summary>
    public bool IsEnabled => CanExecute && !IsExecuting;
}

/// <summary>
/// Parameterized command descriptor. The action receives an argument of type <typeparamref name="T"/>,
/// enabling a single command definition to operate on different targets:
///   var delete = new Command&lt;Item&gt; { Label = "Delete", Execute = item => Remove(item) };
///   MenuItem(delete, selectedItem)
/// </summary>
public sealed record Command<T>
{
    /// <summary>Human-readable label shown on buttons, menu items, tooltips, etc.</summary>
    public required string Label { get; init; }

    /// <summary>Synchronous action that receives a parameter.</summary>
    public Action<T>? Execute { get; init; }

    /// <summary>Asynchronous action that receives a parameter. Use with
    /// <see cref="RenderContext.UseCommand{T}"/> for lifecycle tracking.</summary>
    public Func<T, Task>? ExecuteAsync { get; init; }

    /// <summary>Whether the command's action can be invoked. Defaults to true.</summary>
    public bool CanExecute { get; init; } = true;

    /// <summary>Whether the command is currently executing an async operation.</summary>
    public bool IsExecuting { get; init; }

    /// <summary>Icon to display alongside the command.</summary>
    public IconData? Icon { get; init; }

    /// <summary>Tooltip / accessibility description.</summary>
    public string? Description { get; init; }

    /// <summary>Keyboard shortcut for this command.</summary>
    public KeyboardAcceleratorData? Accelerator { get; init; }

    /// <summary>Access key (Alt+key) for this command.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Computed: the command is enabled only when it can execute and is not currently executing.</summary>
    public bool IsEnabled => CanExecute && !IsExecuting;
}
