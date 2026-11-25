using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private IReadOnlyList<OnboardingDesignStepState>? _states;
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibility;
    private SetupStepRow? _hovered;

    public SetupStepTimeline()
    {
        InitializeComponent();
        _rows = [Step0, Step1, Step2, Step3];
        _tracks = [Track0, Track1, Track2];
        _pours = [Pour0, Pour1, Pour2];
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
        HidePours();
        var previousIndex = previous is null ? -1 : IndexOfCurrent(previous);
        if (previous is null || nextIndex <= previousIndex || !CanAnimate)
        {
            ApplyStates(next, animate: false);
            return;
        }
        PourThenApply(previousIndex, nextIndex, next);
    }

    private void PourThenApply(int from, int to, IReadOnlyList<OnboardingDesignStepState> target)
    {
        var pours = _pours.Skip(Math.Max(0, from)).Take(to - Math.Max(0, from)).ToArray();
        foreach (var pour in pours)
        {
            pour.Opacity = 1;
            SetupVisualTransition.GetVisual(pour).Opacity = 1;
        }
        SetupVisualTransition.Play(AdvancePlayer, PourDuration, motion =>
        {
            using var ease = CompositionEasingFunction.CreatePowerEasingFunction(
                motion.RootVisual.Compositor, CompositionEasingFunctionMode.InOut, 2);
            foreach (var pour in pours)
                motion.Animate(SetupVisualTransition.GetVisual(pour), "Scale.Y", (0, 0, null), (1, 1, ease));
        }, () =>
        {
            ApplyStates(target, animate: true);
            SetupVisualTransition.Play(AdvancePlayer, PourFadeDuration, motion =>
            {
                foreach (var pour in pours)
                    motion.Animate(SetupVisualTransition.GetVisual(pour), "Opacity", (0, 1, null), (1, 0, null));
            }, HidePours);
            Advanced?.Invoke(this, EventArgs.Empty);
        });
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

    private void HidePours()
    {
        SetupVisualTransition.Stop(AdvancePlayer);
        foreach (var pour in _pours)
        {
            pour.Opacity = 0;
            var visual = SetupVisualTransition.GetVisual(pour);
            visual.Opacity = 0;
            visual.Scale = Vector3.One;
        }
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
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _accessibility ??= new AccessibilitySettings();
        if (_states is not null)
            ApplyStates(_states, animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HidePours();
        if (_uiSettings is not null)
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _uiSettings = null;
        _accessibility = null;
        if (_hovered is { } hovered)
        {
            _hovered = null;
            hovered.SetHovered(false, animate: false, highContrast: false);
            HoveredDetailChanged?.Invoke(this, null);
        }
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (CanAnimate) return;
            HidePours();
            if (_states is not null) ApplyStates(_states, animate: false);
        }))
            Trace.TraceWarning("Setup timeline could not dispatch a Windows animation preference change.");
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
