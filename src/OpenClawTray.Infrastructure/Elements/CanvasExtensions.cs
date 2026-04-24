using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Fluent extension methods for Canvas attached properties.
/// Usage: Rectangle().Canvas(left: 50, top: 100)
/// </summary>
public static class CanvasExtensions
{
    /// <summary>
    /// Sets Canvas attached properties (left, top) on this element.
    /// Only meaningful when the element is a child of a Canvas.
    /// </summary>
    public static T Canvas<T>(this T el, double left = 0, double top = 0) where T : Element =>
        (T)el.SetAttached(new CanvasAttached(left, top));
}
