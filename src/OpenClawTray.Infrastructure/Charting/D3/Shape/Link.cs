// Port of d3-shape/src/link.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Generates SVG path data for smooth links/edges between two points.
/// Direct port of d3.link(), d3.linkHorizontal(), d3.linkVertical().
/// </summary>
public sealed class LinkGenerator<T>
{
    private Func<T, (double x, double y)> _source;
    private Func<T, (double x, double y)> _target;
    private LinkOrientation _orientation;
    private int? _digits = 3;

    public LinkGenerator(
        Func<T, (double x, double y)> source,
        Func<T, (double x, double y)> target,
        LinkOrientation orientation = LinkOrientation.Vertical)
    {
        _source = source;
        _target = target;
        _orientation = orientation;
    }

    /// <summary>Generates the SVG path string for the given link data.</summary>
    public string? Generate(T datum)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        var s = _source(datum);
        var t = _target(datum);

        switch (_orientation)
        {
            case LinkOrientation.Horizontal:
                double mx = (s.x + t.x) / 2;
                path.MoveTo(s.x, s.y);
                path.BezierCurveTo(mx, s.y, mx, t.y, t.x, t.y);
                break;

            case LinkOrientation.Vertical:
                double my = (s.y + t.y) / 2;
                path.MoveTo(s.x, s.y);
                path.BezierCurveTo(s.x, my, t.x, my, t.x, t.y);
                break;
        }

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public LinkGenerator<T> SetSource(Func<T, (double x, double y)> source) { _source = source; return this; }
    public LinkGenerator<T> SetTarget(Func<T, (double x, double y)> target) { _target = target; return this; }
    public LinkGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

public enum LinkOrientation
{
    Horizontal,
    Vertical
}

public static class LinkGenerator
{
    /// <summary>Creates a horizontal link generator (source left, target right).</summary>
    public static LinkGenerator<(TNode source, TNode target)> Horizontal<TNode>(
        Func<TNode, double> x, Func<TNode, double> y)
        => new(d => (x(d.source), y(d.source)), d => (x(d.target), y(d.target)), LinkOrientation.Horizontal);

    /// <summary>Creates a vertical link generator (source top, target bottom).</summary>
    public static LinkGenerator<(TNode source, TNode target)> Vertical<TNode>(
        Func<TNode, double> x, Func<TNode, double> y)
        => new(d => (x(d.source), y(d.source)), d => (x(d.target), y(d.target)), LinkOrientation.Vertical);
}
