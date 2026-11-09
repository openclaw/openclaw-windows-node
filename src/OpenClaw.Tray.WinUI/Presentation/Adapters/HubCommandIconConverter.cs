// <summary>
// Maps existing command icon strings to the shared Segoe Fluent catalog.
// The strings remain unchanged for app.search wire compatibility.
// Usage: bind HubCommand.Icon to FontIcon.Glyph with this converter.
// </summary>

using Microsoft.UI.Xaml.Data;
using OpenClawTray.Helpers;

namespace OpenClawTray.Presentation.Adapters;

public sealed class HubCommandIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string icon
            ? icon switch
            {
                "\U0001F50C" => FluentIconCatalog.System,
                "AI" => FluentIconCatalog.Inference,
                "\U0001F4AC" or "\U0001F9E0" => FluentIconCatalog.Sessions,
                "\U0001F4E1" => FluentIconCatalog.ServerEnvironment,
                "\U0001F6E1\uFE0F" => FluentIconCatalog.Permissions,
                "\u2699\uFE0F" => FluentIconCatalog.Settings,
                "\U0001F514" => FluentIconCatalog.Notifications,
                "\u2139\uFE0F" => FluentIconCatalog.About,
                "\U0001F310" => FluentIconCatalog.Browser,
                "\U0001F41B" => FluentIconCatalog.Bug,
                "\U0001F4F7" => FluentIconCatalog.Camera,
                "\U0001F3A8" => FluentIconCatalog.Canvas,
                "\U0001F5A5\uFE0F" => FluentIconCatalog.Screen,
                _ => FluentIconCatalog.Settings
            }
            : FluentIconCatalog.Settings;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
