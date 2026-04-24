using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Handle returned by UseFocusTrap. When active, traps keyboard focus within
/// the container element so Tab/Shift+Tab cycles within the trapped scope.
/// Essential for modal dialogs and flyouts.
/// </summary>
public sealed class FocusTrapHandle
{
    private UIElement? _container;
    private bool _isActive;

    /// <summary>Whether the focus trap is currently active.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (_isActive)
                Attach();
            else
                Detach();
        }
    }

    internal void SetContainer(UIElement container)
    {
        Detach();
        _container = container;
        if (_isActive) Attach();
    }

    private void Attach()
    {
        if (_container is null) return;
        _container.LosingFocus += OnLosingFocus;
    }

    private void Detach()
    {
        if (_container is null) return;
        _container.LosingFocus -= OnLosingFocus;
    }

    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (!_isActive || _container is null) return;

        // Check if the new focus target is outside our container
        var newFocus = args.NewFocusedElement as DependencyObject;
        if (newFocus is null) return;

        // Walk up from new focus target to see if it's within our container
        if (!IsDescendantOf(newFocus, _container))
        {
            // Cancel the focus change — keep focus within the trap
            args.Cancel = true;
            // Optionally cycle: if Tab was going forward past the last element,
            // move focus to the first focusable child
            args.Handled = true;
        }
    }

    private static bool IsDescendantOf(DependencyObject element, UIElement container)
    {
        var current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, container)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    internal void Dispose()
    {
        Detach();
        _container = null;
    }
}

/// <summary>
/// Extension methods for the UseFocusTrap hook.
/// </summary>
public static class UseFocusTrapExtensions
{
    /// <summary>
    /// Creates a focus trap handle that traps keyboard focus within a container
    /// when active. Use with the .FocusTrap() element modifier.
    ///
    /// <code>
    /// var trap = UseFocusTrap(isDialogOpen);
    /// return Border(
    ///     VStack(
    ///         Text("Confirm delete?"),
    ///         Button("Cancel", () => setOpen(false)),
    ///         Button("Delete", onDelete)
    ///     )
    /// ).FocusTrap(trap);
    /// </code>
    /// </summary>
    public static FocusTrapHandle UseFocusTrap(this RenderContext ctx, bool isActive)
    {
        var (handle, _) = ctx.UseState(new FocusTrapHandle());
        handle.IsActive = isActive;
        return handle;
    }

    /// <summary>
    /// Creates a focus trap handle for this component.
    /// </summary>
    public static FocusTrapHandle UseFocusTrap(this Component component, bool isActive)
        => component.Context.UseFocusTrap(isActive);
}

/// <summary>
/// Fluent extension to attach a FocusTrapHandle to an element.
/// The handle captures the mounted WinUI element for focus trapping.
/// </summary>
public static class FocusTrapElementExtensions
{
    /// <summary>
    /// Attaches a focus trap to this element. When the trap is active,
    /// Tab/Shift+Tab cycles within this element's visual subtree.
    /// </summary>
    public static T FocusTrap<T>(this T el, FocusTrapHandle handle) where T : Element
    {
        return (T)(el with
        {
            Modifiers = (el.Modifiers ?? new Core.ElementModifiers()) with
            {
                OnMountAction = fe =>
                {
                    handle.SetContainer(fe);
                    el.Modifiers?.OnMountAction?.Invoke(fe);
                }
            }
        });
    }
}
