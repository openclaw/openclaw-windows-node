using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

/// <summary>
/// Placeholder for unrecognized component types. Never crashes; surfaces
/// the offending name + a warning glyph so operators can see catalog drift.
/// Also used for renderer-thrown exceptions (registered under "__error__").
/// </summary>
internal sealed class UnknownRenderer : IComponentRenderer
{
    public string ComponentName => "<unknown>";

    public FrameworkElement Render(A2UIComponentDef component, RenderContext ctx)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            CornerRadius = new CornerRadius(4),
        };
        stack.Children.Add(new FontIcon
        {
            Glyph = "", // outlined warning
            FontFamily = new FontFamily("Segoe Fluent Icons"),
        });
        var template = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_UnsupportedComponent");
        stack.Children.Add(new TextBlock
        {
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, template, component.ComponentName),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return stack;
    }
}
