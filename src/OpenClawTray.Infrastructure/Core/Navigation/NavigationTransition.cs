using Microsoft.UI.Composition;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Direction for slide-based transitions.
/// </summary>
public enum SlideDirection
{
    FromRight,
    FromLeft,
    FromBottom,
    FromTop,
}

/// <summary>
/// Controls whether a page's component tree is cached across navigations.
/// </summary>
public enum NavigationCacheMode
{
    /// <summary>Page is always unmounted on navigate-away and remounted on navigate-to.</summary>
    Disabled,
    /// <summary>Page is cached up to the host's CacheSize limit (LRU eviction).</summary>
    Enabled,
    /// <summary>Page is always cached and never evicted by LRU.</summary>
    Required,
}

/// <summary>
/// Abstract base for navigation transition definitions.
/// Concrete types describe the animation; the TransitionEngine (Phase 4) executes them.
/// Static factory methods provide a convenient API.
/// </summary>
public abstract record NavigationTransition
{
    /// <summary>Default slide-from-right transition.</summary>
    public static readonly NavigationTransition Default = new SlideTransition();

    /// <summary>No animation — instant swap.</summary>
    public static readonly NavigationTransition None = new SuppressTransition();

    /// <summary>Slide transition with configurable direction, duration, distance, and easing.</summary>
    public static NavigationTransition Slide(
        SlideDirection direction = SlideDirection.FromRight,
        TimeSpan? duration = null,
        CompositionEasingFunction? easing = null,
        float? distance = null)
        => new SlideTransition
        {
            Direction = direction,
            Duration = duration,
            Easing = easing,
            Distance = distance,
        };

    /// <summary>Crossfade transition.</summary>
    public static NavigationTransition Fade(TimeSpan? duration = null)
        => new FadeTransition { Duration = duration };

    /// <summary>Drill-in (scale + fade from center) transition.</summary>
    public static NavigationTransition DrillIn(TimeSpan? duration = null)
        => new DrillInTransition { Duration = duration };

    /// <summary>Connected animation transition (stub — falls back to slide in Phase 4).</summary>
    public static NavigationTransition Connected(string animationKey)
        => new ConnectedTransition { AnimationKey = animationKey };

    /// <summary>Spring-physics slide transition.</summary>
    public static NavigationTransition Spring(
        float dampingRatio = 0.6f,
        float period = 0.08f,
        SlideDirection direction = SlideDirection.FromRight)
        => new SpringSlideTransition
        {
            DampingRatio = dampingRatio,
            Period = period,
            Direction = direction,
        };

    /// <summary>
    /// Slide-in-only transition: instantly hides old content, then slides new content
    /// in from the specified direction. Direction auto-reverses on back navigation.
    /// </summary>
    public static NavigationTransition SlideInOnly(
        SlideDirection direction = SlideDirection.FromRight,
        TimeSpan? duration = null,
        double distance = 80)
        => new SlideInOnlyTransition
        {
            Direction = direction,
            Duration = duration,
            Distance = distance,
        };
}

/// <summary>Slide transition — animate offset and opacity.</summary>
public sealed record SlideTransition : NavigationTransition
{
    public SlideDirection Direction { get; init; } = SlideDirection.FromRight;
    public TimeSpan? Duration { get; init; }
    public CompositionEasingFunction? Easing { get; init; }
    /// <summary>Slide distance in pixels. When null, defaults to 200px.</summary>
    public float? Distance { get; init; }
}

/// <summary>Crossfade transition — animate opacity on both visuals.</summary>
public sealed record FadeTransition : NavigationTransition
{
    public TimeSpan? Duration { get; init; }
}

/// <summary>Drill-in transition — scale + fade from center.</summary>
public sealed record DrillInTransition : NavigationTransition
{
    public TimeSpan? Duration { get; init; }
}

/// <summary>Connected animation transition (stub — full implementation deferred to Phase 6).</summary>
public sealed record ConnectedTransition : NavigationTransition
{
    public required string AnimationKey { get; init; }
}

/// <summary>Spring-physics slide transition.</summary>
public sealed record SpringSlideTransition : NavigationTransition
{
    public float DampingRatio { get; init; } = 0.6f;
    public float Period { get; init; } = 0.08f;
    public SlideDirection Direction { get; init; } = SlideDirection.FromRight;
}

/// <summary>No animation — instant swap.</summary>
public sealed record SuppressTransition : NavigationTransition;

/// <summary>
/// Slide-in-only: instantly hides old content, then slides + fades new content in.
/// Direction auto-reverses on back navigation (Pop mode).
/// </summary>
public sealed record SlideInOnlyTransition : NavigationTransition
{
    public SlideDirection Direction { get; init; } = SlideDirection.FromRight;
    public TimeSpan? Duration { get; init; }
    public double Distance { get; init; } = 80;
}
