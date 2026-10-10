// Adapted from OpenClaw's MIT-licensed Mac mascot. See Assets/Setup/Mascot-NOTICE.txt.
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// Decorative native vector mascot. The containing page owns accessible status text.
/// Size with Width/Height; Mood selects the expression. No work runs before Loaded.
/// </summary>
public sealed class OnboardingMascot : UserControl
{
    // Match the approved 128-DIP artwork optically, retaining the 24-unit motion/glow gutter.
    // A 180-DIP native frame renders the 120/168 artwork at approximately 129 DIPs.
    public const double HeroSize = 180;
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(OnboardingMascotMood), typeof(OnboardingMascot),
        new PropertyMetadata(OnboardingMascotMood.Idle, OnMoodChanged));

    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(OnboardingMascot),
        new PropertyMetadata(true, OnAnimationEnabledChanged));

    public static readonly DependencyProperty AccessoryProperty = DependencyProperty.Register(
        nameof(Accessory), typeof(OnboardingMascotAccessory), typeof(OnboardingMascot),
        new PropertyMetadata(OnboardingMascotAccessory.None, OnAccessoryChanged));

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(OnboardingMascot),
        new PropertyMetadata(true, OnInteractiveChanged));

    private readonly OnboardingMascotAnimator _animator = new(allowsAutoSleep: true);
    private readonly OnboardingMascotDrawing _drawing = new();
    private readonly Stopwatch _clock = new();
    private readonly List<(UIElement Element, long Token)> _visibilitySubscriptions = [];
    private DispatcherTimer? _timer;
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibilitySettings;
    private XamlRoot? _root;
    private bool _loaded;
    private bool _inViewport = true;
    private OnboardingMascotGlow? _heroGlow;

    public OnboardingMascotAccessory Accessory
    {
        get => (OnboardingMascotAccessory)GetValue(AccessoryProperty);
        set => SetValue(AccessoryProperty, value);
    }

    /// <summary>Tap reactions and idle sleep. Decorative heroes never enter the keyboard tab order.</summary>
    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    public OnboardingMascotMood Mood
    {
        get => (OnboardingMascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>Optional host pause. Windows' animation preference always takes precedence.</summary>
    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public OnboardingMascot()
    {
        IsTabStop = false;
        Width = Height = HeroSize;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Content = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { new Viewbox { Stretch = Stretch.Uniform, Child = _drawing.Surface } },
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnThemeChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        Tapped += OnTapped;
        _drawing.Render(OnboardingMascotPose.Static(Mood));
    }

    /// <summary>
    /// Optional pointer forwarding from the parent, normalized to -1..1 around the mascot center.
    /// Pass null on pointer exit. Reduced motion suppresses all pointer effects.
    /// </summary>
    public void SetPointerGaze(double? x, double? y)
    {
        if (x.HasValue != y.HasValue)
            throw new ArgumentException("Both gaze coordinates must be supplied or both must be null.");
        if (CanAnimate)
            _animator.SetPointer(x is { } px && y is { } py ? new(px, py) : null);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!CanAnimate || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var point = args.GetCurrentPoint(this).Position;
        var artRadius = Math.Min(ActualWidth, ActualHeight) * 120 / 168 / 2;
        SetPointerGaze((point.X - ActualWidth / 2) / artRadius,
            (point.Y - ActualHeight / 2) / artRadius);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args) => SetPointerGaze(null, null);

    private void OnTapped(object sender, TappedRoutedEventArgs args)
    {
        if (!CanAnimate || !IsInteractive)
            return;
        _animator.HandleTap();
        args.Handled = true;
    }

    private static void OnAccessoryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._animator.SetAccessory((OnboardingMascotAccessory)args.NewValue);
        mascot.RenderFrame(TimeSpan.Zero);
    }

    private static void OnInteractiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._animator.AllowsAutoSleep = (bool)args.NewValue;
    }

    private static void OnMoodChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._animator.SetMood((OnboardingMascotMood)args.NewValue);
        mascot.RenderFrame(TimeSpan.Zero);
    }

    private static void OnAnimationEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((OnboardingMascot)sender).UpdateAnimation();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
            return;
        _loaded = true;
        _inViewport = true;
        _uiSettings = new UISettings();
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _uiSettings.ColorValuesChanged += OnColorsChanged;
        _accessibilitySettings = new AccessibilitySettings();
        _root = XamlRoot;
        if (_root is not null)
            _root.Changed += OnRootChanged;
        for (DependencyObject? ancestor = this; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement element)
            {
                var token = element.RegisterPropertyChangedCallback(VisibilityProperty, OnAncestorVisibilityChanged);
                _visibilitySubscriptions.Add((element, token));
            }
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1d / OnboardingMascotAnimator.FramesPerSecond) };
        _timer.Tick += OnTick;
        _heroGlow = new OnboardingMascotGlow(_drawing);
        UpdatePalette();
        UpdateAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _heroGlow?.Dispose();
        _heroGlow = null;
        _clock.Reset();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        if (_uiSettings is not null)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            _uiSettings.ColorValuesChanged -= OnColorsChanged;
        }
        if (_root is not null)
            _root.Changed -= OnRootChanged;
        foreach (var (element, token) in _visibilitySubscriptions)
            element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        _visibilitySubscriptions.Clear();
        _uiSettings = null;
        _accessibilitySettings = null;
        _root = null;
        _animator.SetPointer(null);
    }

    private bool CanAnimate => _loaded && IsAnimationEnabled && _uiSettings?.AnimationsEnabled == true &&
        _inViewport && _root?.IsHostVisible == true &&
        _visibilitySubscriptions.All(subscription => subscription.Element.Visibility == Visibility.Visible);

    private void UpdateAnimation()
    {
        if (CanAnimate)
        {
            if (_timer is { IsEnabled: false })
            {
                _clock.Restart();
                _timer.Start();
            }
        }
        else
        {
            _timer?.Stop();
            _clock.Reset();
        }
        RenderFrame(TimeSpan.Zero);
    }

    private void OnTick(object? sender, object args)
    {
        if (!CanAnimate)
        {
            UpdateAnimation();
            return;
        }
        var elapsed = _clock.Elapsed;
        _clock.Restart();
        RenderFrame(elapsed);
    }

    private void RenderFrame(TimeSpan elapsed)
    {
        if (_loaded)
            _drawing.Render(_animator.Advance(elapsed, CanAnimate));
        else
        {
            var pose = OnboardingMascotPose.Static(Mood);
            if (Accessory != OnboardingMascotAccessory.None && pose.HardHat == 0)
                pose = pose with { Accessory = Accessory, AccessoryAmount = 1 };
            _drawing.Render(pose);
        }
    }
    private void OnAncestorVisibilityChanged(DependencyObject sender, DependencyProperty property) => UpdateAnimation();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateAnimation();
    private void OnThemeChanged(FrameworkElement sender, object args) => UpdatePalette();

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var viewport = args.EffectiveViewport;
        _inViewport = viewport.Width > 0 && viewport.Height > 0 &&
            viewport.Right > 0 && viewport.Bottom > 0 && viewport.Left < ActualWidth && viewport.Top < ActualHeight;
        UpdateAnimation();
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) UpdateAnimation(); });

    private void OnColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) UpdatePalette(); });

    private void UpdatePalette()
    {
        var light = ActualTheme == ElementTheme.Light;
        var highContrast = _accessibilitySettings?.HighContrast == true;
        _drawing.SetPalette(light, highContrast);
        _heroGlow?.SetPalette(light, highContrast);
    }
}
