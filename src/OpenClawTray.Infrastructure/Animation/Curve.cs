namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Describes the timing/physics of an animation. Immutable, shareable, zero-allocation.
/// Maps to either a CompositionEasingFunction or a SpringAnimation at the compositor layer.
/// </summary>
public abstract record Curve
{
    // -- Factory methods --

    /// <summary>Spring natural motion. DampingRatio 0=undamped, 1=critically damped.</summary>
    public static Curve Spring(float dampingRatio = 0.8f, float period = 0.05f)
        => new SpringCurve(dampingRatio, period);

    /// <summary>Cubic-bezier eased animation over a fixed duration.</summary>
    public static Curve Ease(int durationMs, Easing easing = default)
        => new EaseCurve(TimeSpan.FromMilliseconds(durationMs), easing);

    /// <summary>Constant-speed animation over a fixed duration.</summary>
    public static Curve Linear(int durationMs)
        => new LinearCurve(TimeSpan.FromMilliseconds(durationMs));
}

public sealed record SpringCurve(float DampingRatio, float Period) : Curve;
public sealed record EaseCurve(TimeSpan Duration, Easing Easing) : Curve;
public sealed record LinearCurve(TimeSpan Duration) : Curve;

/// <summary>
/// Cubic bezier easing definition. Presets match WinUI/Fluent Design motion guidelines.
/// </summary>
public readonly record struct Easing(float X1, float Y1, float X2, float Y2)
{
    public static readonly Easing Linear      = new(0f, 0f, 1f, 1f);
    public static readonly Easing EaseIn      = new(0.42f, 0f, 1f, 1f);
    public static readonly Easing EaseOut     = new(0f, 0f, 0.58f, 1f);
    public static readonly Easing EaseInOut   = new(0.42f, 0f, 0.58f, 1f);
    public static readonly Easing Accelerate  = new(0.9f, 0.1f, 1f, 0.2f);
    public static readonly Easing Decelerate  = new(0.1f, 0.9f, 0.2f, 1f);
    public static readonly Easing Standard    = new(0.8f, 0f, 0.2f, 1f);    // Fluent standard

    public static Easing CubicBezier(float x1, float y1, float x2, float y2) => new(x1, y1, x2, y2);
}
