using System.Text;
using OpenClawTray.A2UI.Rendering;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Boundary tests for <see cref="SvgGeometryBudget.Exceeds"/>: the pre-decode guard that
/// keeps pathological SVG geometry away from D2D, whose parse can block the UI thread past
/// the render timeout.
/// </summary>
public sealed class A2UISvgGeometryBudgetTests
{
    private static readonly int Budget = SvgGeometryBudget.MaxAttributeChars;

    private static bool Exceeds(string svg) => SvgGeometryBudget.Exceeds(Encoding.UTF8.GetBytes(svg));

    private static string Svg(string body) => $"<svg xmlns='http://www.w3.org/2000/svg'>{body}</svg>";

    [Fact]
    public void SmallPath_IsWithinBudget()
    {
        Assert.False(Exceeds(Svg("<path d='M0 0 L1 1'/>")));
    }

    [Fact]
    public void SinglePathData_RejectsOnlyAboveTheBudget()
    {
        Assert.False(Exceeds(Svg($"<path d=\"{new string('1', Budget)}\"/>")));
        Assert.True(Exceeds(Svg($"<path d=\"{new string('1', Budget + 1)}\"/>")));
    }

    [Fact]
    public void PathData_IsSummedAcrossElements()
    {
        var part = new string('1', Budget * 6 / 10);
        Assert.False(Exceeds(Svg($"<path d='{part}'/>")));
        Assert.True(Exceeds(Svg($"<path d='{part}'/><path d='{part}'/>")));
    }

    [Fact]
    public void PolygonPoints_CountTowardTheBudget()
    {
        Assert.True(Exceeds(Svg($"<polygon points='{new string('1', Budget + 1)}'/>")));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("data-d")]
    [InlineData("dx")]
    public void OtherAttributes_DoNotCount(string attribute)
    {
        Assert.False(Exceeds(Svg($"<path {attribute}='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void WhitespaceAroundEquals_StillCounts()
    {
        Assert.True(Exceeds(Svg($"<path\nd = \"{new string('1', Budget + 1)}\"/>")));
    }

    [Fact]
    public void Exceeds_CommentContainingAttributeLikeText_DoesNotHideRealAttribute()
    {
        Assert.True(Exceeds(Svg($"<!-- d=' --><path d='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void Exceeds_OversizedAttributeInsideComment_IsIgnored()
    {
        Assert.False(Exceeds(Svg($"<!-- d='{new string('1', Budget + 1)}' --><path d='M0 0'/>")));
    }

    [Fact]
    public void Exceeds_CdataAndProcessingInstruction_AreSkipped()
    {
        Assert.False(Exceeds(Svg($"<![CDATA[ d='{new string('1', Budget + 1)}' ]]><?pi d='{new string('1', Budget + 1)}'?>")));
        Assert.True(Exceeds(Svg($"<![CDATA[ d=' ]]><path d='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void Exceeds_QuotedValueContainingAttributeText_IsNotRescanned()
    {
        Assert.False(Exceeds(Svg($"<path id=\" d='{new string('1', Budget + 1)}'\" d='M0 0'/>")));
    }

    [Fact]
    public void IsUnsupportedEncoding_RejectsUtf16Utf32AndNul_AcceptsUtf8()
    {
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<svg/>")).ToArray()));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("<svg/>")).ToArray()));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.Unicode.GetBytes("<svg/>")));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, (byte)'<' }));
        Assert.False(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.UTF8.GetBytes(Svg(""))));
        Assert.False(SvgGeometryBudget.IsUnsupportedEncoding(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'s' }));
    }
}
