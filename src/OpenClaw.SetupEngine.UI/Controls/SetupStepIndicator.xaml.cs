using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace OpenClaw.SetupEngine.UI.Controls;

public enum SetupStepState { Upcoming, Current, Completed }

/// <summary>One noninteractive presentation step. The caller owns its state and optional helper text.</summary>
public sealed record SetupStepItem(string Title, SetupStepState State, string? Description = null);

/// <summary>Reusable native timeline. Rendering and finite transitions only; no installation or navigation authority.</summary>
public sealed partial class SetupStepIndicator : UserControl, ISetupProgressIndicator
{
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(SetupStepIndicator),
        new PropertyMetadata(true, (sender, _) => ((SetupStepIndicator)sender).StopTransitions()));

    private readonly List<StepVisual> _rows = [];
    private SetupStepItem[] _steps = [];
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibilitySettings;
    private XamlRoot? _root;
    private int _current = -1;
    private int? _pendingTransitionFrom;
    private bool _loaded;
    private bool _inViewport = true;

    /// <summary>Optional host pause. Windows' reduced-motion preference always takes precedence.</summary>
    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public SetupStepIndicator()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => QueuePaletteRefresh();
        EffectiveViewportChanged += (_, args) =>
        {
            _inViewport = args.EffectiveViewport.Width > 0 && args.EffectiveViewport.Height > 0;
            if (!_inViewport)
                StopTransitions();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (!CanAnimate) StopTransitions(); });
    }

    public void Update(IReadOnlyList<SetupStepItem> steps)
        => UpdateSteps(steps, null);

    private void UpdateSteps(IReadOnlyList<SetupStepItem> steps, int? previousCurrent)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var current = -1;
        var sawUpcoming = false;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Title);
            if (!Enum.IsDefined(step.State))
                throw new ArgumentOutOfRangeException(nameof(steps), "Unknown step state.");
            if (step.State == SetupStepState.Current)
            {
                if (current >= 0 || sawUpcoming)
                    throw new ArgumentException("Current steps must follow completed steps and precede upcoming steps.", nameof(steps));
                current = index;
            }
            else if (step.State == SetupStepState.Completed && (current >= 0 || sawUpcoming))
                throw new ArgumentException("Completed steps must form a prefix.", nameof(steps));
            else if (step.State == SetupStepState.Upcoming)
                sawUpcoming = true;
        }

        AutomationProperties.SetName(this, current >= 0
            ? SetupLocalization.Format("Onboarding_Flow_Position", current + 1, steps.Count) : "");
        if (_steps.SequenceEqual(steps))
            return;
        _steps = steps.ToArray();
        var previous = previousCurrent ?? _current;
        _pendingTransitionFrom = null;
        StopTransitions();
        while (_rows.Count > steps.Count)
        {
            _rows[^1].Dispose();
            _rows.RemoveAt(_rows.Count - 1);
            StepRows.Children.RemoveAt(StepRows.Children.Count - 1);
        }
        while (_rows.Count < steps.Count)
        {
            var row = new StepVisual((Grid)((DataTemplate)Resources["StepTemplate"]).LoadContent());
            _rows.Add(row);
            StepRows.Children.Add(row.Root);
        }
        for (var index = 0; index < steps.Count; index++)
        {
            var item = steps[index];
            var row = _rows[index];
            row.Title.Text = item.Title;
            row.Title.Style = (Style)Resources[item.State == SetupStepState.Current ? "CurrentTitleStyle" : "SecondaryTitleStyle"];
            row.Description.Text = item.Description ?? "";
            row.Description.Visibility = string.IsNullOrWhiteSpace(item.Description) ? Visibility.Collapsed : Visibility.Visible;
            row.Text.Padding = new Thickness(0, 0, 0,
                row.Description.Visibility == Visibility.Visible ? (index == 0 && item.State == SetupStepState.Current ? 16 : 8) : 14);
            row.Upcoming.Visibility = item.State == SetupStepState.Upcoming ? Visibility.Visible : Visibility.Collapsed;
            row.Current.Visibility = item.State == SetupStepState.Current ? Visibility.Visible : Visibility.Collapsed;
            row.Completed.Visibility = item.State == SetupStepState.Completed ? Visibility.Visible : Visibility.Collapsed;
            row.Top.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
            row.Bottom.Visibility = index == steps.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
            row.Filled.Visibility = index < steps.Count - 1 && item.State == SetupStepState.Completed
                ? Visibility.Visible : Visibility.Collapsed;
            row.FilledTop.Visibility = index > 0 && steps[index - 1].State == SetupStepState.Completed
                ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(row.Title, item.State switch
            {
                SetupStepState.Current => SetupLocalization.Format("Onboarding_Milestone_Current", item.Title),
                SetupStepState.Completed => SetupLocalization.Format("Onboarding_Milestone_Complete", item.Title),
                _ => SetupLocalization.Format("Onboarding_Milestone_Upcoming", item.Title),
            });
        }
        _current = current;
        Visibility = steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshGlows();
        if (previous >= 0 && current >= 0 && previous != current)
            StartOrQueueTransition(previous, current);
    }

    public void Update(IReadOnlyList<OnboardingStage> stages, OnboardingStage current)
        => Update(stages, current, null);

    public void Update(IReadOnlyList<OnboardingStage> stages, OnboardingStage current, OnboardingStage? previous)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var position = stages.ToList().IndexOf(current);
        if (position < 0)
        {
            Update(Array.Empty<SetupStepItem>());
            return;
        }
        var groups = stages.Select((stage, index) => (Stage: stage, Key: StepKey(stage), Index: index))
            .GroupBy(stage => stage.Key).ToArray();
        var steps = groups.Select(group =>
        {
            var state = group.Any(stage => stage.Index == position) ? SetupStepState.Current :
                group.All(stage => stage.Index < position) ? SetupStepState.Completed : SetupStepState.Upcoming;
            var description = group.Key switch
            {
                "Tools" when state != SetupStepState.Upcoming => SetupLocalization.GetString("Onboarding_Milestone_ToolsDescription"),
                "Setup" when state == SetupStepState.Current && current == OnboardingStage.Install =>
                    SetupLocalization.GetString("Onboarding_Milestone_SetupDescription"),
                _ => null,
            };
            return new SetupStepItem(SetupLocalization.GetString("Onboarding_Milestone_" + group.Key), state, description);
        }).ToArray();
        var currentMilestone = Array.FindIndex(groups, group => group.Any(stage => stage.Stage == current));
        int? previousMilestone = previous is { } previousStage
            ? Array.FindIndex(groups, group => group.Any(stage => stage.Stage == previousStage))
            : null;
        UpdateSteps(steps, previousMilestone);
        AutomationProperties.SetName(this, SetupLocalization.Format(
            "Onboarding_Flow_Position", currentMilestone + 1, steps.Length));
    }

    private static string StepKey(OnboardingStage stage) => stage switch
    {
        OnboardingStage.Welcome or OnboardingStage.Gateway or OnboardingStage.Capabilities => "Tools",
        OnboardingStage.GatewayReview or OnboardingStage.Install => "Setup",
        OnboardingStage.AiSetup => "Ai",
        OnboardingStage.Ready => "Chat",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    private bool CanAnimate => _loaded && IsAnimationEnabled && _uiSettings?.AnimationsEnabled == true &&
        _inViewport && _root?.IsHostVisible == true && Visibility == Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _inViewport = true;
        _uiSettings = new UISettings();
        _accessibilitySettings = new AccessibilitySettings();
        _uiSettings.AnimationsEnabledChanged += OnAnimationsChanged;
        _uiSettings.ColorValuesChanged += OnColorsChanged;
        _root = XamlRoot;
        if (_root is not null) _root.Changed += OnRootChanged;
        RefreshGlows();
        if (_pendingTransitionFrom is { } previous)
        {
            _pendingTransitionFrom = null;
            if (_current >= 0 && CanAnimate)
                PlayTransition(previous, _current);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _pendingTransitionFrom = null;
        StopTransitions();
        foreach (var row in _rows) row.DisposeGlow();
        if (_uiSettings is not null)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsChanged;
            _uiSettings.ColorValuesChanged -= OnColorsChanged;
        }
        if (_root is not null) _root.Changed -= OnRootChanged;
        _uiSettings = null;
        _accessibilitySettings = null;
        _root = null;
    }

    private void OnAnimationsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!CanAnimate) StopTransitions(); });

    private void OnColorsChanged(UISettings sender, object args) => QueuePaletteRefresh();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!CanAnimate) StopTransitions();
    }

    private void QueuePaletteRefresh() =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) RefreshGlows(); });

    private void StopTransitions()
    {
        foreach (var row in _rows) row.Stop();
    }

    private void StartOrQueueTransition(int previous, int current)
    {
        if (CanAnimate)
            PlayTransition(previous, current);
        else if (!_loaded)
            _pendingTransitionFrom = previous;
    }

    private void PlayTransition(int previous, int current)
    {
        _rows[current].Pop();
        if (current > previous && current > 0)
            _rows[current - 1].Pour();
    }

    private void RefreshGlows()
    {
        for (var index = 0; index < _rows.Count; index++)
            _rows[index].SetGlow(_loaded && index == _current && _accessibilitySettings?.HighContrast != true);
    }

    private sealed class StepVisual : IDisposable
    {
        public Grid Root { get; }
        public StackPanel Text { get; }
        public TextBlock Title { get; }
        public TextBlock Description { get; }
        public Ellipse Upcoming { get; }
        public Ellipse Current { get; }
        public Grid Completed { get; }
        public Border Top { get; }
        public Border Bottom { get; }
        public Border Filled { get; }
        public Border FilledTop { get; }
        private readonly Grid _glowHost;
        private CompositionVisualSurface? _surface;
        private CompositionSurfaceBrush? _mask;
        private DropShadow? _shadow;
        private SpriteVisual? _glow;
        private Storyboard? _pop;
        private Storyboard? _pour;

        public StepVisual(Grid root)
        {
            Root = root;
            Text = (StackPanel)root.FindName("StepText");
            Title = (TextBlock)root.FindName("StepTitle");
            Description = (TextBlock)root.FindName("StepDescription");
            Upcoming = (Ellipse)root.FindName("UpcomingDot");
            Current = (Ellipse)root.FindName("CurrentDot");
            Completed = (Grid)root.FindName("CompletedDot");
            Top = (Border)root.FindName("TopConnector");
            Bottom = (Border)root.FindName("BottomConnector");
            Filled = (Border)root.FindName("FilledConnector");
            FilledTop = (Border)root.FindName("FilledTopConnector");
            _glowHost = (Grid)root.FindName("GlowHost");
        }

        public void SetGlow(bool visible)
        {
            if (!visible) { DisposeGlow(); return; }
            if (_glow is null)
            {
                var compositor = ElementCompositionPreview.GetElementVisual(Current).Compositor;
                _surface = compositor.CreateVisualSurface();
                _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(Current);
                _surface.SourceSize = new Vector2(12);
                _mask = compositor.CreateSurfaceBrush(_surface);
                _shadow = compositor.CreateDropShadow();
                _shadow.Mask = _mask;
                _shadow.BlurRadius = 8;
                _shadow.Offset = Vector3.Zero;
                _glow = compositor.CreateSpriteVisual();
                _glow.Size = new Vector2(12);
                _glow.Shadow = _shadow;
                ElementCompositionPreview.SetElementChildVisual(_glowHost, _glow);
            }
            _shadow!.Color = ((SolidColorBrush)Current.Fill).Color;
            _shadow.Opacity = Current.ActualTheme == ElementTheme.Light ? 0.3f : 0.5f;
        }

        public void Pop()
        {
            _pop = Animate((ScaleTransform)Current.RenderTransform, 4d / 3, "ScaleX", "ScaleY");
            _pop.Begin();
        }

        public void Pour()
        {
            _pour = Animate((ScaleTransform)Filled.RenderTransform, 0, "ScaleY");
            _pour.Begin();
        }

        private static Storyboard Animate(DependencyObject target, double from, params string[] properties)
        {
            var storyboard = new Storyboard();
            foreach (var property in properties)
            {
                var animation = new DoubleAnimation
                {
                    From = from, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, property);
                storyboard.Children.Add(animation);
            }
            return storyboard;
        }

        public void Stop()
        {
            _pop?.Stop();
            _pour?.Stop();
            _pop = _pour = null;
        }

        public void DisposeGlow()
        {
            if (_glow is null) return;
            ElementCompositionPreview.SetElementChildVisual(_glowHost, null);
            _glow.Shadow = null;
            _shadow!.Mask = null;
            _mask!.Surface = null;
            _surface!.SourceVisual = null;
            _glow.Dispose();
            _shadow.Dispose();
            _mask.Dispose();
            _surface.Dispose();
            _glow = null;
            _shadow = null;
            _mask = null;
            _surface = null;
        }

        public void Dispose() { Stop(); DisposeGlow(); }
    }
}
