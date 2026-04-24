using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Runs GPU-accelerated Composition-layer transitions between navigation pages.
/// All animations run on the compositor thread — zero managed-code involvement during playback.
/// </summary>
internal static class TransitionEngine
{
    /// <summary>
    /// Runs a transition animation between two page visuals.
    /// </summary>
    /// <param name="outgoing">The page being navigated away from.</param>
    /// <param name="incoming">The page being navigated to (mounted at Opacity 0).</param>
    /// <param name="transition">The transition type to apply.</param>
    /// <param name="mode">The navigation mode (used for automatic reverse on GoBack).</param>
    /// <param name="onComplete">Callback invoked when the transition finishes.</param>
    public static void RunTransition(
        UIElement outgoing, UIElement incoming,
        NavigationTransition transition, NavigationMode mode,
        Action onComplete)
    {
        if (transition is SuppressTransition)
        {
            // Instant swap — no animation
            var inVis = ElementCompositionPreview.GetElementVisual(incoming);
            inVis.Opacity = 1;
            inVis.Offset = Vector3.Zero;
            inVis.Scale = Vector3.One;
            onComplete();
            return;
        }

        var outVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        var inVisual = ElementCompositionPreview.GetElementVisual(incoming);
        var compositor = outVisual.Compositor;

        // Reset stale compositor properties from previous animations
        inVisual.Offset = Vector3.Zero;
        inVisual.Scale = Vector3.One;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        switch (transition)
        {
            case SlideTransition slide:
                RunSlide(compositor, outVisual, inVisual, slide, mode);
                break;
            case FadeTransition fade:
                RunFade(compositor, outVisual, inVisual, fade);
                break;
            case DrillInTransition drill:
                RunDrillIn(compositor, outVisual, inVisual, drill, mode);
                break;
            case SpringSlideTransition spring:
                RunSpringSlide(compositor, outVisual, inVisual, spring, mode);
                break;
            case ConnectedTransition:
                // Stub — fall back to default slide
                global::System.Diagnostics.Debug.WriteLine("[Reactor] ConnectedTransition not yet implemented; falling back to SlideTransition.");
                RunSlide(compositor, outVisual, inVisual, new SlideTransition(), mode);
                break;
            default:
                // Unknown transition — instant swap
                inVisual.Opacity = 1;
                onComplete();
                return;
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            // Finalize: ensure incoming is fully visible, reset outgoing
            inVisual.Opacity = 1;
            inVisual.Offset = Vector3.Zero;
            inVisual.Scale = Vector3.One;
            onComplete();
            batch.Dispose();
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  Slide transition
    // ════════════════════════════════════════════════════════════════

    private static void RunSlide(
        Compositor compositor, Visual outVisual, Visual inVisual,
        SlideTransition slide, NavigationMode mode)
    {
        var duration = slide.Duration ?? TimeSpan.FromMilliseconds(250);
        var distance = slide.Distance ?? 200f;
        var direction = slide.Direction;

        // Reverse direction for GoBack/Forward-reverse
        if (mode == NavigationMode.Pop)
            direction = ReverseDirection(direction);

        var (outEnd, inStart) = GetSlideOffsets(direction, distance);
        var easing = slide.Easing ?? compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        // Outgoing: slide out + fade out
        var outOffset = compositor.CreateVector3KeyFrameAnimation();
        outOffset.InsertKeyFrame(0f, Vector3.Zero);
        outOffset.InsertKeyFrame(1f, outEnd, easing);
        outOffset.Duration = duration;

        var outFade = compositor.CreateScalarKeyFrameAnimation();
        outFade.InsertKeyFrame(0f, 1f);
        outFade.InsertKeyFrame(1f, 0f, easing);
        outFade.Duration = duration;

        outVisual.StartAnimation("Offset", outOffset);
        outVisual.StartAnimation("Opacity", outFade);

        // Incoming: slide in + fade in
        inVisual.Offset = inStart;

        var inOffset = compositor.CreateVector3KeyFrameAnimation();
        inOffset.InsertKeyFrame(0f, inStart);
        inOffset.InsertKeyFrame(1f, Vector3.Zero, easing);
        inOffset.Duration = duration;

        var inFade = compositor.CreateScalarKeyFrameAnimation();
        inFade.InsertKeyFrame(0f, 0f);
        inFade.InsertKeyFrame(1f, 1f, easing);
        inFade.Duration = duration;

        inVisual.StartAnimation("Offset", inOffset);
        inVisual.StartAnimation("Opacity", inFade);
    }

    internal static SlideDirection ReverseDirection(SlideDirection direction) => direction switch
    {
        SlideDirection.FromRight => SlideDirection.FromLeft,
        SlideDirection.FromLeft => SlideDirection.FromRight,
        SlideDirection.FromBottom => SlideDirection.FromTop,
        SlideDirection.FromTop => SlideDirection.FromBottom,
        _ => direction,
    };

    internal static (Vector3 OutEnd, Vector3 InStart) GetSlideOffsets(SlideDirection direction, float distance = 200f)
    {
        return direction switch
        {
            SlideDirection.FromRight => (new Vector3(-distance, 0, 0), new Vector3(distance, 0, 0)),
            SlideDirection.FromLeft => (new Vector3(distance, 0, 0), new Vector3(-distance, 0, 0)),
            SlideDirection.FromBottom => (new Vector3(0, -distance, 0), new Vector3(0, distance, 0)),
            SlideDirection.FromTop => (new Vector3(0, distance, 0), new Vector3(0, -distance, 0)),
            _ => (Vector3.Zero, Vector3.Zero),
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  Fade transition
    // ════════════════════════════════════════════════════════════════

    private static void RunFade(
        Compositor compositor, Visual outVisual, Visual inVisual,
        FadeTransition fade)
    {
        var duration = fade.Duration ?? TimeSpan.FromMilliseconds(200);

        // Outgoing: fade out
        var outFade = compositor.CreateScalarKeyFrameAnimation();
        outFade.InsertKeyFrame(0f, 1f);
        outFade.InsertKeyFrame(1f, 0f);
        outFade.Duration = duration;
        outVisual.StartAnimation("Opacity", outFade);

        // Incoming: fade in
        var inFade = compositor.CreateScalarKeyFrameAnimation();
        inFade.InsertKeyFrame(0f, 0f);
        inFade.InsertKeyFrame(1f, 1f);
        inFade.Duration = duration;
        inVisual.StartAnimation("Opacity", inFade);
    }

    // ════════════════════════════════════════════════════════════════
    //  DrillIn transition
    // ════════════════════════════════════════════════════════════════

    private static void RunDrillIn(
        Compositor compositor, Visual outVisual, Visual inVisual,
        DrillInTransition drill, NavigationMode mode)
    {
        var duration = drill.Duration ?? TimeSpan.FromMilliseconds(300);
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        if (mode == NavigationMode.Pop)
        {
            // Reverse: outgoing scales down + fades out, incoming fades in
            var outScale = compositor.CreateVector3KeyFrameAnimation();
            outScale.InsertKeyFrame(0f, Vector3.One);
            outScale.InsertKeyFrame(1f, new Vector3(0.85f, 0.85f, 1f), easing);
            outScale.Duration = duration;

            var outFade = compositor.CreateScalarKeyFrameAnimation();
            outFade.InsertKeyFrame(0f, 1f);
            outFade.InsertKeyFrame(1f, 0f, easing);
            outFade.Duration = duration;

            outVisual.CenterPoint = new Vector3(outVisual.Size / 2, 0);
            outVisual.StartAnimation("Scale", outScale);
            outVisual.StartAnimation("Opacity", outFade);

            var inFade = compositor.CreateScalarKeyFrameAnimation();
            inFade.InsertKeyFrame(0f, 0f);
            inFade.InsertKeyFrame(1f, 1f, easing);
            inFade.Duration = duration;
            inVisual.StartAnimation("Opacity", inFade);
        }
        else
        {
            // Forward: incoming scales up from 0.85 + fades in, outgoing fades out
            inVisual.Scale = new Vector3(0.85f, 0.85f, 1f);
            inVisual.CenterPoint = new Vector3(inVisual.Size / 2, 0);

            var inScale = compositor.CreateVector3KeyFrameAnimation();
            inScale.InsertKeyFrame(0f, new Vector3(0.85f, 0.85f, 1f));
            inScale.InsertKeyFrame(1f, Vector3.One, easing);
            inScale.Duration = duration;

            var inFade = compositor.CreateScalarKeyFrameAnimation();
            inFade.InsertKeyFrame(0f, 0f);
            inFade.InsertKeyFrame(1f, 1f, easing);
            inFade.Duration = duration;

            inVisual.StartAnimation("Scale", inScale);
            inVisual.StartAnimation("Opacity", inFade);

            var outFade = compositor.CreateScalarKeyFrameAnimation();
            outFade.InsertKeyFrame(0f, 1f);
            outFade.InsertKeyFrame(1f, 0f, easing);
            outFade.Duration = duration;
            outVisual.StartAnimation("Opacity", outFade);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Spring slide transition
    // ════════════════════════════════════════════════════════════════

    private static void RunSpringSlide(
        Compositor compositor, Visual outVisual, Visual inVisual,
        SpringSlideTransition spring, NavigationMode mode)
    {
        var direction = spring.Direction;
        if (mode == NavigationMode.Pop)
            direction = ReverseDirection(direction);

        var (outEnd, inStart) = GetSlideOffsets(direction);

        // Outgoing: spring offset + fade
        var outSpring = compositor.CreateSpringVector3Animation();
        outSpring.DampingRatio = spring.DampingRatio;
        outSpring.Period = TimeSpan.FromSeconds(spring.Period);
        outSpring.FinalValue = outEnd;
        outVisual.StartAnimation("Offset", outSpring);

        var outFade = compositor.CreateScalarKeyFrameAnimation();
        outFade.InsertKeyFrame(0f, 1f);
        outFade.InsertKeyFrame(1f, 0f);
        outFade.Duration = TimeSpan.FromMilliseconds(200);
        outVisual.StartAnimation("Opacity", outFade);

        // Incoming: spring offset + fade
        inVisual.Offset = inStart;

        var inSpring = compositor.CreateSpringVector3Animation();
        inSpring.DampingRatio = spring.DampingRatio;
        inSpring.Period = TimeSpan.FromSeconds(spring.Period);
        inSpring.FinalValue = Vector3.Zero;
        inVisual.StartAnimation("Offset", inSpring);

        var inFade = compositor.CreateScalarKeyFrameAnimation();
        inFade.InsertKeyFrame(0f, 0f);
        inFade.InsertKeyFrame(1f, 1f);
        inFade.Duration = TimeSpan.FromMilliseconds(200);
        inVisual.StartAnimation("Opacity", inFade);
    }
}
