using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenClawTray.Chat;

[GenerateReactorWrapper(typeof(NativeWorkingClaw), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeWorkingClawElement;

internal sealed class NativeWorkingClaw : Control
{
    private static readonly uint Salt = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    private readonly Grid _root = new() { Width = 18, Height = 18, IsHitTestVisible = false };
    private readonly Grid _body = new() { Width = 18, Height = 18 };
    private readonly RotateTransform _jaw = new() { CenterX = 8.6, CenterY = 11 };
    private readonly CompositeTransform _flex = new() { CenterX = 9, CenterY = 9 };
    private readonly ScaleTransform _flip = new() { CenterX = 9, CenterY = 9 };
    private readonly ScaleTransform _sparkScale = new() { CenterX = 5.5, CenterY = 5.5 };
    private readonly TextBlock _spark = new()
    {
        Text = "\u2726", FontSize = 11, Width = 11, Height = 14, Opacity = 0,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(21, -2, -14, 0),
    };
    private readonly global::Windows.UI.ViewManagement.UISettings _settings = new();
    private readonly Stopwatch _clock = new();
    private bool _active;
    private bool _subscribed;
    private string? _key;
    private int _surprise = -1;

    public NativeWorkingClaw()
    {
        Template = (ControlTemplate)Application.Current.Resources["ChatWorkingClawTemplate"];
        var body = Shape(ChatWorkingClawMotion.BodyPath);
        var jaw = Shape(ChatWorkingClawMotion.JawPath);
        jaw.RenderTransform = _jaw;
        var vectors = new Grid { Width = 24, Height = 24 };
        vectors.Children.Add(body);
        vectors.Children.Add(jaw);
        _body.Children.Add(new Viewbox { Width = 18, Height = 18, Child = vectors });
        _body.RenderTransform = _flex;
        _root.RenderTransform = _flip;
        _spark.RenderTransform = _sparkScale;
        _spark.Style = (Style)Application.Current.Resources["ChatClawSparkStyle"];
        _root.Children.Add(_body);
        _root.Children.Add(_spark);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("ClawRoot")).Content = _root;
    }

    internal void Update(bool active, bool waiting, string key)
    {
        if (_key != key || active != _active)
        {
            _clock.Restart();
            _key = key;
        }
        _active = active;
        _surprise = ChatWorkingClawMotion.SelectSurprise(key, Salt, !waiting);
        Apply();
        if (IsLoaded) Start();
    }

    private static Path Shape(string data)
    {
        var path = (Path)XamlReader.Load(
            $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}\"/>");
        path.Style = (Style)Application.Current.Resources["ChatClawShapeStyle"];
        return path;
    }

    private void Start()
    {
        if (!_active || !_settings.AnimationsEnabled)
        {
            Stop();
            Apply();
            return;
        }
        if (_subscribed) return;
        _clock.Start();
        CompositionTarget.Rendering += Render;
        _subscribed = true;
    }

    private void Stop()
    {
        if (_subscribed)
            CompositionTarget.Rendering -= Render;
        _subscribed = false;
        _clock.Stop();
    }

    private void Render(object? sender, object args)
    {
        Apply();
        if (!_settings.AnimationsEnabled) Stop();
    }

    private void Apply()
    {
        var sample = ChatWorkingClawMotion.At(_clock.Elapsed.TotalSeconds, _active, !_settings.AnimationsEnabled, _surprise);
        _jaw.Angle = sample.Jaw;
        _flex.Rotation = sample.Body.Rotation;
        _flex.TranslateX = sample.Body.X;
        _flex.TranslateY = sample.Body.Y;
        _flex.ScaleX = _flex.ScaleY = sample.Body.Scale;
        _flip.ScaleX = sample.Flip;
        _spark.Opacity = sample.SparkOpacity;
        _sparkScale.ScaleX = _sparkScale.ScaleY = sample.SparkScale;
        if (sample.Body.RotationY == 0)
            _body.Projection = null;
        else
        {
            // CSS perspective(60px) rotateY(...) about the 18px SVG's center.
            var perspective = Matrix4x4.Identity;
            perspective.M34 = -1f / 60;
            var matrix = Matrix4x4.CreateTranslation(-9, -9, 0)
                * Matrix4x4.CreateRotationY((float)(sample.Body.RotationY * Math.PI / 180))
                * perspective * Matrix4x4.CreateTranslation(9, 9, 0);
            _body.Projection = new Matrix3DProjection
            {
                ProjectionMatrix = new Matrix3D(matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                    matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                    matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                    matrix.M41, matrix.M42, matrix.M43, matrix.M44),
            };
        }
    }
}
