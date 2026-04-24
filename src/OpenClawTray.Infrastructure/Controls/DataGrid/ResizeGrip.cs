using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// A Reactor element that renders as a panel with a west-east resize cursor.
/// Used as the drag handle for column resizing in the DataGrid header.
/// Background is set once at mount time for hit-testing and is NOT updated
/// by the reconciler — this lets event handlers change the background
/// (hover/drag feedback) without being overwritten on re-render.
/// </summary>
internal record ResizeGripElement(Element? Child = null) : Element;

/// <summary>
/// Grid subclass that exposes ProtectedCursor (which is protected on UIElement).
/// WinUI's Border is sealed so we can't subclass it. Grid supports Background
/// natively and is not sealed, making it ideal for cursor customization.
/// </summary>
internal sealed partial class ResizeGripControl : Grid
{
    public ResizeGripControl()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>
/// Registers the ResizeGripElement with a Reactor reconciler so the reconciler
/// knows how to mount and update it.
/// </summary>
internal static class ResizeGripRegistration
{
    private static readonly HashSet<Reconciler> _registered = new();

    public static void Register(Reconciler reconciler)
    {
        lock (_registered)
        {
            if (!_registered.Add(reconciler)) return;
        }

        reconciler.RegisterType<ResizeGripElement, ResizeGripControl>(
            mount: (r, el, rerender) =>
            {
                var panel = new ResizeGripControl();
                // Transparent background enables hit-testing. Set once at mount;
                // event handlers (hover/drag) mutate it directly without reconciler interference.
                panel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Transparent);
                if (el.Child is not null)
                {
                    var child = r.Mount(el.Child, rerender);
                    if (child is not null) panel.Children.Add(child);
                }
                panel.Tag = el;
                return panel;
            },
            update: (r, oldEl, newEl, panel, rerender) =>
            {
                // Deliberately do NOT touch panel.Background here — it is managed
                // by pointer event handlers (hover/drag) attached via OnMount.
                // Re-setting it would overwrite the hover/drag visual state.

                if (newEl.Child is not null && oldEl.Child is not null)
                {
                    if (panel.Children.Count > 0 && panel.Children[0] is UIElement existingChild)
                    {
                        var replacement = r.UpdateChild(oldEl.Child, newEl.Child, existingChild, rerender);
                        if (replacement is not null)
                            panel.Children[0] = replacement;
                    }
                }
                else if (newEl.Child is not null && oldEl.Child is null)
                {
                    var child = r.Mount(newEl.Child, rerender);
                    if (child is not null) panel.Children.Add(child);
                }
                else if (newEl.Child is null && oldEl.Child is not null)
                {
                    panel.Children.Clear();
                }

                panel.Tag = newEl;
                return null;
            });
    }
}
