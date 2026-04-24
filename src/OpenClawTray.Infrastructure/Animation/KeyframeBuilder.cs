using System.Numerics;

namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Immutable definition of a keyframe animation, built by the fluent builder.
/// </summary>
public record KeyframeAnimationDef
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(300);
    public bool Loop { get; init; }
    public KeyframeDef[] Keyframes { get; init; } = [];
}

/// <summary>
/// A single keyframe in a keyframe animation.
/// </summary>
public record KeyframeDef(float Progress)
{
    public float? Opacity { get; init; }
    public Vector3? Scale { get; init; }
    public Vector3? Translation { get; init; }
    public float? Rotation { get; init; }
    public Easing? Easing { get; init; }
}

/// <summary>
/// Fluent builder for constructing keyframe animation definitions.
/// </summary>
public class KeyframeBuilder
{
    private TimeSpan _duration = TimeSpan.FromMilliseconds(300);
    private bool _loop;
    private readonly List<KeyframeDef> _keyframes = new();

    public KeyframeBuilder Duration(int ms)
    {
        _duration = TimeSpan.FromMilliseconds(ms);
        return this;
    }

    public KeyframeBuilder Loop()
    {
        _loop = true;
        return this;
    }

    public KeyframeBuilder At(float progress,
        float? opacity = null, Vector3? scale = null,
        Vector3? translation = null, float? rotation = null,
        Easing? easing = null)
    {
        if (progress < 0f || progress > 1f)
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be between 0.0 and 1.0.");

        _keyframes.Add(new KeyframeDef(progress)
        {
            Opacity = opacity,
            Scale = scale,
            Translation = translation,
            Rotation = rotation,
            Easing = easing,
        });
        return this;
    }

    public KeyframeAnimationDef Build() => new()
    {
        Duration = _duration,
        Loop = _loop,
        Keyframes = _keyframes.ToArray(),
    };
}

/// <summary>
/// A named keyframe animation entry stored on the Element.
/// </summary>
public record KeyframeEntry(string Name, object? Trigger, KeyframeAnimationDef Definition);

/// <summary>
/// Stagger configuration for container children animations.
/// </summary>
public record StaggerConfig(TimeSpan Delay, Curve? Curve = null);
