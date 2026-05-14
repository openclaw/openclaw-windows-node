using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Lightweight WinUI Composition animation helpers for the OnboardingV2 pages.
///
/// These animations are intentionally subtle — entrance fades + a slow breathing
/// pulse on the lobster — and are designed to make the UI feel alive without
/// distracting from the content.
///
/// Capture mode discipline:
///   <see cref="DisableForCapture"/> can be set to true by a caller (the
///   SetupPreview headless capture path does this) to skip all animations and
///   leave each visual at its final steady state. This keeps the side-by-side
///   diff PNGs deterministic regardless of the animation phase, so the LLM
///   visual-eval loop is not flaky.
/// </summary>
public static class V2Animations
{
    /// <summary>
    /// Globally disables all V2 animations. SetupPreview sets this to true in
    /// headless capture mode so RenderTargetBitmap never sees an in-flight
    /// transform.
    /// </summary>
    public static bool DisableForCapture { get; set; }

    /// <summary>
    /// Fade an element from 0 → 1 opacity on Loaded. Returns the element so it
    /// can be chained alongside the existing FunctionalUI builder methods.
    /// </summary>
    public static BorderElement WithEntranceFadeIn(this BorderElement element, double durationMs = 320, double delayMs = 0)
    {
        return element.Set(b => HookFadeIn(b, durationMs, delayMs));
    }

    /// <summary>
    /// Fade an Image element from 0 → 1 opacity on Loaded.
    /// </summary>
    public static ImageElement WithEntranceFadeIn(this ImageElement element, double durationMs = 320, double delayMs = 0)
    {
        return element.Set(i => HookFadeIn(i, durationMs, delayMs));
    }

    /// <summary>
    /// Pop an Image into view: scale 0.6 → 1.05 → 1.0 with a fade. Used by the
    /// AllSet party-popper hero so it lands with a tiny celebration bounce.
    /// </summary>
    public static ImageElement WithEntrancePopIn(this ImageElement element, double durationMs = 480, double delayMs = 0)
    {
        return element.Set(i => HookPopIn(i, durationMs, delayMs));
    }

    /// <summary>
    /// Slide an element up from <paramref name="distance"/> px below its final
    /// position with a fade. Used by the inline error card on the progress
    /// page so it doesn't jarringly snap into existence.
    /// </summary>
    public static BorderElement WithSlideInFromBelow(this BorderElement element, double distance = 16, double durationMs = 320, double delayMs = 0)
    {
        return element.Set(b => HookSlideInFromBelow(b, distance, durationMs, delayMs));
    }

    /// <summary>
    /// Apply a slow continuous "breathing" scale loop (e.g. 1.0 → 1.03 → 1.0)
    /// to the element. Used by the Welcome lobster.
    /// </summary>
    public static ImageElement WithBreathe(this ImageElement element, double maxScale = 1.03, double durationMs = 3600)
    {
        return element.Set(i => HookBreathe(i, maxScale, durationMs));
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private static void HookFadeIn(FrameworkElement fe, double durationMs, double delayMs)
    {
        if (DisableForCapture) return;
        fe.Opacity = 0;
        fe.Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            fe.Opacity = 1;
            visual.StartAnimation("Opacity", fade);
        };
    }

    private static void HookPopIn(FrameworkElement fe, double durationMs, double delayMs)
    {
        if (DisableForCapture) return;
        fe.Opacity = 0;
        fe.Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(fe.ActualWidth / 2),
                (float)(fe.ActualHeight / 2),
                0);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new System.Numerics.Vector3(0.6f, 0.6f, 1f));
            scale.InsertKeyFrame(0.7f, new System.Numerics.Vector3(1.05f, 1.05f, 1f));
            scale.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
            scale.Duration = TimeSpan.FromMilliseconds(durationMs);
            scale.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.4f, 1f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            fe.Opacity = 1;
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        };
    }

    private static void HookSlideInFromBelow(FrameworkElement fe, double distance, double durationMs, double delayMs)
    {
        if (DisableForCapture) return;
        fe.Opacity = 0;
        ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
        fe.Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, (float)distance, 0f));
            slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);
            slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            fe.Opacity = 1;
            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
        };
    }

    private static void HookBreathe(FrameworkElement fe, double maxScale, double durationMs)
    {
        if (DisableForCapture) return;
        fe.Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(fe.ActualWidth / 2),
                (float)(fe.ActualHeight / 2),
                0);

            var pulse = compositor.CreateVector3KeyFrameAnimation();
            pulse.InsertKeyFrame(0f, new System.Numerics.Vector3(1f, 1f, 1f));
            pulse.InsertKeyFrame(0.5f, new System.Numerics.Vector3((float)maxScale, (float)maxScale, 1f));
            pulse.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
            pulse.Duration = TimeSpan.FromMilliseconds(durationMs);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Scale", pulse);
        };
    }
}
