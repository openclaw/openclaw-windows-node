using System.Numerics;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Values for a single interaction state (PointerOver, Pressed, Focused).
/// Only visual/brush properties — no layout properties (Width, Margin, etc.).
/// The record IS the boundary: adding a field is a deliberate API decision.
/// </summary>
public record InteractionStateValues(
    // Compositor-accelerated (zero cost during interaction)
    float? Opacity = null,
    float? Scale = null,        // Uniform scale (convenience)
    Vector3? ScaleV = null,     // Non-uniform scale
    Vector3? Translation = null,
    float? Rotation = null,
    // Direct property set (pre-cached brush swap, ~1us)
    Brush? Background = null,
    Brush? Foreground = null,
    Brush? BorderBrush = null);

/// <summary>
/// Configuration for the .InteractionStates() modifier.
/// Defines visual state values for PointerOver, Pressed, and Focused states,
/// plus an optional animation curve for state transitions.
/// </summary>
public record InteractionStatesConfig(
    InteractionStateValues? PointerOver = null,
    InteractionStateValues? Pressed = null,
    InteractionStateValues? Focused = null,
    Curve? Curve = null);

/// <summary>
/// Builder for constructing InteractionStatesConfig fluently.
/// </summary>
public class InteractionStatesBuilder
{
    private InteractionStateValues? _pointerOver;
    private InteractionStateValues? _pressed;
    private InteractionStateValues? _focused;

    public InteractionStatesBuilder PointerOver(
        float? opacity = null, float? scale = null, Vector3? scaleV = null,
        Vector3? translation = null, float? rotation = null,
        Brush? background = null, Brush? foreground = null, Brush? borderBrush = null)
    {
        _pointerOver = new InteractionStateValues(opacity, scale, scaleV, translation, rotation, background, foreground, borderBrush);
        return this;
    }

    public InteractionStatesBuilder Pressed(
        float? opacity = null, float? scale = null, Vector3? scaleV = null,
        Vector3? translation = null, float? rotation = null,
        Brush? background = null, Brush? foreground = null, Brush? borderBrush = null)
    {
        _pressed = new InteractionStateValues(opacity, scale, scaleV, translation, rotation, background, foreground, borderBrush);
        return this;
    }

    public InteractionStatesBuilder Focused(
        float? opacity = null, float? scale = null, Vector3? scaleV = null,
        Vector3? translation = null, float? rotation = null,
        Brush? background = null, Brush? foreground = null, Brush? borderBrush = null)
    {
        _focused = new InteractionStateValues(opacity, scale, scaleV, translation, rotation, background, foreground, borderBrush);
        return this;
    }

    internal InteractionStatesConfig Build() =>
        new(_pointerOver, _pressed, _focused);
}
