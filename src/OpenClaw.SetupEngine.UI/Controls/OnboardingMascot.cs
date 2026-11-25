// Adapted from OpenClaw's MIT-licensed Mac mascot. See Assets/Setup/Mascot-NOTICE.txt.
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// Decorative Composition mascot. The containing page owns accessible status text.
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

    private readonly OnboardingMascotPlayback _playback = new();
    private readonly AnimatedVisualPlayer _player = new() { AutoPlay = false, Width = 168, Height = 168 };
    private readonly Grid _glowHost = new() { Width = 168, Height = 168, IsHitTestVisible = false };
    private readonly List<(UIElement Element, long Token)> _visibilitySubscriptions = [];
    private OnboardingMascotVisual? _source;
    private Vector2? _pointer;
    private int _playbackVersion;
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
            Children = { new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new Grid { Width = 168, Height = 168, Children = { _glowHost, _player } },
            } },
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnThemeChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        Tapped += OnTapped;
        _player.RegisterPropertyChangedCallback(AnimatedVisualPlayer.IsAnimatedVisualLoadedProperty,
            (_, _) => UpdateAnimation());
    }

    /// <summary>
    /// Optional pointer forwarding from the parent, normalized to -1..1 around the mascot center.
    /// Pass null on pointer exit. Reduced motion suppresses all pointer effects.
    /// </summary>
    public void SetPointerGaze(double? x, double? y)
    {
        if (x.HasValue != y.HasValue)
            throw new ArgumentException("Both gaze coordinates must be supplied or both must be null.");
        if (x is { } px && (!double.IsFinite(px) || !double.IsFinite(y!.Value)))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!CanAnimate) return;
        if (x is not null && !_playback.PointerActivity()) return;
        _pointer = x is { } value ? new Vector2((float)Math.Clamp(value, -1, 1), (float)Math.Clamp(y!.Value, -1, 1)) : null;
        UpdateGaze();
    }

    /// <summary>Wakes an interactive mascot during a host-owned drag without counting a tap.</summary>
    public void WakeUp()
    {
        if (_playback.WakeUp()) RestartPlayback();
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
        if (_playback.Tap()) RestartPlayback();
        args.Handled = true;
    }

    private static void OnAccessoryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._playback.Accessory = (OnboardingMascotAccessory)args.NewValue;
        mascot.UpdateHeadwear();
    }

    private static void OnInteractiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._playback.Interactive = (bool)args.NewValue;
        mascot.RestartPlayback();
    }

    private static void OnMoodChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var mascot = (OnboardingMascot)sender;
        mascot._playback.SetMood((OnboardingMascotMood)args.NewValue);
        mascot.RestartPlayback();
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
        _heroGlow = new OnboardingMascotGlow(_player, _glowHost);
        UpdatePalette();
        UpdateAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _playbackVersion++;
        _player.Stop();
        _playback.SetActive(false);
        _player.Source = null;
        _source = null;
        _heroGlow?.Dispose();
        _heroGlow = null;
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
        _pointer = null;
    }

    private bool CanAnimate => _loaded && _player.IsAnimatedVisualLoaded && IsAnimationEnabled && _uiSettings?.AnimationsEnabled == true &&
        _inViewport && _root?.IsHostVisible == true &&
        _visibilitySubscriptions.All(subscription => subscription.Element.Visibility == Visibility.Visible);

    private void UpdateAnimation()
    {
        if (_playback.Active == CanAnimate) return;
        _playback.SetActive(CanAnimate);
        if (!CanAnimate) _pointer = null;
        RestartPlayback();
    }

    private void RestartPlayback()
    {
        var version = ++_playbackVersion;
        _player.Stop();
        if (!_loaded || _source is null) return;
        UpdateHeadwear();
        UpdateGaze();
        if (CanAnimate) _ = PlayClipsAsync(version);
        else
        {
            _source.Configure(Mood, _playback.RestingClip, still: true, phase: 0);
            _player.SetProgress(0);
        }
    }

    private async Task PlayClipsAsync(int version)
    {
        try
        {
            while (version == _playbackVersion && CanAnimate)
            {
                var clip = _playback.Clip;
                var mood = _playback.IsDozing ? OnboardingMascotMood.Sleepy : Mood;
                _source!.Configure(mood, clip, still: false, phase: _playback.Elapsed);
                UpdateHeadwear();
                UpdateGaze();
                await _player.PlayAsync(0, OnboardingMascotMotion.Duration(clip) / OnboardingMascotMotion.TimelineSeconds, looped: false);
                // Stop completes the old PlayAsync too. It must not advance a newer mood or mount.
                if (version != _playbackVersion || !CanAnimate) return;
                _playback.Complete();
            }
        }
        catch (OperationCanceledException) when (version != _playbackVersion || !_loaded)
        {
            // The player was stopped or detached by a newer host state.
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or OperationCanceledException)
        {
            Trace.TraceError($"Mascot animation playback failed: {exception}");
            if (version == _playbackVersion)
            {
                _player.Stop();
                _playback.SetActive(false);
            }
        }
    }

    private void UpdateHeadwear() => _source?.SetHeadwear(_playback.HardHat, _playback.Accessory, CanAnimate);

    private void UpdateGaze()
    {
        var gaze = CanAnimate && !_playback.IsDozing ? _pointer : null;
        if (_playback.Clip is OnboardingMascotClip.Dizzy or OnboardingMascotClip.Shake or
            OnboardingMascotClip.Peek or OnboardingMascotClip.Startle) gaze = null;
        _source?.SetGaze(gaze, CanAnimate);
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

    private void OnAnimationsEnabledChanged(UISettings sender, object args) => Dispatch(UpdateAnimation);

    private void OnColorsChanged(UISettings sender, object args) => Dispatch(UpdatePalette);

    private void Dispatch(Action update)
    {
        if (!DispatcherQueue.TryEnqueue(() => { if (_loaded) update(); }))
            Trace.TraceWarning("Mascot could not dispatch a Windows preference change.");
    }

    private void UpdatePalette()
    {
        if (!_loaded) return;
        var light = ActualTheme == ElementTheme.Light;
        var highContrast = _accessibilitySettings?.HighContrast == true;
        if (_source is null)
        {
            _source = new OnboardingMascotVisual(light, highContrast);
            _source.Configure(Mood, _playback.RestingClip, still: true, phase: 0);
            _source.SetHeadwear(_playback.HardHat, _playback.Accessory, animate: false);
            _player.Source = _source;
            RestartPlayback();
        }
        else _source.SetPalette(light, highContrast);
        _heroGlow?.SetPalette(light, highContrast);
    }
}
