using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClaw.SetupEngine;
using Windows.UI.ViewManagement;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Decoration drawn on the companion mascot for milestone states.</summary>
public enum SetupCompanionBadge
{
    None,
    Success,
    Restart,
}

/// <summary>
/// Left pane of the setup window: the speech bubble, the mascot with its glow and shadow, and
/// the four-step <see cref="SetupStepTimeline"/>. <see cref="SetupWindow"/> owns one instance and
/// presents each page's line and mood; pages adjust them through the window, never directly.
/// </summary>
/// <example>
/// <code>
/// companion.Present("Pick a home for me.", OnboardingMascotMood.Curious);
/// companion.UpdateSteps(stages, OnboardingStage.Gateway);
/// </code>
/// </example>
public sealed partial class SetupCompanion : UserControl
{
    private static readonly TimeSpan BubbleFadeHalf = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan BounceDuration = TimeSpan.FromMilliseconds(360);
    private const double BounceHeight = 6;
    // Design proportions within the 472-DIP pane at the 1120-DIP reference width.
    private const double ReferencePaneWidth = 472;
    private const double ReferenceTimelineInset = 134;
    private const double TimelineEndMargin = 16;

    private string _pageLine = string.Empty;
    private string? _hoverLine;
    private Storyboard? _bubbleFade;
    private UISettings? _uiSettings;

    public SetupCompanion()
    {
        InitializeComponent();
        Timeline.HoveredDetailChanged += (_, detail) =>
        {
            _hoverLine = detail;
            ShowLine(detail ?? _pageLine);
        };
        Timeline.Advanced += (_, _) => Bounce();
        SizeChanged += (_, args) => Timeline.Margin = new Thickness(
            Math.Round(args.NewSize.Width * ReferenceTimelineInset / ReferencePaneWidth), 0, TimelineEndMargin, 0);
        Loaded += (_, _) => _uiSettings ??= new UISettings();
        Unloaded += (_, _) =>
        {
            _bubbleFade?.Stop();
            _bubbleFade = null;
            _uiSettings = null;
        };
    }

    public OnboardingMascot Mascot => MascotControl;

    internal SetupStepTimeline Steps => Timeline;

    public string Line => _pageLine;

    private bool CanAnimate => IsLoaded && _uiSettings?.AnimationsEnabled == true;

    public void Present(string line, OnboardingMascotMood mood, SetupCompanionBadge badge = SetupCompanionBadge.None)
    {
        SetLine(line);
        Mascot.Mood = mood;
        Mascot.Accessory = OnboardingMascotAccessory.None;
        SetBadge(badge);
    }

    public void SetLine(string line)
    {
        _pageLine = line;
        if (_hoverLine is null)
            ShowLine(line);
    }

    public void SetBadge(SetupCompanionBadge badge)
    {
        RestartBadge.Visibility = badge == SetupCompanionBadge.Restart ? Visibility.Visible : Visibility.Collapsed;
        SuccessBadge.Visibility = badge == SetupCompanionBadge.Success ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateSteps(IReadOnlyList<OnboardingStage> routeStages, OnboardingStage current) =>
        Timeline.Update(routeStages, current);

    /// <summary>
    /// The companion of the setup composition hosting <paramref name="element"/>, found among the
    /// siblings of its ancestors. Null until the element is in the visual tree.
    /// </summary>
    public static SetupCompanion? Find(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Panel panel && panel.Children.OfType<SetupCompanion>().FirstOrDefault() is { } companion)
                return companion;
        }
        return null;
    }

    private void ShowLine(string text)
    {
        if (BubbleText.Text == text && _bubbleFade is null)
            return;
        _bubbleFade?.Stop();
        _bubbleFade = null;
        Bubble.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!CanAnimate || string.IsNullOrEmpty(BubbleText.Text))
        {
            BubbleText.Text = text;
            BubbleText.Opacity = 1;
            return;
        }
        var fadeOut = Fade(0);
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_bubbleFade, fadeOut))
                return;
            BubbleText.Text = text;
            var fadeIn = Fade(1);
            fadeIn.Completed += (_, _) =>
            {
                if (ReferenceEquals(_bubbleFade, fadeIn))
                    _bubbleFade = null;
            };
            _bubbleFade = fadeIn;
            fadeIn.Begin();
        };
        _bubbleFade = fadeOut;
        fadeOut.Begin();
    }

    private Storyboard Fade(double to)
    {
        var animation = new DoubleAnimation { To = to, Duration = BubbleFadeHalf };
        Storyboard.SetTarget(animation, BubbleText);
        Storyboard.SetTargetProperty(animation, nameof(Opacity));
        return new Storyboard { Children = { animation } };
    }

    private void Bounce()
    {
        if (!CanAnimate)
            return;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 },
                new EasingDoubleKeyFrame
                {
                    KeyTime = BounceDuration / 3, Value = -BounceHeight,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                },
                new EasingDoubleKeyFrame
                {
                    KeyTime = BounceDuration, Value = 0,
                    EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 1, Bounciness = 3 },
                },
            },
        };
        Storyboard.SetTarget(animation, MascotBounce);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.Y));
        new Storyboard { Children = { animation } }.Begin();
    }
}
