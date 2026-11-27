// Shared one-shot motion for native chat entrances, disclosures and hover reveals.
// Every motion is render-only, so layout, scrolling and focus never move. Windows animations
// off skips it.
//
// Usage:
//   ChatMotion.Host(bubble).EnterOnMount(rise: 8, milliseconds: 180, enabled: isNew)  // rise into place once
//   ChatMotion.Host(card).EnterOnMount(rise: 6, milliseconds: 160, fade: true)      // rise and fade in
//   card.Transition(...).RiseWithTransition(6, 200, enabled: settle)        // beside a Reactor Transition
//   ChatMotion.Host(button).Opacity(visible ? 1 : 0).DeferredRevealTransitions(180)  // reveal
//   ToolIcon(glyph).Set(text => ChatMotion.RotateDisclosure(text, glyph))
//   ChatMotion.SlideFromCurrentPosition(measured, moved, 260)              // call in Render before a reposition
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Helpers;

namespace OpenClawTray.Chat;

/// <summary>
/// Owns the chat's one-shot motion and its cleanup.
/// </summary>
/// <remarks>
/// Reactor pools layout controls (Border, Grid, StackPanel, TextBlock and similar). Its pool
/// clears transforms on reuse, which WinUI rejects while an app-set implicit transition is in
/// use, and app calls to <c>ElementCompositionPreview.GetElementVisual</c> permanently cost a
/// control those APIs without Reactor knowing. Motion therefore lives on a
/// <see cref="ChatMotionHost"/>, a type Reactor never pools, through XAML <c>Translation</c>,
/// <c>Opacity</c> and <c>Rotation</c> with implicit transitions or facade keyframe glides;
/// <see cref="Reset"/> stops it on unmount. Buttons are pooled too, so they never carry these
/// transitions themselves.
/// Only <see cref="RiseWithTransition"/>, used beside a Reactor
/// <c>Transition</c> that already owns (and keeps unpooled) the element's composition visual,
/// touches a visual.
/// </remarks>
internal static class ChatMotion
{
    /// <summary>Wraps content in an unpooled host that motion can safely animate.</summary>
    internal static ChatMotionHostElement Host(Element content) =>
        ChatMotionHostElement.ChatMotionHost(content: content);

    // The web chat's --ease-out curve, cubic-bezier(0.16, 1, 0.3, 1), for composition rises.
    private static readonly Vector2 s_easeOut1 = new(.16f, 1f);
    private static readonly Vector2 s_easeOut2 = new(.3f, 1f);
    private static global::Windows.UI.ViewManagement.UISettings? s_settings;
    private static readonly ConditionalWeakTable<UIElement, MotionState> s_states = new();
    private static readonly ConditionalWeakTable<FrameworkElement, PendingSlide> s_pendingSlides = new();
    private static readonly ConditionalWeakTable<FrameworkElement, TokenBox> s_tokens = new();

    internal const int DisclosureRotationMilliseconds = 150;
    // Opened details grow to their height over this; the rows below follow through layout.
    internal const int DisclosureRevealMilliseconds = 200;
    // Closing details shrink to nothing over this before they are removed.
    internal const int DisclosureCloseMilliseconds = 180;
    // How long a closing disclosure keeps its details mounted for that shrink.
    internal const int DisclosureCloseHoldMilliseconds = 240;
    private const int SlideLayoutPassLimit = 30;

    /// <summary>Live Windows "Animation effects" setting; read at the moment motion would start.</summary>
    internal static bool AnimationsEnabled => AnimationsOverride ?? (s_settings ??= new()).AnimationsEnabled;

    /// <summary>
    /// Test-only override of the Windows setting. UI tests that assert layout run with chat motion
    /// off, so they never sample a row mid-rise; motion proofs turn it on explicitly.
    /// </summary>
    internal static bool? AnimationsOverride { get; set; }

    /// <summary>Test-only override for components that subscribe to the platform reduced-motion setting.</summary>
    internal static bool? ReducedMotionOverride { get; set; }

    internal static bool IsReducedMotion(bool platformValue) => ReducedMotionOverride ?? platformValue;

    /// <summary>
    /// Rises the mounted control from <paramref name="rise"/> pixels below its slot, optionally
    /// fading it in, when <paramref name="enabled"/>. The unmount reset is registered on every
    /// render, because Reactor replaces unmount actions on update: a later render that no longer
    /// admits the entrance must still clean up one that may be in flight.
    /// </summary>
    internal static T EnterOnMount<T>(this T element, float rise, int milliseconds, bool fade = false,
        bool enabled = true) where T : Element =>
        enabled
            ? (T)element.OnMountAdd(control => Enter(control, rise, milliseconds, fade)).OnUnmountAdd(Reset)
            : (T)element.OnUnmountAdd(Reset);

    /// <summary>
    /// Adds a composition rise to an element that also declares a Reactor enter
    /// <c>Transition</c>. Reactor's transition already owns (and keeps out of the pool) the
    /// element's composition visual, so the XAML Translation facade cannot be used there.
    /// </summary>
    internal static T RiseWithTransition<T>(this T element, float rise, int milliseconds, bool enabled)
        where T : Element =>
        enabled
            ? (T)element.OnMountAdd(control => RiseVisual(control, rise, milliseconds)).OnUnmountAdd(Reset)
            : (T)element.OnUnmountAdd(Reset);

    /// <summary>
    /// Opens freshly mounted disclosure details by growing their height from zero to their
    /// natural height while fading them in, so the rows below move down with real layout each
    /// frame instead of jumping or overlapping. The height is released when the reveal ends.
    /// </summary>
    internal static ChatMotionHostElement ExpandOnMount(this ChatMotionHostElement element, int milliseconds,
        bool enabled) =>
        enabled
            ? (ChatMotionHostElement)element.OnMountAdd(control => Expand(control, milliseconds)).OnUnmountAdd(Reset)
            : (ChatMotionHostElement)element.OnUnmountAdd(Reset);

    private static void Expand(FrameworkElement control, int milliseconds)
    {
        if (!AnimationsEnabled)
            return;
        var state = StateFor(control);
        // Reopening mid-close takes over from the shrink.
        state.Storyboard?.Stop();
        state.Storyboard = null;
        if (!TrySet(() =>
            {
                control.Height = 0;
                control.OpacityTransition = null;
                control.Opacity = 0;
            }))
            return;
        state.Facade = true;
        state.Faded = true;
        state.Sized = true;
        AfterFirstRender(control, state, () =>
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            var target = control is ContentControl { Content: UIElement content } && control.ActualWidth > 0
                ? Measure(content, control.ActualWidth) : 0;
            TrySet(() => control.OpacityTransition = new ScalarTransition { Duration = duration });
            control.Opacity = 1;
            if (target <= 0)
            {
                ReleaseSize(control, state);
                return;
            }
            var grow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(grow, control);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(grow, "Height");
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(grow);
            var generation = state.Generation;
            storyboard.Completed += (_, _) =>
            {
                if (state.Generation == generation && ReferenceEquals(state.Storyboard, storyboard))
                    ReleaseSize(control, state);
            };
            state.Storyboard = storyboard;
            storyboard.Begin();
        });
    }

    private static double Measure(UIElement content, double width)
    {
        content.Measure(new global::Windows.Foundation.Size(width, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    // Returns the host to its natural height; later content changes (streamed output) then size it.
    private static void ReleaseSize(FrameworkElement control, MotionState state)
    {
        TrySet(() => control.ClearValue(FrameworkElement.HeightProperty));
        state.Storyboard?.Stop();
        state.Storyboard = null;
        state.Sized = false;
    }

    /// <summary>
    /// Closes disclosure details that stay mounted while <paramref name="closing"/>, shrinking
    /// their height to zero and fading them out so the rows below rise with layout instead of
    /// snapping up when the details are removed. Reopening mid-close restores them at once.
    /// </summary>
    internal static ChatMotionHostElement ShrinkWhen(this ChatMotionHostElement element, bool closing,
        int milliseconds) =>
        (ChatMotionHostElement)element.Set(control =>
            {
                var previous = SwapToken(control, closing);
                if (closing && previous is false)
                    Shrink(control, milliseconds);
                else if (!closing && previous is true && s_states.TryGetValue(control, out var state) && state.Sized)
                {
                    ReleaseSize(control, state);
                    TrySet(() => control.Opacity = 1);
                }
            })
            .OnUnmountAdd(Reset);

    private static void Shrink(FrameworkElement control, int milliseconds)
    {
        if (!AnimationsEnabled || control.ActualHeight <= 0)
            return;
        var state = StateFor(control);
        state.Storyboard?.Stop();
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var shrink = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = control.ActualHeight,
            To = 0,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(shrink, control);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(shrink, "Height");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(shrink);
        state.Storyboard = storyboard;
        state.Sized = true;
        state.Facade = true;
        state.Faded = true;
        TrySet(() => control.OpacityTransition = new ScalarTransition { Duration = duration });
        control.Opacity = 0;
        storyboard.Begin();
    }

    /// <summary>
    /// For disclosure details that stay built while closed (the caller hides the host when they
    /// are neither <paramref name="expanded"/> nor <paramref name="closing"/>): grows them open each
    /// time they open when <paramref name="animate"/>, and shrinks them away while closing.
    /// </summary>
    internal static ChatMotionHostElement RevealWhenShown(this ChatMotionHostElement element, bool expanded,
        bool closing, bool animate, int openMilliseconds, int closeMilliseconds) =>
        (ChatMotionHostElement)element.Set(control =>
            {
                var state = expanded ? DisclosureOpen : closing ? DisclosureClosing : DisclosureClosed;
                var previous = SwapToken(control, state) as string;
                if (state == DisclosureOpen && previous != DisclosureOpen && animate)
                    Expand(control, openMilliseconds);
                else if (state == DisclosureClosing && previous == DisclosureOpen)
                    Shrink(control, closeMilliseconds);
                else if (previous == DisclosureClosing && state != DisclosureClosing &&
                    s_states.TryGetValue(control, out var motion) && motion.Sized)
                {
                    ReleaseSize(control, motion);
                    TrySet(() => control.Opacity = 1);
                }
            })
            .OnUnmountAdd(Reset);

    private const string DisclosureOpen = "open";
    private const string DisclosureClosing = "closing";
    private const string DisclosureClosed = "closed";

    /// <summary>
    /// Eases later Opacity, Scale and Translation changes of a <see cref="ChatMotionHost"/>. The
    /// transitions attach after the first render, so the initial values apply without motion.
    /// </summary>
    internal static ChatMotionHostElement DeferredRevealTransitions(this ChatMotionHostElement element, int milliseconds) =>
        (ChatMotionHostElement)element.OnMountAdd(control => DeferTransitions(control, milliseconds))
            .OnUnmountAdd(Reset);

    /// <summary>
    /// Plays an entrance each time <paramref name="shown"/> turns true after the first render,
    /// for content that stays mounted while hidden (a side panel or a tab body). With
    /// <paramref name="onFirstRender"/>, content that mounts already shown (a newly opened tab) enters too.
    /// While <paramref name="animate"/> is false the change is still tracked but appears without an
    /// entrance, for a layout where another motion already presents it.
    /// </summary>
    internal static ChatMotionHostElement EnterWhenShown(this ChatMotionHostElement element, bool shown,
        Vector3 offset, int milliseconds, bool onFirstRender = false, bool animate = true) =>
        (ChatMotionHostElement)element.Set(control =>
            {
                var previous = SwapToken(control, shown);
                if (animate && shown && (previous is false || previous is null && onFirstRender))
                    EnterFrom(control, offset, milliseconds, fade: true);
            })
            .OnUnmountAdd(Reset);

    /// <summary>
    /// Fades the content in again whenever <paramref name="label"/> changes after the first render,
    /// so a status or title swap reads as a change instead of a jump. Render-only: the new text
    /// is in place (and announced) immediately.
    /// </summary>
    internal static ChatMotionHostElement FadeOnChange(this ChatMotionHostElement element, string label,
        int milliseconds) =>
        (ChatMotionHostElement)element.Set(control =>
            {
                if (SwapToken(control, label) is string previous && previous != label)
                    EnterFrom(control, Vector3.Zero, milliseconds, fade: true);
            })
            .OnUnmountAdd(Reset);

    // Records the control's current trigger value and returns the previous one (null on first render).
    private static object? SwapToken(FrameworkElement control, object token)
    {
        var previous = s_tokens.TryGetValue(control, out var box) ? box.Value : null;
        s_tokens.AddOrUpdate(control, new TokenBox(token));
        return previous;
    }

    internal static void Enter(FrameworkElement control, float rise, int milliseconds, bool fade) =>
        EnterFrom(control, new Vector3(0, rise, 0), milliseconds, fade);

    /// <summary>Moves the control from <paramref name="offset"/> back to its slot, optionally fading it in.</summary>
    internal static void EnterFrom(FrameworkElement control, Vector3 offset, int milliseconds, bool fade)
    {
        if (!AnimationsEnabled)
            return;
        var state = StateFor(control);
        var moves = offset != Vector3.Zero;
        if (moves && !fade && offset.X == 0 && offset.Z == 0)
        {
            // A pure rise eases out from its first frame, like the web chat's sent prompt.
            state.Facade = true;
            state.Generation++;
            if (TrySet(() => PlaceAt(control, state, offset)))
                StartGlide(control, state, offset.Y, milliseconds, shift: null);
            return;
        }
        if (!TrySet(() =>
            {
                if (moves)
                    PlaceAt(control, state, offset);
                if (fade)
                {
                    control.OpacityTransition = null;
                    control.Opacity = 0;
                }
            }))
            return;
        state.Facade = true;
        state.Faded |= fade;
        AfterFirstRender(control, state, () =>
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            // Rest values apply even if a transition is rejected, so a control is never left hidden or offset.
            TrySet(() =>
            {
                if (moves)
                    control.TranslationTransition = new Vector3Transition { Duration = duration };
                if (fade)
                    control.OpacityTransition = new ScalarTransition { Duration = duration };
            });
            if (moves)
                control.Translation = Vector3.Zero;
            if (fade)
                control.Opacity = 1;
        });
    }

    /// <summary>
    /// Shows the control <paramref name="offsetY"/> pixels from its new slot, where it was before a
    /// reflow, and eases it into place from the frame that first draws the new layout. When a later
    /// layout moves the slot again, pass the offset this reflow already applied as
    /// <paramref name="appliedOffsetY"/>: a glide in flight then continues from where it is drawn
    /// instead of jumping back.
    /// </summary>
    internal static void SlideBy(FrameworkElement control, float offsetY, int milliseconds, float appliedOffsetY = 0)
    {
        if (!AnimationsEnabled)
        {
            Reset(control);
            return;
        }
        var state = StateFor(control);
        state.Facade = true;
        // Supersedes a two-step motion still waiting for its first frame.
        state.Generation++;
        if (state.Gliding)
        {
            // The slot moved by the change in offset; where the control is drawn must not.
            StartGlide(control, state, offsetY, milliseconds, shift: offsetY - appliedOffsetY);
            return;
        }
        if (TrySet(() => PlaceAt(control, state, new Vector3(0, offsetY, 0))))
            StartGlide(control, state, offsetY, milliseconds, shift: null);
    }

    /// <summary>Fades an already mounted control in from transparent.</summary>
    internal static void FadeIn(FrameworkElement control, int milliseconds) =>
        Enter(control, 0, milliseconds, fade: true);

    // Draws the control at a static Translation, taking over from any glide in flight.
    // Writing the static value a glide started from cancels the glide without redrawing the
    // control, which would keep its last glide frame, so an equal value is first nudged through.
    private static void PlaceAt(UIElement control, MotionState state, Vector3 value)
    {
        control.TranslationTransition = null;
        if (state.Gliding)
        {
            state.Gliding = false;
            if (control.Translation == value)
                control.Translation = value + Vector3.UnitY;
        }
        control.Translation = value;
    }

    // Glides Translation to rest along the web chat's ease-out curve. It starts in this tick, so
    // the frame that first draws the new layout already shows the start, rather than after a
    // dispatcher turn and the slow start of an implicit transition. The static value keeps the
    // start until the glide completes and writes back zero, so any later write takes over cleanly.
    // A shift continues the glide in flight from where it is drawn (or from rest once it ended).
    private static void StartGlide(UIElement control, MotionState state, float offsetY, int milliseconds, float? shift)
    {
        try
        {
            var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var glide = compositor.CreateVector3KeyFrameAnimation();
            glide.Target = "Translation";
            if (shift is { } delta)
            {
                glide.SetScalarParameter("Shift", delta);
                glide.InsertExpressionKeyFrame(0, "this.StartingValue + Vector3(0, Shift, 0)");
            }
            else
            {
                glide.InsertKeyFrame(0, new Vector3(0, offsetY, 0));
            }
            glide.InsertKeyFrame(1, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(s_easeOut1, s_easeOut2));
            glide.Duration = TimeSpan.FromMilliseconds(milliseconds);
            control.StartAnimation(glide);
            state.Gliding = true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Never leave the control drawn away from its slot.
            TrySet(() => PlaceAt(control, state, Vector3.Zero));
        }
    }

    private static void RiseVisual(FrameworkElement control, float rise, int milliseconds)
    {
        if (!AnimationsEnabled)
            return;
        TrySet(() =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(control);
            StateFor(control).Visual = true;
            var compositor = visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(control, true);
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector3(0, rise, 0));
            move.InsertKeyFrame(1, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(s_easeOut1, s_easeOut2));
            move.Duration = TimeSpan.FromMilliseconds(milliseconds);
            visual.StartAnimation("Translation", move);
        });
    }

    /// <summary>
    /// Records where <paramref name="measured"/> is now and, after the next layout pass, slides
    /// <paramref name="moved"/> from that old position to its new one. Call during Render, before
    /// the reconciler applies the change that repositions the control. <see cref="Reset"/> on
    /// <paramref name="moved"/> cancels a slide that has not started yet.
    /// </summary>
    internal static void SlideFromCurrentPosition(FrameworkElement measured, FrameworkElement moved, int milliseconds)
    {
        CancelPendingSlide(moved);
        if (!AnimationsEnabled || measured.XamlRoot is null || measured.ActualHeight <= 0)
            return;
        var before = measured.TransformToVisual(null).TransformPoint(default);
        var pending = new PendingSlide(measured);
        var passes = 0;
        pending.Handler = (_, _) =>
        {
            // A reset (unmount) or newer slide since this was scheduled owns the control now.
            if (!s_pendingSlides.TryGetValue(moved, out var current) || !ReferenceEquals(current, pending))
            {
                measured.LayoutUpdated -= pending.Handler;
                return;
            }
            if (measured.XamlRoot is null || moved.XamlRoot is null)
            {
                CancelPendingSlide(moved);
                return;
            }
            var after = measured.TransformToVisual(null).TransformPoint(default);
            var offset = (float)(before.Y - after.Y);
            // Reactor can commit the reposition a few layout passes after Render; keep waiting briefly.
            if (Math.Abs(offset) < 1)
            {
                if (++passes >= SlideLayoutPassLimit)
                    CancelPendingSlide(moved);
                return;
            }
            CancelPendingSlide(moved);
            // Applied during this layout pass, so the first frame at the new slot already shows the old position.
            var state = StateFor(moved);
            state.Facade = true;
            state.Generation++;
            if (TrySet(() => PlaceAt(moved, state, new Vector3(0, offset, 0))))
                StartGlide(moved, state, offset, milliseconds, shift: null);
        };
        s_pendingSlides.AddOrUpdate(moved, pending);
        measured.LayoutUpdated += pending.Handler;
    }

    /// <summary>
    /// The starting rotation that turns the previous disclosure chevron into the new one, or 0
    /// when the glyph change is not a chevron turn (for example a tool icon becoming a chevron).
    /// </summary>
    internal static float DisclosureRotationFrom(string? previous, string next) =>
        (previous, next) switch
        {
            (FluentIconCatalog.ChevronR, FluentIconCatalog.ChevronDown) => -90,
            (FluentIconCatalog.ChevronDown, FluentIconCatalog.ChevronR) => 90,
            _ => 0,
        };

    /// <summary>
    /// Turns a disclosure chevron glyph into place when it swaps between right and down. The glyph
    /// itself still states the expansion, so automation and contrast never depend on the motion.
    /// </summary>
    internal static void RotateDisclosure(FrameworkElement icon, string glyph)
    {
        var state = StateFor(icon);
        var previous = state.Glyph;
        state.Glyph = glyph;
        var from = DisclosureRotationFrom(previous, glyph);
        if (from == 0)
        {
            // The icon left the chevron (for example back to the tool glyph): end any turn in flight.
            if (previous is not null && previous != glyph && state.Facade)
                ResetFacade(icon, state);
            return;
        }
        if (!AnimationsEnabled || icon.ActualWidth <= 0 || icon.ActualHeight <= 0)
            return;
        if (!TrySet(() =>
            {
                icon.RotationTransition = null;
                icon.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0);
                icon.Rotation = from;
            }))
            return;
        state.Facade = true;
        AfterFirstRender(icon, state, () =>
        {
            TrySet(() => icon.RotationTransition = new ScalarTransition
            {
                Duration = TimeSpan.FromMilliseconds(DisclosureRotationMilliseconds),
            });
            icon.Rotation = 0;
        });
    }

    /// <summary>Stops chat motion on a control and restores the neutral state before reuse.</summary>
    internal static void Reset(FrameworkElement control)
    {
        CancelPendingSlide(control);
        s_tokens.Remove(control);
        if (!s_states.TryGetValue(control, out var state))
            return;
        s_states.Remove(control);
        if (state.Sized)
            ReleaseSize(control, state);
        if (state.Facade)
            ResetFacade(control, state);
        if (state.Visual)
        {
            // Only reached for elements whose visual Reactor's own Transition already owns.
            TrySet(() =>
            {
                var visual = ElementCompositionPreview.GetElementVisual(control);
                if (visual.Properties.TryGetVector3("Translation", out _) == CompositionGetValueStatus.Succeeded)
                {
                    visual.StopAnimation("Translation");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                }
            });
        }
    }

    private static void ResetFacade(FrameworkElement control, MotionState state)
    {
        TrySet(() =>
        {
            control.OpacityTransition = null;
            control.ScaleTransition = null;
            control.RotationTransition = null;
            // Defaults release the XAML transform facade so a later owner may use the composition visual.
            PlaceAt(control, state, Vector3.Zero);
            control.Scale = Vector3.One;
            control.Rotation = 0;
            control.CenterPoint = Vector3.Zero;
            if (state.Faded)
                control.Opacity = 1;
        });
        state.Facade = false;
        state.Faded = false;
        state.Generation++;
    }

    private static void DeferTransitions(FrameworkElement control, int milliseconds)
    {
        // Owners write facade values (such as Scale or CenterPoint) even with animations off,
        // so cleanup is registered regardless of whether the transitions are attached.
        var state = StateFor(control);
        state.Facade = true;
        if (!AnimationsEnabled)
            return;
        AfterFirstRender(control, state, () =>
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            control.OpacityTransition = new ScalarTransition { Duration = duration };
            control.ScaleTransition = new Vector3Transition { Duration = duration };
            control.TranslationTransition = new Vector3Transition { Duration = duration };
        });
    }

    // Runs the second half of a two-step facade animation once the first values have applied.
    // Reset (unmount) replaces the control's state, and a cancelled motion advances its generation,
    // so a control reset or reused in between is left alone.
    private static void AfterFirstRender(FrameworkElement control, MotionState state, Action apply)
    {
        var generation = state.Generation;
        control.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (state.Generation == generation && s_states.TryGetValue(control, out var current)
                && ReferenceEquals(current, state))
                TrySet(apply);
        });
    }

    // A control another owner already drives through its composition visual rejects the XAML
    // facade (and vice versa); motion is optional, so it is skipped rather than surfaced.
    private static bool TrySet(Action apply)
    {
        try
        {
            apply();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static MotionState StateFor(UIElement control) => s_states.GetOrCreateValue(control);

    /// <summary>Finds a realized descendant by its UI Automation ID, or null.</summary>
    internal static FrameworkElement? FindByAutomationId(DependencyObject root, string automationId)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && AutomationProperties.GetAutomationId(element) == automationId)
                return element;
            if (FindByAutomationId(child, automationId) is { } found)
                return found;
        }
        return null;
    }

    private static void CancelPendingSlide(FrameworkElement moved)
    {
        if (!s_pendingSlides.TryGetValue(moved, out var pending))
            return;
        s_pendingSlides.Remove(moved);
        if (pending.Handler is { } handler)
            pending.Measured.LayoutUpdated -= handler;
    }

    private sealed class MotionState
    {
        public bool Facade { get; set; }
        public bool Faded { get; set; }
        public bool Visual { get; set; }
        public bool Sized { get; set; }
        public Microsoft.UI.Xaml.Media.Animation.Storyboard? Storyboard { get; set; }
        // A keyframe glide may still draw the control; its static Translation is the glide's start.
        public bool Gliding { get; set; }
        public string? Glyph { get; set; }
        public int Generation { get; set; }
    }

    private sealed class PendingSlide(FrameworkElement measured)
    {
        public FrameworkElement Measured { get; } = measured;
        public EventHandler<object>? Handler { get; set; }
    }

    private sealed class TokenBox(object value)
    {
        public object Value { get; } = value;
    }
}

/// <summary>
/// Glides transcript rows from where they were to where a row change or a disclosure toggle
/// moves them (FLIP), so a run folding into its Worked group or details opening never jump.
/// New messages animate on their own row; existing rows and tail-follow scrolling stay immediate.
/// Render-only: layout, scrolling and focus are already final when the slide starts.
/// </summary>
/// <remarks>
/// The timeline owns one instance and tracks each realized row's unpooled motion host.
/// Disclosures call <see cref="CaptureAll"/> before changing their content; only rows with a
/// prior position glide, while newly realized rows appear in place.
/// <code>
///   var reflow = UseRef(new ChatTranscriptReflow()).Current;
///   ChatMotion.Host(row).Set(host => reflow.Track(host, key)).OnUnmountAdd(reflow.Untrack);
///   ChatTranscriptReflow.CaptureAll();
/// </code>
/// </remarks>
internal sealed class ChatTranscriptReflow
{
    internal const int SlideMilliseconds = 220;
    // Drop captures that never produce a layout change.
    private const long CaptureLifetimeMilliseconds = 600;
    private const long SettleMilliseconds = SlideMilliseconds;
    // Sub-pixel and rounding drift between frames is not a move worth restarting a slide for.
    private const float ReadjustThreshold = 1.5f;

    private static readonly List<WeakReference<ChatTranscriptReflow>> s_active = [];
    private readonly Dictionary<FrameworkElement, string> _rows = [];
    private Dictionary<string, double>? _before;
    private readonly Dictionary<FrameworkElement, float> _applied = [];
    private readonly Func<long> _getTimestamp;
    private FrameworkElement? _root;
    private long _deadline;
    private long _expiresAt;

    internal ChatTranscriptReflow(Func<long>? getTimestamp = null) =>
        _getTimestamp = getTimestamp ?? ReadTimestamp;

    private static long ReadTimestamp() => Environment.TickCount64;

    internal void Attach(FrameworkElement root)
    {
        _root = root;
        s_active.RemoveAll(reference => !reference.TryGetTarget(out var active) || ReferenceEquals(active, this));
        s_active.Add(new(this));
    }

    internal void Detach()
    {
        Cancel(stopMotion: true);
        _root = null;
        _rows.Clear();
        s_active.RemoveAll(reference => !reference.TryGetTarget(out var active) || ReferenceEquals(active, this));
    }

    internal void Track(FrameworkElement row, string key) => _rows[row] = key;

    internal void Untrack(FrameworkElement row)
    {
        _rows.Remove(row);
        _applied.Remove(row);
        ChatMotion.Reset(row);
    }

    /// <summary>Captures every live transcript, for a disclosure that does not know its timeline.</summary>
    internal static void CaptureAll()
    {
        foreach (var reference in s_active.ToArray())
        {
            if (reference.TryGetTarget(out var reflow))
                reflow.Capture();
        }
    }

    /// <summary>Records current row positions before the next reader-initiated layout change.</summary>
    private void Capture()
    {
        if (!ChatMotion.AnimationsEnabled)
        {
            Cancel(stopMotion: true);
            return;
        }
        if (_root is not { XamlRoot: not null } root || _rows.Count == 0 || !root.DispatcherQueue.HasThreadAccess)
            return;
        // One change can report several times (a disclosure's events, then a render). The first
        // capture holds the last drawn positions; a later one could sample a half-applied layout.
        if (_before is not null && _getTimestamp() <= _deadline)
            return;
        // A previous glide owns its own completion; only stop observing its layout.
        Cancel();
        var before = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (row, key) in _rows)
        {
            if (Position(row, root) is { } y)
                before[key] = y;
        }
        if (before.Count == 0)
            return;
        _before = before;
        _expiresAt = _deadline = _getTimestamp() + CaptureLifetimeMilliseconds;
        // Layout can pass through transient arrangements (a list re-measuring around its scroll
        // anchor) within one frame, so rows are compared once per drawn frame, never per pass.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        // Reader input takes precedence over layout motion and stops rows at their new slots.
        root.AddHandler(UIElement.PointerWheelChangedEvent, s_cancelOnInput ??= new PointerEventHandler(CancelOnInput), true);
        root.AddHandler(UIElement.PointerPressedEvent, s_cancelOnInput, true);
    }

    private static PointerEventHandler? s_cancelOnInput;

    private static void CancelOnInput(object sender, PointerRoutedEventArgs args)
    {
        foreach (var reference in s_active.ToArray())
        {
            if (reference.TryGetTarget(out var reflow) && ReferenceEquals(reflow._root, sender))
                reflow.CancelForReaderInput();
        }
    }

    private void OnRendering(object? sender, object args)
    {
        if (!ChatMotion.AnimationsEnabled)
        {
            Cancel(stopMotion: true);
            return;
        }
        if (_root is not { } root || _before is not { } before)
        {
            Cancel(stopMotion: true);
            return;
        }
        var now = _getTimestamp();
        if (now > _deadline)
        {
            Cancel();
            return;
        }
        var (measured, offsets) = Measure(root, before);
        var moved = false;
        for (var index = 0; index < measured.Count; index++)
        {
            if (offsets[index] is not { } offset)
                continue;
            var row = measured[index].Row;
            var applied = _applied.TryGetValue(row, out var value) ? value : 0;
            if (Math.Abs(offset - applied) < ReadjustThreshold)
                continue;
            _applied[row] = offset;
            ChatMotion.SlideBy(row, offset, SlideMilliseconds, applied);
            moved = true;
        }
        // Retain pointer cancellation through the full glide; later layout readjustments extend
        // this only to the original capture lifetime.
        if (moved)
            _deadline = Math.Min(_expiresAt, now + SettleMilliseconds);
    }

    private (List<(FrameworkElement Row, double Now, double? Was)> Rows, float?[] Offsets) Measure(
        FrameworkElement root, Dictionary<string, double> before)
    {
        var measured = new List<(FrameworkElement Row, double Now, double? Was)>();
        foreach (var (row, key) in _rows.ToArray())
        {
            if (Position(row, root) is not { } now)
                continue;
            measured.Add((row, now, before.TryGetValue(key, out var was) ? was : null));
        }
        var offsets = PlanOffsets(measured.Select(item => (item.Now, item.Was)).ToList());
        return (measured, offsets);
    }

    /// <summary>
    /// Plans each existing row's drawing offset (where it was minus where it is). Rows without
    /// an earlier position stay in place.
    /// </summary>
    internal static float?[] PlanOffsets(IReadOnlyList<(double Now, double? Was)> rows)
    {
        var offsets = new float?[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Was is { } was)
            {
                offsets[index] = (float)(was - rows[index].Now);
            }
        }
        return offsets;
    }

    // Measures the row's container rather than the host, so the host's own slide offset never
    // feeds back into the next measurement. A row outside the viewport's neighborhood (such as a
    // container the list parked for reuse) has no meaningful position.
    private static double? Position(FrameworkElement row, FrameworkElement root)
    {
        var slot = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(row) as FrameworkElement ?? row;
        if (slot.XamlRoot is null || !row.IsLoaded || row.Visibility != Visibility.Visible)
            return null;
        try
        {
            var y = slot.TransformToVisual(root).TransformPoint(default).Y;
            var height = root.ActualHeight;
            return y + slot.ActualHeight < -height || y > 2 * height ? null : y;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal void CancelForReaderInput() => Cancel(stopMotion: true);

    private void Cancel(bool stopMotion = false)
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        if (_root is { } root)
        {
            if (s_cancelOnInput is { } handler)
            {
                root.RemoveHandler(UIElement.PointerWheelChangedEvent, handler);
                root.RemoveHandler(UIElement.PointerPressedEvent, handler);
            }
        }
        if (stopMotion)
            foreach (var row in _applied.Keys)
                ChatMotion.Reset(row);
        _before = null;
        _deadline = 0;
        _expiresAt = 0;
        _applied.Clear();
    }
}

/// <summary>
/// Content host for chat motion. As a distinct type it stays outside Reactor's exact-type
/// layout-control pool, so implicit transitions and transforms never reach a recycled control.
/// It is transparent to layout, keyboard focus and UI Automation.
/// </summary>
internal sealed class ChatMotionHost : ContentControl
{
    public ChatMotionHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }
}

[GenerateReactorWrapper(typeof(ChatMotionHost), AutoDiscover = false, RegisterAssembly = false)]
[WrapContent(nameof(ContentControl.Content))]
internal sealed partial record ChatMotionHostElement;
