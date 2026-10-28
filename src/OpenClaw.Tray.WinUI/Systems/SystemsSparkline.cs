using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared.Systems;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Systems;

/// <summary>Everything one sparkline draws. Format and Age produce the hover readout.</summary>
internal sealed record SparklineData(
    IReadOnlyList<TelemetryPoint> Points,
    double Ceiling,
    bool Autorange,
    bool Live,
    Func<double, string> Format,
    Func<DateTimeOffset, string> Age);

/// <summary>
/// Native area sparkline: a gradient wash under a line, with a glowing "now" dot that pulses
/// while the data is live. Hovering shows a hairline and the nearest sample's value and age.
/// The themed <see cref="Control.Foreground"/> drives every color, so light, dark and high
/// contrast follow the Systems resources; high contrast drops the gradient wash. The pulse
/// honours the Windows "Animation effects" setting.
/// </summary>
internal sealed class SystemsSparkline : Control
{
    private static readonly UISettings s_uiSettings = new();
    private static readonly AccessibilitySettings s_accessibility = new();
    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(1600);

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Polygon _area = new();
    private readonly Polyline _line = new()
    {
        StrokeThickness = 2,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };
    private readonly Rectangle _hairline = new() { Width = 1, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _glowScale = new();
    private readonly Ellipse _glow = new() { Width = GlowSize, Height = GlowSize, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Ellipse _dot = new() { Width = DotSize, Height = DotSize };
    private readonly TextBlock _readout = new()
    {
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private const double DotSize = 7;
    private const double GlowSize = 16;

    private Grid? _root;
    private SparklineData? _data;
    private Storyboard? _pulse;
    private int? _hover;
    private bool _loaded;

    public SystemsSparkline()
    {
        Template = (ControlTemplate)Application.Current.Resources["SystemsSparklineTemplate"];
        IsTabStop = false;
        _glow.RenderTransform = _glowScale;
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_hairline);
        _canvas.Children.Add(_glow);
        _canvas.Children.Add(_dot);
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => ApplyBrushes());
        ActualThemeChanged += (_, _) => ApplyBrushes();
        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => SetHover(null);
        PointerCanceled += (_, _) => SetHover(null);
        Loaded += (_, _) => { _loaded = true; UpdatePulse(); };
        Unloaded += (_, _) => { _loaded = false; StopPulse(); };
    }

    internal void Update(SparklineData data)
    {
        _data = data;
        if (_hover >= data.Points.Count)
            _hover = null;
        Redraw();
        UpdatePulse();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root?.Children.Clear();
        _root = GetTemplateChild("Root") as Grid;
        if (_root is null)
            return;
        _root.Children.Add(_canvas);
        _root.Children.Add(_readout);
        ApplyBrushes();
        Redraw();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_data is not { Points.Count: > 1 } data)
            return;
        var x = e.GetCurrentPoint(this).Position.X;
        SetHover(SparklineGeometry.NearestIndex(x, ActualWidth, data.Points.Count));
    }

    private void SetHover(int? index)
    {
        if (_hover == index)
            return;
        _hover = index;
        Redraw();
        UpdatePulse();
    }

    private void ApplyBrushes()
    {
        var color = (Foreground as SolidColorBrush)?.Color ?? Colors.Gray;
        _line.Stroke = new SolidColorBrush(color);
        _dot.Fill = new SolidColorBrush(color);
        _glow.Fill = new SolidColorBrush(color);
        _hairline.Fill = new SolidColorBrush(color) { Opacity = 0.5 };
        _area.Fill = s_accessibility.HighContrast
            ? null
            : new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                // Fade to the same hue at zero alpha so the wash never turns gray.
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Color.FromArgb(84, color.R, color.G, color.B) },
                    new GradientStop { Offset = 1, Color = Color.FromArgb(0, color.R, color.G, color.B) },
                },
            };
    }

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var visible = _data is { Points.Count: > 0 } && width > 0 && height > 0;
        _canvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _readout.Visibility = Visibility.Collapsed;
            return;
        }

        var data = _data!;
        var values = data.Points.Select(point => point.Value).ToArray();
        var xy = SparklineGeometry.Points(values, SparklineGeometry.Scale(values, data.Ceiling, data.Autorange), width, height);
        var hasLine = xy.Count > 1;
        _line.Visibility = _area.Visibility = hasLine ? Visibility.Visible : Visibility.Collapsed;
        if (hasLine)
        {
            var line = new PointCollection();
            var area = new PointCollection { new Point(0, height) };
            foreach (var (x, y) in xy)
            {
                line.Add(new Point(x, y));
                area.Add(new Point(x, y));
            }
            area.Add(new Point(width, height));
            _line.Points = line;
            _area.Points = area;
        }

        var shown = _hover ?? xy.Count - 1;
        var (dotX, dotY) = xy[shown];
        Place(_dot, dotX, dotY, DotSize);
        Place(_glow, dotX, dotY, GlowSize);
        _glow.Visibility = data.Live && _hover is null ? Visibility.Visible : Visibility.Collapsed;

        if (_hover is { } hover)
        {
            _hairline.Height = height;
            Canvas.SetLeft(_hairline, Math.Round(dotX));
            _hairline.Visibility = Visibility.Visible;
            var point = data.Points[hover];
            _readout.Text = $"{data.Format(point.Value)} · {data.Age(point.At)}";
            // Keep the readout on the side away from the hairline.
            _readout.HorizontalAlignment = dotX > width / 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _readout.Visibility = Visibility.Visible;
        }
        else
        {
            _hairline.Visibility = Visibility.Collapsed;
            _readout.Visibility = Visibility.Collapsed;
        }
    }

    private static void Place(FrameworkElement element, double x, double y, double size)
    {
        Canvas.SetLeft(element, x - size / 2);
        Canvas.SetTop(element, y - size / 2);
    }

    private void UpdatePulse()
    {
        var pulse = _loaded && _data?.Live == true && _hover is null && s_uiSettings.AnimationsEnabled;
        if (!pulse)
        {
            StopPulse();
            _glow.Opacity = 0.3;
            return;
        }
        if (_pulse is not null)
            return;

        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Add(storyboard, _glow, "Opacity", 0.55, 0, ease);
        Add(storyboard, _glowScale, "ScaleX", 0.5, 1.6, ease);
        Add(storyboard, _glowScale, "ScaleY", 0.5, 1.6, ease);
        storyboard.Begin();
        _pulse = storyboard;
    }

    private static void Add(Storyboard storyboard, DependencyObject target, string property, double from, double to, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = PulseDuration, EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void StopPulse()
    {
        _pulse?.Stop();
        _pulse = null;
    }
}
