// WinUI renderer for a composed lobster: turns a LobsterArtwork paint tree into a 120x105 Canvas of Path shapes
// (geometry built from the parsed segments, not XAML path markup) and exposes the animatable role elements, each
// wrapped in a Canvas whose RenderTransform is a CompositeTransform centred on the upstream CSS transform-origin.
// Brushes are created once per build and shared between shapes; nothing is allocated per frame.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-pet-artwork.tsx and
// ui/src/styles/lobster-pet.css (MIT; see THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var art = LobsterArtwork.Compose(look, grumpy, offline, DateTimeOffset.Now, accent, accentHover);
//   var canvas = LobsterArtworkCanvas.Build(art);
//   viewbox.Child = canvas.Root;                                  // 120x105, scale it with a Viewbox
//   var claw = canvas.Element(LobsterArtRole.ClawRight);            // animate RenderTransform / Opacity
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinPath = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>A built lobster canvas and its animatable role elements.</summary>
internal sealed class LobsterArtworkCanvas
{
    private readonly Dictionary<LobsterArtRole, FrameworkElement> _roles;

    private LobsterArtworkCanvas(Canvas root, FrameworkElement svg, Dictionary<LobsterArtRole, FrameworkElement> roles)
    {
        Root = root;
        Svg = svg;
        _roles = roles;
    }

    /// <summary>The 120x105 viewBox canvas. Content may overflow it, as upstream's <c>overflow: visible</c>.</summary>
    internal Canvas Root { get; }

    /// <summary>The artwork root (<c>.lobster-pet__svg</c>), origin 50% 100%; target of breathe.</summary>
    internal FrameworkElement Svg { get; }

    /// <summary>
    /// The first element with <paramref name="role"/> (antennae, claws, open/closed eyes), or null when the palette's
    /// geometry has none (pixel and portal art). Its RenderTransform is a CompositeTransform centred on the role origin.
    /// </summary>
    internal FrameworkElement? Element(LobsterArtRole role) =>
        role == LobsterArtRole.Root ? Svg : _roles.GetValueOrDefault(role);

    internal static (string Accent, string AccentHover) ReadAccentColors()
    {
        var resources = Application.Current.Resources;
        return (Read("SystemAccentColor", "#7f77dd"), Read("SystemAccentColorLight1", "#9a93e8"));

        string Read(string key, string fallback) =>
            resources.TryGetValue(key, out var value) && value is global::Windows.UI.Color c
                ? $"#{c.R:x2}{c.G:x2}{c.B:x2}"
                : fallback;
    }

    /// <summary>Builds the WinUI tree for <paramref name="artwork"/>. Call on the UI thread.</summary>
    internal static LobsterArtworkCanvas Build(LobsterArtwork artwork)
    {
        var builder = new Builder();
        var svg = builder.Node(artwork.Root);
        Prepare(svg, LobsterArtwork.RootOrigin);
        var root = new Canvas
        {
            Width = LobsterArtwork.ViewBoxWidth,
            Height = LobsterArtwork.ViewBoxHeight,
            IsHitTestVisible = false,
        };
        root.Children.Add(svg);
        return new LobsterArtworkCanvas(root, svg, builder.Roles);
    }

    /// <summary>
    /// Builds a standalone effect sprite (<see cref="LobsterArtwork.Sprite"/>) on a <paramref name="width"/> x
    /// <paramref name="height"/> viewBox canvas; scale it with a Viewbox. Call on the UI thread.
    /// </summary>
    internal static Canvas BuildSprite(LobsterArtNode sprite, double width, double height)
    {
        var root = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        root.Children.Add(new Builder().Node(sprite));
        return root;
    }

    private static void Prepare(FrameworkElement element, (double X, double Y) origin)
    {
        if (element.RenderTransform is not CompositeTransform)
            element.RenderTransform = new CompositeTransform { CenterX = origin.X, CenterY = origin.Y };
    }

    private sealed class Builder
    {
        private readonly Dictionary<LobsterRgba, SolidColorBrush> _brushes = [];

        internal Dictionary<LobsterArtRole, FrameworkElement> Roles { get; } = [];

        internal FrameworkElement Node(LobsterArtNode node)
        {
            FrameworkElement content;
            if (node.Element == LobsterSvgElement.Group)
            {
                var group = new Canvas();
                foreach (var child in node.Children)
                    group.Children.Add(Node(child));
                content = group;
            }
            else
            {
                content = Shape(node);
            }

            if (node.Transform != LobsterMatrix.Identity)
            {
                var m = node.Transform;
                content.RenderTransform = new MatrixTransform { Matrix = new Matrix(m.A, m.B, m.C, m.D, m.E, m.F) };
            }

            if (node.Role is LobsterArtRole.None or LobsterArtRole.Pattern or LobsterArtRole.Accessory or LobsterArtRole.Face or LobsterArtRole.Tail)
            {
                content.Opacity = node.Opacity;
                return content;
            }

            // Animatable roles: an outer wrapper owns the CompositeTransform and the opacity motion animates, so the
            // static SVG transform stays on the inner element.
            var wrapper = new Canvas { Opacity = node.Opacity };
            wrapper.Children.Add(content);
            Prepare(wrapper, node.Origin);
            Roles.TryAdd(node.Role, wrapper);
            return wrapper;
        }

        private WinPath Shape(LobsterArtNode node)
        {
            var path = new WinPath
            {
                Data = Geometry(node.Segments, node.EvenOdd),
                Fill = Brush(node.Fill),
            };
            if (node.Stroke is { } stroke && node.StrokeWidth > 0)
            {
                path.Stroke = Brush(stroke);
                path.StrokeThickness = node.StrokeWidth;
                var cap = Cap(node.LineCap);
                path.StrokeStartLineCap = cap;
                path.StrokeEndLineCap = cap;
                path.StrokeDashCap = cap;
                path.StrokeLineJoin = node.LineJoin switch
                {
                    "round" => PenLineJoin.Round,
                    "bevel" => PenLineJoin.Bevel,
                    _ => PenLineJoin.Miter,
                };
                if (node.Dash is { Count: > 0 } dash)
                {
                    // SVG dashes are user units; XAML dashes are multiples of the stroke thickness.
                    var dashes = new DoubleCollection();
                    foreach (var d in dash) dashes.Add(d / node.StrokeWidth);
                    path.StrokeDashArray = dashes;
                }
            }
            return path;
        }

        private static PenLineCap Cap(string cap) => cap switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat,
        };

        private SolidColorBrush? Brush(LobsterRgba? color)
        {
            if (color is not { } c || c.A == 0)
                return null;
            if (!_brushes.TryGetValue(c, out var brush))
            {
                brush = new SolidColorBrush(ColorHelper.FromArgb(c.A, c.R, c.G, c.B));
                _brushes[c] = brush;
            }
            return brush;
        }

        private static PathGeometry Geometry(IReadOnlyList<LobsterPathSegment> segments, bool evenOdd)
        {
            var geometry = new PathGeometry { FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
            PathFigure? figure = null;
            var start = new Point();
            foreach (var s in segments)
            {
                var to = new Point(s.X, s.Y);
                if (s.Command == LobsterPathCommand.Move)
                {
                    figure = new PathFigure { StartPoint = to, IsClosed = false, IsFilled = true };
                    geometry.Figures.Add(figure);
                    start = to;
                    continue;
                }
                if (s.Command == LobsterPathCommand.Close)
                {
                    if (figure is not null) figure.IsClosed = true;
                    figure = null;
                    continue;
                }
                if (figure is null)
                {
                    // Drawing after Z without a moveto continues from the closed subpath's start.
                    figure = new PathFigure { StartPoint = start, IsFilled = true };
                    geometry.Figures.Add(figure);
                }
                figure.Segments.Add(s.Command switch
                {
                    LobsterPathCommand.Cubic => new BezierSegment
                    {
                        Point1 = new Point(s.X1, s.Y1), Point2 = new Point(s.X2, s.Y2), Point3 = to,
                    },
                    LobsterPathCommand.Quadratic => new QuadraticBezierSegment { Point1 = new Point(s.X1, s.Y1), Point2 = to },
                    LobsterPathCommand.Arc => new ArcSegment
                    {
                        Point = to,
                        Size = new Size(Math.Abs(s.Rx), Math.Abs(s.Ry)),
                        RotationAngle = s.Rotation,
                        IsLargeArc = s.LargeArc,
                        SweepDirection = s.Sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    },
                    _ => new LineSegment { Point = to },
                });
            }
            return geometry;
        }
    }
}
