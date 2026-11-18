using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using OpenClawTray.Controls;

namespace OpenClawTray.Chat;

[GenerateReactorWrapper(typeof(NativeWorkingDots), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeWorkingDotsElement;

[GenerateReactorWrapper(typeof(NativeWorkingBrand), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeWorkingBrandElement;

/// <summary>
/// The pulsing working indicators for themes that ask for dots or the brand icon (upstream
/// <c>workingIndicator: "dots"</c> or <c>"brand"</c>): the parts pulse in turn on the dashboard's 1.2 s cycle while
/// a run is active and rest when it ends or Windows animations are off. The pulse is a storyboard, so it runs off
/// the UI thread. Under High Contrast the parts stay fully opaque (dimming would break the system contrast) and
/// bounce instead.
/// <example><code>
/// NativeWorkingDotsElement.NativeWorkingDots().Set(dots => dots.Update(progress.Active))
/// NativeWorkingBrandElement.NativeWorkingBrand().Set(brand => brand.Update(progress.Active))
/// </code></example>
/// </summary>
internal abstract class NativeWorkingPulse : Control
{
    private const double RestingOpacity = 0.45;
    private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(1200);
    private readonly FrameworkElement[] _parts;
    private readonly StackPanel _root = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 3,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        IsHitTestVisible = false,
    };
    private readonly global::Windows.UI.ViewManagement.UISettings _settings = new();
    private Storyboard? _pulse;
    private bool _active;
    private bool _highContrast;

    protected NativeWorkingPulse(params FrameworkElement[] parts)
    {
        _parts = parts;
        Template = (ControlTemplate)Application.Current.Resources["ChatWorkingClawTemplate"];
        foreach (var part in parts)
        {
            part.Opacity = RestingOpacity;
            part.RenderTransform = new TranslateTransform();
            _root.Children.Add(part);
        }
        Loaded += (_, _) =>
        {
            ChatThemeChanges.Watch(this, Sync);
            Sync();
        };
        Unloaded += (_, _) =>
        {
            ChatThemeChanges.Unwatch(this);
            Stop();
        };
    }

    /// <summary>True while the parts are pulsing.</summary>
    internal bool IsPulsing => _pulse is not null;

    /// <summary>The pulsing parts, for tests that check their effective opacity.</summary>
    internal IReadOnlyList<FrameworkElement> Parts => _parts;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("ClawRoot")).Content = _root;
    }

    internal void Update(bool active)
    {
        _active = active;
        if (IsLoaded)
            Sync();
    }

    private void Sync()
    {
        var highContrast = ChatThemeChanges.IsHighContrast();
        if (highContrast != _highContrast)
        {
            Stop();
            _highContrast = highContrast;
        }
        foreach (var part in _parts)
            part.Opacity = highContrast ? 1 : RestingOpacity;
        if (!_active || !_settings.AnimationsEnabled)
        {
            Stop();
            return;
        }
        if (_pulse is not null)
            return;
        // Opacity pulse normally; under High Contrast a small bounce keeps the parts at full system contrast.
        var (property, rest, peak) = _highContrast
            ? ("(UIElement.RenderTransform).(TranslateTransform.Y)", 0.0, -2.0)
            : ("Opacity", RestingOpacity, 1.0);
        _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        for (var index = 0; index < _parts.Length; index++)
        {
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = Cycle };
            var start = TimeSpan.FromMilliseconds(index * 160);
            pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = rest });
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = start, Value = rest });
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = start + TimeSpan.FromMilliseconds(240), Value = peak });
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = start + TimeSpan.FromMilliseconds(480), Value = rest });
            Storyboard.SetTarget(pulse, _parts[index]);
            Storyboard.SetTargetProperty(pulse, property);
            _pulse.Children.Add(pulse);
        }
        _pulse.Begin();
    }

    private void Stop()
    {
        _pulse?.Stop();
        _pulse = null;
    }
}

/// <summary>Three dots in the theme accent (<c>ChatClawBrush</c>), like the dashboard's dots indicator.</summary>
internal sealed class NativeWorkingDots : NativeWorkingPulse
{
    public NativeWorkingDots()
        : base([.. Enumerable.Range(0, 3).Select(_ => new Ellipse
        {
            Width = 4,
            Height = 4,
            Style = (Style)Application.Current.Resources["ChatWorkingDotStyle"],
        })])
    {
    }
}

/// <summary>The theme's brand icon (claw or mark), like the dashboard's brand indicator.</summary>
internal sealed class NativeWorkingBrand : NativeWorkingPulse
{
    public NativeWorkingBrand()
        : base(new BrandMark { MarkSize = 16 })
    {
    }
}