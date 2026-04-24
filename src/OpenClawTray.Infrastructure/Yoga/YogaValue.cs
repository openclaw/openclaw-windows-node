// C# port of Meta's Yoga layout engine value types.
// Ported from yoga/YGValue.h, yoga/numeric/FloatOptional.h, yoga/numeric/Comparison.h,
// yoga/style/StyleLength.h, yoga/style/StyleSizeLength.h

using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Represents a CSS-like value with a unit (point, percent, auto, etc.).
/// </summary>
internal readonly record struct YogaValue(float Value, YogaUnit Unit)
{
    public static readonly YogaValue Auto = new(0, YogaUnit.Auto);
    public static readonly YogaValue Undefined = new(float.NaN, YogaUnit.Undefined);
    public static readonly YogaValue Zero = new(0, YogaUnit.Point);

    public static YogaValue Point(float v) => new(v, YogaUnit.Point);
    public static YogaValue Percent(float v) => new(v, YogaUnit.Percent);

    public bool IsAuto => Unit == YogaUnit.Auto;
    public bool IsUndefined => Unit == YogaUnit.Undefined;
    public bool IsPoint => Unit == YogaUnit.Point;
    public bool IsPercent => Unit == YogaUnit.Percent;
    public bool IsDefined => Unit != YogaUnit.Undefined;

    /// <summary>
    /// Resolve a percentage value against a reference length. Returns NaN if not resolvable.
    /// </summary>
    public float Resolve(float referenceLength)
    {
        return Unit switch
        {
            YogaUnit.Point => Value,
            YogaUnit.Percent => Value * referenceLength / 100.0f,
            _ => float.NaN,
        };
    }
}

/// <summary>
/// Float comparison and undefined-value utilities, ported from yoga/numeric/Comparison.h
/// and yoga/numeric/FloatOptional.h.
/// </summary>
internal static class YogaFloat
{
    private const float Epsilon = 0.0001f;

    public static bool IsUndefined(float value) => float.IsNaN(value);
    public static bool IsDefined(float value) => !float.IsNaN(value);

    /// <summary>
    /// Epsilon-based float comparison. Two undefined (NaN) values are considered equal.
    /// </summary>
    public static bool InexactEquals(float a, float b)
    {
        if (IsDefined(a) && IsDefined(b))
            return MathF.Abs(a - b) < Epsilon;
        return IsUndefined(a) && IsUndefined(b);
    }

    /// <summary>
    /// Returns the larger of two values, preferring defined values over undefined.
    /// </summary>
    public static float MaxOrDefined(float a, float b)
    {
        if (IsDefined(a) && IsDefined(b))
            return MathF.Max(a, b);
        return IsDefined(a) ? a : b;
    }

    /// <summary>
    /// Returns the smaller of two values, preferring defined values over undefined.
    /// </summary>
    public static float MinOrDefined(float a, float b)
    {
        if (IsDefined(a) && IsDefined(b))
            return MathF.Min(a, b);
        return IsDefined(a) ? a : b;
    }

    /// <summary>
    /// Unwrap a float value, returning the default if undefined (NaN).
    /// </summary>
    public static float UnwrapOrDefault(float value, float defaultValue = 0)
    {
        return IsDefined(value) ? value : defaultValue;
    }
}

/// <summary>
/// Extension methods for StyleLength-like operations on YogaValue.
/// Ported from yoga/style/StyleLength.h
/// </summary>
internal static class YogaValueExtensions
{
    /// <summary>
    /// Resolve a YogaValue against a reference length. Auto and Undefined resolve to NaN.
    /// </summary>
    public static float ResolveValue(this YogaValue value, float referenceLength)
    {
        return value.Unit switch
        {
            YogaUnit.Point => value.Value,
            YogaUnit.Percent => value.Value * referenceLength / 100.0f,
            _ => float.NaN,
        };
    }

    /// <summary>
    /// For size lengths: also handles MaxContent, FitContent, Stretch keywords.
    /// </summary>
    public static bool IsMaxContent(this YogaValue value) => value.Unit == YogaUnit.MaxContent;
    public static bool IsFitContent(this YogaValue value) => value.Unit == YogaUnit.FitContent;
    public static bool IsStretch(this YogaValue value) => value.Unit == YogaUnit.Stretch;
}
