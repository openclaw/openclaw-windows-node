// A small, WinUI-free SVG subset for the vendored lobster artwork: an immutable element tree, a path-data
// parser that yields absolute segments, and transform/point-list parsing. Renderers build native geometry from
// these segments instead of XAML path markup, so every coordinate is testable.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-pet-{artwork,sprites,
// sprites-wild,sprite}.tsx and lobster-hat-sprites.tsx supply the markup this subset covers (MIT; see
// THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var nodes = LobsterSvg.ParseFragment("""<g fill="none"><path d="M46 14 Q38 4 31 7"></path></g>""");
//   var segments = LobsterSvg.ParsePath("m10 10 h5 v5 z");   // absolute Move, Line, Line, Close
//   var matrix = LobsterSvg.ParseTransform("rotate(-32 16 84)");
using System.Globalization;
using System.Xml.Linq;

namespace OpenClawTray.Chat.LobsterVisits;

internal enum LobsterSvgElement { Group, Path, Circle, Ellipse, Rect, Polygon }

/// <summary>One parsed element. Attributes are the raw presentation values; <c>style</c> declarations are folded in.</summary>
internal sealed class LobsterSvgNode
{
    internal LobsterSvgNode(
        LobsterSvgElement element,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<LobsterSvgNode> children)
    {
        Element = element;
        Attributes = attributes;
        Children = children;
        Classes = attributes.TryGetValue("class", out var cls)
            ? cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    internal LobsterSvgElement Element { get; }
    internal IReadOnlyDictionary<string, string> Attributes { get; }
    internal IReadOnlyList<LobsterSvgNode> Children { get; }
    internal IReadOnlySet<string> Classes { get; }

    internal string? this[string name] => Attributes.TryGetValue(name, out var value) ? value : null;

    internal double Number(string name, double fallback = 0) =>
        this[name] is { } value ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;

    internal LobsterSvgNode With(string name, string value)
    {
        var attributes = new Dictionary<string, string>(Attributes, StringComparer.Ordinal) { [name] = value };
        return new LobsterSvgNode(Element, attributes, Children);
    }

    internal LobsterSvgNode WithChildren(IReadOnlyList<LobsterSvgNode> children) => new(Element, Attributes, children);

    /// <summary>A synthetic group, used by the composer to wrap pieces with a role.</summary>
    internal static LobsterSvgNode Group(IReadOnlyList<LobsterSvgNode> children, string? cls = null) => new(
        LobsterSvgElement.Group,
        cls is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["class"] = cls },
        children);
}

internal enum LobsterPathCommand { Move, Line, Cubic, Quadratic, Arc, Close }

/// <summary>
/// One absolute segment. <c>X</c>/<c>Y</c> is the end point (for Close, the subpath start); <c>X1..Y2</c> are
/// control points (Quadratic uses X1/Y1); arcs carry radii, rotation and flags.
/// </summary>
internal readonly record struct LobsterPathSegment(
    LobsterPathCommand Command,
    double X,
    double Y,
    double X1 = 0,
    double Y1 = 0,
    double X2 = 0,
    double Y2 = 0,
    double Rx = 0,
    double Ry = 0,
    double Rotation = 0,
    bool LargeArc = false,
    bool Sweep = false);

/// <summary>A 2D affine matrix in SVG order: (a b c d e f) maps (x, y) to (ax + cy + e, bx + dy + f).</summary>
internal readonly record struct LobsterMatrix(double A, double B, double C, double D, double E, double F)
{
    internal static readonly LobsterMatrix Identity = new(1, 0, 0, 1, 0, 0);

    internal bool IsIdentity => this == Identity;

    /// <summary>The matrix that applies <paramref name="inner"/> first, then this one.</summary>
    internal LobsterMatrix Then(LobsterMatrix inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    internal (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    internal static LobsterMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    internal static LobsterMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    internal static LobsterMatrix Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return new LobsterMatrix(cos, sin, -sin, cos, 0, 0);
    }

    internal static LobsterMatrix Around(LobsterMatrix matrix, double x, double y) =>
        Translate(x, y).Then(matrix).Then(Translate(-x, -y));
}

internal static class LobsterSvg
{
    private static readonly Dictionary<string, LobsterSvgElement> Elements = new(StringComparer.Ordinal)
    {
        ["g"] = LobsterSvgElement.Group,
        ["path"] = LobsterSvgElement.Path,
        ["circle"] = LobsterSvgElement.Circle,
        ["ellipse"] = LobsterSvgElement.Ellipse,
        ["rect"] = LobsterSvgElement.Rect,
        ["polygon"] = LobsterSvgElement.Polygon,
    };

    /// <summary>Parses sibling SVG elements. Throws <see cref="FormatException"/> for elements outside the subset.</summary>
    internal static IReadOnlyList<LobsterSvgNode> ParseFragment(string markup)
    {
        var root = XElement.Parse("<svg>" + markup + "</svg>");
        return root.Elements().Select(ToNode).ToArray();
    }

    private static LobsterSvgNode ToNode(XElement element)
    {
        if (!Elements.TryGetValue(element.Name.LocalName, out var kind))
            throw new FormatException($"Unsupported SVG element <{element.Name.LocalName}>.");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName != "style")
                attributes[attribute.Name.LocalName] = attribute.Value.Trim();
        }
        // Inline style beats presentation attributes; custom properties (--i) only drive CSS-only loops.
        if (element.Attribute("style")?.Value is { } style)
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = declaration[..colon].Trim();
                if (!name.StartsWith("--", StringComparison.Ordinal))
                    attributes[name] = declaration[(colon + 1)..].Trim();
            }
        }
        return new LobsterSvgNode(kind, attributes, element.Elements().Select(ToNode).ToArray());
    }

    /// <summary>SVG path data to absolute segments; S/T reflections are expanded to plain cubic/quadratic curves.</summary>
    internal static IReadOnlyList<LobsterPathSegment> ParsePath(string data)
    {
        var reader = new NumberReader(data);
        var segments = new List<LobsterPathSegment>();
        double x = 0, y = 0, startX = 0, startY = 0;
        double lastControlX = 0, lastControlY = 0;
        var lastCommand = ' ';
        var command = ' ';
        while (reader.SkipSeparators())
        {
            if (char.IsLetter(reader.Peek))
                command = reader.Next();
            else if (command == ' ')
                throw new FormatException($"Path data must start with a command: '{data}'.");
            var relative = char.IsLower(command);
            double ox = relative ? x : 0, oy = relative ? y : 0;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    x = ox + reader.Number();
                    y = oy + reader.Number();
                    startX = x;
                    startY = y;
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Move, x, y));
                    // Extra coordinate pairs after a move are implicit line-tos.
                    command = relative ? 'l' : 'L';
                    lastCommand = 'M';
                    continue;
                case 'L':
                    x = ox + reader.Number();
                    y = oy + reader.Number();
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Line, x, y));
                    break;
                case 'H':
                    x = ox + reader.Number();
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Line, x, y));
                    break;
                case 'V':
                    y = oy + reader.Number();
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Line, x, y));
                    break;
                case 'C':
                case 'S':
                {
                    double x1, y1;
                    if (char.ToUpperInvariant(command) == 'C')
                    {
                        x1 = ox + reader.Number();
                        y1 = oy + reader.Number();
                    }
                    else
                    {
                        var reflect = lastCommand is 'C' or 'S';
                        x1 = reflect ? 2 * x - lastControlX : x;
                        y1 = reflect ? 2 * y - lastControlY : y;
                    }
                    var x2 = ox + reader.Number();
                    var y2 = oy + reader.Number();
                    x = ox + reader.Number();
                    y = oy + reader.Number();
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Cubic, x, y, x1, y1, x2, y2));
                    lastControlX = x2;
                    lastControlY = y2;
                    lastCommand = char.ToUpperInvariant(command);
                    continue;
                }
                case 'Q':
                case 'T':
                {
                    double x1, y1;
                    if (char.ToUpperInvariant(command) == 'Q')
                    {
                        x1 = ox + reader.Number();
                        y1 = oy + reader.Number();
                    }
                    else
                    {
                        var reflect = lastCommand is 'Q' or 'T';
                        x1 = reflect ? 2 * x - lastControlX : x;
                        y1 = reflect ? 2 * y - lastControlY : y;
                    }
                    x = ox + reader.Number();
                    y = oy + reader.Number();
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Quadratic, x, y, x1, y1));
                    lastControlX = x1;
                    lastControlY = y1;
                    lastCommand = char.ToUpperInvariant(command);
                    continue;
                }
                case 'A':
                {
                    var rx = reader.Number();
                    var ry = reader.Number();
                    var rotation = reader.Number();
                    var large = reader.Flag();
                    var sweep = reader.Flag();
                    x = ox + reader.Number();
                    y = oy + reader.Number();
                    segments.Add(new LobsterPathSegment(
                        LobsterPathCommand.Arc, x, y, Rx: Math.Abs(rx), Ry: Math.Abs(ry), Rotation: rotation,
                        LargeArc: large, Sweep: sweep));
                    break;
                }
                case 'Z':
                    x = startX;
                    y = startY;
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Close, x, y));
                    lastCommand = 'Z';
                    // Z takes no arguments; a following number is an error, a following command resets it.
                    command = ' ';
                    continue;
                default:
                    throw new FormatException($"Unsupported path command '{command}' in '{data}'.");
            }
            lastCommand = char.ToUpperInvariant(command);
        }
        return segments;
    }

    /// <summary>A polygon/polyline <c>points</c> list.</summary>
    internal static IReadOnlyList<(double X, double Y)> ParsePoints(string points)
    {
        var reader = new NumberReader(points);
        var result = new List<(double, double)>();
        while (reader.SkipSeparators())
            result.Add((reader.Number(), reader.Number()));
        return result;
    }

    /// <summary>An SVG transform list (translate, rotate with optional centre, scale, matrix) as one matrix.</summary>
    internal static LobsterMatrix ParseTransform(string? transform)
    {
        var result = LobsterMatrix.Identity;
        if (string.IsNullOrWhiteSpace(transform))
            return result;
        var index = 0;
        while (index < transform.Length)
        {
            var open = transform.IndexOf('(', index);
            if (open < 0)
                break;
            var name = transform[index..open].Trim(' ', ',', '\t', '\n');
            var close = transform.IndexOf(')', open);
            var reader = new NumberReader(transform[(open + 1)..close]);
            var args = new List<double>();
            while (reader.SkipSeparators())
                args.Add(reader.Number());
            var step = name switch
            {
                "translate" => LobsterMatrix.Translate(args[0], args.Count > 1 ? args[1] : 0),
                "scale" => LobsterMatrix.Scale(args[0], args.Count > 1 ? args[1] : args[0]),
                "rotate" when args.Count >= 3 => LobsterMatrix.Around(LobsterMatrix.Rotate(args[0]), args[1], args[2]),
                "rotate" => LobsterMatrix.Rotate(args[0]),
                "matrix" => new LobsterMatrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => throw new FormatException($"Unsupported transform '{name}'."),
            };
            result = result.Then(step);
            index = close + 1;
        }
        return result;
    }

    /// <summary>Converts any leaf element to absolute path segments, so renderers need only one geometry builder.</summary>
    internal static IReadOnlyList<LobsterPathSegment> Geometry(LobsterSvgNode node)
    {
        switch (node.Element)
        {
            case LobsterSvgElement.Path:
                return ParsePath(node["d"] ?? string.Empty);
            case LobsterSvgElement.Circle:
                return EllipseSegments(node.Number("cx"), node.Number("cy"), node.Number("r"), node.Number("r"));
            case LobsterSvgElement.Ellipse:
                return EllipseSegments(node.Number("cx"), node.Number("cy"), node.Number("rx"), node.Number("ry"));
            case LobsterSvgElement.Rect:
                return RectSegments(node);
            case LobsterSvgElement.Polygon:
            {
                var points = ParsePoints(node["points"] ?? string.Empty);
                var segments = new List<LobsterPathSegment>();
                for (var i = 0; i < points.Count; i++)
                {
                    segments.Add(new LobsterPathSegment(
                        i == 0 ? LobsterPathCommand.Move : LobsterPathCommand.Line, points[i].X, points[i].Y));
                }
                if (points.Count > 0)
                    segments.Add(new LobsterPathSegment(LobsterPathCommand.Close, points[0].X, points[0].Y));
                return segments;
            }
            default:
                return [];
        }
    }

    private static LobsterPathSegment[] EllipseSegments(double cx, double cy, double rx, double ry) =>
    [
        new(LobsterPathCommand.Move, cx - rx, cy),
        new(LobsterPathCommand.Arc, cx + rx, cy, Rx: rx, Ry: ry, Sweep: true),
        new(LobsterPathCommand.Arc, cx - rx, cy, Rx: rx, Ry: ry, Sweep: true),
        new(LobsterPathCommand.Close, cx - rx, cy),
    ];

    private static LobsterPathSegment[] RectSegments(LobsterSvgNode node)
    {
        double x = node.Number("x"), y = node.Number("y"), w = node.Number("width"), h = node.Number("height");
        // SVG: a missing rx/ry copies the other; both clamp to half the side.
        var rx = node["rx"] is null ? node.Number("ry") : node.Number("rx");
        var ry = node["ry"] is null ? rx : node.Number("ry");
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);
        if (rx <= 0 || ry <= 0)
        {
            return
            [
                new(LobsterPathCommand.Move, x, y),
                new(LobsterPathCommand.Line, x + w, y),
                new(LobsterPathCommand.Line, x + w, y + h),
                new(LobsterPathCommand.Line, x, y + h),
                new(LobsterPathCommand.Close, x, y),
            ];
        }
        return
        [
            new(LobsterPathCommand.Move, x + rx, y),
            new(LobsterPathCommand.Line, x + w - rx, y),
            new(LobsterPathCommand.Arc, x + w, y + ry, Rx: rx, Ry: ry, Sweep: true),
            new(LobsterPathCommand.Line, x + w, y + h - ry),
            new(LobsterPathCommand.Arc, x + w - rx, y + h, Rx: rx, Ry: ry, Sweep: true),
            new(LobsterPathCommand.Line, x + rx, y + h),
            new(LobsterPathCommand.Arc, x, y + h - ry, Rx: rx, Ry: ry, Sweep: true),
            new(LobsterPathCommand.Line, x, y + ry),
            new(LobsterPathCommand.Arc, x + rx, y, Rx: rx, Ry: ry, Sweep: true),
            new(LobsterPathCommand.Close, x + rx, y),
        ];
    }

    /// <summary>Reads SVG numbers, including compact forms such as <c>1.5.5</c> and <c>-1-2</c>, plus arc flags.</summary>
    private sealed class NumberReader(string text)
    {
        private int _index;

        internal char Peek => text[_index];

        internal char Next() => text[_index++];

        /// <summary>Skips whitespace and commas; returns false at the end of input.</summary>
        internal bool SkipSeparators()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
                _index++;
            return _index < text.Length;
        }

        internal bool Flag()
        {
            if (!SkipSeparators() || text[_index] is not ('0' or '1'))
                throw new FormatException($"Expected an arc flag at {_index} in '{text}'.");
            return text[_index++] == '1';
        }

        internal double Number()
        {
            if (!SkipSeparators())
                throw new FormatException($"Expected a number at the end of '{text}'.");
            var start = _index;
            if (text[_index] is '+' or '-')
                _index++;
            var digits = 0;
            while (_index < text.Length && char.IsAsciiDigit(text[_index]))
            {
                _index++;
                digits++;
            }
            if (_index < text.Length && text[_index] == '.')
            {
                _index++;
                while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                {
                    _index++;
                    digits++;
                }
            }
            if (digits == 0)
                throw new FormatException($"Expected a number at {start} in '{text}'.");
            if (_index < text.Length && text[_index] is 'e' or 'E')
            {
                var save = _index++;
                if (_index < text.Length && text[_index] is '+' or '-')
                    _index++;
                if (_index < text.Length && char.IsAsciiDigit(text[_index]))
                {
                    while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                        _index++;
                }
                else
                {
                    _index = save;
                }
            }
            return double.Parse(text.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
