using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class NativeProofLayoutTests
{
    [Theory]
    [InlineData(1, 258)]
    [InlineData(1.25, 257)]
    [InlineData(1.5, 257)]
    [InlineData(1.75, 257)]
    [InlineData(2, 258)]
    public void Centering_RoundsOffsetInsideCommonParent_NotIndependentCenters(double scale, int heroLeft)
    {
        var parent = 48 / scale;
        var width = 601 / scale;
        var hero = NativeProofLayout.CenteredPhysicalEdges(parent, 0, width, 182 / scale, scale);
        var text = NativeProofLayout.CenteredPhysicalEdges(parent, 0, width, 301 / scale, scale);
        // Float subtraction before double-precision LayoutRound can put the half-pixel
        // just below its midpoint on fractional plateaus. These are exact, not tolerances.
        Assert.Equal((heroLeft, heroLeft + 182), hero);
        Assert.Equal((198, 499), text);
        Assert.NotEqual((hero.Left + hero.Right) / 2d, (text.Left + text.Right) / 2d);
        Assert.NotEqual(text, (text.Left + 1, text.Right + 1));
        Assert.NotEqual(text, (text.Left - 1, text.Right - 1));
    }

    [Theory]
    [InlineData(1, 182, 182)]
    [InlineData(1.25, 182.39999389648438, 228)]
    [InlineData(1.5, 182, 273)]
    [InlineData(1.75, 182.28572082519531, 319)]
    [InlineData(2, 182, 364)]
    public void HeroSize_UsesExactPhysicalRoundingAndWinUiFloatStorage(double scale, double expectedView, int physical)
    {
        Assert.Equal(expectedView, NativeProofLayout.RoundedViewSize(182, scale));
        Assert.Equal(physical, NativeProofLayout.PhysicalPixels(182, scale));
        Assert.Equal(physical, NativeProofLayout.PhysicalPixels(expectedView, scale));
    }

    [Fact]
    public void ContentScale_IsIndependentOfTheWindowDpiValue()
    {
        Assert.Equal(876, NativeProofLayout.PhysicalPixels(876, 1));
        Assert.Equal(876, NativeProofLayout.PhysicalPixels((float)(876 / 1.75), 1.75));
        Assert.Equal(1533, NativeProofLayout.PhysicalPixels(876, 1.75));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public void ChromeInset_IsConditionalAndPhysical(bool extended, bool maximized, int expected) =>
        Assert.Equal(expected, NativeProofLayout.ContentTopInset(extended, maximized));

    [Fact]
    public void RecordedSetupContent_FillsExactClientBelowChromeBorder()
    {
        const double scale = 1.75;
        Assert.Equal(1236, NativeProofLayout.PhysicalPixels(706.2857055664062, scale));
        Assert.Equal(1422, NativeProofLayout.PhysicalPixels(812.5714111328125, scale));
        Assert.Equal(1423, NativeProofLayout.PhysicalPixels(812.5714111328125, scale) +
            NativeProofLayout.ContentTopInset(extendsContentIntoTitleBar: true, maximized: false));
    }

    [Fact]
    public void RecordedConsentEdges_SnapToTheSamePixelWithoutDipTolerance()
    {
        const double scale = 1.5;
        var viewport = (Left: 0d, Top: 0d, Right: 600d, Bottom: 424.6666564941406);
        var consent = (Left: 0d, Top: 382.0000305175781, Right: 488.6666564941406,
            Bottom: 382.0000305175781 + 42.666656494140625);
        Assert.Equal(637, NativeProofLayout.PhysicalEdge(viewport.Bottom, scale));
        Assert.Equal(637, NativeProofLayout.PhysicalEdge(consent.Bottom, scale));
        Assert.True(NativeProofLayout.ContainsPhysicalEdges(viewport, consent, scale));
        Assert.False(NativeProofLayout.ContainsPhysicalEdges(viewport, consent with { Bottom = consent.Bottom + 1 / scale }, scale));
    }

    [Theory]
    [InlineData(-1, 0, 30, 30)]
    [InlineData(0, -1, 30, 30)]
    [InlineData(0, 0, 31, 30)]
    [InlineData(0, 0, 30, 31)]
    public void Containment_RejectsOnePhysicalPixelOverflowOnEveryEdge(double left, double top, double right, double bottom)
    {
        const double scale = 1.5;
        Assert.False(NativeProofLayout.ContainsPhysicalEdges((0, 0, 20, 20),
            (left / scale, top / scale, right / scale, bottom / scale), scale));
    }

    [Fact]
    public void Containment_RoundsEdgesInOneSharedCoordinateSpace()
    {
        const double scale = 1.75;
        var viewport = (Left: 12.285714149475098, Top: 18.85714340209961, Right: 132.2857141494751, Bottom: 88.285717);
        Assert.True(NativeProofLayout.ContainsPhysicalEdges(viewport, viewport, scale));
        Assert.False(NativeProofLayout.ContainsPhysicalEdges(viewport,
            viewport with { Left = viewport.Left - 1 / scale }, scale));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(182, 0)]
    [InlineData(182, double.NaN)]
    [InlineData(double.PositiveInfinity, 1.75)]
    public void InvalidGeometryFailsExplicitly(double size, double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeProofLayout.RoundedViewSize(size, scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeProofLayout.PhysicalPixels(size, scale));
    }
}
