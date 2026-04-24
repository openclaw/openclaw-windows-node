using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Opaque ref to a mounted <see cref="FrameworkElement"/>. Populated by the reconciler
/// when the element is mounted; consumed by <see cref="FocusManager.Focus"/> and friends.
/// Null until the target element is mounted. Survives re-renders — the same
/// <see cref="ElementRef"/> instance is reused by the reconciler while the underlying
/// control is recycled from the element pool.
/// </summary>
public sealed class ElementRef
{
    internal FrameworkElement? _current;

    /// <summary>
    /// The currently-mounted control, or null if the referenced element has not mounted yet
    /// (or has been unmounted without a replacement).
    /// </summary>
    public FrameworkElement? Current => _current;
}

/// <summary>
/// Imperative focus helpers (spec 027 Tier 5). These operate on <see cref="ElementRef"/>
/// refs obtained via the <c>UseElementFocus</c> hook or an explicit <c>.Ref(...)</c>
/// modifier; use them when declarative focus (<c>.Focus(...)</c> form helpers,
/// <see cref="Microsoft.UI.Xaml.UIElement.IsTabStop"/>) is not enough.
/// </summary>
public static class FocusManager
{
    /// <summary>
    /// Synchronously focus the referenced element. Returns <c>false</c> when the ref
    /// has no mounted target, the target cannot receive focus, or WinUI rejects the
    /// focus request.
    /// </summary>
    public static bool Focus(ElementRef target, FocusState state = FocusState.Programmatic)
    {
        if (target?._current is not { } fe) return false;
        if (fe is Control ctrl) return ctrl.Focus(state);
        return fe.Focus(state);
    }

    /// <summary>
    /// Asynchronously focus the referenced element using WinUI's FocusManager.TryFocusAsync.
    /// Prefer this when focus is requested from a non-UI thread or when the caller needs
    /// confirmation that the focus change succeeded.
    /// </summary>
    public static async global::System.Threading.Tasks.Task<bool> FocusAsync(ElementRef target, FocusState state = FocusState.Programmatic)
    {
        if (target?._current is not { } fe) return false;
        var result = await Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(fe, state);
        return result.Succeeded;
    }
}
