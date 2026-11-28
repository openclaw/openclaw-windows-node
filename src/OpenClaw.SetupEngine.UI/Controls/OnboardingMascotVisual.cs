// Native character geometry and Composition bindings. The player supplies Progress.
// Call Configure on clip changes, SetPalette on theme changes and SetGaze on pointer input.
// MIT OpenClaw artwork attribution: Assets/Setup/Mascot-NOTICE.txt.
using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Windows.UI.ViewManagement;
using static OpenClaw.SetupEngine.OnboardingMascotMotion;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>One retained native character per player, with no generated assets or frame callbacks.</summary>
internal sealed class OnboardingMascotVisual(bool light, bool highContrast) : IAnimatedVisualSource
{
    private NativeVisual? _visual;
    private OnboardingMascotMood _mood;
    private OnboardingMascotClip _clip;
    private bool _still = true, _hardHat;
    private OnboardingMascotAccessory _accessory;
    private double _phase;
    private Vector2? _gaze;

    public IAnimatedVisual TryCreateAnimatedVisual(Compositor compositor, out object diagnostics)
    {
        diagnostics = null!;
        _visual = new NativeVisual(compositor);
        _visual.SetPalette(light, highContrast);
        _visual.Configure(_mood, _clip, _still, _phase);
        _visual.SetHeadwear(_hardHat, _accessory, animate: false);
        _visual.SetGaze(_gaze, animate: false);
        return _visual;
    }

    public void Configure(OnboardingMascotMood mood, OnboardingMascotClip clip, bool still, double phase)
    {
        if (!Enum.IsDefined(mood)) throw new ArgumentOutOfRangeException(nameof(mood));
        _ = OnboardingMascotMotion.Duration(clip);
        if (!double.IsFinite(phase) || phase < 0) throw new ArgumentOutOfRangeException(nameof(phase));
        _mood = mood; _clip = clip; _still = still; _phase = phase;
        _visual?.Configure(mood, clip, still, phase);
    }

    public void SetPalette(bool isLight, bool isHighContrast)
    {
        light = isLight; highContrast = isHighContrast;
        _visual?.SetPalette(light, highContrast);
    }

    public void SetHeadwear(bool hardHat, OnboardingMascotAccessory accessory, bool animate)
    {
        if (!Enum.IsDefined(accessory)) throw new ArgumentOutOfRangeException(nameof(accessory));
        _hardHat = hardHat; _accessory = accessory;
        _visual?.SetHeadwear(hardHat, accessory, animate);
    }

    public void SetGaze(Vector2? direction, bool animate)
    {
        if (direction is { } value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) throw new ArgumentOutOfRangeException(nameof(direction));
            direction = Vector2.Clamp(value, -Vector2.One, Vector2.One);
        }
        _gaze = direction;
        _visual?.SetGaze(direction, animate);
    }

    private sealed class NativeVisual : IAnimatedVisual
    {
        private readonly Compositor _c;
        private readonly List<CompositionObject> _owned = [];
        private readonly HashSet<(CompositionObject Target, string Property)> _bindings = [];
        private readonly ContainerVisual _root, _body;
        private readonly CompositionPropertySet _motion, _inputs;
        private readonly Dictionary<string, CompositionColorBrush> _brushes = [];
        private readonly CompositionColorGradientStop _coralTop, _coralBottom;
        private readonly CompositionLinearGradientBrush _coral;
        private bool _disposed;
        private OnboardingMascotAccessory _accessory;
        public Visual RootVisual => _root;
        public TimeSpan Duration => TimeSpan.FromSeconds(TimelineSeconds);
        public Vector2 Size => new(168);

        public NativeVisual(Compositor compositor)
        {
            _c = compositor;
            _root = Group();
            _root.Size = Size;
            _root.Properties.InsertScalar("Progress", 0);
            _motion = Own(_c.CreatePropertySet());
            _inputs = Own(_c.CreatePropertySet());
            _inputs.InsertVector2("Gaze", Vector2.Zero);
            _inputs.InsertScalar("Pointer", 0);
            _inputs.InsertScalar("HardHat", 0);
            _inputs.InsertScalar("Accessory", 0);
            _inputs.InsertScalar("AccessoryEntrance", 1);
            foreach (var name in new[] { "T", "P", "Work", "Blink", "Envelope", "PeekLeft", "PeekRight", "Effect" })
                _motion.InsertScalar(name, 0);
            foreach (var (name, expression) in Create(OnboardingMascotMood.Idle, OnboardingMascotClip.Idle, true).Channels)
                _motion.InsertScalar(name, float.Parse(expression, System.Globalization.CultureInfo.InvariantCulture));
            _coral = Own(_c.CreateLinearGradientBrush());
            _coral.MappingMode = CompositionMappingMode.Absolute;
            _coral.StartPoint = Vector2.Zero; _coral.EndPoint = new(120);
            _coralTop = Own(_c.CreateColorGradientStop()); _coralBottom = Own(_c.CreateColorGradientStop());
            _coralBottom.Offset = 1;
            _coral.ColorStops.Add(_coralTop); _coral.ColorStops.Add(_coralBottom);

            var floating = Group(_root);
            Bind(floating, "Offset", "Vector3(24,24+Clamp(m.Float,-12,2),0)");
            var tilt = Group(floating, new(60, 60, 0));
            Bind(tilt, "RotationAngleInDegrees", "Clamp(m.Tilt,-8,8)");
            _body = Group(tilt, new(60, 110, 0));
            Bind(_body, "Scale", "Vector3(Clamp(1+(1-Clamp(m.Stretch,0.86,1.05))*0.5,0.97,1.03),Clamp(m.Stretch,0.86,1.05),1)");
            Path(_body, new(60, 10), p =>
            {
                p.AddCubicBezier(new(30, 10), new(15, 35), new(15, 55));
                p.AddCubicBezier(new(15, 75), new(30, 95), new(45, 100));
                p.AddLine(new(45, 110)); p.AddLine(new(55, 110)); p.AddLine(new(55, 100));
                p.AddCubicBezier(new(55, 100), new(60, 102), new(65, 100));
                p.AddLine(new(65, 110)); p.AddLine(new(75, 110)); p.AddLine(new(75, 100));
                p.AddCubicBezier(new(90, 95), new(105, 75), new(105, 55));
                p.AddCubicBezier(new(105, 35), new(90, 10), new(60, 10));
            }, _coral);
            var left = Path(_body, new(20, 45), p =>
            {
                p.AddCubicBezier(new(5, 40), new(0, 50), new(5, 60));
                p.AddCubicBezier(new(10, 70), new(20, 65), new(25, 55));
                p.AddCubicBezier(new(28, 48), new(25, 45), new(20, 45));
            }, _coral);
            var right = Path(_body, new(100, 45), p =>
            {
                p.AddCubicBezier(new(115, 40), new(120, 50), new(115, 60));
                p.AddCubicBezier(new(110, 70), new(100, 65), new(95, 55));
                p.AddCubicBezier(new(92, 48), new(95, 45), new(100, 45));
            }, _coral);
            left.CenterPoint = new(26, 53, 0); right.CenterPoint = new(94, 53, 0);
            Bind(left, "RotationAngleInDegrees", "Clamp(m.LeftClaw,-45,45)");
            Bind(right, "RotationAngleInDegrees", "Clamp(m.RightClaw,-45,45)");
            foreach (var (x, sign) in new[] { (45, -1), (75, 1) })
            {
                var droop = Group(_body, new(x, 15, 0));
                Bind(droop, "RotationAngleInDegrees", $"{sign * 40}*Clamp(m.Droop,0,1)");
                var antenna = Path(droop, new(x, 15), p => p.AddQuadraticBezier(new(x + sign * 10, 5), new(x + sign * 15, 8)), Brush("Coral"), stroke: 2);
                antenna.CenterPoint = new(x + sign * 7.5f, 11, 0);
                Bind(antenna, "RotationAngleInDegrees", "Clamp(m.Antenna,-14,14)*(1-Clamp(m.Droop,0,1))");
            }
            AddHeadwear();
            foreach (var x in new[] { 37f, 83f })
                Bind(Ellipse(_body, new(x, 45), new(4.5f, 2.5f), Brush("Blush")), "Opacity", "Clamp(m.Blush,0,1)*0.55");
            AddEyes();
            AddMouth();
            AddParticles();
        }

        public void Configure(OnboardingMascotMood mood, OnboardingMascotClip clip, bool still, double phase)
        {
            if (_disposed) return;
            var definition = Create(mood, clip, still);
            Bind(_motion, "T", still ? "0" : $"clock.Progress*{TimelineSeconds}+{Number(phase)}");
            Bind(_motion, "P", still ? "0" : $"Clamp(clock.Progress*{TimelineSeconds}/{Number(OnboardingMascotMotion.Duration(clip))},0,1)");
            Bind(_motion, "Work", "Mod(m.T/0.95,1)");
            Bind(_motion, "Blink", still ? "0" : Bell("(Mod(m.T,3.8)-2.5)/0.16"));
            var (attack, release) = Envelope(clip);
            Bind(_motion, "Envelope", Plateau("m.P", attack, release));
            Bind(_motion, "PeekLeft", Plateau("m.P/0.5", .3, .7));
            Bind(_motion, "PeekRight", Plateau("(m.P-0.5)/0.5", .3, .7));
            _motion.InsertScalar("Effect", (float)definition.Effect);
            foreach (var (name, expression) in definition.Channels) Bind(_motion, name, expression);
        }

        public void SetPalette(bool light, bool highContrast)
        {
            if (_disposed) return;
            var settings = new UISettings();
            var foreground = settings.UIElementColor(UIElementType.WindowText);
            var background = settings.UIElementColor(UIElementType.Window);
            var highlight = settings.UIElementColor(UIElementType.Highlight);
            // These are the original brand artwork colors, not UI preview swatches.
            _coralTop.Color = highContrast ? foreground : Rgb(255, 77, 77);
            _coralBottom.Color = highContrast ? foreground : light ? Rgb(200, 40, 48) : Rgb(153, 27, 27);
            foreach (var (name, color, system) in new[]
            {
                ("Coral",Rgb(255,77,77),foreground), ("Ink",Rgb(5,8,16),background), ("Glow",Rgb(0,229,204),highlight),
                ("Blush",Rgb(255,158,173),highlight), ("Heart",Rgb(255,115,140),highlight), ("Amber",Rgb(242,168,51),foreground),
                ("Hat",Rgb(255,214,89),foreground), ("HatOutline",Color.FromArgb(179,184,115,31),background),
                ("Nightcap",Rgb(168,199,232),foreground), ("Band",Rgb(120,163,207),foreground),
                ("CapOutline",Color.FromArgb(179,72,108,146),background),
                ("GradCap",Rgb(28,31,38),foreground), ("GradOutline",Color.FromArgb(191,0,0,0),background),
                ("Sweat",Rgb(128,212,255),highlight),
            }) Brush(name).Color = highContrast ? system : color;
        }

        public void SetHeadwear(bool hardHat, OnboardingMascotAccessory accessory, bool animate)
        {
            if (_disposed) return;
            _inputs.InsertScalar("HardHat", hardHat ? 1 : 0);
            _inputs.InsertScalar("Accessory", (float)accessory);
            if (animate && accessory != _accessory && accessory != OnboardingMascotAccessory.None)
            {
                using var entrance = _c.CreateScalarKeyFrameAnimation();
                using var easing = _c.CreateCubicBezierEasingFunction(new(.215f, .61f), new(.355f, 1));
                entrance.Duration = TimeSpan.FromSeconds(.5);
                entrance.InsertKeyFrame(0, 0);
                entrance.InsertKeyFrame(1, 1, easing);
                _inputs.StartAnimation("AccessoryEntrance", entrance);
                _bindings.Add((_inputs, "AccessoryEntrance"));
            }
            else if (!animate) _inputs.InsertScalar("AccessoryEntrance", 1);
            _accessory = accessory;
        }

        public void SetGaze(Vector2? gaze, bool animate)
        {
            if (_disposed) return;
            _inputs.InsertScalar("Pointer", gaze.HasValue ? 1 : 0);
            var direction = gaze ?? Vector2.Zero;
            if (!animate) { _inputs.InsertVector2("Gaze", direction); return; }
            using var movement = _c.CreateVector2KeyFrameAnimation();
            movement.Duration = TimeSpan.FromMilliseconds(160);
            movement.InsertExpressionKeyFrame(0, "this.StartingValue");
            movement.InsertKeyFrame(1, direction);
            _inputs.StartAnimation("Gaze", movement);
            _bindings.Add((_inputs, "Gaze"));
        }

        private void AddEyes()
        {
            var gaze = Group(_body);
            Bind(gaze, "Offset", "Vector3((input.Pointer>0 ? input.Gaze.X : m.GazeX)*2,(input.Pointer>0 ? input.Gaze.Y : m.GazeY)*1.5,0)");
            foreach (var (x, side) in new[] { (45, "LeftEye"), (75, "RightEye") })
            {
                var eye = Ellipse(gaze, new(x, 35), new(6), Brush("Ink"));
                eye.CenterPoint = new(x, 36.8f, 0);
                Bind(eye, "Scale.Y", $"Max(0.1,Clamp(m.{side},0,1)*(1-0.6*Clamp(m.HappyEyes,0,1)))");
                Bind(eye, "Opacity", "1-Clamp(m.HappyEyes,0,1)");
                var happy = Path(gaze, new(x - 6, 37), p => p.AddQuadraticBezier(new(x, 29.5f), new(x + 6, 37)), Brush("Ink"), stroke: 2.6f);
                Bind(happy, "Opacity", "Clamp(m.HappyEyes,0,1)");
                var pupil = Ellipse(gaze, new(x + 1, 34), new(2), Brush("Glow"));
                pupil.CenterPoint = new(x + 1, 34, 0);
                Bind(pupil, "Opacity", $"Clamp(m.EyeGlow,0,1)*Clamp(m.{side},0,1)*(1-Clamp(m.HappyEyes,0,1))*(1-Clamp(m.Dizzy,0,1))");
                Bind(pupil, "Scale", "Vector3(m.GlowScale,m.GlowScale,1)");
                var dizzy = Ellipse(gaze, new(x, 35), new(1.8f), Brush("Glow"));
                Bind(dizzy, "Opacity", "Clamp(m.Dizzy,0,1)");
                Bind(dizzy, "Offset", $"Vector3(3.4*Cos(m.T/0.55*2*Pi+{(x == 75 ? "Pi" : "0")}),2.6*Sin(m.T/0.55*2*Pi+{(x == 75 ? "Pi" : "0")}),0)");
            }
        }

        private void AddMouth()
        {
            var smile = Path(_body, new(52.5f, 49), p => p.AddQuadraticBezier(new(60, 49), new(67.5f, 49)), Brush("Ink"), stroke: 2.2f);
            Morph(smile, "Clamp((m.Smile+1)/2,0,1)", v => Geometry(new(52.5f, 49),
                p => p.AddQuadraticBezier(new(60, 49 + 8 * (v * 2 - 1)), new(67.5f, 49)), false));
            Bind(smile, "Opacity", "((m.Round>0.05) || (m.Open>0.05) || (Abs(m.Smile)<=0.05)) ? 0 : 1");
            var open = Path(_body, new(52.5f, 48.5f), p => p.AddQuadraticBezier(new(60, 48.5f), new(67.5f, 48.5f)), Brush("Ink"));
            Morph(open, "Clamp(m.Open,0,1)", v => Geometry(new(52.5f, 48.5f),
                p => p.AddQuadraticBezier(new(60, 48.5f + 14 * v), new(67.5f, 48.5f)), true));
            Bind(open, "Opacity", "((m.Round<=0.05) && (m.Open>0.05)) ? 1 : 0");
            var round = Ellipse(_body, new(60, 51), Vector2.One, Brush("Ink"));
            round.CenterPoint = new(60, 51, 0);
            Bind(round, "Scale", "Vector3(1+3.2*Clamp(m.Round,0,1),1+4.2*Clamp(m.Round,0,1),1)");
            Bind(round, "Opacity", "m.Round>0.05 ? 1 : 0");
        }

        private void AddHeadwear()
        {
            var hat = Group(_body);
            hat.CenterPoint = new(60, 15, 0); hat.RotationAngleInDegrees = -5;
            Bind(hat, "Opacity", "input.HardHat*Clamp(m.Hat,0,1)");
            Bind(hat, "Offset", "Vector3(0,-14*(1-Clamp(m.Hat,0,1)),0)");
            Path(hat, new(45, 15), p => { p.AddCubicBezier(new(47, 7), new(54, 3), new(60, 3)); p.AddCubicBezier(new(66, 3), new(73, 7), new(75, 15)); }, Brush("Hat"), outline: Brush("HatOutline"));
            Rectangle(hat, new(41, 14), new(38, 5), Brush("Amber"), Brush("HatOutline"));
            var night = Group(_body); var grad = Group(_body);
            foreach (var (visual, accessory) in new[] { (night, 1), (grad, 2) })
            {
                Bind(visual, "Opacity", $"((input.HardHat>0) || (input.Accessory!={accessory})) ? 0 : input.AccessoryEntrance");
                Bind(visual, "Offset", "Vector3(0,-14*(1-input.AccessoryEntrance),0)");
            }
            Path(night, new(47, 14), p => { p.AddCubicBezier(new(53, 1), new(70, 0), new(86, 8)); p.AddCubicBezier(new(82, 11), new(77, 13), new(72, 14)); }, Brush("Nightcap"), outline: Brush("CapOutline"));
            Rectangle(night, new(44, 12), new(32, 5), Brush("Band"), Brush("CapOutline"));
            Ellipse(night, new(86, 8), new(3), Brush("Nightcap"), Brush("CapOutline"));
            Path(grad, new(50, 8), p => { p.AddLine(new(70, 8)); p.AddLine(new(68, 15)); p.AddLine(new(52, 15)); }, Brush("GradCap"), outline: Brush("GradOutline"));
            Path(grad, new(60, 3), p => { p.AddLine(new(77, 8)); p.AddLine(new(60, 13)); p.AddLine(new(43, 8)); }, Brush("GradCap"), outline: Brush("GradOutline"));
            var tassel = Group(grad, new(60, 8, 0));
            Bind(tassel, "RotationAngleInDegrees", "m.Tilt*1.5");
            Path(tassel, new(60, 8), p => p.AddLine(new(72, 14)), Brush("Amber"), stroke: 1.2f);
            Ellipse(tassel, new(72, 14), new(2), Brush("Amber"));
            Ellipse(grad, new(60, 8), new(1.5f), Brush("Amber"));
        }

        private void AddParticles()
        {
            for (var index = 0; index < 6; index++)
            {
                var angle = Math.PI + Math.PI * (index + .5) / 6;
                var sparkle = Star(index % 2 == 0 ? Brush("Glow") : Brush("Coral"));
                var phase = $"Mod(m.EffectPhase+{Number(index * .37)},1)";
                Particle(sparkle, OnboardingMascotEffect.Sparkles, Number(60 + Math.Cos(angle) * (50 + index % 3 * 4)),
                    Number(55 + Math.Sin(angle) * (40 + index * 5 % 3 * 4)), $"2.5+2*{Bell(phase)}", Bell(phase));
                if (index < 3)
                {
                    var heart = Path(_body, new(0, .75f), p => { p.AddLine(new(-.7f, -.05f)); p.AddCubicBezier(new(-1, -.8f), new(-.25f, -1), new(0, -.35f)); p.AddCubicBezier(new(.25f, -1), new(1, -.8f), new(.7f, -.05f)); }, Brush("Heart"));
                    phase = $"Mod(m.EffectPhase*1.15+{Number(index / 3d)},1)";
                    Particle(heart, OnboardingMascotEffect.Hearts, $"{60 + (index - 1) * 26}+4*Sin(({phase})*2*Pi+{index})",
                        $"30-28*({phase})", Number(4.5 + index % 2 * 1.5), $"Min(({phase})/0.15,(1-({phase}))/0.85)");
                    var z = Path(_body, new(-.2f, -.3f), p => { p.AddLine(new(.2f, -.3f)); p.AddLine(new(-.2f, .3f)); p.AddLine(new(.2f, .3f)); }, Brush("Glow"), stroke: .12f);
                    phase = $"Mod(m.EffectPhase+{Number(index * .33)},1)";
                    Particle(z, OnboardingMascotEffect.Zzz, $"86+14*({phase})+2*Sin(({phase})*4*Pi)",
                        $"24-20*({phase})", $"6+4*({phase})", $"0.9*Min(({phase})/0.2,(1-({phase}))/0.8)");
                }
                if (index < 5)
                {
                    var theta = (-160 + index * 35) * Math.PI / 180;
                    phase = $"(m.EffectPhase-{Number(index * .025)})";
                    var radius = $"(5+12*{phase}/0.45)";
                    var size = Number(2.2 + index % 3 * .8);
                    Particle(Star(index % 2 == 0 ? Brush("Glow") : Brush("Amber")), OnboardingMascotEffect.Sparks,
                        $"Clamp(106+{Number(Math.Cos(theta))}*{radius},{size},120-{size})",
                        $"Clamp(66+{Number(Math.Sin(theta))}*{radius},{size},120-{size})", size,
                        $"(({phase}<0) || ({phase}>=0.45)) ? 0 : Min({phase}/0.08,(0.45-{phase})/0.37)");
                }
            }
            var sweat = Path(_body, new(42, 21), p => { p.AddCubicBezier(new(38, 25), new(40, 27), new(42, 27)); p.AddCubicBezier(new(44, 27), new(46, 25), new(42, 21)); }, Brush("Sweat"));
            Bind(sweat, "Opacity", $"m.Effect=={(int)OnboardingMascotEffect.Sweat} ? {Bell("m.EffectPhase")} : 0");
            Bind(sweat, "Offset", "Vector3(0,7*m.EffectPhase,0)");
        }

        private ShapeVisual Star(CompositionBrush brush) => Path(_body, new(0, -1), p =>
        {
            p.AddQuadraticBezier(Vector2.Zero, new(1, 0)); p.AddQuadraticBezier(Vector2.Zero, new(0, 1));
            p.AddQuadraticBezier(Vector2.Zero, new(-1, 0)); p.AddQuadraticBezier(Vector2.Zero, new(0, -1));
        }, brush);

        private void Particle(Visual visual, OnboardingMascotEffect effect, string x, string y, string size, string opacity)
        {
            Bind(visual, "Offset", $"Vector3({x},{y},0)");
            Bind(visual, "Scale", $"Vector3({size},{size},1)");
            Bind(visual, "Opacity", $"m.Effect=={(int)effect} ? Clamp(({opacity}),0,1) : 0");
        }
        private T Own<T>(T value) where T : CompositionObject { _owned.Add(value); return value; }
        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
        private CompositionColorBrush Brush(string name)
        {
            if (!_brushes.TryGetValue(name, out var brush)) _brushes[name] = brush = Own(_c.CreateColorBrush());
            return brush;
        }
        private ContainerVisual Group(ContainerVisual? parent = null, Vector3 center = default)
        {
            var group = Own(_c.CreateContainerVisual()); group.CenterPoint = center;
            parent?.Children.InsertAtTop(group); return group;
        }
        private void Bind(CompositionObject target, string property, string expression)
        {
            using var animation = _c.CreateExpressionAnimation(expression);
            animation.SetReferenceParameter("clock", _root.Properties);
            animation.SetReferenceParameter("m", _motion);
            animation.SetReferenceParameter("input", _inputs);
            target.StartAnimation(property, animation); _bindings.Add((target, property));
        }
        private CompositionPath Geometry(Vector2 start, Action<CanvasPathBuilder> draw, bool closed)
        {
            using var builder = new CanvasPathBuilder(null);
            builder.BeginFigure(start); draw(builder);
            builder.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(builder);
            return new CompositionPath(geometry);
        }
        private ShapeVisual Path(ContainerVisual parent, Vector2 start, Action<CanvasPathBuilder> draw, CompositionBrush brush, float stroke = 0, CompositionBrush? outline = null) =>
            Shape(parent, Own(_c.CreatePathGeometry(Geometry(start, draw, stroke == 0))), brush, stroke, outline);
        private ShapeVisual Ellipse(ContainerVisual parent, Vector2 center, Vector2 radius, CompositionBrush brush, CompositionBrush? outline = null)
        {
            var geometry = Own(_c.CreateEllipseGeometry()); geometry.Center = center; geometry.Radius = radius;
            return Shape(parent, geometry, brush, 0, outline);
        }
        private void Rectangle(ContainerVisual parent, Vector2 offset, Vector2 size, CompositionBrush brush, CompositionBrush outline)
        {
            var geometry = Own(_c.CreateRoundedRectangleGeometry()); geometry.Offset = offset; geometry.Size = size; geometry.CornerRadius = new(2);
            Shape(parent, geometry, brush, 0, outline);
        }
        private ShapeVisual Shape(ContainerVisual parent, CompositionGeometry geometry, CompositionBrush brush, float stroke, CompositionBrush? outline)
        {
            var shape = Own(_c.CreateSpriteShape(geometry));
            if (stroke == 0) shape.FillBrush = brush;
            shape.StrokeBrush = stroke > 0 ? brush : outline; shape.StrokeThickness = stroke > 0 ? stroke : .8f;
            shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
            var visual = Own(_c.CreateShapeVisual()); visual.Size = Size; visual.Shapes.Add(shape);
            parent.Children.InsertAtTop(visual); return visual;
        }
        private void Morph(ShapeVisual visual, string progress, Func<float, CompositionPath> path)
        {
            var geometry = ((CompositionSpriteShape)visual.Shapes[0]).Geometry;
            using var animation = _c.CreatePathKeyFrameAnimation(); animation.Duration = Duration;
            animation.InsertKeyFrame(0, path(0)); animation.InsertKeyFrame(1, path(1), _c.CreateLinearEasingFunction());
            geometry.StartAnimation("Path", animation); _bindings.Add((geometry, "Path"));
            var controller = Own(geometry.TryGetAnimationController("Path")); controller.Pause();
            Bind(controller, "Progress", progress);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (target, property) in _bindings) target.StopAnimation(property);
            _root.Children.RemoveAll();
            for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        }
    }
}
