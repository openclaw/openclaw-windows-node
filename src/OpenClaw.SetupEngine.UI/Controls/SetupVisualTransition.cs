// Player-driven transitions for setup's live XAML visuals, not snapshots or a second frame clock.
// Example: Play(player, duration, motion => motion.Animate(visual, "Opacity", (0, 0, null), (1, 1, null))).
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Shares the mascot's native player contract with finite setup transitions.</summary>
internal sealed class SetupVisualTransition(TimeSpan duration, Action<SetupVisualTransition.NativeVisual> animate)
    : IAnimatedVisualSource
{
    private bool _cancelled;

    public IAnimatedVisual TryCreateAnimatedVisual(Compositor compositor, out object diagnostics)
    {
        diagnostics = null!;
        var visual = new NativeVisual(compositor, duration);
        try
        {
            animate(visual);
            return visual;
        }
        catch
        {
            visual.Dispose();
            throw;
        }
    }

    /// <summary>Gets the live XAML visual without replacing its text, input or accessibility tree.</summary>
    internal static Visual GetVisual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    /// <summary>Replaces a transition. Only an uninterrupted, mounted play may run its completion.</summary>
    internal static void Play(AnimatedVisualPlayer player, TimeSpan duration,
        Action<NativeVisual> animate, Action? completed = null)
    {
        Stop(player);
        _ = PlayAsync();

        async Task PlayAsync()
        {
            var source = new SetupVisualTransition(duration, animate);
            try
            {
                player.Source = source;
                await player.PlayAsync(0, 1, looped: false);
            }
            catch (OperationCanceledException) when (source._cancelled || !player.IsLoaded)
            {
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                Trace.TraceError($"Setup visual transition failed: {exception}");
            }
            finally
            {
                if (!source._cancelled && ReferenceEquals(player.Source, source))
                {
                    Stop(player);
                    // A rendering failure also settles the requested state, after logging above.
                    if (player.IsLoaded)
                        completed?.Invoke();
                }
            }
        }
    }

    /// <summary>Releases native bindings before a new state, reduced motion or unload.</summary>
    internal static void Stop(AnimatedVisualPlayer player)
    {
        if (player.Source is SetupVisualTransition source)
            source._cancelled = true;
        player.Stop();
        player.Source = null;
    }

    /// <summary>Native keyframes seek from the player's Progress, using Windows' own easing functions.</summary>
    internal sealed class NativeVisual : IAnimatedVisual
    {
        private readonly List<(CompositionObject Target, string Property, AnimationController Controller)> _bindings = [];
        private bool _disposed;
        public Visual RootVisual { get; }
        public TimeSpan Duration { get; }
        public Vector2 Size => Vector2.One;

        internal NativeVisual(Compositor compositor, TimeSpan duration)
        {
            Duration = duration;
            RootVisual = compositor.CreateContainerVisual();
            RootVisual.Size = Size;
            RootVisual.Properties.InsertScalar("Progress", 0);
        }

        internal void Animate(CompositionObject target, string property,
            params (float Progress, float Value, CompositionEasingFunction? Ease)[] frames)
        {
            using var animation = RootVisual.Compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = Duration;
            animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            using var linear = RootVisual.Compositor.CreateLinearEasingFunction();
            foreach (var (progress, value, ease) in frames)
                animation.InsertKeyFrame(progress, value, ease ?? linear);
            target.StartAnimation(property, animation);
            var controller = target.TryGetAnimationController(property);
            _bindings.Add((target, property, controller));
            controller.Pause();
            using var progressAnimation = RootVisual.Compositor.CreateExpressionAnimation("clock.Progress");
            progressAnimation.SetReferenceParameter("clock", RootVisual.Properties);
            controller.StartAnimation("Progress", progressAnimation);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (target, property, controller) in _bindings)
            {
                controller.StopAnimation("Progress");
                target.StopAnimation(property);
                controller.Dispose();
            }
            _bindings.Clear();
            RootVisual.Dispose();
        }
    }
}
