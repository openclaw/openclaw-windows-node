// Adapted from OpenClaw's MIT-licensed Mac mascot. See Assets/Setup/Mascot-NOTICE.txt.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using NativePath = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Retained WinUI shapes. Geometry and brushes are allocated once, not on each animation tick.</summary>
internal sealed class OnboardingMascotDrawing
{
    // A gutter contains the original 120-unit art, its whole-body travel, and the hat entrance.
    public Canvas Surface { get; } = new() { Width = 168, Height = 168, IsHitTestVisible = false };
    internal Canvas Artwork { get; } = new() { Width = 168, Height = 168, IsHitTestVisible = false };
    internal Canvas GlowHost { get; } = new() { Width = 168, Height = 168, IsHitTestVisible = false };
    private readonly Canvas _body = Layer();
    private readonly CompositeTransform _bodyTransform = new() { CenterX = 60, CenterY = 110 };
    private readonly RotateTransform _bodyTilt = new() { CenterX = 60, CenterY = 60 };
    private readonly TranslateTransform _float = new();
    private readonly RotateTransform _leftClaw = new() { CenterX = 26, CenterY = 53 };
    private readonly RotateTransform _rightClaw = new() { CenterX = 94, CenterY = 53 };
    private readonly RotateTransform _leftAntenna = new() { CenterX = 37.5, CenterY = 11 };
    private readonly RotateTransform _rightAntenna = new() { CenterX = 82.5, CenterY = 11 };
    private readonly RotateTransform _leftDroop = new() { CenterX = 45, CenterY = 15 };
    private readonly RotateTransform _rightDroop = new() { CenterX = 75, CenterY = 15 };
    private readonly Canvas _hat = Layer();
    private readonly CompositeTransform _hatTransform = new() { CenterX = 60, CenterY = 15, Rotation = -5 };
    private readonly LinearGradientBrush _coral = Gradient();
    private readonly SolidColorBrush _antenna = new();
    private readonly SolidColorBrush _eyes = new();
    private readonly SolidColorBrush _glow = new();
    private readonly SolidColorBrush _amber = new();
    private readonly LinearGradientBrush _hatFill = Gradient();
    private readonly SolidColorBrush _hatOutline = new();
    private readonly SolidColorBrush _sweatFill = new();
    private readonly SolidColorBrush _blushFill = new();
    private readonly SolidColorBrush _heartFill = new();
    private readonly SolidColorBrush _capBlue = new();
    private readonly SolidColorBrush _bandBlue = new();
    private readonly SolidColorBrush _capOutline = new();
    private readonly SolidColorBrush _gradFill = new();
    private readonly SolidColorBrush _gradOutline = new();
    private readonly Canvas _nightcap = Layer();
    private readonly Canvas _gradCap = Layer();
    private readonly TranslateTransform _accessoryTransform = new();
    private readonly LineSegment _tasselEnd = Line(72, 14);
    private readonly TranslateTransform _tasselBob = new();
    private readonly Canvas _blush = Layer();
    private readonly Eye _leftEye;
    private readonly Eye _rightEye;
    private readonly NativePath _mouth;
    private readonly QuadraticBezierSegment _mouthCurve = new() { Point2 = new(67.5, 49) };
    private readonly PathFigure _mouthFigure;
    private readonly Ellipse _roundMouth;
    private readonly CompositeTransform _roundMouthTransform = new() { CenterX = 1, CenterY = 1 };
    private readonly NativePath[] _particles = new NativePath[OnboardingMascotParticles.Capacity];
    private readonly NativePath[] _hearts = new NativePath[OnboardingMascotParticles.Capacity];
    private readonly CompositeTransform[] _particleTransforms = new CompositeTransform[OnboardingMascotParticles.Capacity];
    private readonly NativePath _sweat;
    private readonly TranslateTransform _sweatTransform = new();
    private readonly TextBlock[] _sleepLetters = new TextBlock[3];
    private readonly CompositeTransform[] _sleepTransforms = new CompositeTransform[3];

    public OnboardingMascotDrawing()
    {
        Canvas.SetLeft(_body, 24);
        Canvas.SetTop(_body, 24);
        Surface.Children.Add(GlowHost);
        Surface.Children.Add(Artwork);
        Artwork.Children.Add(_body);
        _body.RenderTransform = Group(_bodyTilt, _bodyTransform, _float);
        _hatFill.MappingMode = BrushMappingMode.Absolute;
        _hatFill.StartPoint = new(60, 3);
        _hatFill.EndPoint = new(60, 16);

        _body.Children.Add(Shape(Figure(60, 10, true,
            Curve(30, 10, 15, 35, 15, 55), Curve(15, 75, 30, 95, 45, 100),
            Line(45, 110), Line(55, 110), Line(55, 100), Curve(55, 100, 60, 102, 65, 100),
            Line(65, 110), Line(75, 110), Line(75, 100),
            Curve(90, 95, 105, 75, 105, 55), Curve(105, 35, 90, 10, 60, 10)), _coral));
        _body.Children.Add(Shape(Figure(20, 45, true,
            Curve(5, 40, 0, 50, 5, 60), Curve(10, 70, 20, 65, 25, 55), Curve(28, 48, 25, 45, 20, 45)), _coral, _leftClaw));
        _body.Children.Add(Shape(Figure(100, 45, true,
            Curve(115, 40, 120, 50, 115, 60), Curve(110, 70, 100, 65, 95, 55), Curve(92, 48, 95, 45, 100, 45)), _coral, _rightClaw));
        _body.Children.Add(Stroke(Figure(45, 15, false, Quad(35, 5, 30, 8)), _antenna, 2, Group(_leftAntenna, _leftDroop)));
        _body.Children.Add(Stroke(Figure(75, 15, false, Quad(85, 5, 90, 8)), _antenna, 2, Group(_rightAntenna, _rightDroop)));

        _hat.RenderTransform = _hatTransform;
        var dome = Shape(Figure(45, 15, true, Curve(47, 7, 54, 3, 60, 3), Curve(66, 3, 73, 7, 75, 15)), _hatFill);
        dome.Stroke = _hatOutline;
        dome.StrokeThickness = 0.8;
        _hat.Children.Add(dome);
        var brim = new Rectangle
        {
            Width = 38, Height = 5, RadiusX = 2, RadiusY = 2, Fill = _amber, Stroke = _hatOutline, StrokeThickness = 0.8,
        };
        Canvas.SetLeft(brim, 41);
        Canvas.SetTop(brim, 14);
        _hat.Children.Add(brim);
        _body.Children.Add(_hat);
        AddAccessories();
        _blush.Children.Add(EllipseAt(32.5, 42.5, 9, 5, _blushFill));
        _blush.Children.Add(EllipseAt(78.5, 42.5, 9, 5, _blushFill));
        _body.Children.Add(_blush);

        _leftEye = AddEye(45);
        _rightEye = AddEye(75);
        _mouthFigure = Figure(52.5, 49, false, _mouthCurve);
        _mouth = Stroke(_mouthFigure, _eyes, 2.2);
        _body.Children.Add(_mouth);
        _roundMouth = EllipseAt(59, 50, 2, 2, _eyes);
        _roundMouth.RenderTransform = _roundMouthTransform;
        _body.Children.Add(_roundMouth);
        for (var i = 0; i < _particles.Length; i++)
        {
            var particleLayer = Layer();
            var transform = new CompositeTransform();
            particleLayer.RenderTransform = transform;
            var star = Shape(Figure(0, -1, true, Quad(0, 0, 1, 0), Quad(0, 0, 0, 1),
                Quad(0, 0, -1, 0), Quad(0, 0, 0, -1)), _glow);
            // WinUI geometry has a single owner. Each retained path owns its geometry;
            // frame changes only switch opacity, never reparent shared Path.Data.
            var heart = new NativePath
            {
                Width = 120, Height = 120, Stretch = Stretch.None, Fill = _heartFill,
                Data = new GeometryGroup
                {
                    FillRule = FillRule.Nonzero,
                    Children =
                    {
                        new EllipseGeometry { Center = new(-0.35, -0.25), RadiusX = 0.4, RadiusY = 0.4 },
                        new EllipseGeometry { Center = new(0.35, -0.25), RadiusX = 0.4, RadiusY = 0.4 },
                        new PathGeometry { Figures = { Figure(-0.7, -0.05, true, Line(0.7, -0.05), Line(0, 0.75)) } },
                    },
                },
            };
            _particles[i] = star;
            _hearts[i] = heart;
            _particleTransforms[i] = transform;
            particleLayer.Children.Add(star);
            particleLayer.Children.Add(heart);
            _body.Children.Add(particleLayer);
        }
        for (var i = 0; i < _sleepLetters.Length; i++)
        {
            var letter = new TextBlock
            {
                Text = "z", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = _glow, IsHitTestVisible = false,
            };
            var transform = new CompositeTransform();
            letter.RenderTransform = transform;
            _sleepLetters[i] = letter;
            _sleepTransforms[i] = transform;
            _body.Children.Add(letter);
        }
        _sweat = Shape(Figure(42, 21, true,
            Curve(38, 25, 40, 27, 42, 27), Curve(44, 27, 46, 25, 42, 21)), _sweatFill, _sweatTransform);
        _body.Children.Add(_sweat);
        SetPalette(light: false, highContrast: false);
    }

    // Brand artwork intentionally preserves upstream colors; high contrast uses the user's system colors.
    public void SetPalette(bool light, bool highContrast)
    {
        if (highContrast)
        {
            var settings = new UISettings();
            var foreground = settings.UIElementColor(UIElementType.WindowText);
            var background = settings.UIElementColor(UIElementType.Window);
            var highlight = settings.UIElementColor(UIElementType.Highlight);
            SetGradient(_coral, foreground, foreground);
            _antenna.Color = foreground;
            _eyes.Color = background;
            _glow.Color = highlight;
            _amber.Color = foreground;
            SetGradient(_hatFill, foreground, foreground);
            _hatOutline.Color = background;
            _sweatFill.Color = highlight;
            _blushFill.Color = _heartFill.Color = highlight;
            _capBlue.Color = _bandBlue.Color = _gradFill.Color = foreground;
            _capOutline.Color = _gradOutline.Color = background;
            return;
        }
        SetGradient(_coral, light ? Rgb(255, 112, 121) : Rgb(255, 77, 77),
            light ? Rgb(234, 76, 89) : Rgb(153, 27, 27));
        _antenna.Color = light ? Rgb(239, 75, 88) : Rgb(255, 77, 77);
        _eyes.Color = Rgb(5, 8, 16);
        _glow.Color = Rgb(0, 229, 204);
        _amber.Color = Rgb(242, 168, 51);
        SetGradient(_hatFill, Rgb(255, 214, 89), _amber.Color);
        _hatOutline.Color = Color.FromArgb(179, 184, 115, 31);
        _sweatFill.Color = Rgb(128, 212, 255);
        _blushFill.Color = Rgb(255, 158, 173);
        _heartFill.Color = Rgb(255, 115, 140);
        _capBlue.Color = Rgb(168, 199, 232);
        _bandBlue.Color = Rgb(120, 163, 207);
        _capOutline.Color = Color.FromArgb(179, 72, 108, 146);
        _gradFill.Color = Rgb(28, 31, 38);
        _gradOutline.Color = Color.FromArgb(191, 0, 0, 0);
    }

    public void Render(OnboardingMascotPose pose)
    {
        _bodyTransform.ScaleY = pose.BodyStretch;
        _bodyTransform.ScaleX = Math.Clamp(1 + (1 - pose.BodyStretch) * 0.5, 0.97, 1.03);
        _bodyTilt.Angle = pose.BodyTilt;
        _float.Y = pose.FloatOffset;
        _leftClaw.Angle = pose.LeftClawDegrees;
        _rightClaw.Angle = pose.RightClawDegrees;
        _leftAntenna.Angle = _rightAntenna.Angle = pose.AntennaDegrees * (1 - pose.AntennaDroop);
        _leftDroop.Angle = -40 * pose.AntennaDroop;
        _rightDroop.Angle = 40 * pose.AntennaDroop;
        _hat.Opacity = pose.HardHat > 0.01 ? pose.HardHat : 0;
        _hatTransform.TranslateY = -14 * (1 - pose.HardHat);
        var accessoryAmount = pose.HardHat > 0.01 || pose.AccessoryAmount <= 0.01 ? 0 : pose.AccessoryAmount;
        _nightcap.Opacity = pose.Accessory == OnboardingMascotAccessory.Nightcap ? accessoryAmount : 0;
        _gradCap.Opacity = pose.Accessory == OnboardingMascotAccessory.GradCap ? accessoryAmount : 0;
        _accessoryTransform.Y = -14 * (1 - accessoryAmount);
        var angle = (26.565 + pose.BodyTilt * 1.5) * Math.PI / 180;
        var tasselX = 60 + Math.Cos(angle) * 13.416;
        var tasselY = 8 + Math.Sin(angle) * 13.416;
        _tasselEnd.Point = new(tasselX, tasselY);
        _tasselBob.X = tasselX;
        _tasselBob.Y = tasselY;
        _blush.Opacity = pose.Blush > 0.02 ? pose.Blush * 0.55 : 0;
        RenderEye(_leftEye, pose.LeftEyeOpenness, pose);
        RenderEye(_rightEye, pose.RightEyeOpenness, pose);
        var open = pose.MouthOpen > 0.05;
        var round = pose.MouthRound > 0.05;
        _mouth.Opacity = !round && (open || Math.Abs(pose.MouthCurve) > 0.05) ? 1 : 0;
        _roundMouth.Opacity = round ? 1 : 0;
        _roundMouthTransform.ScaleX = 1 + 3.2 * pose.MouthRound;
        _roundMouthTransform.ScaleY = 1 + 4.2 * pose.MouthRound;
        _mouthFigure.StartPoint = new(52.5, open ? 48.5 : 49);
        _mouthFigure.IsClosed = open;
        _mouthCurve.Point1 = new(60, open ? 48.5 + 14 * pose.MouthOpen : 49 + 8 * pose.MouthCurve);
        _mouthCurve.Point2 = new(67.5, open ? 48.5 : 49);
        _mouth.Fill = open ? _eyes : null;
        _mouth.StrokeThickness = open ? 0 : 2.2;
        for (var i = 0; i < _particles.Length; i++)
        {
            var particle = OnboardingMascotParticles.Sample(pose, i);
            _particles[i].Opacity = pose.Effect == OnboardingMascotEffect.Sparks ? particle.Opacity :
                pose.Effect == OnboardingMascotEffect.Sparkles && particle.Opacity > 0.05 ? particle.Opacity : 0;
            _hearts[i].Opacity = pose.Effect == OnboardingMascotEffect.Hearts && particle.Opacity > 0.05
                ? particle.Opacity : 0;
            _particles[i].Fill = particle.Accent ? _glow : pose.Effect == OnboardingMascotEffect.Sparks ? _amber : _antenna;
            _particleTransforms[i].ScaleX = _particleTransforms[i].ScaleY = particle.Size;
            _particleTransforms[i].TranslateX = particle.X;
            _particleTransforms[i].TranslateY = particle.Y;
        }
        for (var i = 0; i < _sleepLetters.Length; i++)
        {
            var particle = OnboardingMascotParticles.Sample(pose, i);
            _sleepLetters[i].Opacity = pose.Effect == OnboardingMascotEffect.Zzz && particle.Opacity > 0.045 ? particle.Opacity : 0;
            var scale = particle.Size / 10;
            _sleepTransforms[i].ScaleX = _sleepTransforms[i].ScaleY = scale;
            _sleepTransforms[i].TranslateX = particle.X - _sleepLetters[i].ActualWidth * scale / 2;
            _sleepTransforms[i].TranslateY = particle.Y - _sleepLetters[i].ActualHeight * scale / 2;
        }
        var sweatOpacity = pose.Effect == OnboardingMascotEffect.Sweat ? OnboardingMascotAnimator.Bell(pose.EffectPhase) : 0;
        _sweat.Opacity = sweatOpacity > 0.02 ? sweatOpacity : 0;
        _sweatTransform.Y = 7 * pose.EffectPhase;
    }

    private Eye AddEye(double x)
    {
        var layer = Layer();
        var gaze = new TranslateTransform();
        layer.RenderTransform = gaze;
        var ellipse = new Ellipse { Width = 12, Height = 12, Fill = _eyes };
        Canvas.SetLeft(ellipse, x - 6);
        Canvas.SetTop(ellipse, 29);
        var lid = new CompositeTransform();
        ellipse.RenderTransform = lid;
        var arc = Stroke(Figure(x - 6, 37, false, Quad(x, 29.5, x + 6, 37)), _eyes, 2.6);
        var glow = new Ellipse { Width = 4, Height = 4, Fill = _glow };
        Canvas.SetLeft(glow, x - 1);
        Canvas.SetTop(glow, 32);
        var glowTransform = new CompositeTransform { CenterX = 2, CenterY = 2 };
        glow.RenderTransform = glowTransform;
        layer.Children.Add(ellipse);
        layer.Children.Add(arc);
        layer.Children.Add(glow);
        var dizzy = EllipseAt(x - 1.8, 33.2, 3.6, 3.6, _glow);
        var dizzyTransform = new TranslateTransform();
        dizzy.RenderTransform = dizzyTransform;
        layer.Children.Add(dizzy);
        _body.Children.Add(layer);
        return new(ellipse, arc, glow, gaze, lid, glowTransform, dizzy, dizzyTransform, x > 60);
    }

    private static void RenderEye(Eye eye, double openness, OnboardingMascotPose pose)
    {
        eye.Gaze.X = pose.Gaze.X * 2;
        eye.Gaze.Y = pose.Gaze.Y * 1.5;
        var height = Math.Max(1.2, 12 * openness * (1 - 0.6 * pose.HappyEyes));
        eye.Lid.ScaleY = height / 12;
        eye.Lid.TranslateY = (12 - height) * 0.65;
        eye.Ellipse.Opacity = 1 - pose.HappyEyes;
        eye.Arc.Opacity = pose.HappyEyes;
        var glowOpacity = pose.EyeGlowOpacity * openness * (1 - pose.HappyEyes) * (1 - pose.Dizzy);
        eye.Glow.Opacity = glowOpacity > 0.01 ? glowOpacity : 0;
        eye.GlowTransform.ScaleX = eye.GlowTransform.ScaleY = pose.GlowScale;
        eye.GlowTransform.TranslateX = pose.Gaze.X * 1.2;
        eye.GlowTransform.TranslateY = pose.Gaze.Y * 0.9;
        eye.Dizzy.Opacity = pose.Dizzy;
        var angle = pose.DizzyPhase * 2 * Math.PI + (eye.Right ? Math.PI : 0);
        eye.DizzyTransform.X = Math.Cos(angle) * 3.4;
        eye.DizzyTransform.Y = Math.Sin(angle) * 2.6;
    }

    private sealed record Eye(Ellipse Ellipse, NativePath Arc, Ellipse Glow, TranslateTransform Gaze,
        CompositeTransform Lid, CompositeTransform GlowTransform, Ellipse Dizzy, TranslateTransform DizzyTransform, bool Right);

    private void AddAccessories()
    {
        _nightcap.RenderTransform = _gradCap.RenderTransform = _accessoryTransform;
        _nightcap.Children.Add(Outlined(Figure(47, 14, true,
            Curve(53, 1, 70, 0, 86, 8), Curve(82, 11, 77, 13, 72, 14)), _capBlue, _capOutline));
        var brim = new Rectangle
        {
            Width = 32, Height = 5, RadiusX = 2, RadiusY = 2,
            Fill = _bandBlue, Stroke = _capOutline, StrokeThickness = 0.8,
        };
        Canvas.SetLeft(brim, 44);
        Canvas.SetTop(brim, 12);
        _nightcap.Children.Add(brim);
        var pompom = EllipseAt(83, 5, 6, 6, _capBlue);
        pompom.Stroke = _capOutline;
        pompom.StrokeThickness = 0.8;
        _nightcap.Children.Add(pompom);
        _gradCap.Children.Add(Outlined(Figure(50, 8, true, Line(70, 8), Line(68, 15), Line(52, 15)), _gradFill, _gradOutline));
        _gradCap.Children.Add(Outlined(Figure(60, 3, true, Line(77, 8), Line(60, 13), Line(43, 8)), _gradFill, _gradOutline));
        _gradCap.Children.Add(Stroke(Figure(60, 8, false, _tasselEnd), _amber, 1.2));
        _gradCap.Children.Add(EllipseAt(58.5, 6.5, 3, 3, _amber));
        var bob = EllipseAt(-2, -2, 4, 4, _amber);
        bob.RenderTransform = _tasselBob;
        _gradCap.Children.Add(bob);
        _body.Children.Add(_nightcap);
        _body.Children.Add(_gradCap);
    }

    private static NativePath Outlined(PathFigure figure, Brush fill, Brush outline)
    {
        var path = Shape(figure, fill);
        path.Stroke = outline;
        path.StrokeThickness = 0.8;
        return path;
    }

    private static Ellipse EllipseAt(double x, double y, double width, double height, Brush fill)
    {
        var ellipse = new Ellipse { Width = width, Height = height, Fill = fill };
        Canvas.SetLeft(ellipse, x);
        Canvas.SetTop(ellipse, y);
        return ellipse;
    }

    private static Canvas Layer() => new() { Width = 120, Height = 120 };
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    private static LinearGradientBrush Gradient() => new()
    {
        StartPoint = new(0, 0), EndPoint = new(1, 1),
        GradientStops = { new() { Offset = 0 }, new() { Offset = 1 } },
    };
    private static void SetGradient(LinearGradientBrush brush, Color top, Color bottom)
    {
        brush.GradientStops[0].Color = top;
        brush.GradientStops[1].Color = bottom;
    }
    private static TransformGroup Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms)
            group.Children.Add(transform);
        return group;
    }
    private static NativePath Shape(PathFigure figure, Brush fill, Transform? transform = null) => new()
    {
        Width = 120, Height = 120, Stretch = Stretch.None,
        Data = new PathGeometry { Figures = { figure } }, Fill = fill, RenderTransform = transform,
    };
    private static NativePath Stroke(PathFigure figure, Brush brush, double width, Transform? transform = null)
    {
        var shape = Shape(figure, brush, transform);
        shape.Fill = null;
        shape.Stroke = brush;
        shape.StrokeThickness = width;
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        return shape;
    }
    private static PathFigure Figure(double x, double y, bool closed, params PathSegment[] segments)
    {
        var figure = new PathFigure { StartPoint = new(x, y), IsClosed = closed };
        foreach (var segment in segments)
            figure.Segments.Add(segment);
        return figure;
    }
    private static LineSegment Line(double x, double y) => new() { Point = new(x, y) };
    private static QuadraticBezierSegment Quad(double x, double y, double endX, double endY) =>
        new() { Point1 = new(x, y), Point2 = new(endX, endY) };
    private static BezierSegment Curve(double x1, double y1, double x2, double y2, double x, double y) =>
        new() { Point1 = new(x1, y1), Point2 = new(x2, y2), Point3 = new(x, y) };
}
