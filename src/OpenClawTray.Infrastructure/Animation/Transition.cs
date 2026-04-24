namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Describes enter/exit transitions for conditional rendering.
/// Transitions are composable: Fade + Slide(Bottom) plays both in parallel.
/// Asymmetric: MakeEnter(Fade) | MakeExit(Scale) uses different animations for enter/exit.
/// </summary>
public abstract record Transition
{
    // -- Presets --
    public static readonly Transition Fade = new FadeTransition();
    public static Transition Slide(Edge edge = Edge.Bottom) => new SlideTransition(edge);
    public static Transition Scale(float from = 0.85f) => new ScaleTransition(from);

    // -- Asymmetric factory --
    public static Transition Enter(Transition enter) => new DirectionalTransition(enter, null);
    public static Transition Exit(Transition exit) => new DirectionalTransition(null, exit);

    // -- Combinators --
    /// <summary>Combine two transitions to play in parallel (e.g., Fade + Slide).</summary>
    public static Transition operator +(Transition a, Transition b) => new CombinedTransition(a, b);

    /// <summary>Asymmetric: left side is enter, right side is exit.</summary>
    public static Transition operator |(Transition enter, Transition exit)
        => new AsymmetricTransition(enter, exit);
}

public sealed record FadeTransition : Transition;
public sealed record SlideTransition(Edge Edge) : Transition;
public sealed record ScaleTransition(float From) : Transition;
public sealed record CombinedTransition(Transition First, Transition Second) : Transition;

public sealed record AsymmetricTransition : Transition
{
    public Transition EnterTransition { get; init; }
    public Transition ExitTransition { get; init; }
    public AsymmetricTransition(Transition enter, Transition exit)
    {
        EnterTransition = enter;
        ExitTransition = exit;
    }
}

public sealed record DirectionalTransition : Transition
{
    public Transition? EnterTransition { get; init; }
    public Transition? ExitTransition { get; init; }
    public DirectionalTransition(Transition? enter, Transition? exit)
    {
        EnterTransition = enter;
        ExitTransition = exit;
    }
}

/// <summary>
/// Edge enum for slide direction.
/// </summary>
public enum Edge { Left, Top, Right, Bottom }

/// <summary>
/// Stores transition configuration on an Element. Includes the transition definition
/// and an optional curve override (default: 300ms Decelerate).
/// </summary>
public record ElementTransition(Transition Transition, Curve? Curve = null)
{
    /// <summary>Gets the enter-side transition (or the full transition if symmetric).</summary>
    internal Transition? GetEnterTransition() => Transition switch
    {
        AsymmetricTransition a => a.EnterTransition,
        DirectionalTransition { EnterTransition: not null } d => d.EnterTransition,
        DirectionalTransition { EnterTransition: null } => null,
        _ => Transition,
    };

    /// <summary>Gets the exit-side transition (or the full transition if symmetric).</summary>
    internal Transition? GetExitTransition() => Transition switch
    {
        AsymmetricTransition a => a.ExitTransition,
        DirectionalTransition { ExitTransition: not null } d => d.ExitTransition,
        DirectionalTransition { ExitTransition: null } => null,
        _ => Transition,
    };
}
