using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
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
    private const float BounceHeight = 6;
    // Design proportions within the 472-DIP pane at the 1120-DIP reference width.
    private const double ReferencePaneWidth = 472;
    private const double ReferenceTimelineInset = 134;
    private const double TimelineEndMargin = 16;

    private string _pageLine = string.Empty;
    private string? _hoverLine;
    private string _shownLine = string.Empty;
    private UISettings? _uiSettings;

    public SetupCompanion()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(MascotHost, true);
        Timeline.HoveredDetailChanged += (_, detail) =>
        {
            _hoverLine = detail;
            ShowLine(detail ?? _pageLine);
        };
        Timeline.Advanced += (_, _) => Bounce();
        SizeChanged += (_, args) => Timeline.Margin = new Thickness(
            Math.Round(args.NewSize.Width * ReferenceTimelineInset / ReferencePaneWidth), 0, TimelineEndMargin, 0);
        Loaded += (_, _) =>
        {
            _uiSettings = new UISettings();
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            SettleMotion();
        };
        Unloaded += (_, _) =>
        {
            if (_uiSettings is not null)
                _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            _uiSettings = null;
            SettleMotion();
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
        if (_shownLine == text)
            return;
        _shownLine = text;
        SetupVisualTransition.Stop(BubblePlayer);
        Bubble.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!CanAnimate || string.IsNullOrEmpty(BubbleText.Text) || string.IsNullOrEmpty(text))
        {
            SettleBubble();
            return;
        }
        var visual = SetupVisualTransition.GetVisual(BubbleText);
        SetupVisualTransition.Play(BubblePlayer, BubbleFadeHalf,
            motion => motion.Animate(visual, "Opacity", (0, 1, null), (1, 0, null)), () =>
        {
            BubbleText.Text = text;
            SetupVisualTransition.Play(BubblePlayer, BubbleFadeHalf,
                motion => motion.Animate(visual, "Opacity", (0, 0, null), (1, 1, null)));
        });
    }

    private void SettleBubble()
    {
        SetupVisualTransition.Stop(BubblePlayer);
        BubbleText.Text = _shownLine;
        SetupVisualTransition.GetVisual(BubbleText).Opacity = 1;
    }

    private void SettleMotion()
    {
        SettleBubble();
        SetupVisualTransition.Stop(HopPlayer);
        SetupVisualTransition.GetVisual(MascotHost).Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() => { if (!CanAnimate) SettleMotion(); }))
            Trace.TraceWarning("Setup companion could not dispatch a Windows animation preference change.");
    }

    private void Bounce()
    {
        if (!CanAnimate)
            return;
        var visual = SetupVisualTransition.GetVisual(MascotHost);
        SetupVisualTransition.Play(HopPlayer, BounceDuration, motion =>
        {
            using var rise = CompositionEasingFunction.CreatePowerEasingFunction(
                visual.Compositor, CompositionEasingFunctionMode.Out, 2);
            using var land = CompositionEasingFunction.CreateBounceEasingFunction(
                visual.Compositor, CompositionEasingFunctionMode.Out, 1, 3);
            motion.Animate(visual.Properties, "Translation.Y",
                (0, 0, null), (1f / 3, -BounceHeight, rise), (1, 0, land));
        });
    }
}
