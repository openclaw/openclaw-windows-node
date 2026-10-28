using OpenClawTray.Systems;

namespace OpenClaw.Tray.Tests.Systems;

public sealed class SparklineGeometryTests
{
    [Fact]
    public void FixedRange_ScalesFromZeroToTheCeilingOrPeak()
    {
        Assert.Equal(new SparklineGeometry.Range(0, 64), SparklineGeometry.Scale([10, 20], 64, autorange: false));
        Assert.Equal(new SparklineGeometry.Range(0, 80), SparklineGeometry.Scale([10, 80], 64, autorange: false));
        Assert.Equal(new SparklineGeometry.Range(0, 1), SparklineGeometry.Scale([], 0, autorange: false));
    }

    [Fact]
    public void Autorange_KeepsASteadySeriesVisibleWithoutDroppingToZero()
    {
        var range = SparklineGeometry.Scale([400, 401, 402], 1000, autorange: true);
        Assert.True(range.Min > 300);
        Assert.True(range.Min < 400);
        Assert.True(range.Min + range.Span > 402);

        var flat = SparklineGeometry.Scale([500, 500], 1000, autorange: true);
        Assert.True(flat.Span > 0);
    }

    [Fact]
    public void Points_RunOldestLeftToNewestRightInsideThePadding()
    {
        var points = SparklineGeometry.Points([0, 50, 100], new SparklineGeometry.Range(0, 100), 200, 48);
        Assert.Equal([0d, 100d, 200d], points.Select(p => p.X));
        Assert.Equal(48 - SparklineGeometry.BottomPadding, points[0].Y);
        Assert.Equal(SparklineGeometry.TopPadding, points[2].Y);
        Assert.True(points[1].Y < points[0].Y && points[1].Y > points[2].Y);
    }

    [Fact]
    public void Points_PlaceASingleSampleAtTheRightEdgeAndClampOutliers()
    {
        var single = SparklineGeometry.Points([5], new SparklineGeometry.Range(0, 10), 120, 40);
        Assert.Equal(120, Assert.Single(single).X);

        var clamped = SparklineGeometry.Points([-5, 50], new SparklineGeometry.Range(0, 10), 100, 40);
        Assert.Equal(40 - SparklineGeometry.BottomPadding, clamped[0].Y);
        Assert.Equal(SparklineGeometry.TopPadding, clamped[1].Y);
        Assert.Empty(SparklineGeometry.Points([1, 2], new SparklineGeometry.Range(0, 10), 0, 40));
    }

    [Fact]
    public void NearestIndex_SnapsPointerToTheClosestSample()
    {
        Assert.Equal(0, SparklineGeometry.NearestIndex(-10, 100, 5));
        Assert.Equal(2, SparklineGeometry.NearestIndex(50, 100, 5));
        Assert.Equal(4, SparklineGeometry.NearestIndex(500, 100, 5));
        Assert.Equal(0, SparklineGeometry.NearestIndex(50, 100, 1));
    }
}
