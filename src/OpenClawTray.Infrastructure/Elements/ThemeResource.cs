using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Lookup helpers for WinUI theme resources.
/// These resolve against Application.Current.Resources, which includes
/// all merged dictionaries (XamlControlsResources, custom themes, etc.).
///
/// Usage:
///   var brush = ThemeResource.Brush("TextFillColorSecondaryBrush");
///   var radius = ThemeResource.CornerRadius("OverlayCornerRadius");
/// </summary>
public static class ThemeResource
{
    public static Brush Brush(string key) => Get<Brush>(key)
        ?? throw new KeyNotFoundException($"Theme resource '{key}' not found or is not a Brush");

    public static double Double(string key) => Get<double>(key, double.NaN) is double v && !double.IsNaN(v)
        ? v
        : throw new KeyNotFoundException($"Theme resource '{key}' not found or is not a double");

    public static CornerRadius CornerRadius(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is CornerRadius cr)
            return cr;
        throw new KeyNotFoundException($"Theme resource '{key}' not found or is not a CornerRadius");
    }

    public static Thickness Thickness(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Thickness t)
            return t;
        throw new KeyNotFoundException($"Theme resource '{key}' not found or is not a Thickness");
    }

    /// <summary>
    /// Try to look up a resource, returning default if not found.
    /// </summary>
    public static T Get<T>(string key, T defaultValue = default!)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return defaultValue;
    }
}
