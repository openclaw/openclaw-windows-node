// Port of d3-chord — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Computes chord layout from a square matrix of flows.
/// Direct port of d3.chord().
/// </summary>
public sealed class ChordLayout
{
    private double _padAngle = 0;

    /// <summary>
    /// Computes chord groups and chords from a square flow matrix.
    /// matrix[i][j] = flow from group i to group j.
    /// </summary>
    public ChordData Generate(double[][] matrix)
    {
        int n = matrix.Length;
        var groupSums = new double[n];
        double total = 0;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                groupSums[i] += matrix[i][j];
            }
            total += groupSums[i];
        }

        double da = Math.Max(0, 2 * Math.PI - _padAngle * n) / total;
        double dp = total > 0 ? _padAngle : 2 * Math.PI / n;

        // Compute group arcs
        var groups = new ChordGroup[n];
        double angle = 0;
        for (int i = 0; i < n; i++)
        {
            double a0 = angle;
            angle += groupSums[i] * da + dp;
            groups[i] = new ChordGroup
            {
                Index = i,
                StartAngle = a0,
                EndAngle = a0 + groupSums[i] * da,
                Value = groupSums[i]
            };
        }

        // Compute chords
        var chords = new List<ChordArc>();
        var subAngle = new double[n];
        for (int i = 0; i < n; i++)
            subAngle[i] = groups[i].StartAngle;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double v = matrix[i][j];
                if (v <= 0) continue;

                double sa = subAngle[i];
                double ea = sa + v * da;
                subAngle[i] = ea;

                if (j > i)
                {
                    // Pair with the reverse chord
                    double v2 = matrix[j][i];
                    double sa2 = subAngle[j];
                    double ea2 = sa2 + v2 * da;
                    subAngle[j] = ea2;

                    chords.Add(new ChordArc
                    {
                        Source = new ChordEnd { Index = i, StartAngle = sa, EndAngle = ea, Value = v },
                        Target = new ChordEnd { Index = j, StartAngle = sa2, EndAngle = ea2, Value = v2 },
                    });
                }
                else if (j == i)
                {
                    chords.Add(new ChordArc
                    {
                        Source = new ChordEnd { Index = i, StartAngle = sa, EndAngle = ea, Value = v },
                        Target = new ChordEnd { Index = j, StartAngle = sa, EndAngle = ea, Value = v },
                    });
                }
            }
        }

        return new ChordData { Groups = groups, Chords = chords.ToArray() };
    }

    public ChordLayout SetPadAngle(double angle) { _padAngle = angle; return this; }
}

/// <summary>
/// Generates SVG path data for chord ribbons.
/// Direct port of d3.ribbon().
/// </summary>
public sealed class RibbonGenerator
{
    private double _radius = 100;
    private int? _digits = 3;

    /// <summary>Generates the SVG path for a chord arc.</summary>
    public string? Generate(ChordArc chord)
    {
        var path = _digits is int d ? new PathBuilder(d) : new PathBuilder();
        double r = _radius;

        double sa0 = chord.Source.StartAngle - Math.PI / 2;
        double sa1 = chord.Source.EndAngle - Math.PI / 2;
        double ta0 = chord.Target.StartAngle - Math.PI / 2;
        double ta1 = chord.Target.EndAngle - Math.PI / 2;

        path.MoveTo(r * Math.Cos(sa0), r * Math.Sin(sa0));
        path.Arc(0, 0, r, sa0, sa1);
        if (sa0 != ta0 || sa1 != ta1)
        {
            path.QuadraticCurveTo(0, 0, r * Math.Cos(ta0), r * Math.Sin(ta0));
            path.Arc(0, 0, r, ta0, ta1);
        }
        path.QuadraticCurveTo(0, 0, r * Math.Cos(sa0), r * Math.Sin(sa0));
        path.ClosePath();

        string result = path.ToString();
        return result.Length > 0 ? result : null;
    }

    public RibbonGenerator SetRadius(double r) { _radius = r; return this; }
    public RibbonGenerator SetDigits(int? digits) { _digits = digits; return this; }
}

public record struct ChordData
{
    public ChordGroup[] Groups { get; init; }
    public ChordArc[] Chords { get; init; }
}

public record struct ChordGroup
{
    public int Index { get; init; }
    public double StartAngle { get; init; }
    public double EndAngle { get; init; }
    public double Value { get; init; }
}

public record struct ChordArc
{
    public ChordEnd Source { get; init; }
    public ChordEnd Target { get; init; }
}

public record struct ChordEnd
{
    public int Index { get; init; }
    public double StartAngle { get; init; }
    public double EndAngle { get; init; }
    public double Value { get; init; }
}
