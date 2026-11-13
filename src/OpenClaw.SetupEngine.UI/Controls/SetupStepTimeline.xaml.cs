using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.SetupEngine;
using Windows.UI.ViewManagement;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// Four-step vertical setup progress in the setup window's left pane. The window calls
/// <see cref="Update"/> with the route's stages and the current page's stage; moving forward
/// pours accent down the connector, marks the finished step done and pops the new current node.
/// Hovering a step expands its detail and raises <see cref="HoveredDetailChanged"/> so the
/// companion bubble can echo it. Windows' animation preference makes every change instant.
/// </summary>
/// <example>
/// <code>
/// timeline.Update(OnboardingFlowPolicy.GetStages(route, config), OnboardingStage.Install);
/// timeline.Advanced += (_, _) => companion.Bounce();
/// </code>
/// </example>
public sealed partial class SetupStepTimeline : UserControl
{
    internal static readonly TimeSpan PourDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PourFadeDuration = TimeSpan.FromMilliseconds(150);
    private const double ConnectorGap = 3;

    private static readonly string[] StepKeys = ["ChooseTools", "SetUp", "ConnectAi", "StartChatting"];

    private readonly SetupStepRow[] _rows;
    private readonly Rectangle[] _tracks;
    private readonly Rectangle[] _pours;
    private readonly ScaleTransform[] _pourScales;
    private IReadOnlyList<OnboardingDesignStepState>? _states;
    private Storyboard? _advance;
    private Storyboard? _fade;
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibility;
    private SetupStepRow? _hovered;

    public SetupStepTimeline()
    {
        InitializeComponent();
        _rows = [Step0, Step1, Step2, Step3];
        _tracks = [Track0, Track1, Track2];
        _pours = [Pour0, Pour1, Pour2];
        _pourScales = [Pour0Scale, Pour1Scale, Pour2Scale];
        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            row.LabelText = SetupLocalization.GetString($"Onboarding_Steps_{StepKeys[index]}");
            row.DetailText = SetupLocalization.GetString($"Onboarding_Steps_{StepKeys[index]}Detail");
            row.PointerEntered += Row_PointerEntered;
            row.PointerExited += Row_PointerExited;
            row.PointerCanceled += Row_PointerExited;
            row.PointerCaptureLost += Row_PointerExited;
        }
        Rows.LayoutUpdated += (_, _) => PositionConnectors();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The bubble should echo this detail while a step is hovered; null restores the page line.</summary>
    public event EventHandler<string?>? HoveredDetailChanged;

    /// <summary>Raised when an animated forward move lands, so the mascot can bounce.</summary>
    public event EventHandler? Advanced;

    public IReadOnlyList<OnboardingDesignStepState>? States => _states;

    private bool CanAnimate => IsLoaded && _uiSettings?.AnimationsEnabled == true;
    private bool HighContrast => _accessibility?.HighContrast == true;

    public void Update(IReadOnlyList<OnboardingStage> routeStages, OnboardingStage current)
    {
        var next = OnboardingStepPlan.Build(routeStages, current);
        var previous = _states;
        if (previous is not null && previous.SequenceEqual(next))
            return;
        _states = next;
        var nextIndex = IndexOfCurrent(next);
        AutomationProperties.SetName(this, SetupLocalization.Format(
            "Onboarding_Flow_Position", nextIndex + 1, OnboardingStepPlan.StepCount));
        _advance?.Stop();
        _advance = null;
        var previousIndex = previous is null ? -1 : IndexOfCurrent(previous);
        if (previous is null || nextIndex <= previousIndex || !CanAnimate)
        {
            ApplyStates(next, animate: false);
            HidePours();
            return;
        }
        PourThenApply(previousIndex, nextIndex, next);
    }

    private void PourThenApply(int from, int to, IReadOnlyList<OnboardingDesignStepState> target)
    {
        HidePours();
        var storyboard = new Storyboard();
        for (var segment = Math.Max(0, from); segment < to && segment < _pours.Length; segment++)
        {
            _pours[segment].Opacity = 1;
            _pourScales[segment].ScaleY = 0;
            var pour = new DoubleAnimation
            {
                From = 0, To = 1, Duration = PourDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(pour, _pourScales[segment]);
            Storyboard.SetTargetProperty(pour, nameof(ScaleTransform.ScaleY));
            storyboard.Children.Add(pour);
        }
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_advance, storyboard))
                return;
            _advance = null;
            // Keep the filled connectors while releasing the held animation values.
            foreach (var scale in _pourScales)
                scale.ScaleY = 1;
            storyboard.Stop();
            ApplyStates(target, animate: true);
            FadePours();
            Advanced?.Invoke(this, EventArgs.Empty);
        };
        _advance = storyboard;
        storyboard.Begin();
    }

    private void ApplyStates(IReadOnlyList<OnboardingDesignStepState> states, bool animate)
    {
        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            row.Apply(states[index], animate, HighContrast);
            row.SetAutomationName(SetupLocalization.Format("Onboarding_Steps_ItemName",
                row.LabelText, SetupLocalization.GetString($"Onboarding_Steps_State{states[index]}")));
        }
        PositionConnectors();
    }

    private void FadePours()
    {
        var storyboard = new Storyboard();
        foreach (var pour in _pours.Where(pour => pour.Opacity > 0))
        {
            var fade = new DoubleAnimation { To = 0, Duration = PourFadeDuration };
            Storyboard.SetTarget(fade, pour);
            Storyboard.SetTargetProperty(fade, nameof(Opacity));
            storyboard.Children.Add(fade);
        }
        storyboard.Completed += (_, _) =>
        {
            // HidePours stops this storyboard so its held opacity cannot hide a later pour.
            if (ReferenceEquals(_fade, storyboard))
                HidePours();
        };
        _fade = storyboard;
        storyboard.Begin();
    }

    // Stops any fade first: a held fade value would otherwise hide later local opacity changes.
    private void HidePours()
    {
        _fade?.Stop();
        _fade = null;
        foreach (var pour in _pours)
            pour.Opacity = 0;
    }

    private void PositionConnectors()
    {
        for (var index = 0; index < _tracks.Length; index++)
        {
            var upper = _rows[index];
            var lower = _rows[index + 1];
            var top = upper.ActualOffset.Y + upper.NodeCenterY + upper.NodeRadius + ConnectorGap;
            var bottom = lower.ActualOffset.Y + lower.NodeCenterY - lower.NodeRadius - ConnectorGap;
            var height = Math.Max(0, bottom - top);
            foreach (var line in new[] { _tracks[index], _pours[index] })
            {
                if (Canvas.GetTop(line) != top)
                    Canvas.SetTop(line, top);
                if (line.Height != height)
                    line.Height = height;
            }
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not SetupStepRow row || _states is null)
            return;
        if (_hovered is { } previous && !ReferenceEquals(previous, row))
            previous.SetHovered(false, CanAnimate, HighContrast);
        _hovered = row;
        row.SetHovered(true, CanAnimate, HighContrast);
        HoveredDetailChanged?.Invoke(this, row.DetailText);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not SetupStepRow row || !ReferenceEquals(_hovered, row))
            return;
        _hovered = null;
        row.SetHovered(false, CanAnimate, HighContrast);
        HoveredDetailChanged?.Invoke(this, null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _uiSettings ??= new UISettings();
        _accessibility ??= new AccessibilitySettings();
        if (_states is not null)
            ApplyStates(_states, animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _advance?.Stop();
        _advance = null;
        HidePours();
        _uiSettings = null;
        _accessibility = null;
    }

    private static int IndexOfCurrent(IReadOnlyList<OnboardingDesignStepState> states)
    {
        for (var index = 0; index < states.Count; index++)
        {
            if (states[index] == OnboardingDesignStepState.Current)
                return index;
        }
        return -1;
    }
}
