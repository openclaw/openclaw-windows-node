// WinUI motion player: turns LobsterAnimation tables into Storyboards of DoubleAnimationUsingKeyFrames on an
// element's CompositeTransform and Opacity only, so every animation is independent (composition thread). Each CSS
// segment becomes a SplineDoubleKeyFrame with the animation's cubic-bezier (Linear for linear, Discrete for
// duplicate-offset holds). rotateY has no 2D equivalent; it is sampled into ScaleX = cos(angle).
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/styles/lobster-pet.css (MIT; see THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var player = new LobsterMotionPlayer();
//   foreach (var a in LobsterMotion.ForAct(LobsterAct.Wave))
//       player.Add(a, canvas.Element(LobsterArtRole.ClawRight)!, width: 120, height: 105);
//   player.Start();   // later: player.Stop();
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>A set of storyboards started and stopped together.</summary>
internal sealed class LobsterMotionPlayer
{
    private const int RotateYSamples = 24;
    private readonly List<(Storyboard Board, TimeSpan Seek)> _boards = [];

    /// <summary>
    /// Adds <paramref name="animation"/> on <paramref name="element"/>. <paramref name="width"/>/<paramref name="height"/>
    /// are the element's box in the units its translations use (px, or viewBox units inside the artwork); they
    /// resolve percent translations and percent transform-origins. <paramref name="index"/> applies the stagger.
    /// Replaces a non-composite RenderTransform with a CompositeTransform.
    /// </summary>
    internal void Add(LobsterAnimation animation, UIElement element, double width, double height, int index = 0)
    {
        var transform = element.RenderTransform as CompositeTransform;
        if (transform is null)
        {
            transform = new CompositeTransform();
            element.RenderTransform = transform;
        }
        var origin = animation.TransformOrigin;
        if (animation.Origin is not null)
        {
            transform.CenterX = origin.Unit == LobsterOriginUnit.Percent ? width * origin.X / 100 : origin.X;
            transform.CenterY = origin.Unit == LobsterOriginUnit.Percent ? height * origin.Y / 100 : origin.Y;
        }
        else if (animation.Target is LobsterMotionTarget.Motion or LobsterMotionTarget.Sprite or LobsterMotionTarget.Body
                 or LobsterMotionTarget.Balloon or LobsterMotionTarget.EntryBubble or LobsterMotionTarget.Heart
                 or LobsterMotionTarget.Broom or LobsterMotionTarget.Z or LobsterMotionTarget.Bubble
                 or LobsterMotionTarget.Sparkle)
        {
            transform.CenterX = width / 2;
            transform.CenterY = height / 2;
        }

        var board = new Storyboard();
        var delay = animation.DelayFor(index);
        foreach (var track in animation.Tracks)
        {
            var keys = track.Property == LobsterMotionProperty.RotateY
                ? RotateYFrames(animation, track)
                : Frames(animation, track, track.Property switch
                {
                    LobsterMotionProperty.TranslateX when track.Percent => width / 100,
                    LobsterMotionProperty.TranslateY when track.Percent => height / 100,
                    _ => 1,
                });
            var target = track.Property == LobsterMotionProperty.Opacity ? (DependencyObject)element : transform;
            Storyboard.SetTarget(keys, target);
            Storyboard.SetTargetProperty(keys, track.Property switch
            {
                LobsterMotionProperty.TranslateX => nameof(CompositeTransform.TranslateX),
                LobsterMotionProperty.TranslateY => nameof(CompositeTransform.TranslateY),
                LobsterMotionProperty.Rotate => nameof(CompositeTransform.Rotation),
                LobsterMotionProperty.RotateY or LobsterMotionProperty.ScaleX => nameof(CompositeTransform.ScaleX),
                LobsterMotionProperty.ScaleY => nameof(CompositeTransform.ScaleY),
                _ => nameof(UIElement.Opacity),
            });
            keys.Duration = TimeSpan.FromMilliseconds(animation.DurationMs);
            keys.RepeatBehavior = animation.IsInfinite ? RepeatBehavior.Forever : new RepeatBehavior(animation.Iterations);
            keys.FillBehavior = animation.Fill == LobsterFill.None ? FillBehavior.Stop : FillBehavior.HoldEnd;
            if (delay > 0) keys.BeginTime = TimeSpan.FromMilliseconds(delay);
            board.Children.Add(keys);
        }
        // Negative CSS delays start mid-cycle: begin, then seek forward.
        _boards.Add((board, delay < 0 ? TimeSpan.FromMilliseconds(-delay) : TimeSpan.Zero));
    }

    /// <summary>Starts every added storyboard.</summary>
    internal void Start()
    {
        foreach (var (board, seek) in _boards)
        {
            board.Begin();
            if (seek > TimeSpan.Zero) board.Seek(seek);
        }
    }

    /// <summary>Stops every storyboard, restoring base values, and forgets them.</summary>
    internal void Stop()
    {
        foreach (var (board, _) in _boards) board.Stop();
        _boards.Clear();
    }

    private static DoubleAnimationUsingKeyFrames Frames(LobsterAnimation animation, LobsterMotionTrack track, double unit)
    {
        var keys = new DoubleAnimationUsingKeyFrames();
        var identity = LobsterMotionTrack.Identity(track.Property);
        var frames = new List<LobsterKeyframe>(track.Keyframes.Count + 2);
        // Missing 0%/100% keyframes interpolate from the underlying (identity) value, as CSS does.
        if (track.Keyframes[0].Offset > 0) frames.Add(new(0, identity));
        frames.AddRange(track.Keyframes);
        if (track.Keyframes[^1].Offset < 1) frames.Add(new(1, identity));

        var e = animation.Easing;
        for (var i = 0; i < frames.Count; i++)
        {
            var time = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frames[i].Offset * animation.DurationMs));
            var value = frames[i].Value * unit;
            DoubleKeyFrame frame = i == 0 || frames[i].Offset <= frames[i - 1].Offset
                ? new DiscreteDoubleKeyFrame()
                : e.IsLinear
                    ? new LinearDoubleKeyFrame()
                    : new SplineDoubleKeyFrame { KeySpline = new KeySpline { ControlPoint1 = new(e.X1, e.Y1), ControlPoint2 = new(e.X2, e.Y2) } };
            frame.KeyTime = time;
            frame.Value = value;
            keys.KeyFrames.Add(frame);
        }
        return keys;
    }

    private static DoubleAnimationUsingKeyFrames RotateYFrames(LobsterAnimation animation, LobsterMotionTrack track)
    {
        var keys = new DoubleAnimationUsingKeyFrames();
        for (var i = 0; i <= RotateYSamples; i++)
        {
            var p = (double)i / RotateYSamples;
            var degrees = track.ValueAt(p, animation.Easing);
            keys.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(p * animation.DurationMs)),
                Value = Math.Cos(degrees * Math.PI / 180),
            });
        }
        return keys;
    }
}
