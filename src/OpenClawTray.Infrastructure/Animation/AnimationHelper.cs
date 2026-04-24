using System.Numerics;
using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Bridge between Reactor's animation system and the WinUI compositor.
/// Routes property changes through compositor animations when an ambient curve is present,
/// or falls back to direct property assignment.
/// </summary>
internal static class AnimationHelper
{
    /// <summary>
    /// Sets a scalar property on a UIElement, animating via the compositor if
    /// <see cref="AnimationScope.Current"/> is non-null.
    /// </summary>
    internal static void SetOrAnimate(UIElement element, string property, float value)
    {
        // Resolve animation curve: prefer ambient WithAnimation scope,
        // fall back to element's .Animate() config curve.
        var curve = AnimationScope.HasScope ? AnimationScope.Current : null;
        curve ??= (element is FrameworkElement fe && fe.Tag is Element tagEl)
            ? tagEl.AnimationConfig?.Curve : null;

        if (curve is null)
        {
            SetScalarDirect(element, property, value);
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var target = CompositorPropertyName(property);

        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringScalarAnimation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.FinalValue = value;
                visual.StartAnimation(target, anim);
                break;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertKeyFrame(1.0f, value, easing);
                anim.Duration = ease.Duration;
                visual.StartAnimation(target, anim);
                break;
            }
            case LinearCurve linear:
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, value);
                anim.Duration = linear.Duration;
                visual.StartAnimation(target, anim);
                break;
            }
            default:
                SetScalarDirect(element, property, value);
                break;
        }
    }

    /// <summary>
    /// Creates a scalar implicit animation from a Curve instance for use in ImplicitAnimationCollections.
    /// </summary>
    internal static CompositionAnimation CreateScalarImplicitAnimation(Compositor compositor, string target, Curve curve)
    {
        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringScalarAnimation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.Target = target;
                return anim;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                anim.Duration = ease.Duration;
                anim.Target = target;
                return anim;
            }
            default: // LinearCurve
            {
                var linear = curve as LinearCurve ?? new LinearCurve(TimeSpan.FromMilliseconds(300));
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = linear.Duration;
                anim.Target = target;
                return anim;
            }
        }
    }

    /// <summary>
    /// Creates a Vector3 implicit animation from a Curve instance for use in ImplicitAnimationCollections.
    /// </summary>
    internal static CompositionAnimation CreateVector3ImplicitAnimation(Compositor compositor, string target, Curve curve)
    {
        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringVector3Animation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.Target = target;
                return anim;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                anim.Duration = ease.Duration;
                anim.Target = target;
                return anim;
            }
            default: // LinearCurve
            {
                var linear = curve as LinearCurve ?? new LinearCurve(TimeSpan.FromMilliseconds(300));
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = linear.Duration;
                anim.Target = target;
                return anim;
            }
        }
    }

    /// <summary>
    /// Creates a scalar animation targeting a specific value (for explicit StartAnimation on Visual).
    /// Returns CompositionAnimation (may be KeyFrameAnimation or SpringNaturalMotionAnimation).
    /// </summary>
    internal static CompositionAnimation CreateScalarTargetAnimation(Compositor compositor, float targetValue, Curve curve)
    {
        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringScalarAnimation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.FinalValue = targetValue;
                return anim;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertKeyFrame(1.0f, targetValue, easing);
                anim.Duration = ease.Duration;
                return anim;
            }
            default:
            {
                var linear = curve as LinearCurve ?? new LinearCurve(TimeSpan.FromMilliseconds(300));
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, targetValue);
                anim.Duration = linear.Duration;
                return anim;
            }
        }
    }

    /// <summary>
    /// Creates a Vector3 animation targeting a specific value (for explicit StartAnimation on Visual).
    /// Returns CompositionAnimation (may be KeyFrameAnimation or SpringNaturalMotionAnimation).
    /// </summary>
    internal static CompositionAnimation CreateVector3TargetAnimation(Compositor compositor, Vector3 targetValue, Curve curve)
    {
        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringVector3Animation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.FinalValue = targetValue;
                return anim;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertKeyFrame(1.0f, targetValue, easing);
                anim.Duration = ease.Duration;
                return anim;
            }
            default:
            {
                var linear = curve as LinearCurve ?? new LinearCurve(TimeSpan.FromMilliseconds(300));
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, targetValue);
                anim.Duration = linear.Duration;
                return anim;
            }
        }
    }

    /// <summary>
    /// Sets DelayTime on a CompositionAnimation (works for KeyFrameAnimation and NaturalMotionAnimation).
    /// </summary>
    internal static void SetDelay(CompositionAnimation anim, TimeSpan delay)
    {
        if (anim is KeyFrameAnimation kfa) kfa.DelayTime = delay;
        else if (anim is NaturalMotionAnimation nma) nma.DelayTime = delay;
    }

    /// <summary>
    /// Maps Reactor property names to WinUI compositor facade property names.
    /// </summary>
    /// <summary>
    /// Sets a Vector3 property on a UIElement, animating via the compositor if
    /// <see cref="AnimationScope.Current"/> is non-null.
    /// </summary>
    internal static void SetOrAnimateVector3(UIElement element, string property, Vector3 value)
    {
        var curve = AnimationScope.HasScope ? AnimationScope.Current : null;
        curve ??= (element is FrameworkElement fe && fe.Tag is Element tagEl)
            ? tagEl.AnimationConfig?.Curve : null;

        if (curve is null)
        {
            SetVector3Direct(element, property, value);
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var target = CompositorPropertyName(property);

        switch (curve)
        {
            case SpringCurve spring:
            {
                var anim = compositor.CreateSpringVector3Animation();
                anim.DampingRatio = spring.DampingRatio;
                anim.Period = TimeSpan.FromSeconds(spring.Period);
                anim.FinalValue = value;
                visual.StartAnimation(target, anim);
                break;
            }
            case EaseCurve ease:
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(ease.Easing.X1, ease.Easing.Y1),
                    new Vector2(ease.Easing.X2, ease.Easing.Y2));
                anim.InsertKeyFrame(1.0f, value, easing);
                anim.Duration = ease.Duration;
                visual.StartAnimation(target, anim);
                break;
            }
            case LinearCurve linear:
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, value);
                anim.Duration = linear.Duration;
                visual.StartAnimation(target, anim);
                break;
            }
            default:
                SetVector3Direct(element, property, value);
                break;
        }
    }

    private static string CompositorPropertyName(string property) => property switch
    {
        "Opacity" => "Opacity",
        "Scale" => "Scale",
        "Rotation" => "RotationAngle",
        "Translation" => "Translation",
        "CenterPoint" => "CenterPoint",
        _ => property,
    };

    private static void SetScalarDirect(UIElement element, string property, float value)
    {
        switch (property)
        {
            case "Opacity":
                element.Opacity = value;
                break;
            case "Rotation":
                element.Rotation = value;
                break;
        }
    }

    private static void SetVector3Direct(UIElement element, string property, Vector3 value)
    {
        switch (property)
        {
            case "Scale":
                element.Scale = value;
                break;
            case "Translation":
                element.Translation = value;
                break;
            case "CenterPoint":
                element.CenterPoint = value;
                break;
        }
    }
}

/// <summary>
/// Static accessor for the current Compositor instance.
/// </summary>
public static class CompositorProvider
{
    [ThreadStatic] private static Compositor? _current;

    public static Compositor? Current
    {
        get => _current;
        set => _current = value;
    }

    public static Compositor EnsureCompositor(UIElement element)
    {
        if (_current is not null) return _current;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        _current = visual.Compositor;
        return _current;
    }
}
