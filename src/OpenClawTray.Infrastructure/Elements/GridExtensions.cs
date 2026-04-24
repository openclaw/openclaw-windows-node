using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Fluent extension methods for Grid attached properties.
/// Usage: Text("hello").Grid(row: 1, column: 2)
/// </summary>
public static class GridExtensions
{
    /// <summary>
    /// Sets Grid attached properties (row, column, spans) on this element.
    /// Only meaningful when the element is a child of a Grid.
    /// </summary>
    public static T Grid<T>(this T el, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : Element =>
        (T)el.SetAttached(new GridAttached(row, column, rowSpan, columnSpan));
}
