using OpenClawTray.Infrastructure.Charting.D3;
// Converts SVG path data strings to WinUI PathGeometry objects.
// This is a minimal parser that handles the subset of SVG path commands
// produced by our PathBuilder: M, L, A, Q, C, Z, h, v

using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OpenClawTray.Infrastructure.Charting;

/// <summary>
/// Parses SVG path data strings into WinUI PathGeometry.
/// Handles the subset of commands output by D3's PathBuilder.
/// </summary>
public static class PathDataParser
{
    public static Geometry Parse(string pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData))
            return new PathGeometry();

        var geometry = new PathGeometry();
        PathFigure? figure = null;
        double cx = 0, cy = 0; // current point

        int i = 0;
        while (i < pathData.Length)
        {
            char cmd = pathData[i];
            if (char.IsWhiteSpace(cmd) || cmd == ',') { i++; continue; }

            switch (cmd)
            {
                case 'M':
                    i++;
                    double mx = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double my = ReadNumber(pathData, ref i);
                    figure = new PathFigure { StartPoint = new Point(mx, my), IsClosed = false };
                    geometry.Figures.Add(figure);
                    cx = mx; cy = my;
                    break;

                case 'L':
                    i++;
                    double lx = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double ly = ReadNumber(pathData, ref i);
                    figure?.Segments.Add(new LineSegment { Point = new Point(lx, ly) });
                    cx = lx; cy = ly;
                    break;

                case 'h':
                    i++;
                    double hdx = ReadNumber(pathData, ref i);
                    cx += hdx;
                    figure?.Segments.Add(new LineSegment { Point = new Point(cx, cy) });
                    break;

                case 'v':
                    i++;
                    double vdy = ReadNumber(pathData, ref i);
                    cy += vdy;
                    figure?.Segments.Add(new LineSegment { Point = new Point(cx, cy) });
                    break;

                case 'A':
                    i++;
                    double rx = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double ry = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double rotation = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    int largeArc = (int)ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    int sweep = (int)ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double ax = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double ay = ReadNumber(pathData, ref i);
                    figure?.Segments.Add(new ArcSegment
                    {
                        Point = new Point(ax, ay),
                        Size = new Size(rx, ry),
                        RotationAngle = rotation,
                        IsLargeArc = largeArc != 0,
                        SweepDirection = sweep != 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    });
                    cx = ax; cy = ay;
                    break;

                case 'Q':
                    i++;
                    double qx1 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double qy1 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double qx = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double qy = ReadNumber(pathData, ref i);
                    figure?.Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(qx1, qy1),
                        Point2 = new Point(qx, qy),
                    });
                    cx = qx; cy = qy;
                    break;

                case 'C':
                    i++;
                    double cx1 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double cy1 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double cx2 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double cy2 = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double cex = ReadNumber(pathData, ref i);
                    SkipComma(pathData, ref i);
                    double cey = ReadNumber(pathData, ref i);
                    figure?.Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(cx1, cy1),
                        Point2 = new Point(cx2, cy2),
                        Point3 = new Point(cex, cey),
                    });
                    cx = cex; cy = cey;
                    break;

                case 'Z':
                    i++;
                    if (figure != null) figure.IsClosed = true;
                    break;

                default:
                    // Skip unknown characters
                    i++;
                    break;
            }
        }

        return geometry;
    }

    private static void SkipComma(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ',' || s[i] == ' ')) i++;
    }

    private static double ReadNumber(string s, ref int i)
    {
        // Skip whitespace and commas
        while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;

        int start = i;
        // Optional sign
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        // Digits and decimal
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        // Scientific notation
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }

        if (start == i) return 0;
        return double.Parse(s[start..i], CultureInfo.InvariantCulture);
    }
}
