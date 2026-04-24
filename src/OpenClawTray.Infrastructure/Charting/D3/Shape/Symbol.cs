// Port of d3-shape/src/symbol/ — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Defines a symbol shape type (circle, cross, diamond, square, star, triangle, wye).
/// </summary>
public interface ISymbolType
{
    void Draw(PathBuilder path, double size);
}

/// <summary>
/// Generates SVG path data for symbols (markers) at data points.
/// Direct port of d3.symbol().
/// </summary>
public sealed class SymbolGenerator<T>
{
    private Func<T, int, ISymbolType> _type;
    private Func<T, int, double> _size;
    private int? _digits = 3;

    public SymbolGenerator(Func<T, int, ISymbolType> type, Func<T, int, double> size)
    {
        _type = type;
        _size = size;
    }

    /// <summary>Generates the SVG path string for a symbol.</summary>
    public string? Generate(T datum, int index = 0)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        _type(datum, index).Draw(path, _size(datum, index));
        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public SymbolGenerator<T> SetType(Func<T, int, ISymbolType> type) { _type = type; return this; }
    public SymbolGenerator<T> SetType(ISymbolType type) { _type = (_, _) => type; return this; }
    public SymbolGenerator<T> SetSize(Func<T, int, double> size) { _size = size; return this; }
    public SymbolGenerator<T> SetSize(double size) { _size = (_, _) => size; return this; }
    public SymbolGenerator<T> SetDigits(int? digits) { _digits = digits; return this; }
}

public static class SymbolGenerator
{
    /// <summary>Creates a symbol generator with default circle type and size 64.</summary>
    public static SymbolGenerator<object?> Create()
        => new((_, _) => D3Symbol.Circle, (_, _) => 64);

    /// <summary>Creates a symbol generator with the given type and size.</summary>
    public static SymbolGenerator<T> Create<T>(ISymbolType type, double size = 64)
        => new((_, _) => type, (_, _) => size);
}

/// <summary>Built-in symbol types matching d3-shape.</summary>
public static class D3Symbol
{
    public static readonly ISymbolType Circle = new SymbolCircle();
    public static readonly ISymbolType Cross = new SymbolCross();
    public static readonly ISymbolType Diamond = new SymbolDiamond();
    public static readonly ISymbolType Square = new SymbolSquare();
    public static readonly ISymbolType Star = new SymbolStar();
    public static readonly ISymbolType Triangle = new SymbolTriangle();
    public static readonly ISymbolType Wye = new SymbolWye();

    /// <summary>All built-in symbol types.</summary>
    public static readonly ISymbolType[] All = [Circle, Cross, Diamond, Square, Star, Triangle, Wye];
}

internal sealed class SymbolCircle : ISymbolType
{
    public void Draw(PathBuilder path, double size)
    {
        double r = Math.Sqrt(size / Math.PI);
        path.MoveTo(r, 0);
        path.Arc(0, 0, r, 0, 2 * Math.PI);
    }
}

internal sealed class SymbolCross : ISymbolType
{
    public void Draw(PathBuilder path, double size)
    {
        double r = Math.Sqrt(size / 5) / 2;
        path.MoveTo(-3 * r, -r);
        path.LineTo(-3 * r, r);
        path.LineTo(-r, r);
        path.LineTo(-r, 3 * r);
        path.LineTo(r, 3 * r);
        path.LineTo(r, r);
        path.LineTo(3 * r, r);
        path.LineTo(3 * r, -r);
        path.LineTo(r, -r);
        path.LineTo(r, -3 * r);
        path.LineTo(-r, -3 * r);
        path.LineTo(-r, -r);
        path.ClosePath();
    }
}

internal sealed class SymbolDiamond : ISymbolType
{
    private static readonly double Tan30 = Math.Sqrt(1.0 / 3);
    private static readonly double Tan30_2 = Tan30 * 2;

    public void Draw(PathBuilder path, double size)
    {
        double y = Math.Sqrt(size / Tan30_2);
        double x = y * Tan30;
        path.MoveTo(0, -y);
        path.LineTo(x, 0);
        path.LineTo(0, y);
        path.LineTo(-x, 0);
        path.ClosePath();
    }
}

internal sealed class SymbolSquare : ISymbolType
{
    public void Draw(PathBuilder path, double size)
    {
        double w = Math.Sqrt(size);
        double x = -w / 2;
        path.Rect(x, x, w, w);
    }
}

internal sealed class SymbolStar : ISymbolType
{
    private static readonly double Ka = 0.89081309152928522810;

    public void Draw(PathBuilder path, double size)
    {
        double r = Math.Sqrt(size * Ka);
        double ri = r * 0.38196601125; // inner radius ratio for pentagram

        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 5 * i - Math.PI / 2;
            double rad = (i & 1) != 0 ? ri : r;
            double x = Math.Cos(angle) * rad;
            double y = Math.Sin(angle) * rad;
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        path.ClosePath();
    }
}

internal sealed class SymbolTriangle : ISymbolType
{
    private static readonly double Sqrt3 = Math.Sqrt(3);

    public void Draw(PathBuilder path, double size)
    {
        double y = -Math.Sqrt(size / (Sqrt3 * 3));
        path.MoveTo(0, y * 2);
        path.LineTo(-Sqrt3 * y, -y);
        path.LineTo(Sqrt3 * y, -y);
        path.ClosePath();
    }
}

internal sealed class SymbolWye : ISymbolType
{
    private static readonly double C = -0.5;
    private static readonly double S = Math.Sqrt(3) / 2;
    private static readonly double K = 1.0 / Math.Sqrt(12);
    private static readonly double A = (K / 2 + 1) * 3;

    public void Draw(PathBuilder path, double size)
    {
        double r = Math.Sqrt(size / A);
        double x0 = r / 2, y0 = r * K;
        double x1 = x0, y1 = r * K + r;
        double x2 = -x1, y2 = y1;

        path.MoveTo(x0, y0);
        path.LineTo(x1, y1);
        path.LineTo(x2, y2);
        path.LineTo(-(x0), y0);

        path.LineTo(C * x0 - S * y0, S * x0 + C * y0);
        path.LineTo(C * x1 - S * y1, S * x1 + C * y1);
        path.LineTo(C * x2 - S * y2, S * x2 + C * y2);
        path.LineTo(-(C * x0 - S * y0), -(S * x0 + C * y0));

        path.LineTo(C * x0 + S * y0, C * y0 - S * x0);
        path.LineTo(C * x1 + S * y1, C * y1 - S * x1);
        path.LineTo(C * x2 + S * y2, C * y2 - S * x2);
        path.LineTo(-(C * x0 + S * y0), -(C * y0 - S * x0));

        path.ClosePath();
    }
}
