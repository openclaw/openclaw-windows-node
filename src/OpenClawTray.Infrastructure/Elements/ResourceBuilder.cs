using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Elements;

/// <summary>
/// Immutable snapshot of per-control resource overrides built by <see cref="ResourceBuilder"/>.
/// Contains both literal values (colors, doubles, corner radii) and theme-reactive
/// <see cref="ThemeRef"/> entries that are re-resolved on theme change.
/// </summary>
public sealed record ResourceOverrides(
    IReadOnlyDictionary<string, object> Literals,
    IReadOnlyDictionary<string, ThemeRef> ThemeRefs)
{
    internal IEnumerable<string> AllKeys => Literals.Keys.Concat(ThemeRefs.Keys).Distinct();
}

/// <summary>
/// Fluent builder for per-control resource overrides (WinUI lightweight styling).
/// Each <c>Set()</c> call registers a resource key override that will be injected
/// into the control's <see cref="FrameworkElement.Resources"/> dictionary at mount time.
/// <para>
/// Lightweight styling lets you override built-in control resources (e.g.,
/// <c>ButtonBackground</c>, <c>ButtonBackgroundPointerOver</c>) without
/// replacing the entire control template. The control's <see cref="Microsoft.UI.Xaml.VisualStateManager"/>
/// continues to function normally, so hover/pressed/disabled states respect
/// the overrides automatically.
/// </para>
/// <para>
/// <b>Example — brand-colored button:</b>
/// <code>
/// Button("Submit").Resources(r => r
///     .Set("ButtonBackground", "#0078D4")
///     .Set("ButtonBackgroundPointerOver", "#106EBE")
///     .Set("ButtonBackgroundPressed", "#005A9E")
///     .Set("ButtonForeground", "#FFFFFF"))
/// </code>
/// </para>
/// </summary>
public sealed class ResourceBuilder
{
    private readonly Dictionary<string, object> _literals = new();
    private readonly Dictionary<string, ThemeRef> _themeRefs = new();

    /// <summary>Sets a color resource override from a hex string or named color.</summary>
    public ResourceBuilder Set(string key, string color)
    {
        _literals[key] = BrushHelper.Parse(color);
        return this;
    }

    /// <summary>Sets a brush resource override.</summary>
    public ResourceBuilder Set(string key, Brush brush)
    {
        _literals[key] = brush;
        return this;
    }

    /// <summary>Sets a theme-reactive resource override that re-resolves on theme change.</summary>
    public ResourceBuilder Set(string key, ThemeRef themeRef)
    {
        _themeRefs[key] = themeRef;
        return this;
    }

    /// <summary>Sets a numeric resource override (e.g., <c>ButtonBorderThemeThickness</c>).</summary>
    public ResourceBuilder Set(string key, double value)
    {
        _literals[key] = value;
        return this;
    }

    /// <summary>Sets a corner radius resource override (e.g., <c>ControlCornerRadius</c>).</summary>
    public ResourceBuilder Set(string key, CornerRadius value)
    {
        _literals[key] = value;
        return this;
    }

    /// <summary>Builds an immutable <see cref="ResourceOverrides"/> snapshot.</summary>
    internal ResourceOverrides Build()
    {
        return new ResourceOverrides(
            new Dictionary<string, object>(_literals),
            new Dictionary<string, ThemeRef>(_themeRefs));
    }
}
