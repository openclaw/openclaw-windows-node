using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// One node of <see cref="SetupStepTimeline"/>: a state glyph, a label, and a detail line that
/// expands for the current step or while hovered. The timeline owns ordering and connectors.
/// </summary>
public sealed partial class SetupStepRow : UserControl
{
    internal static readonly TimeSpan DetailDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PopDuration = TimeSpan.FromMilliseconds(320);
    private const double UpcomingLabelOpacity = 0.68;
    private const double DoneLabelOpacity = 0.8;

    private OnboardingDesignStepState _state = OnboardingDesignStepState.Upcoming;
    private bool _hovered;
    private bool _detailExpanded;
    private Storyboard? _detailAnimation;

    public SetupStepRow() => InitializeComponent();

    public OnboardingDesignStepState State => _state;

    public string LabelText
    {
        get => Label.Text;
        set => Label.Text = value;
    }

    public string DetailText
    {
        get => Detail.Text;
        set => Detail.Text = value;
    }

    /// <summary>Center of the node relative to this row's top, and its rendered radius.</summary>
    internal double NodeCenterY => 9;
    internal double NodeRadius => _state == OnboardingDesignStepState.Current ? 6 : 5;

    internal void Apply(OnboardingDesignStepState state, bool animate, bool highContrast)
    {
        var becameCurrent = state == OnboardingDesignStepState.Current && _state != state;
        _state = state;
        VisualStateManager.GoToState(this, state switch
        {
            OnboardingDesignStepState.Current => "Current",
            OnboardingDesignStepState.Done or OnboardingDesignStepState.Skipped => "Done",
            _ => "Upcoming",
        }, false);
        ApplyVisuals(animate, highContrast);
        if (becameCurrent && animate)
            Pop();
    }

    internal void SetHovered(bool hovered, bool animate, bool highContrast)
    {
        _hovered = hovered;
        ApplyVisuals(animate, highContrast);
    }

    private void ApplyVisuals(bool animate, bool highContrast)
    {
        Label.Opacity = highContrast || _hovered || _state == OnboardingDesignStepState.Current
            ? 1
            : _state == OnboardingDesignStepState.Upcoming ? UpcomingLabelOpacity : DoneLabelOpacity;
        SetDetailExpanded(_hovered || _state == OnboardingDesignStepState.Current, animate);
    }

    private void SetDetailExpanded(bool expanded, bool animate)
    {
        if (expanded == _detailExpanded && _detailAnimation is null)
            return;
        _detailExpanded = expanded;
        _detailAnimation?.Stop();
        _detailAnimation = null;
        var width = Math.Max(0, Detail.ActualWidth > 0 ? Detail.ActualWidth : ActualWidth - 36);
        Detail.Measure(new Windows.Foundation.Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        var target = expanded ? Detail.DesiredSize.Height : 0;
        if (!animate)
        {
            DetailHost.MaxHeight = expanded ? double.PositiveInfinity : 0;
            DetailHost.Opacity = expanded ? 1 : 0;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation
        {
            From = Math.Min(DetailHost.ActualHeight, Detail.DesiredSize.Height),
            To = target,
            Duration = DetailDuration,
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(height, DetailHost);
        Storyboard.SetTargetProperty(height, nameof(FrameworkElement.MaxHeight));
        var fade = new DoubleAnimation { To = expanded ? 1 : 0, Duration = DetailDuration, EasingFunction = ease };
        Storyboard.SetTarget(fade, DetailHost);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var storyboard = new Storyboard { Children = { height, fade } };
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_detailAnimation, storyboard))
                return;
            _detailAnimation = null;
            // Release the held end values so later instant changes and text reflow take effect.
            storyboard.Stop();
            DetailHost.MaxHeight = _detailExpanded ? double.PositiveInfinity : 0;
            DetailHost.Opacity = _detailExpanded ? 1 : 0;
        };
        _detailAnimation = storyboard;
        storyboard.Begin();
    }

    // The new current node pops toward 16 DIPs with a small overshoot, then settles at 12.
    private void Pop()
    {
        var x = PopFrames();
        Storyboard.SetTarget(x, NodeScale);
        Storyboard.SetTargetProperty(x, nameof(Microsoft.UI.Xaml.Media.ScaleTransform.ScaleX));
        var y = PopFrames();
        Storyboard.SetTarget(y, NodeScale);
        Storyboard.SetTargetProperty(y, nameof(Microsoft.UI.Xaml.Media.ScaleTransform.ScaleY));
        new Storyboard { Children = { x, y } }.Begin();
    }

    private static DoubleAnimationUsingKeyFrames PopFrames() => new()
    {
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.8 },
            new EasingDoubleKeyFrame
            {
                KeyTime = PopDuration / 2, Value = 16d / 12 * 1.08,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            },
            new EasingDoubleKeyFrame
            {
                KeyTime = PopDuration, Value = 1,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            },
        },
    };

    internal void SetAutomationName(string name) => AutomationProperties.SetName(this, name);
}
