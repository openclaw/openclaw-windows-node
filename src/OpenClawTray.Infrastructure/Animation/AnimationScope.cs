using Microsoft.UI.Composition;

namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Provides ambient animation context via [ThreadStatic] fields.
/// When a curve is active, property changes in the reconciler's ApplyModifiers
/// route through compositor animations instead of direct property assignment.
/// </summary>
public static class AnimationScope
{
    [ThreadStatic] private static Curve? _current;
    [ThreadStatic] private static bool _hasScope;

    /// <summary>Gets the current ambient animation curve, or null if none.</summary>
    public static Curve? Current => _current;

    /// <summary>Returns true if inside a WithAnimation scope (even if curve is null for explicit suppression).</summary>
    public static bool HasScope => _hasScope;

    /// <summary>
    /// Runs the action with the given curve as the ambient animation.
    /// Property changes in the reconciler will animate using this curve.
    /// Nesting is supported: inner scopes override outer ones, restoring correctly in finally.
    /// Pass null to explicitly suppress animation (e.g., inside an outer animated scope).
    /// </summary>
    public static void WithAnimation(Curve? curve, Action action)
    {
        var prevCurve = _current;
        var prevScope = _hasScope;
        _current = curve;
        _hasScope = true;
        try { action(); }
        finally { _current = prevCurve; _hasScope = prevScope; }
    }

    /// <summary>
    /// Pushes a curve onto the animation scope. Must be paired with <see cref="PopScope"/>.
    /// Used by the render host to restore a captured animation scope across async boundaries.
    /// </summary>
    internal static void PushScope(Curve? curve)
    {
        _current = curve;
        _hasScope = true;
    }

    /// <summary>
    /// Pops the animation scope pushed by <see cref="PushScope"/>.
    /// </summary>
    internal static void PopScope()
    {
        _current = null;
        _hasScope = false;
    }

    /// <summary>
    /// Async variant: runs the action with ambient animation, then returns a Task
    /// that completes when all compositor animations started during the scope finish.
    /// Uses CompositionScopedBatch for completion tracking.
    /// </summary>
    public static Task WithAnimationAsync(Curve? curve, Action action)
    {
        var compositor = CompositorProvider.Current;
        if (compositor is null)
        {
            // No compositor available — run synchronously without animation tracking
            WithAnimation(curve, action);
            return Task.CompletedTask;
        }

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var tcs = new TaskCompletionSource();

        try { WithAnimation(curve, action); }
        catch { batch.End(); throw; }

        batch.End();
        batch.Completed += (_, _) => tcs.SetResult();
        return tcs.Task;
    }
}
